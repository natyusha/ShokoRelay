using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs;

/// <summary>
/// Result returned by <see cref="VfsBuilder"/> after a build or clean operation.
/// </summary>
/// <param name="RootPath">The virtual name of the VFS root folder.</param>
/// <param name="SeriesProcessed">Total number of series processed during the run.</param>
/// <param name="CreatedLinks">Total number of symbolic links successfully created.</param>
/// <param name="Skipped">Number of items skipped due to missing files or configuration.</param>
/// <param name="Errors">List of error messages encountered during the build.</param>
/// <param name="PlannedLinks">Total number of links intended to be created.</param>
public record VfsBuildResult(string RootPath, int SeriesProcessed, int CreatedLinks, int Skipped, List<string> Errors, int PlannedLinks);

/// <summary>
/// Builds a virtual filesystem tree of symlinks for Plex by mapping Shoko series/episode metadata to Plex-style folder/file naming conventions.
/// </summary>
public class VfsBuilder
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMetadataService _metadataService;
    private readonly string _pluginDataPath;

    private ConcurrentDictionary<int, MapHelper.SeriesFileData>? _seriesFileDataCacheForBuild;
    private ConcurrentDictionary<string, string[]>? _subtitleFileCacheForBuild;
    private ConcurrentDictionary<string, string[]>? _metadataFileCacheForBuild;
    private ConcurrentBag<string>? _warningsForBuild;
    private ConcurrentDictionary<string, byte>? _createdDirsForBuild;

    private static readonly HashSet<string> MetadataExtensions = PlexConstants.LocalMediaAssets.Artwork.Union(PlexConstants.LocalMediaAssets.ThemeSongs).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> SubtitleExtensions = PlexConstants.LocalMediaAssets.Subtitles;

    /// <summary>
    /// Initializes a new instance of the <see cref="VfsBuilder"/> class.
    /// </summary>
    /// <param name="metadataService">Service for accessing Shoko metadata.</param>
    /// <param name="configProvider">Provider for plugin configuration paths.</param>
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

    /// <summary>
    /// Build (or clean) the VFS for a single optional series.
    /// </summary>
    /// <param name="seriesId">Optional series ID to restrict the build.</param>
    /// <param name="cleanRoot">If true, delete existing root before building.</param>
    /// <param name="pruneSeries">If true, remove empty per-series folders.</param>
    /// <returns>A result describing the build outcome.</returns>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot, pruneSeries, false);

    /// <summary>
    /// Build (or clean) the VFS for the given collection of series IDs.
    /// </summary>
    /// <param name="seriesIds">Set of series IDs to process.</param>
    /// <param name="cleanRoot">If true, delete existing root before building.</param>
    /// <param name="pruneSeries">If true, remove empty per-series folders.</param>
    /// <returns>A result describing the build outcome.</returns>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false) => BuildInternal(seriesIds, cleanRoot, pruneSeries, false);

    /// <summary>
    /// Perform a clean operation without generating any VFS links.
    /// </summary>
    /// <param name="seriesId">Optional series ID to restrict the clean scope.</param>
    /// <returns>A result with zero created links.</returns>
    public VfsBuildResult Clean(int? seriesId = null) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, true, false, true);

    /// <summary>
    /// Clean the VFS for the given collection of series IDs.
    /// </summary>
    /// <param name="seriesIds">Set of series IDs whose VFS folders should be deleted.</param>
    /// <returns>A result with zero created links.</returns>
    public VfsBuildResult Clean(IReadOnlyCollection<int> seriesIds) => BuildInternal(seriesIds, true, false, true);

    /// <summary>
    /// Internal core logic for orchestrating a VFS build or clean run.
    /// </summary>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, bool pruneSeries, bool cleanOnly)
    {
        var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
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

        IEnumerable<IShokoSeries> seriesList =
            (seriesIds?.Count > 0)
                ?
                [
                    .. seriesIds
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

        Parallel.ForEach(
            seriesList,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism) },
            series =>
            {
                try
                {
                    var (Created, Skipped, Errors, Planned) = BuildSeries(series, rootName, cleanRoot, cleanedRoots, cleanedSeries, seriesIds?.Count > 0, cleanOnly, rootTasks, seriesTasks);
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

        sw.Stop();
        var (errors, warnings) = (errorsBag.ToList(), _warningsForBuild.ToList());
        WriteFinalReport(sw, seriesProcessed, _createdDirsForBuild.Count, created, planned, skipped, warnings, errors);

        (_seriesFileDataCacheForBuild, _subtitleFileCacheForBuild, _metadataFileCacheForBuild, _warningsForBuild, _createdDirsForBuild) = (null, null, null, null, null);
        Logger.Info(
            "VFS BuildInternal completed in {Elapsed}ms: processed={Processed}, created={Created}, skipped={Skipped}, errors={Errors}",
            sw.ElapsedMilliseconds,
            seriesProcessed,
            created,
            skipped,
            errors.Count
        );
        return new VfsBuildResult(rootName, seriesProcessed, created, skipped, errors, planned);
    }

    /// <summary>
    /// Processes a single series to generate VFS symlinks and link associated metadata.
    /// </summary>
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
        var (DisplayTitle, SortTitle, OriginalTitle) = TextHelper.ResolveFullSeriesTitles(series);
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
        Logger.Debug("BuildSeries {SeriesId} completed in {Elapsed}ms: created={Created}, errors={Errors}", series.ID, sSw.ElapsedMilliseconds, created, errors.Count);
        return (created, skipped, errors, planned);
    }

    /// <summary>
    /// Handles the thread-safe deletion of a directory during a build.
    /// </summary>
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

    /// <summary>
    /// Links local metadata (posters, banners, etc.) from the source directory to the VFS.
    /// </summary>
    private void LinkMetadata(string sourceDir, string destDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        var candidates = _metadataFileCacheForBuild?.GetOrAdd(sourceDir, _ => [.. Directory.EnumerateFiles(sourceDir).Where(f => MetadataExtensions.Contains(Path.GetExtension(f)))]) ?? [];
        foreach (var file in candidates)
        {
            string name = Path.GetFileName(file),
                destName = string.Equals(Path.GetFileNameWithoutExtension(name), "Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name;
            VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), Logger);
        }
    }

    /// <summary>
    /// Links sidecar subtitle files from the source directory to the VFS, renaming them to match the linked video.
    /// </summary>
    private void LinkSubtitles(string sourceFile, string sourceDir, string destBase, string destDir, ref int planned, ref int skipped, List<string> errors, ref int created)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var dirSw = Stopwatch.StartNew();
        var candidates = _subtitleFileCacheForBuild?.GetOrAdd(sourceDir, _ => [.. Directory.GetFiles(sourceDir).Where(f => SubtitleExtensions.Contains(Path.GetExtension(f)))]) ?? [];
        dirSw.Stop();
        if (dirSw.ElapsedMilliseconds > 50)
            Logger.Debug("Directory.GetFiles({SourceDir}) took {Elapsed}ms and returned {Count} entries", sourceDir, dirSw.ElapsedMilliseconds, candidates.Length);
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

    /// <summary>
    /// Deletes the per-series VFS folder for the specified series across all import roots.
    /// </summary>
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

    /// <summary>
    /// Retrieves file mapping data for a series, utilizing the build-session cache.
    /// </summary>
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

    /// <summary>
    /// Writes the final text report to the plugin's logs directory and logs a summary to the server.
    /// </summary>
    private void WriteFinalReport(Stopwatch sw, int processed, int dirs, int created, int planned, int skipped, List<string> warnings, List<string> errors)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"VFS Generation Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nElapsed: {sw.Elapsed}\nSeries processed: {processed}\nDirectories created: {dirs}\nLinks created: {created}\nPlanned links: {planned}\nLinks skipped: {skipped}"
        );
        if (warnings.Any())
        {
            sb.AppendLine("\nWarnings:");
            foreach (var w in warnings)
                sb.AppendLine(w);
        }
        if (errors.Any())
        {
            sb.AppendLine("\nErrors:");
            foreach (var e in errors)
                sb.AppendLine(e);
        }
        try
        {
            LogHelper.WriteLog(_pluginDataPath, "vfs-report.log", sb.ToString());
            Logger.Info("VFS report written");
        }
        catch { }
    }
}
