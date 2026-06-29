using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

#region Data Models

/// <summary>Information about a specific series processed during a VFS build.</summary>
/// <param name="Name">The display name of the series.</param>
/// <param name="ElapsedMs">Time taken to process in milliseconds.</param>
/// <param name="CreatedLinks">Number of links created for this series.</param>
public record SeriesProcessDetails(string Name, long ElapsedMs, int CreatedLinks);

/// <summary>Information about a root cleanup operation.</summary>
/// <param name="Path">The filesystem path that was cleaned.</param>
/// <param name="ElapsedMs">Time taken in milliseconds.</param>
public record RootCleanupDetails(string Path, long ElapsedMs);

/// <summary>Result returned by <see cref="VfsBuilder"/> after a build or clean operation.</summary>
/// <param name="RootPath">VFS root folder name.</param>
/// <param name="SeriesProcessed">Processed series count.</param>
/// <param name="ConsolidatedSeries">Count of secondary series merged into primary series via overrides.</param>
/// <param name="CreatedLinks">Successful links created.</param>
/// <param name="Skipped">Skipped items count.</param>
/// <param name="SkippedDetails">List of specific skipped link descriptions.</param>
/// <param name="Errors">Encountered errors.</param>
/// <param name="PlannedLinks">Target link count.</param>
/// <param name="SeriesDetails">List of detailed processing stats for each series.</param>
/// <param name="CleanupDetails">Details regarding root folder deletions.</param>
/// <param name="TotalElapsed">Total time taken for the entire operation.</param>
public record VfsBuildResult(
    string RootPath,
    int SeriesProcessed,
    int ConsolidatedSeries,
    int CreatedLinks,
    int Skipped,
    List<string> SkippedDetails,
    List<string> Errors,
    int PlannedLinks,
    List<SeriesProcessDetails> SeriesDetails,
    List<RootCleanupDetails> CleanupDetails,
    TimeSpan TotalElapsed
);

/// <summary>Result returned by <see cref="VfsBuilder.Audit"/>.</summary>
/// <param name="SeriesChecked">Number of valid series folders checked.</param>
/// <param name="BrokenLinksRemoved">Number of broken symlinks deleted.</param>
/// <param name="OrphanedFoldersRemoved">Number of orphaned series or empty subfolders deleted.</param>
/// <param name="RemovedItems">List of deleted paths.</param>
/// <param name="Errors">Encountered errors.</param>
public record VfsAuditResult(int SeriesChecked, int BrokenLinksRemoved, int OrphanedFoldersRemoved, List<string> RemovedItems, List<string> Errors);

/// <summary>Represents a file entity inside the VFS Blueprint.</summary>
/// <param name="Name">The formatted filename.</param>
/// <param name="Source">The absolute path to the source file.</param>
public record VfsBlueprintFile([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("source")] string Source);

/// <summary>Represents a season folder entity inside the VFS Blueprint.</summary>
/// <param name="Name">The formatted folder name.</param>
/// <param name="SeasonId">The numeric season ID identifier.</param>
/// <param name="Files">The collection of files within the season.</param>
public record VfsBlueprintSeason(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seasonId")] int? SeasonId,
    [property: JsonPropertyName("files")] IEnumerable<VfsBlueprintFile> Files
);

/// <summary>Represents a full series entity inside the VFS Blueprint.</summary>
/// <param name="Id">The Shoko Series ID.</param>
/// <param name="AnidbId">The AniDB Series ID.</param>
/// <param name="Title">The formatted display title of the series.</param>
/// <param name="RootFiles">Files located in the root of the series folder.</param>
/// <param name="Seasons">The collection of season folders.</param>
public record VfsBlueprintSeries(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("anidbId")] int AnidbId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("rootFiles")] IEnumerable<VfsBlueprintFile> RootFiles,
    [property: JsonPropertyName("seasons")] IEnumerable<VfsBlueprintSeason> Seasons
);

#endregion

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
/// <param name="metadataService">Metadata service used for series and episode resolution.</param>
/// <param name="assetLinker">Service for linking local media assets and Plex extras.</param>
/// <param name="videoService">Shoko video and import folder service.</param>
public class VfsBuilder(IMetadataService metadataService, VfsAssetLinker assetLinker, IVideoService videoService)
{
    #region Setup & State

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private sealed class VfsBuildSession
    {
        public ConcurrentDictionary<int, MapHelper.SeriesFileData> SeriesFileDataCache { get; } = new();
        public ConcurrentDictionary<string, Lazy<string[]>> SubtitleFileCache { get; } = new(VfsShared.PathComparer);
        public ConcurrentDictionary<string, Lazy<string[]>> MetadataFileCache { get; } = new(VfsShared.PathComparer);
        public ConcurrentDictionary<string, byte> CreatedDirs { get; } = new(VfsShared.PathComparer);
    }

