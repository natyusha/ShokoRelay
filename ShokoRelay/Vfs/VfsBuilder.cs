using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs;

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
/// <param name="CreatedLinks">Successful links created.</param>
/// <param name="Skipped">Skipped items count.</param>
/// <param name="Errors">Encountered errors.</param>
/// <param name="PlannedLinks">Target link count.</param>
/// <param name="SeriesDetails">List of detailed processing stats for each series.</param>
/// <param name="CleanupDetails">Details regarding root folder deletions.</param>
/// <param name="TotalElapsed">Total time taken for the entire operation.</param>
public record VfsBuildResult(
    string RootPath,
    int SeriesProcessed,
    int CreatedLinks,
    int Skipped,
    List<string> Errors,
    int PlannedLinks,
    List<SeriesProcessDetails> SeriesDetails,
    List<RootCleanupDetails> CleanupDetails,
    TimeSpan TotalElapsed
);

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
public class VfsBuilder
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMetadataService _metadataService;
    private readonly string _pluginDataPath;

    private ConcurrentDictionary<int, MapHelper.SeriesFileData>? _seriesFileDataCacheForBuild;
    private ConcurrentDictionary<string, Lazy<string[]>>? _subtitleFileCacheForBuild;
    private ConcurrentDictionary<string, Lazy<string[]>>? _metadataFileCacheForBuild;
    private ConcurrentBag<string>? _warningsForBuild;
    private ConcurrentDictionary<string, byte>? _createdDirsForBuild;

    private static readonly HashSet<string> MetadataExtensions = PlexConstants.LocalMediaAssets.Artwork.Union(PlexConstants.LocalMediaAssets.ThemeSongs).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> SubtitleExtensions = PlexConstants.LocalMediaAssets.Subtitles;

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
        OverrideHelper.EnsureLoaded();
    }

    /// <summary>Build or clean VFS for a single series ID.</summary>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot, pruneSeries, false);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesIds, cleanRoot, pruneSeries, false);

    /// <summary>Clean VFS for a series without building.</summary>
    public VfsBuildResult Clean(int? seriesId = null) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, true, false, true);

    /// <summary>Clean VFS for multiple series without building.</summary>
    public VfsBuildResult Clean(IReadOnlyCollection<int> seriesIds) => BuildInternal(seriesIds, true, false, true);

    /// <summary> Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="pruneSeries">Whether to remove per-series folders specifically.</param>
    /// <param name="cleanOnly">If true, performs cleanup without creating new links.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, bool pruneSeries, bool cleanOnly)
    {
        var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
        var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
        var cleanupDetails = new List<RootCleanupDetails>();

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
            Logger.Info("VFS Build: Performing global root cleanup...");
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
                            Logger.Info("VFS Build: Cleaned root folder '{0}' in {1}ms", path, cleanSw.ElapsedMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "VFS Build: Failed to clean root {0}", path);
                        }
                    }
                    else
                    {
                        Logger.Warn("VFS Build: Refusing to delete unsafe path: {0}", path);
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

        OverrideHelper.EnsureLoaded();
        if (ShokoRelay.Settings.TmdbEpNumbering)
            seriesList = [.. seriesList.GroupBy(s => OverrideHelper.GetPrimary(s.ID, _metadataService)).Select(g => g.FirstOrDefault(s => s.ID == g.Key) ?? g.First())];

        // Process series in parallel
        Parallel.ForEach(
            seriesList,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism) },
            series =>
            {
                try
                {
                    var seriesSw = Stopwatch.StartNew();
                    var (Created, Skipped, Errors, Planned) = BuildSeries(series, rootName, cleanRoot, cleanedRoots, cleanedSeries, isFiltered, cleanOnly, rootTasks, seriesTasks);
                    seriesSw.Stop();

                    // Capture individual series details for the log report
                    seriesDetailsBag.Add(new SeriesProcessDetails(series.PreferredTitle?.Value ?? series.ID.ToString(), seriesSw.ElapsedMilliseconds, Created));

                    if (Created > 0 || Errors.Count > 0)
                    {
                        Logger.Info("VFS: Processed series '{0}' ({1} links created) in {2}ms", series.PreferredTitle?.Value ?? series.ID.ToString(), Created, seriesSw.ElapsedMilliseconds);
                    }

                    Interlocked.Add(ref created, Created);
                    Interlocked.Add(ref skipped, Skipped);
                    Interlocked.Add(ref planned, Planned);
                    foreach (var err in Errors)
                        errorsBag.Add(err);
                    Interlocked.Increment(ref seriesProcessed);
                }
                catch (Exception ex)
                {
                    errorsBag.Add($"Failed series {series.PreferredTitle?.Value}: {ex.Message}");
                    Logger.Error(ex, "VFS build failed for series {SeriesId}", series.ID);
                }
            }
        );

        sw.Stop(); // Capture total elapsed time here

        var errors = errorsBag.ToList();

        // Cleanup build-session objects
        (_seriesFileDataCacheForBuild, _subtitleFileCacheForBuild, _metadataFileCacheForBuild, _warningsForBuild, _createdDirsForBuild) = (null, null, null, null, null);

        Logger.Info(
            "VFS BuildInternal completed in {Elapsed}ms: processed={Processed}, created={Created}, skipped={Skipped}, errors={Errors}",
            sw.ElapsedMilliseconds,
            seriesProcessed,
            created,
            skipped,
            errors.Count
        );

        return new VfsBuildResult(rootName, seriesProcessed, created, skipped, errors, planned, [.. seriesDetailsBag.OrderBy(x => x.Name)], cleanupDetails, sw.Elapsed);
    }

    private (int Created, int Skipped, List<string> Errors, int Planned) BuildSeries(
        IShokoSeries series,
        string rootFolderName,
        bool cleanRoot,
        ConcurrentDictionary<string, byte> cleanedRoots,
        ConcurrentDictionary<string, byte> cleanedSeries,
        bool filtered,
        bool cleanOnly,
        ConcurrentDictionary<string, Task> rootTasks,
        ConcurrentDictionary<string, Task> seriesTasks
    )
    {
        var (created, skipped, planned, errors, sSw) = (0, 0, 0, new List<string>(), Stopwatch.StartNew());
        var (DisplayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
        int folderId = ShokoRelay.Settings.TmdbEpNumbering ? OverrideHelper.GetPrimary(series.ID, _metadataService) : series.ID;
        var fileData = GetSeriesFileDataCached(series);
        if (!fileData.Mappings.Any())
            return (0, 0, errors, 0);

        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).ToDictionary(g => g.Key, g => g.Count());
        var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

        foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
        {
            var loc = mapping.Video?.Files?.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video?.Files?.FirstOrDefault();
            if (loc?.ManagedFolder == null || loc.ManagedFolder.DropFolderType.HasFlag(Shoko.Abstractions.Enums.DropFolderType.Source))
            {
                skipped++;
                continue;
            }
            string? importRoot = VfsShared.ResolveImportRootPath(loc);
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
                Directory.CreateDirectory(rootPath);
            if (_createdDirsForBuild?.TryAdd(seriesPath, 0) == true)
                Directory.CreateDirectory(seriesPath);

            string? src = VfsShared.ResolveSourcePath(loc, importRoot);
            if (src == null)
            {
                skipped++;
                errors.Add($"No accessible file for {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                continue;
            }
            string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(mapping.Coords.Season)));
            if (_createdDirsForBuild?.TryAdd(seasonPath, 0) == true)
                Directory.CreateDirectory(seasonPath);

            var key = (mapping.Coords.Season, mapping.Coords.Episode);
            bool hasPeer = coordCounts.TryGetValue(key, out var count) && count > 1;
            int? vIdx = (hasPeer && !mapping.PartIndex.HasValue && versionCounters.TryGetValue(key, out var v)) ? (versionCounters[key] = v + 1) - 1 : null;
            string fileName = VfsHelper.SanitizeName(
                PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var ex)
                    ? VfsHelper.BuildExtrasFileName(
                        mapping,
                        ex,
                        extraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                        Path.GetExtension(src),
                        DisplayTitle,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx
                    )
                    : VfsHelper.BuildStandardFileName(
                        mapping,
                        epPad,
                        Path.GetExtension(src),
                        mapping.Video!.ID,
                        mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx
                    )
            );
            string destPath = Path.Combine(seasonPath, fileName);
            if (VfsShared.TryCreateLink(src, destPath, Logger))
            {
                created++;
                planned++;
                if ((mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => cr.ShokoEpisode!.SeriesID).Distinct().Count() ?? 0) <= 1)
                {
                    LinkMetadata(Path.GetDirectoryName(src)!, seriesPath);
                    LinkSubtitles(src, Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(destPath), seasonPath, ref planned, ref skipped, errors, ref created);
                }
                else
                    Logger.Debug("Skipping local assets for crossover video {File} (series {SeriesId})", src, series.ID);
            }
            else
            {
                skipped++;
                errors.Add($"Link failed: {src} -> {destPath}");
            }
        }
        return (created, skipped, errors, planned);
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
                Logger.Warn("Unsafe cleanup path blocked: {Path}", path);
            }
            else
                try
                {
                    Directory.Delete(path, true);
                }
                catch (Exception ex)
                {
                    errors.Add($"Clean failed {path}: {ex.Message}");
                    Logger.Error(ex, "VFS clean failed for {Path}", path);
                }
        }
        tcs.SetResult();
    }

    private void LinkMetadata(string sourceDir, string destDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        var lazyLoader = _metadataFileCacheForBuild?.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(f => MetadataExtensions.Contains(Path.GetExtension(f)))]));
        var candidates = lazyLoader?.Value ?? [];
        foreach (var file in candidates)
        {
            string name = Path.GetFileName(file);
            string destName = string.Equals(Path.GetFileNameWithoutExtension(name), "Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name;
            VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), Logger);
        }
    }

    private void LinkSubtitles(string sourceFile, string sourceDir, string destBase, string destDir, ref int planned, ref int skipped, List<string> errors, ref int created)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var lazyLoader = _subtitleFileCacheForBuild?.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.GetFiles(dir).Where(f => SubtitleExtensions.Contains(Path.GetExtension(f)))]));
        var candidates = lazyLoader?.Value ?? [];
        foreach (var sub in candidates)
        {
            string name = Path.GetFileName(sub);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase))
                continue;
            if (VfsShared.TryCreateLink(sub, Path.Combine(destDir, destBase + name[originalBase.Length..]), Logger))
            {
                planned++;
                created++;
            }
            else
            {
                skipped++;
                errors.Add($"Subtitle link failed: {sub}");
            }
        }
    }

    private void PruneSeries(string rootFolderName, IShokoSeries series)
    {
        var paths = new HashSet<string>(VfsShared.PathComparer);
        foreach (var l in GetSeriesFileDataCached(series).Mappings.SelectMany(m => m.Video.Files))
        {
            string? root = VfsShared.ResolveImportRootPath(l);
            if (root != null && paths.Add(Path.Combine(root, rootFolderName, series.ID.ToString())))
            {
                try
                {
                    if (Directory.Exists(paths.Last()))
                        Directory.Delete(paths.Last(), true);
                }
                catch (Exception ex)
                {
                    _warningsForBuild?.Add($"Prune failed {paths.Last()}: {ex.Message}");
                    Logger.Warn(ex, "Failed to prune series path {Path}", paths.Last());
                }
            }
        }
    }

    private MapHelper.SeriesFileData GetSeriesFileDataCached(IShokoSeries series)
    {
        if (!ShokoRelay.Settings.TmdbEpNumbering)
            return _seriesFileDataCacheForBuild?.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series)) ?? MapHelper.GetSeriesFileData(series);
        int pId = OverrideHelper.GetPrimary(series.ID, _metadataService);
        return _seriesFileDataCacheForBuild?.GetOrAdd(
                pId,
                _ =>
                {
                    var group = OverrideHelper.GetGroup(pId, _metadataService).Select(_metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                    return group.Count <= 1
                        ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series)
                        : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<Shoko.Abstractions.Metadata.ISeries>());
                }
            )
            ?? MapHelper.GetSeriesFileData(series);
    }
}
