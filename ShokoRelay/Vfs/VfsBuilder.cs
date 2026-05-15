using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video.Enums;
using ShokoRelay.Plex;

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

#endregion

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
public class VfsBuilder
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    internal static readonly Lock GlobalBuildLock = new();
    private readonly IMetadataService _metadataService;
    private readonly string _pluginDataPath;

    private ConcurrentDictionary<int, MapHelper.SeriesFileData>? _seriesFileDataCacheForBuild;
    private ConcurrentDictionary<string, Lazy<string[]>>? _subtitleFileCacheForBuild;
    private ConcurrentDictionary<string, Lazy<string[]>>? _metadataFileCacheForBuild;
    private ConcurrentBag<string>? _warningsForBuild;
    private ConcurrentDictionary<string, byte>? _createdDirsForBuild;

    // <summary>Used for show/season level assets (Art, Theme Songs, NFO)</summary>
    private static readonly HashSet<string> s_seriesMetadataExtensions = [.. PlexConstants.LocalMediaAssets.Artwork, .. PlexConstants.LocalMediaAssets.SeriesMetadata];

    // <summary>Used for episode level sidecars (Subs, NFO, and episode-specific Art)</summary>
    private static readonly HashSet<string> s_episodeMetadataExtensions = [.. PlexConstants.LocalMediaAssets.Artwork, .. PlexConstants.LocalMediaAssets.EpisodeMetadata];

    /// <summary>Initializes a VfsBuilder.</summary>
    public VfsBuilder(IMetadataService metadataService, ConfigProvider configProvider)
    {
        _metadataService = metadataService;
        _pluginDataPath = configProvider.PluginDirectory;
        try
        {
            Directory.CreateDirectory(_pluginDataPath);
        }
        catch { }
    }

    #endregion

    #region Public Interface

    /// <summary>Build or clean VFS for a single series ID.</summary>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot, pruneSeries, false);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesIds, cleanRoot, pruneSeries, false);

    /// <summary>Clean VFS for a series without building.</summary>
    public VfsBuildResult Clean(int? seriesId = null) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, true, false, true);

    /// <summary>Clean VFS for multiple series without building.</summary>
    public VfsBuildResult Clean(IReadOnlyCollection<int> seriesIds) => BuildInternal(seriesIds, true, false, true);

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
        // Prevent multiple concurrent build/clean operations
        lock (GlobalBuildLock)
        {
            var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
            var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
            var cleanupDetails = new List<RootCleanupDetails>();
            var skippedDetailsBag = new ConcurrentBag<string>();
            var ignoredFolders = VfsShared.GetIgnoredFolderNames(Settings);

            // Initialize build-session caches
            (_seriesFileDataCacheForBuild, _subtitleFileCacheForBuild, _metadataFileCacheForBuild, _warningsForBuild, _createdDirsForBuild) = (
                new(),
                new(VfsShared.PathComparer),
                new(VfsShared.PathComparer),
                [],
                new(VfsShared.PathComparer)
            );

            string rootName = VfsShared.ResolveRootFolderName();
            var (cleanedRoots, cleanedSeries, rootTasks, seriesTasks) = (
                new ConcurrentDictionary<string, byte>(VfsShared.PathComparer),
                new ConcurrentDictionary<string, byte>(VfsShared.PathComparer),
                new ConcurrentDictionary<string, Task>(VfsShared.PathComparer),
                new ConcurrentDictionary<string, Task>(VfsShared.PathComparer)
            );
            var errorsBag = new ConcurrentBag<string>();

            bool isFiltered = seriesIds?.Count > 0;

            // Global Pre-Cleanup - prevent parallel threads from waiting on a slow deletion while their stopwatches are running.
            if (cleanRoot && !isFiltered)
            {
                s_logger.Info("VFS: Performing global root cleanup...");
                var allRoots = _metadataService
                    .GetAllShokoSeries()
                    .SelectMany(s => s.Episodes.SelectMany(ep => ep.VideoList).SelectMany(v => v.Files))
                    .Select(VfsShared.ResolveImportRootPath)
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Distinct()
                    .ToList();

                foreach (var root in allRoots)
                {
                    string path = Path.Combine(root!, rootName);
                    if (Directory.Exists(path))
                    {
                        if (VfsShared.IsSafeToDelete(path))
                        {
                            try
                            {
                                var cleanSw = Stopwatch.StartNew();
                                Directory.Delete(path, true);
                                cleanSw.Stop();
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
                        else
                        {
                            s_logger.Warn("VFS: Refusing to delete unsafe path -> {0}", path);
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
                        .Select(_metadataService.GetShokoSeriesByID)
                        .OfType<IShokoSeries>()
                        .Select(s =>
                        {
                            if (pruneSeries)
                                PruneSeries(rootName, s);
                            return s;
                        }),
                ]
                : (_metadataService.GetAllShokoSeries() ?? []);

            int totalInScope = seriesList.Count();
            int consolidatedCount = 0;

            if (EnforceTmdbNumbering)
            {
                // Group by primary ID and count how many secondary series are being merged
                var grouped = seriesList.GroupBy(s => OverrideHelper.GetPrimary(s.ID, _metadataService)).ToList();
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
                        var (created, skipped, skippedList, errors, planned) = BuildSeries(
                            series,
                            rootName,
                            cleanRoot,
                            cleanedRoots,
                            cleanedSeries,
                            isFiltered,
                            cleanOnly,
                            rootTasks,
                            seriesTasks,
                            ignoredFolders
                        );

                        // Capture individual series details for the log report
                        seriesDetailsBag.Add(new SeriesProcessDetails(series.PreferredTitle?.Value ?? series.ID.ToString(), seriesSw.ElapsedMilliseconds, created));

                        if (created > 0 || errors.Count > 0)
                        {
                            s_logger.Info("VFS: Processed series -> '{0}' ({1} links created) in {2}ms", series.PreferredTitle?.Value ?? series.ID.ToString(), created, seriesSw.ElapsedMilliseconds);
                        }

                        Interlocked.Add(ref created, created);
                        Interlocked.Add(ref skipped, skipped);
                        Interlocked.Add(ref planned, planned);
                        foreach (var s in skippedList)
                            skippedDetailsBag.Add(s);
                        foreach (var err in errors)
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

            // Cleanup build-session objects
            (_seriesFileDataCacheForBuild, _subtitleFileCacheForBuild, _metadataFileCacheForBuild, _warningsForBuild, _createdDirsForBuild) = (null, null, null, null, null);

            s_logger.Info(
                "VFS: Build completed in {Elapsed}ms -> processed={Processed}, consolidated={Consolidated}, created={Created}, skipped={Skipped}, errors={Errors}",
                sw.ElapsedMilliseconds,
                seriesProcessed,
                consolidatedCount,
                created,
                skipped,
                errors.Count
            );

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
    }

    /// <summary>Builds the VFS structure for a specific series, handling naming and de-duplication.</summary>
    /// <param name="series">Shoko series metadata.</param>
    /// <param name="rootFolderName">Name of the VFS root folder.</param>
    /// <param name="cleanRoot">Whether to perform cleanup.</param>
    /// <param name="cleanedRoots">Thread-safe tracker for cleaned roots.</param>
    /// <param name="cleanedSeries">Thread-safe tracker for cleaned series folders.</param>
    /// <param name="filtered">Whether this is a filtered build.</param>
    /// <param name="cleanOnly">If true, skips link creation.</param>
    /// <param name="rootTasks">Task tracker for root operations.</param>
    /// <param name="seriesTasks">Task tracker for series operations.</param>
    /// <param name="ignoredFolders">List of folders in Shoko destinations to exclude from processing.</param>
    /// <returns>A tuple of counts: Created, Skipped, SkippedDetails, Errors, Planned.</returns>
    private (int Created, int Skipped, List<string> SkippedDetails, List<string> Errors, int Planned) BuildSeries(
        IShokoSeries series,
        string rootFolderName,
        bool cleanRoot,
        ConcurrentDictionary<string, byte> cleanedRoots,
        ConcurrentDictionary<string, byte> cleanedSeries,
        bool filtered,
        bool cleanOnly,
        ConcurrentDictionary<string, Task> rootTasks,
        ConcurrentDictionary<string, Task> seriesTasks,
        HashSet<string> ignoredFolders
    )
    {
        var (created, skipped, planned, errors, skippedDetails, sSw) = (0, 0, 0, new List<string>(), new List<string>(), Stopwatch.StartNew());
        var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
        int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, _metadataService) : series.ID;
        var fileData = GetSeriesFileDataCached(series);
        if (!fileData.Mappings.Any())
            return (0, 0, skippedDetails, errors, 0);

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(m => Path.GetFileNameWithoutExtension(m.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).ToDictionary(g => g.Key, g => g.Count());
        var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

        foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
        {
            var loc = mapping.Video?.Files?.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video?.Files?.FirstOrDefault();

            // Logical Skip: File is in a protected Source folder
            if (loc?.ManagedFolder == null || loc.ManagedFolder.DropFolderType.HasFlag(DropFolderType.Source))
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
            if (cleanRoot)
            {
                if (filtered)
                    HandleCleanup(seriesPath, cleanedSeries, seriesTasks, errors);
                else
                    HandleCleanup(rootPath, cleanedRoots, rootTasks, errors);
            }

            if (cleanOnly)
                continue;
            if (_createdDirsForBuild?.TryAdd(rootPath, 0) == true)
            {
                Directory.CreateDirectory(rootPath);
                try
                {
                    File.WriteAllText(Path.Combine(rootPath, ".ignore"), ""); // Add an .ignore file immediately after the build starts for Emby / Jellyfin users
                }
                catch { }
            }
            if (_createdDirsForBuild?.TryAdd(seriesPath, 0) == true)
                Directory.CreateDirectory(seriesPath);

            string? src = VfsShared.ResolveSourcePath(loc, importRoot);

            // Error: The file record exists in Shoko but is missing from the physical disk
            if (src == null)
            {
                skipped++;
                errors.Add($"No accessible file for {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                continue;
            }

            // Check if any segment of the source path is in the ignored folders set
            var pathSegments = src.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Any(ignoredFolders.Contains))
            {
                skipped++;
                skippedDetails.Add($"[Excluded Folder] {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
                continue;
            }

            string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(mapping.Coords.Season)));
            if (_createdDirsForBuild?.TryAdd(seasonPath, 0) == true)
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
            string destPath = Path.Combine(seasonPath, fileName);
            if (VfsShared.TryCreateLink(src, destPath, s_logger))
            {
                created++;
                planned++;

                // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                var distinctPrimarySeriesCount =
                    mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => OverrideHelper.GetPrimary(cr.ShokoEpisode!.SeriesID, _metadataService)).Distinct().Count() ?? 0;
                if (distinctPrimarySeriesCount <= 1)
                {
                    LinkSeriesMetadata(Path.GetDirectoryName(src)!, seriesPath, videoBaseNames);
                    LinkEpisodeMetadata(src, Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(destPath), seasonPath, ref planned, ref skipped, errors, ref created);
                }
                else
                    s_logger.Debug("VFS: Skipping local assets for crossover video -> {0} (series {1})", src, series.ID);
            }
            else
            {
                // Error: OS failed to create the symlink
                skipped++;
                errors.Add($"Link failed: {src} -> {destPath}");
            }
        }
        return (created, skipped, skippedDetails, errors, planned);
    }

    private void HandleCleanup(string path, ConcurrentDictionary<string, byte> cleaned, ConcurrentDictionary<string, Task> tasks, List<string> errors)
    {
        if (!cleaned.TryAdd(path, 0))
        {
            if (tasks.TryGetValue(path, out var t))
                t.Wait();
            return;
        }
        var tcs = new TaskCompletionSource();
        tasks[path] = tcs.Task;
        if (Directory.Exists(path))
        {
            if (!VfsShared.IsSafeToDelete(path))
            {
                errors.Add($"Refusing to clean: {path}");
                s_logger.Warn("VFS: Unsafe cleanup path blocked -> {Path}", path);
            }
            else
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                }
                catch (IOException ex)
                {
                    errors.Add($"Clean failed: Folder in use or locked by another process {path}");
                    s_logger.Warn(ex, "VFS: Cleanup blocked due to in use path -> {Path}", path);
                }
                catch (Exception ex)
                {
                    errors.Add($"Clean failed {path}: {ex.Message}");
                    s_logger.Error(ex, "VFS: Clean failed for path -> {Path}", path);
                }
        }
        tcs.SetResult();
    }

    #endregion

    #region Asset Linking

    /// <summary>Links show-level metadata into the series VFS directory, excluding files that are identified as episode-level sidecars.</summary>
    /// <param name="sourceDir">The physical directory containing the assets.</param>
    /// <param name="destDir">The target VFS series directory.</param>
    /// <param name="videoBaseNames">A set of base names for video files to exclude from series-level linking.</param>
    private void LinkSeriesMetadata(string sourceDir, string destDir, HashSet<string> videoBaseNames)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        var lazyLoader = _metadataFileCacheForBuild?.GetOrAdd(
            sourceDir,
            dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(f => s_seriesMetadataExtensions.Contains(Path.GetExtension(f)))])
        );
        var candidates = lazyLoader?.Value ?? [];
        foreach (var file in candidates)
        {
            string name = Path.GetFileName(file);
            string baseName = Path.GetFileNameWithoutExtension(name);

            // If the file name (without extension) matches a video file base name it is an episode sidecar and should be excluded from show-level linking.
            if (videoBaseNames.Contains(baseName))
                continue;
            string destName = string.Equals(baseName, "Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name; // Allow "Specials" custom naming
            VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), s_logger);
        }
    }

    /// <summary>Links and renames episode-level metadata into the season VFS directory.</summary>
    /// <param name="sourceFile">Path to the original video file used for base name matching.</param>
    /// <param name="sourceDir">The physical directory containing the sidecars.</param>
    /// <param name="destBase">The new base filename in the VFS.</param>
    /// <param name="destDir">The target VFS season directory.</param>
    /// <param name="planned">Reference to the planned links counter.</param>
    /// <param name="skipped">Reference to the skipped links counter.</param>
    /// <param name="errors">List of encountered error messages.</param>
    /// <param name="created">Reference to the successful links created counter.</param>
    private void LinkEpisodeMetadata(string sourceFile, string sourceDir, string destBase, string destDir, ref int planned, ref int skipped, List<string> errors, ref int created)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var lazyLoader = _subtitleFileCacheForBuild?.GetOrAdd(
            sourceDir,
            dir => new Lazy<string[]>(() => [.. Directory.GetFiles(sourceDir).Where(f => s_episodeMetadataExtensions.Contains(Path.GetExtension(f)))])
        );
        var candidates = lazyLoader?.Value ?? [];
        foreach (var sub in candidates)
        {
            string name = Path.GetFileName(sub);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase))
                continue;
            if (VfsShared.TryCreateLink(sub, Path.Combine(destDir, destBase + name[originalBase.Length..]), s_logger))
            {
                planned++;
                created++;
            }
            else
            {
                skipped++;
                errors.Add($"Metadata sidecar link failed: {sub}");
            }
        }
    }

    #endregion

    #region Internal Helpers

    private void PruneSeries(string rootFolderName, IShokoSeries series)
    {
        var paths = new HashSet<string>(VfsShared.PathComparer);
        foreach (var l in GetSeriesFileDataCached(series).Mappings.SelectMany(m => m.Video.Files))
        {
            string? root = VfsShared.ResolveImportRootPath(l);
            if (string.IsNullOrEmpty(root))
                continue;

            string path = Path.Combine(root, rootFolderName, series.ID.ToString());
            if (paths.Add(path))
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                }
                catch (Exception ex)
                {
                    _warningsForBuild?.Add($"Prune failed {path}: {ex.Message}");
                    s_logger.Warn(ex, "VFS: Failed to prune series path {Path}", path);
                }
            }
        }
    }

    private MapHelper.SeriesFileData GetSeriesFileDataCached(IShokoSeries series)
    {
        if (!EnforceTmdbNumbering)
            return _seriesFileDataCacheForBuild?.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series, _metadataService)) ?? MapHelper.GetSeriesFileData(series, _metadataService);

        int pId = OverrideHelper.GetPrimary(series.ID, _metadataService);
        return _seriesFileDataCacheForBuild?.GetOrAdd(
                pId,
                _ =>
                {
                    var group = OverrideHelper.GetGroup(pId, _metadataService).Select(_metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                    return group.Count <= 1
                        ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series, _metadataService)
                        : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<Shoko.Abstractions.Metadata.ISeries>(), _metadataService);
                }
            )
            ?? MapHelper.GetSeriesFileData(series, _metadataService);
    }

    #endregion
}