    #endregion

    #region Public Interface

    /// <summary>Build or clean VFS for a single series ID.</summary>
    /// <param name="seriesId">Optional single series ID to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    /// <param name="seriesIds">Collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true) => BuildInternal(seriesIds, cleanRoot);

    /// <summary>Audits the VFS to find and remove orphaned series folders and broken symlinks.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result object containing audit statistics.</returns>
    public VfsAuditResult Audit(CancellationToken ct = default)
    {
        var removed = new List<string>();
        var errors = new List<string>();
        int brokenLinks = 0,
            orphanedFolders = 0,
            seriesChecked = 0;

        string rootName = VfsShared.ResolveRootFolderName();
        var allRoots = videoService.GetAllManagedFolders()?.Where(f => !VfsShared.IsSourceOnly(f)).Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList() ?? [];

        var validFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSeries = metadataService.GetAllShokoSeries();
        if (allSeries != null)
        {
            foreach (var s in allSeries)
            {
                int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(s.ID, metadataService) : s.ID;
                validFolderIds.Add(folderId.ToString());
            }
        }

        foreach (var root in allRoots)
        {
            ct.ThrowIfCancellationRequested();
            string vfsRoot = Path.Combine(root, rootName);
            if (!Directory.Exists(vfsRoot))
                continue;

            foreach (var seriesFolder in Directory.GetDirectories(vfsRoot))
            {
                ct.ThrowIfCancellationRequested();
                string folderName = Path.GetFileName(seriesFolder);

                // Ignore special files/folders like .ignore
                if (folderName.StartsWith('.'))
                    continue;

                if (!validFolderIds.Contains(folderName))
                {
                    try
                    {
                        Directory.Delete(seriesFolder, true);
                        orphanedFolders++;
                        removed.Add($"[Orphaned Series] {seriesFolder}");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to delete orphaned folder {seriesFolder}: {ex.Message}");
                    }
                    continue;
                }

                seriesChecked++;

                foreach (var file in Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LinkTarget != null && !info.Exists)
                        {
                            File.Delete(file);
                            brokenLinks++;
                            removed.Add($"[Broken Link] {file}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to process file {file}: {ex.Message}");
                    }
                }

                foreach (var subDir in Directory.GetDirectories(seriesFolder))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                        {
                            Directory.Delete(subDir, false);
                            orphanedFolders++;
                            removed.Add($"[Empty Folder] {subDir}");
                        }
                    }
                    catch { }
                }
            }
        }

        return new VfsAuditResult(seriesChecked, brokenLinks, orphanedFolders, removed, errors);
    }

    #endregion

    #region Core Build Logic

    /// <summary>Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot)
    {
        // Generates an orderable pairwise key where non-seasons are sorted alphabetically and standard seasons numerically.
        static (bool IsSeason, int SeasonNumber, string Name) GetSeasonSortKey(string name) =>
            (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int num)) ? (true, num, string.Empty) : (false, 0, name);

        // Resolves the Plex-compatible season number for any given VFS folder name.
        static int? GetSeasonId(string name)
        {
            if (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int num))
                return num;
            if (string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase))
                return PlexConstants.SeasonSpecials;

            var match = PlexConstants.ExtraSeasons.FirstOrDefault(kvp => string.Equals(kvp.Value.Folder, name, StringComparison.OrdinalIgnoreCase));
            return match.Value.Folder != null ? match.Key : null;
        }

        var rootName = VfsShared.ResolveRootFolderName();
        var overlapping =
            videoService
                .GetAllManagedFolders()
                ?.Where(f => !string.IsNullOrEmpty(f.Path) && f.Path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Contains(rootName))
                .Select(f => f.Path)
                .ToList()
            ?? [];

        if (overlapping.Count > 0)
            throw new InvalidOperationException(
                $"VFS generation blocked: Shoko import folder '{overlapping[0]}' resides inside or matches the VFS directory. Remove this import folder in Shoko's settings before generating."
            );

        // Refresh the override cache to catch any link changes made in Shoko since the last operation. This includes VFS Overrides and if MergeTmdbSeries is enabled auto merged TMDB series as well.
        OverrideHelper.Reload(metadataService);

        var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
        var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
        var cleanupDetails = new List<RootCleanupDetails>();
        var skippedDetailsBag = new ConcurrentBag<string>();
        var ignoredFolders = VfsShared.GetIgnoredFolderNames(Settings);
        var errorsBag = new ConcurrentBag<string>();
        var session = new VfsBuildSession();
        bool isFiltered = seriesIds?.Count > 0;

        // Blueprint Cache: Maps RootPath -> Dictionary<SeriesId, SeriesData>
        var blueprint = new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(VfsShared.PathComparer);
        string blueprintPath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsBlueprintCache);

        if (isFiltered && File.Exists(blueprintPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, VfsBlueprintSeries>>>(File.ReadAllText(blueprintPath));
                if (existing != null)
                    foreach (var rKvp in existing)
                    {
                        var rootDict = blueprint.GetOrAdd(rKvp.Key, _ => new());
                        foreach (var sKvp in rKvp.Value)
                            rootDict.TryAdd(sKvp.Key, sKvp.Value);
                    }
            }
            catch { }
        }

        // Global Pre-Cleanup - prevent parallel threads from waiting on a slow deletion while their stopwatches are running.
        if (cleanRoot && !isFiltered)
        {
            s_logger.Info("VFS: Performing global root cleanup...");
            List<string> allRoots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

            foreach (var root in allRoots)
            {
                string path = Path.Combine(root!, rootName);
                if (Directory.Exists(path))
                {
                    if (!VfsShared.IsSafeToDelete(path))
                    {
                        s_logger.Warn("VFS: Refusing to delete unsafe path -> {0}", path);
                        continue;
                    }
                    try
                    {
                        var cleanSw = Stopwatch.StartNew();
                        Directory.Delete(path, true);
                        cleanupDetails.Add(new RootCleanupDetails(path, cleanSw.ElapsedMilliseconds));
                        s_logger.Info("VFS: Cleaned root folder -> '{0}' in {1}ms", path, cleanSw.ElapsedMilliseconds);

                        Directory.CreateDirectory(path);
                        File.WriteAllText(Path.Combine(path, ".ignore"), ""); // Re-create the folder and add an .ignore file immediately after cleanup for Emby / Jellyfin users
                    }
                    catch (Exception ex)
                    {
                        s_logger.Warn(ex, "VFS: Failed to clean root -> {0}", path);
                    }
                }
            }
            cleanRoot = false;
        }

        // Resolve series list to process
        IEnumerable<IShokoSeries> seriesList;
        if (isFiltered)
        {
            var resolved = seriesIds!.Distinct().Select(id => new { Id = id, Series = metadataService.GetShokoSeriesByID(id) }).ToList();

            // Prune series that were completely deleted from Shoko's database
            foreach (var item in resolved.Where(x => x.Series == null))
                PruneSeries(rootName, item.Id, errorsBag.Add);

            seriesList = [.. resolved.Where(x => x.Series != null).Select(x => x.Series!).OfType<IShokoSeries>()];
        }
        else
            seriesList = metadataService.GetAllShokoSeries() ?? [];

        int totalInScope = seriesList.Count();
        int consolidatedCount = 0;

        if (EnforceTmdbNumbering)
        {
            // Group by primary ID and count how many secondary series are being merged
            var grouped = seriesList.GroupBy(s => OverrideHelper.GetPrimary(s.ID, metadataService)).ToList();
            seriesList = [.. grouped.Select(g => g.FirstOrDefault(s => s.ID == g.Key) ?? g.First())];
            consolidatedCount = totalInScope - seriesList.Count();
        }

        // Process series in parallel
        Parallel.ForEach(
            seriesList,
            DefaultParallelOptions(),
            series =>
            {
                try
                {
                    var seriesSw = Stopwatch.StartNew();

                    // SeriesNode structure: { id, anidbId, title, seasons: { "Season 1": [ { name, source } ] }, rootFiles: [ { name, source } ] }
                    var seasons = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    var rootFiles = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;

                    // Ensure stale entries for this series are removed across all roots before rebuilding its node.
                    foreach (var rootDict in blueprint.Values)
                        rootDict.TryRemove(folderId, out _);

                    var (sCreated, sSkipped, sSkippedList, sErrors, sPlanned) = BuildSeries(
                        series,
                        rootName,
                        cleanRoot,
                        isFiltered,
                        ignoredFolders,
                        session,
                        (vfsRoot, season, fileName, source) =>
                        {
                            if (string.IsNullOrEmpty(season))
                                rootFiles.TryAdd(fileName, source ?? "Local Metadata");
                            else
                                seasons.GetOrAdd(season, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(fileName, source ?? "Local Metadata");
                        }
                    );

                    if (sCreated > 0 || seasons.Count > 0 || rootFiles.Count > 0)
                    {
                        // Group files by root physically used during build to support multi-root series (tabs).
                        var rootsInvolved = metadataService.GetShokoSeriesByID(folderId) is { } fs
                            ? VfsShared.ResolveSeriesVfsPaths(fs, metadataService).Select(Path.GetDirectoryName).OfType<string>().Distinct(VfsShared.PathComparer)
                            : [];
                        foreach (var root in rootsInvolved)
                            blueprint
                                .GetOrAdd(root, _ => new())
                                .TryAdd(
                                    folderId,
                                    new VfsBlueprintSeries(
                                        folderId,
                                        series.AnidbAnimeID,
                                        TextHelper.ResolveFullSeriesTitles(series).DisplayTitle,
                                        rootFiles.OrderBy(f => f.Key).Select(f => new VfsBlueprintFile(f.Key, f.Value)),
                                        seasons
                                            .OrderBy(kvp => GetSeasonSortKey(kvp.Key))
                                            .Select(kvp => new VfsBlueprintSeason(kvp.Key, GetSeasonId(kvp.Key), kvp.Value.OrderBy(f => f.Key).Select(f => new VfsBlueprintFile(f.Key, f.Value))))
                                    )
                                );
                    }

                    // Capture individual series details for the log report
                    seriesDetailsBag.Add(new SeriesProcessDetails(series.PreferredTitle?.Value ?? series.ID.ToString(), seriesSw.ElapsedMilliseconds, sCreated));

                    if (sCreated > 0 || sErrors.Count > 0)
                        s_logger.Info("VFS: Processed series -> '{0}' ({1} links created) in {2}ms", series.PreferredTitle?.Value ?? series.ID.ToString(), sCreated, seriesSw.ElapsedMilliseconds);

                    Interlocked.Add(ref created, sCreated);
                    Interlocked.Add(ref skipped, sSkipped);
                    Interlocked.Add(ref planned, sPlanned);
                    foreach (var s in sSkippedList)
                        skippedDetailsBag.Add(s);
                    foreach (var err in sErrors)
                        errorsBag.Add(err);
                    Interlocked.Increment(ref seriesProcessed);
                }
                catch (Exception ex)
                {
                    errorsBag.Add($"Failed series {series.PreferredTitle?.Value}: {ex.Message}");
                    s_logger.Error(ex, "VFS: Build failed for series {SeriesId}", series.ID);
                }
            }
        );

        sw.Stop(); // Capture total elapsed time here
        var errors = errorsBag.ToList();

        s_logger.Info(
            "VFS: Build completed in {Elapsed}ms -> processed={Processed}, consolidated={Consolidated}, created={Created}, skipped={Skipped}, errors={Errors}",
            sw.ElapsedMilliseconds,
            seriesProcessed,
            consolidatedCount,
            created,
            skipped,
            errors.Count
        );

        // Atomically save the Blueprint to disk for the VFS Browser.
        try
        {
            string tmpPath = blueprintPath + ".tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, blueprint);
            File.Move(tmpPath, blueprintPath, overwrite: true);
        }
        catch { }
        return new VfsBuildResult(
            rootName,
            seriesProcessed,
            consolidatedCount,
            created,
            skipped,
            [.. skippedDetailsBag],
            errors,
            planned,
            [.. seriesDetailsBag.OrderBy(x => x.Name)],
            cleanupDetails,
            sw.Elapsed
        );
    }

    /// <summary>Builds the VFS structure for a specific series, synchronizing files with the expected layout.</summary>
    /// <param name="series">Shoko series metadata.</param>
    /// <param name="rootFolderName">Name of the VFS root folder.</param>
    /// <param name="cleanRoot">Whether to skip existence checks because the root was wiped.</param>
    /// <param name="isFiltered">Whether this is a filtered incremental build.</param>
    /// <param name="ignoredFolders">List of folders in Shoko destinations to exclude from processing.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <param name="onLink">Callback to invoke when a link is confirmed as expected.</param>
    /// <returns>A tuple of counts: Created, Skipped, SkippedDetails, Errors, Planned.</returns>
    private (int Created, int Skipped, List<string> SkippedDetails, List<string> Errors, int Planned) BuildSeries(
        IShokoSeries series,
        string rootFolderName,
        bool cleanRoot,
        bool isFiltered,
        HashSet<string> ignoredFolders,
        VfsBuildSession session,
        Action<string, string, string, string?>? onLink = null
    )
    {
        var (created, skipped, planned, errors, skippedDetails) = (0, 0, 0, new List<string>(), new List<string>());
        var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
        int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;

        var fileData = GetSeriesFileDataCached(series, session);
        if (!fileData.Mappings.Any())
        {
            PruneSeries(rootFolderName, folderId, errors.Add);
            return (0, 0, skippedDetails, errors, 0);
        }

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(m => Path.GetFileNameWithoutExtension(m.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).ToDictionary(g => g.Key, g => g.Count());
        var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

        var resolvedVfsSeriesPaths = new HashSet<string>(VfsShared.PathComparer);
        var expectedFiles = new HashSet<string>(VfsShared.PathComparer);
        bool skipCheck = cleanRoot && !isFiltered;

        // Callback wrapper to automatically track the expected physical destination of any correctly generated link to protect it from the cleanup phase
        void LocalOnLink(string importRoot, string season, string fileName, string? source)
        {
            string path = string.IsNullOrEmpty(season)
                ? Path.Combine(importRoot, rootFolderName, folderId.ToString(), fileName)
                : Path.Combine(importRoot, rootFolderName, folderId.ToString(), season, fileName);
            expectedFiles.Add(path);
            onLink?.Invoke(importRoot, season, fileName, source);
        }

        foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
        {
            var loc = mapping.Video?.Files?.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video?.Files?.FirstOrDefault();

            // Logical Skip: File is in a strictly protected Source folder (without Destination)
            if (loc == null || VfsShared.IsSourceOnly(loc.ManagedFolder))
            {
                skipped++;
                skippedDetails.Add($"[Source Folder] {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
                continue;
            }

            string? importRoot = VfsShared.ResolveImportRootPath(loc);

            // Error: Metadata exists but the Import Root cannot be resolved
            if (importRoot == null)
            {
                skipped++;
                errors.Add($"No import root for {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                continue;
            }

            string rootPath = Path.Combine(importRoot, rootFolderName),
                seriesPath = Path.Combine(rootPath, folderId.ToString());
            resolvedVfsSeriesPaths.Add(seriesPath);

            if (session.CreatedDirs.TryAdd(rootPath, 0))
            {
                Directory.CreateDirectory(rootPath);
                try
                {
                    File.WriteAllText(Path.Combine(rootPath, ".ignore"), "");
                }
                catch { }
            }
            if (session.CreatedDirs.TryAdd(seriesPath, 0))
                Directory.CreateDirectory(seriesPath);

            string? src = VfsShared.ResolveSourcePath(loc, importRoot);
            if (src == null)
            {
                skipped++;
                errors.Add($"No accessible file for {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                continue;
            }

            // Check if the source path is ignored under exclusion or extra rules
            if (VfsShared.IsPathIgnored(src, ignoredFolders))
            {
                skipped++;
                skippedDetails.Add($"[Excluded Path] {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
                continue;
            }

            string seasonName = PlexMapping.GetSeasonFolder(mapping.Coords.Season);
            string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(seasonName));
            if (session.CreatedDirs.TryAdd(seasonPath, 0))
                Directory.CreateDirectory(seasonPath);

            var key = (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation);
            bool hasPeer = coordCounts.TryGetValue(key, out var count) && count > 1;
            int? vIdx = (hasPeer && !mapping.PartIndex.HasValue && versionCounters.TryGetValue(key, out var v)) ? (versionCounters[key] = v + 1) - 1 : null;
            string fileName = VfsHelper.SanitizeName(
                PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var ex)
                    ? VfsHelper.BuildExtrasFileName(
                        mapping,
                        ex,
                        extraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                        Path.GetExtension(src),
                        displayTitle,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx,
                        mapping.IsVariation
                    )
                    : VfsHelper.BuildStandardFileName(
                        mapping,
                        epPad,
                        Path.GetExtension(src),
                        mapping.Video!.ID,
                        mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx,
                        mapping.IsVariation
                    )
            );

            var destFilePath = Path.Combine(seasonPath, fileName);

            // Fast inline check for broken symlinks to prevent TryCreateLink from failing on the overwrite
            var fi = new FileInfo(destFilePath);
            if (fi.LinkTarget != null && !fi.Exists)
            {
                try
                {
                    File.Delete(destFilePath);
                }
                catch { }
            }

            if (VfsShared.TryCreateLink(src, destFilePath, s_logger, skipExistenceCheck: skipCheck))
            {
                created++;
                planned++;

                // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                LocalOnLink(importRoot, seasonName, fileName, src);
                var distinctPrimarySeriesCount =
                    mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => OverrideHelper.GetPrimary(cr.ShokoEpisode!.SeriesID, metadataService)).Distinct().Count() ?? 0;
                if (distinctPrimarySeriesCount <= 1)
                {
                    assetLinker.LinkSeriesMetadata(Path.GetDirectoryName(src)!, seriesPath, videoBaseNames, session.MetadataFileCache, (name, s) => LocalOnLink(importRoot, "", name, s), skipCheck);
                    assetLinker.LinkEpisodeMetadata(
                        src,
                        Path.GetDirectoryName(src)!,
                        Path.GetFileNameWithoutExtension(destFilePath),
                        seasonPath,
                        session.SubtitleFileCache,
                        ref planned,
                        ref skipped,
                        errors,
                        ref created,
                        (name, s) => LocalOnLink(importRoot, seasonName, name, s),
                        skipCheck
                    );
                }
            }
            else
            {
                skipped++;
                errors.Add($"Link failed: {src} -> {destFilePath}");
            }
        }

        // Plex Local Extras: Run for every unique VFS series folder created across different roots for this series.
        if (Settings.Advanced.PlexLocalExtras)
            foreach (var seriesPath in resolvedVfsSeriesPaths)
                assetLinker.LinkLocalExtras(fileData, seriesPath, videoBaseNames, epPad, (season, name, s) => LocalOnLink(Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!, season, name, s), skipCheck);

        // Perform diff-based cleanup of unexpected files and empty directories left behind by renames or moves
        string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
        foreach (var seriesPath in resolvedVfsSeriesPaths)
        {
            if (!Directory.Exists(seriesPath))
                continue;

            string importRoot = Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!;

            foreach (var file in Directory.EnumerateFiles(seriesPath, "*", SearchOption.AllDirectories))
            {
                if (!expectedFiles.Contains(file))
                {
                    try
                    {
                        var fi = new FileInfo(file);

                        // Allow AnimeThemesMapping to manage its own files, but register them in the blueprint so they appear in the VFS browser
                        if (fi.LinkTarget != null && fi.LinkTarget.Contains(themeRootName, StringComparison.OrdinalIgnoreCase))
                        {
                            string seasonName = Path.GetFileName(Path.GetDirectoryName(file)!);
                            onLink?.Invoke(importRoot, seasonName, Path.GetFileName(file), fi.LinkTarget);
                            continue;
                        }

                        File.Delete(file);
                    }
                    catch { }
                }
            }

            var dirs = Directory.EnumerateDirectories(seriesPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
                }
                catch { }
            }
        }

        return (created, skipped, skippedDetails, errors, planned);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Retrieves the files and episode mappings for a series, leveraging the build-session cache.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <returns>A data container holding files and coordinates for the series.</returns>
    private MapHelper.SeriesFileData GetSeriesFileDataCached(IShokoSeries series, VfsBuildSession session)
    {
        if (!EnforceTmdbNumbering)
            return session.SeriesFileDataCache.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series, metadataService));
        int pId = OverrideHelper.GetPrimary(series.ID, metadataService);
        return session.SeriesFileDataCache.GetOrAdd(
            pId,
            _ =>
            {
                var group = OverrideHelper.GetGroup(pId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                return group.Count <= 1
                    ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series, metadataService)
                    : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<ISeries>(), metadataService);
            }
        );
    }

    /// <summary>Recursively deletes the virtual directory for a series across all active VFS roots.</summary>
    /// <param name="rootFolderName">The name of the VFS root folder.</param>
    /// <param name="folderId">The folder ID representation of the series to prune.</param>
    /// <param name="onError">Callback to invoke if an exception occurs during pruning.</param>
    private void PruneSeries(string rootFolderName, int folderId, Action<string> onError)
    {
        List<string> roots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

        foreach (var root in roots)
        {
            string path = Path.Combine(root, rootFolderName, folderId.ToString());
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    s_logger.Info("VFS: Pruned empty/orphaned series folder -> '{0}'", path);
                }
            }
            catch (Exception ex)
            {
                onError($"Prune failed {path}: {ex.Message}");
                s_logger.Warn(ex, "VFS: Failed to prune series path {Path}", path);
            }
        }
    }

    #endregion
}
