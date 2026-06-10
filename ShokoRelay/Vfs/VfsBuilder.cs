using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;

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
        public Dictionary<int, List<ThemeMapItem>> ThemeMappings { get; } = AnimeThemesMapping.LoadThemeMappings(ConfigDirectory);
        public ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> PhysicalThemeCaches { get; } = new(VfsShared.PathComparer);
    }

    #endregion

    #region Public Interface

    /// <summary>Build or clean VFS for a single series ID.</summary>
    /// <param name="seriesId">Optional single series ID to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="pruneSeries">Whether to remove per-series folders specifically.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot, pruneSeries, false);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    /// <param name="seriesIds">Collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="pruneSeries">Whether to remove per-series folders specifically.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesIds, cleanRoot, pruneSeries, false);

    #endregion

    #region Core Build Logic

    /// <summary>Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="pruneSeries">Whether to remove per-series folders specifically.</param>
    /// <param name="cleanOnly">If true, performs cleanup without creating new links.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, bool pruneSeries, bool cleanOnly)
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

        // Initialize build-session context
        var session = new VfsBuildSession();
        var cleanedSeries = new ConcurrentDictionary<string, byte>(VfsShared.PathComparer);
        var errorsBag = new ConcurrentBag<string>();

        // Blueprint Cache: Maps RootPath -> Dictionary<SeriesId, SeriesData>
        var blueprint = new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(VfsShared.PathComparer);
        string blueprintPath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsBlueprintCache);
        bool isFiltered = seriesIds?.Count > 0;

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
        IEnumerable<IShokoSeries> seriesList = isFiltered
            ?
            [
                .. seriesIds!
                    .Distinct()
                    .Select(metadataService.GetShokoSeriesByID)
                    .OfType<IShokoSeries>()
                    .Select(s =>
                    {
                        if (pruneSeries)
                            PruneSeries(rootName, s, errorsBag);
                        return s;
                    }),
            ]
            : (metadataService.GetAllShokoSeries() ?? []);

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
                        cleanedSeries,
                        isFiltered,
                        cleanOnly,
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

                    if (!cleanOnly && (sCreated > 0 || seasons.Count > 0 || rootFiles.Count > 0))
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

    /// <summary>Builds the VFS structure for a specific series, handling naming and de-duplication.</summary>
    /// <param name="series">Shoko series metadata.</param>
    /// <param name="rootFolderName">Name of the VFS root folder.</param>
    /// <param name="cleanRoot">Whether to perform cleanup.</param>
    /// <param name="cleanedSeries">Thread-safe tracker for cleaned series folders.</param>
    /// <param name="filtered">Whether this is a filtered build.</param>
    /// <param name="cleanOnly">If true, skips link creation.</param>
    /// <param name="ignoredFolders">List of folders in Shoko destinations to exclude from processing.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <param name="onLink">Callback to invoke when a link is created.</param>
    /// <returns>A tuple of counts: Created, Skipped, SkippedDetails, Errors, Planned.</returns>
    private (int Created, int Skipped, List<string> SkippedDetails, List<string> Errors, int Planned) BuildSeries(
        IShokoSeries series,
        string rootFolderName,
        bool cleanRoot,
        ConcurrentDictionary<string, byte> cleanedSeries,
        bool filtered,
        bool cleanOnly,
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
            return (0, 0, skippedDetails, errors, 0);

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(m => Path.GetFileNameWithoutExtension(m.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).ToDictionary(g => g.Key, g => g.Count());
        var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

        // Track unique VFS series directories resolved for this build to perform Local Extra linking.
        var resolvedVfsSeriesPaths = new HashSet<string>(VfsShared.PathComparer);
        bool skipCheck = cleanRoot && !filtered;

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

            // Inline, thread-safe series directory cleanup
            if (cleanRoot && filtered && cleanedSeries.TryAdd(seriesPath, 0))
            {
                if (Directory.Exists(seriesPath))
                {
                    if (!VfsShared.IsSafeToDelete(seriesPath))
                    {
                        errors.Add($"Refusing to clean: {seriesPath}");
                        s_logger.Warn("VFS: Unsafe cleanup path blocked -> {Path}", seriesPath);
                    }
                    else
                    {
                        try
                        {
                            Directory.Delete(seriesPath, true);
                        }
                        catch (IOException ioEx)
                        {
                            errors.Add($"Clean failed: Folder in use or locked by another process {seriesPath}");
                            s_logger.Warn(ioEx, "VFS: Cleanup blocked due to in use path -> {Path}", seriesPath);
                        }
                        catch (Exception e)
                        {
                            errors.Add($"Clean failed {seriesPath}: {e.Message}");
                            s_logger.Error(e, "VFS: Clean failed for path -> {Path}", seriesPath);
                        }
                    }
                }
            }

            if (cleanOnly)
                continue;
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
            if (VfsShared.TryCreateLink(src, Path.Combine(seasonPath, fileName), s_logger, skipExistenceCheck: skipCheck))
            {
                created++;
                planned++;

                // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                onLink?.Invoke(importRoot, seasonName, fileName, src);
                var distinctPrimarySeriesCount =
                    mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => OverrideHelper.GetPrimary(cr.ShokoEpisode!.SeriesID, metadataService)).Distinct().Count() ?? 0;
                if (distinctPrimarySeriesCount <= 1)
                {
                    assetLinker.LinkSeriesMetadata(Path.GetDirectoryName(src)!, seriesPath, videoBaseNames, session.MetadataFileCache, (name, s) => onLink?.Invoke(importRoot, "", name, s), skipCheck);
                    assetLinker.LinkEpisodeMetadata(
                        src,
                        Path.GetDirectoryName(src)!,
                        Path.GetFileNameWithoutExtension(Path.Combine(seasonPath, fileName)),
                        seasonPath,
                        session.SubtitleFileCache,
                        ref planned,
                        ref skipped,
                        errors,
                        ref created,
                        (name, s) => onLink?.Invoke(importRoot, seasonName, name, s),
                        skipCheck
                    );
                }
            }
            else
            {
                skipped++;
                errors.Add($"Link failed: {src} -> {Path.Combine(seasonPath, fileName)}");
            }
        }

        // Plex Local Extras: Run for every unique VFS series folder created across different roots for this series.
        if (Settings.Advanced.PlexLocalExtras)
            foreach (var seriesPath in resolvedVfsSeriesPaths)
                assetLinker.LinkLocalExtras(
                    fileData,
                    seriesPath,
                    videoBaseNames,
                    epPad,
                    (season, name, s) => onLink?.Invoke(Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!, season, name, s),
                    skipCheck
                );

        // Dynamically register physically present AnimeThemes mapping files inside the blueprint Shorts directory
        foreach (var seriesPath in resolvedVfsSeriesPaths)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!;
            if (session.ThemeMappings.TryGetValue(series.AnidbAnimeID, out var themes))
            {
                string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
                foreach (var theme in themes)
                    if (GetThemeSourcePath(theme.RelativePath, root, themeRootName, session) is string srcPath)
                        onLink?.Invoke(root, "Shorts", theme.FinalName, srcPath);
            }
        }

        return (created, skipped, skippedDetails, errors, planned);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Checks if a mapped theme file physically exists within the import root, using an in-memory cache to prevent disk thrashing.</summary>
    /// <param name="relativePath">The relative path of the theme file inside the !AnimeThemes folder.</param>
    /// <param name="importRoot">The physical import root directory.</param>
    /// <param name="themeRootName">The name of the AnimeThemes directory.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <returns>The absolute path of the theme file if it exists; otherwise, null.</returns>
    private static string? GetThemeSourcePath(string relativePath, string importRoot, string themeRootName, VfsBuildSession session)
    {
        string themeRootPath = Path.Combine(importRoot, themeRootName);
        var cache = session
            .PhysicalThemeCaches.GetOrAdd(
                themeRootPath,
                root => new Lazy<Dictionary<string, string>>(() =>
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (Directory.Exists(root))
                    {
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(root, "*.webm", SearchOption.AllDirectories))
                                dict[Path.GetRelativePath(root, file).Replace('\\', '/').TrimStart('/')] = Path.GetFullPath(file);
                        }
                        catch { }
                    }
                    return dict;
                })
            )
            .Value;

        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        return cache.TryGetValue(normalizedRel, out string? absPath) ? absPath : null;
    }

    /// <summary>Retrieves the files and episode mappings for a series, leveraging the build-session cache.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <returns>A data container holding files and coordinates for the series.</returns>
    private MapHelper.SeriesFileData GetSeriesFileDataCached(IShokoSeries series, VfsBuildSession session)
    {
        if (!EnforceTmdbNumbering)
            return session.SeriesFileDataCache.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series, metadataService)) ?? MapHelper.GetSeriesFileData(series, metadataService);
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
            ) ?? MapHelper.GetSeriesFileData(series, metadataService);
    }

    /// <summary>Recursively deletes the virtual directory for a series across all active VFS roots.</summary>
    /// <param name="rootFolderName">The name of the VFS root folder.</param>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="errors">Collection of accumulated error messages.</param>
    private void PruneSeries(string rootFolderName, IShokoSeries series, ConcurrentBag<string> errors)
    {
        int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;
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
                errors.Add($"Prune failed {path}: {ex.Message}");
                s_logger.Warn(ex, "VFS: Failed to prune series path {Path}", path);
            }
        }
    }

    #endregion
}
