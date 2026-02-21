using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs
{
    public record VfsBuildResult(string RootPath, int SeriesProcessed, int CreatedLinks, int Skipped, List<string> Errors, int PlannedLinks);

    public class VfsBuilder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IMetadataService _metadataService;
        private readonly string _pluginDataPath;

        // Per-build caches (initialized for the duration of BuildInternal and cleared afterwards)
        private ConcurrentDictionary<int, MapHelper.SeriesFileData>? _seriesFileDataCacheForBuild;
        private ConcurrentDictionary<string, string[]>? _subtitleFileCacheForBuild;
        private ConcurrentDictionary<string, string[]>? _metadataFileCacheForBuild;

        // Temp storage for run-wide warnings (not returned by Build result)
        private ConcurrentBag<string>? _warningsForBuild;

        // Tracks directories created during a build (unique keys)
        private ConcurrentDictionary<string, byte>? _createdDirsForBuild;

        private static readonly HashSet<string> MetadataExtensions = PlexConstants
            .LocalMediaAssets.Artwork.Union(PlexConstants.LocalMediaAssets.ThemeSongs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SubtitleExtensions = PlexConstants.LocalMediaAssets.Subtitles;

        public VfsBuilder(IMetadataService metadataService, IApplicationPaths applicationPaths)
        {
            _metadataService = metadataService;
            // reports and other plugin-specific files should live inside our plugin
            // folder rather than the global server data directory.
            _pluginDataPath = ConfigConstants.GetPluginDirectory(applicationPaths);
            try
            {
                Directory.CreateDirectory(_pluginDataPath);
            }
            catch
            {
                // ignore - writing later may still fail and will be logged
            }
        }

        // public build APIs ================================
        public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false)
        {
            return BuildInternal(seriesId.HasValue ? new[] { seriesId.Value } : null, cleanRoot, pruneSeries, cleanOnly: false);
        }

        public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false)
        {
            return BuildInternal(seriesIds, cleanRoot, pruneSeries, cleanOnly: false);
        }

        /// <summary>
        /// Perform a clean operation without generating any VFS links.  This will
        /// delete the root or per-series folders exactly the same way that the
        /// normal build does when <c>cleanRoot</c> is true, but it returns before any
        /// mapping/creation work begins.
        /// </summary>
        public VfsBuildResult Clean(int? seriesId = null)
        {
            return BuildInternal(seriesId.HasValue ? new[] { seriesId.Value } : null, cleanRoot: true, pruneSeries: false, cleanOnly: true);
        }

        public VfsBuildResult Clean(IReadOnlyCollection<int> seriesIds)
        {
            return BuildInternal(seriesIds, cleanRoot: true, pruneSeries: false, cleanOnly: true);
        }

        private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, bool pruneSeries, bool cleanOnly)
        {
            // Initialize per-run caches (cleared before returning)
            _seriesFileDataCacheForBuild = new ConcurrentDictionary<int, MapHelper.SeriesFileData>();
            _subtitleFileCacheForBuild = new ConcurrentDictionary<string, string[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            _metadataFileCacheForBuild = new ConcurrentDictionary<string, string[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // Initialize run-wide helper structures
            _warningsForBuild = new ConcurrentBag<string>();
            _createdDirsForBuild = new ConcurrentDictionary<string, byte>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var sw = Stopwatch.StartNew();
            int created = 0;
            int skipped = 0;
            int seriesProcessed = 0;
            int planned = 0;

            string rootName = VfsShared.ResolveRootFolderName();
            var cleanedRoots = new ConcurrentDictionary<string, byte>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var cleanedSeriesPaths = new ConcurrentDictionary<string, byte>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            // track cleanup tasks so other threads can wait for slow deletions to finish
            var rootCleanupTasks = new ConcurrentDictionary<string, Task>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var seriesCleanupTasks = new ConcurrentDictionary<string, Task>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            bool filterApplied = seriesIds != null && seriesIds.Count > 0;

            // Gather series list (pruneSeries may call PruneSeries which uses cached fileData)
            var errorsBag = new ConcurrentBag<string>();
            // warningsBag is handled by _warningsForBuild
            IEnumerable<IShokoSeries> seriesList = Array.Empty<IShokoSeries>();
            if (seriesIds != null && seriesIds.Count > 0)
            {
                var list = new List<IShokoSeries>();
                foreach (var id in seriesIds.Distinct())
                {
                    var s = _metadataService.GetShokoSeriesByID(id);

                    if (s == null)
                    {
                        errorsBag.Add($"Series {id} not found");
                        continue;
                    }

                    list.Add(s);

                    if (pruneSeries)
                        PruneSeries(rootName, s);
                }

                seriesList = list;
            }
            else
            {
                seriesList = _metadataService.GetAllShokoSeries() ?? Array.Empty<IShokoSeries>();
            }

            // Bounded parallel processing of series to improve throughput while avoiding excessive IO contention.
            int maxDop = Math.Max(1, ShokoRelay.Settings.VfsParallelism);
            var po = new ParallelOptions { MaxDegreeOfParallelism = maxDop };

            Parallel.ForEach(
                seriesList ?? Array.Empty<IShokoSeries>(),
                po,
                series =>
                {
                    if (series == null)
                        return;

                    try
                    {
                        var (c, s, e, p) = BuildSeries(series, rootName, cleanRoot, cleanedRoots, cleanedSeriesPaths, filterApplied, cleanOnly, rootCleanupTasks, seriesCleanupTasks);
                        if (c != 0)
                            Interlocked.Add(ref created, c);
                        if (s != 0)
                            Interlocked.Add(ref skipped, s);
                        if (p != 0)
                            Interlocked.Add(ref planned, p);
                        if (e?.Count > 0)
                        {
                            foreach (var err in e)
                                errorsBag.Add(err);
                        }

                        Interlocked.Increment(ref seriesProcessed);
                    }
                    catch (Exception ex)
                    {
                        errorsBag.Add($"Failed to process series {series.PreferredTitle?.Value}: {ex.Message}");
                        Logger.Error(ex, "VFS build failed for series {SeriesId}", series?.ID);
                    }
                }
            );

            sw.Stop();

            var errors = errorsBag.ToList();
            var warnings = _warningsForBuild?.ToList() ?? new List<string>();
            int dirsCreated = _createdDirsForBuild?.Count ?? 0;

            // write report file to plugin directory
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"VFS Build Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Elapsed: {sw.Elapsed}");
                sb.AppendLine($"Series processed: {seriesProcessed}");
                sb.AppendLine($"Directories created: {dirsCreated}");
                sb.AppendLine($"Links created: {created}");
                sb.AppendLine($"Planned links: {planned}");
                sb.AppendLine($"Links skipped: {skipped}");
                if (warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Warnings:");
                    foreach (var w in warnings)
                        sb.AppendLine(w);
                }
                if (errors.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Errors:");
                    foreach (var e in errors)
                        sb.AppendLine(e);
                }

                string reportPath = Path.Combine(_pluginDataPath, "vfs-report.log");
                System.IO.File.WriteAllText(reportPath, sb.ToString());
                Logger.Info("VFS report written to {Path}", reportPath);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to write VFS report file");
                _warningsForBuild?.Add($"Failed to write VFS report file: {ex.Message}");
            }

            // Clear per-run caches
            _seriesFileDataCacheForBuild = null;
            _subtitleFileCacheForBuild = null;
            _metadataFileCacheForBuild = null;
            _warningsForBuild = null;
            _createdDirsForBuild = null;

            Logger.Info(
                "VFS BuildInternal completed in {Elapsed}ms: seriesProcessed={SeriesProcessed}, created={Created}, planned={Planned}, skipped={Skipped}, errors={ErrorsCount}, dirs={Dirs}",
                sw.ElapsedMilliseconds,
                seriesProcessed,
                created,
                planned,
                skipped,
                errors.Count,
                dirsCreated
            );
            return new VfsBuildResult(rootName, seriesProcessed, created, skipped, errors, planned);
        }

        private (int Created, int Skipped, List<string> Errors, int Planned) BuildSeries(
            IShokoSeries series,
            string rootFolderName,
            bool cleanRoot,
            ConcurrentDictionary<string, byte> cleanedRoots,
            ConcurrentDictionary<string, byte> cleanedSeriesPaths,
            bool filterApplied,
            bool cleanOnly,
            ConcurrentDictionary<string, Task> rootCleanupTasks,
            ConcurrentDictionary<string, Task> seriesCleanupTasks
        )
        {
            int created = 0;
            int skipped = 0;
            int planned = 0;
            var errors = new List<string>();
            var reportedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();

            var titles = TextHelper.ResolveFullSeriesTitles(series);
            string seriesFolder = series.ID.ToString();

            var fileData = GetSeriesFileDataCached(series);
            if (!fileData.Mappings.Any())
            {
                return (0, 0, errors, 0);
            }

            var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).ToDictionary(g => g.Key, g => g.Count());

            var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);

            int maxEpNum = fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1);
            int epPad = Math.Max(2, maxEpNum.ToString().Length);

            var extraPadBySeason = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

            // Use run-level subtitle cache when available, otherwise fall back to a per-series small cache
            IDictionary<string, string[]> subtitleFileCache;
            if (_subtitleFileCacheForBuild != null)
                subtitleFileCache = _subtitleFileCacheForBuild;
            else
                subtitleFileCache = new Dictionary<string, string[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
            {
                var location = mapping.Video?.Files?.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video?.Files?.FirstOrDefault();
                if (location == null)
                {
                    skipped++;
                    errors.Add($"No video locations for mapping {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                string importFolderNameRaw = location.ManagedFolder?.Name ?? "ImportFolder";
                string importFolderSafe = VfsHelper.SanitizeName(importFolderNameRaw);

                if (location.ManagedFolder != null && location.ManagedFolder.DropFolderType.HasFlag(Shoko.Abstractions.Enums.DropFolderType.Source))
                {
                    skipped++;
                    errors.Add($"Skipped source-only import folder for {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}: {importFolderNameRaw}");
                    continue;
                }

                string? importRoot = VfsShared.ResolveImportRootPath(location);
                if (string.IsNullOrWhiteSpace(importRoot))
                {
                    skipped++;
                    errors.Add($"No import root for mapping {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                if (!Directory.Exists(importRoot))
                {
                    skipped++;
                    errors.Add($"Import root not found for mapping {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}: {importRoot}");
                    continue;
                }

                string rootPath = Path.Combine(importRoot, rootFolderName);
                string seriesPath = Path.Combine(rootPath, seriesFolder);

                if (cleanRoot)
                {
                    if (filterApplied)
                    {
                        // When a filter is applied, only remove the series-specific VFS folder(s) for the filtered series (do not delete the entire root).
                        // Because the build is parallel we need to make sure threads that skip the actual deletion still wait until the first thread has finished removing the directory.
                        // Otherwise a slow filesystem could cause "lost" links)
                        if (cleanedSeriesPaths.TryAdd(seriesPath, 0))
                        {
                            // this thread is responsible for cleaning
                            var tcs = new TaskCompletionSource();
                            seriesCleanupTasks[seriesPath] = tcs.Task;
                            if (Directory.Exists(seriesPath))
                            {
                                if (!VfsShared.IsSafeToDelete(seriesPath))
                                {
                                    errors.Add($"Refusing to clean VFS series path at {seriesPath} (path check failed)");
                                }
                                else
                                {
                                    try
                                    {
                                        Directory.Delete(seriesPath, recursive: true);
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"Failed to clean VFS series path {seriesPath}: {ex.Message}");
                                        Logger.Error(ex, "Failed to clean VFS series path {Path}", seriesPath);
                                    }
                                }
                            }
                            tcs.SetResult();
                        }
                        else if (seriesCleanupTasks.TryGetValue(seriesPath, out var waitTask))
                        {
                            // wait for the deletion started by another thread
                            waitTask.Wait();
                        }
                    }
                    else
                    {
                        // Delete the entire VFS root once per import root (when no filter is applied)
                        if (cleanedRoots.TryAdd(rootPath, 0))
                        {
                            var tcs = new TaskCompletionSource();
                            rootCleanupTasks[rootPath] = tcs.Task;
                            if (Directory.Exists(rootPath))
                            {
                                if (!VfsShared.IsSafeToDelete(rootPath))
                                {
                                    errors.Add($"Refusing to clean VFS root at {rootPath} (path check failed)");
                                }
                                else
                                {
                                    try
                                    {
                                        Directory.Delete(rootPath, recursive: true);
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"Failed to clean VFS root {rootPath}: {ex.Message}");
                                        Logger.Error(ex, "Failed to clean VFS root {Root}", rootPath);
                                    }
                                }
                            }
                            tcs.SetResult();
                        }
                        else if (rootCleanupTasks.TryGetValue(rootPath, out var waitTask))
                        {
                            waitTask.Wait();
                        }
                    }
                }

                // when running clean-only we stop here; the loop serves solely to perform
                // cleanup for every encountered root/series path.
                if (cleanOnly)
                    continue;

                Directory.CreateDirectory(rootPath);
                // track directory creation
                if (_createdDirsForBuild != null)
                    _createdDirsForBuild.TryAdd(rootPath, 0);

                string? source = VfsShared.ResolveSourcePath(location, importRoot);

                if (string.IsNullOrWhiteSpace(source))
                {
                    skipped++;
                    errors.Add($"No accessible file for mapping {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                int fileId = mapping.Video?.ID ?? 0;

                string rootDirKey = $"/{importFolderSafe}/{rootFolderName}";
                string seriesDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}";
                Directory.CreateDirectory(seriesPath);
                if (_createdDirsForBuild != null)
                    _createdDirsForBuild.TryAdd(seriesPath, 0);
                // record that we've processed these directories so LinkMetadata/LinkSubtitles can de-duplicate actions
                reportedDirs.Add(rootDirKey);
                reportedDirs.Add(seriesDirKey);

                bool isExtra = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var specialInfo);
                string seasonFolder = VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(mapping.Coords.Season));
                string seasonPath = Path.Combine(seriesPath, seasonFolder);
                string seasonDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}/{seasonFolder}";
                Directory.CreateDirectory(seasonPath);
                if (_createdDirsForBuild != null)
                    _createdDirsForBuild.TryAdd(seasonPath, 0);

                string extension = Path.GetExtension(source) ?? string.Empty;
                int padForExtra = 1;
                if (isExtra && extraPadBySeason.TryGetValue(mapping.Coords.Season, out var padLookup))
                    padForExtra = padLookup;

                var coordKey = (mapping.Coords.Season, mapping.Coords.Episode);
                bool hasPeer = coordCounts.TryGetValue(coordKey, out var coordCount) && coordCount > 1;

                int? effectivePartIndex = hasPeer ? mapping.PartIndex : null;
                int effectivePartCount = hasPeer ? mapping.PartCount : 1;

                int? versionIndex = null;
                if (hasPeer && !effectivePartIndex.HasValue && versionCounters.TryGetValue(coordKey, out var nextVersion))
                {
                    versionIndex = nextVersion;
                    versionCounters[coordKey] = nextVersion + 1;
                }

                bool omitId = mapping.PartCount > 1 && mapping.PartIndex.HasValue; // multipart files must have the exact same name except for the part index, so the fileId must be removed for them to function in plex
                string fileName = isExtra
                    ? VfsHelper.BuildExtrasFileName(mapping, specialInfo, padForExtra, extension, titles.DisplayTitle, effectivePartIndex, effectivePartCount, versionIndex)
                    : VfsHelper.BuildStandardFileName(mapping, epPad, extension, fileId, omitId, effectivePartIndex, effectivePartCount, versionIndex);

                fileName = VfsHelper.SanitizeName(fileName);
                string destPath = Path.Combine(seasonPath, fileName);
                string destBase = Path.GetFileNameWithoutExtension(destPath);
                string sourceDir = Path.GetDirectoryName(source) ?? string.Empty;

                // Define a "crossover" as a video that is cross-referenced to episodes in more than one  distinct AniDB/Shoko series.
                // This is relevant because local metadata and subtitle linking is only safe when we are sure that the linked video file is not shared by multiple series.
                var distinctSeriesCount = mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => cr.ShokoEpisode!.SeriesID).Distinct().Count() ?? 0;
                bool isCrossover = distinctSeriesCount > 1;

                if (VfsShared.TryCreateLink(source, destPath, Logger))
                {
                    created++;
                    planned++;
                    if (!isCrossover)
                    {
                        LinkMetadata(sourceDir, seriesPath, reportedDirs);
                        LinkSubtitles(source, sourceDir, destBase, seasonPath, reportedDirs, ref planned, ref skipped, errors, subtitleFileCache);
                    }
                    else
                    {
                        Logger.Debug("Skipping local metadata/subtitle linking for crossover video {File} (series {SeriesId})", source, series.ID);
                    }
                }
                else
                {
                    skipped++;
                    errors.Add($"Failed to link {source} -> {destPath}");
                }
            }

            sw.Stop();
            Logger.Debug(
                "BuildSeries {SeriesId} completed in {Elapsed}ms: mappings={Mappings} created={Created} planned={Planned} skipped={Skipped} errors={ErrorsCount}",
                series.ID,
                sw.ElapsedMilliseconds,
                fileData?.Mappings?.Count ?? 0,
                created,
                planned,
                skipped,
                errors.Count
            );
            return (created, skipped, errors, planned);
        }

        private void LinkMetadata(string sourceDir, string destDir, HashSet<string> reportedDirs)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return;

            string dirKey = $"meta::{sourceDir}->{destDir}";
            if (!reportedDirs.Add(dirKey))
                return;

            // Prefer run-level metadata file cache (stores only relevant metadata files) to avoid full directory scans
            string[] candidates;
            if (_metadataFileCacheForBuild != null && _metadataFileCacheForBuild.TryGetValue(sourceDir, out var cached))
                candidates = cached;
            else
            {
                candidates = Directory.Exists(sourceDir) ? Directory.EnumerateFiles(sourceDir).Where(f => MetadataExtensions.Contains(Path.GetExtension(f))).ToArray() : Array.Empty<string>();

                if (_metadataFileCacheForBuild != null)
                    _metadataFileCacheForBuild[sourceDir] = candidates;
            }

            foreach (var file in candidates)
            {
                string name = Path.GetFileName(file);
                string destPath = Path.Combine(destDir, name);

                VfsShared.TryCreateLink(file, destPath, Logger);
            }
        }

        private void PruneSeries(string rootFolderName, IShokoSeries series)
        {
            string seriesFolder = series.ID.ToString();

            var fileData = GetSeriesFileDataCached(series);
            var seriesPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var mapping in fileData.Mappings)
            {
                foreach (var location in mapping.Video.Files)
                {
                    string? importRoot = VfsShared.ResolveImportRootPath(location);
                    if (string.IsNullOrWhiteSpace(importRoot))
                        continue;

                    string seriesPath = Path.Combine(importRoot, rootFolderName, seriesFolder);
                    if (!seriesPaths.Add(seriesPath))
                        continue;

                    try
                    {
                        if (Directory.Exists(seriesPath))
                            Directory.Delete(seriesPath, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to prune series path {Path}", seriesPath);
                        _warningsForBuild?.Add($"Failed to prune series path {seriesPath}: {ex.Message}");
                    }
                }
            }
        }

        private void LinkSubtitles(
            string sourceFile,
            string sourceDir,
            string destBaseName,
            string destDir,
            HashSet<string> reportedDirs,
            ref int planned,
            ref int skipped,
            List<string> errors,
            IDictionary<string, string[]>? subFileCache = null
        )
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return;

            string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
            // Use cached subtitle file list when available to avoid repeated Directory.EnumerateFiles scans
            string[] candidates;
            if (subFileCache != null && subFileCache.TryGetValue(sourceDir, out var cached))
                candidates = cached;
            else
            {
                var dirSw = Stopwatch.StartNew();
                // Cache only subtitle files (filter early) to reduce memory and work
                candidates = Directory.Exists(sourceDir) ? Directory.GetFiles(sourceDir).Where(f => SubtitleExtensions.Contains(Path.GetExtension(f))).ToArray() : Array.Empty<string>();
                dirSw.Stop();
                if (dirSw.ElapsedMilliseconds > 50)
                    Logger.Debug("Directory.GetFiles({SourceDir}) took {Elapsed}ms and returned {Count} entries", sourceDir, dirSw.ElapsedMilliseconds, candidates.Length);
                if (subFileCache != null)
                    subFileCache[sourceDir] = candidates;
            }

            foreach (var sub in candidates)
            {
                string ext = Path.GetExtension(sub);
                if (!SubtitleExtensions.Contains(ext))
                    continue;

                string name = Path.GetFileName(sub);
                if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase))
                    continue;

                string suffix = name.Substring(originalBase.Length);
                string destName = destBaseName + suffix;
                string destPath = Path.Combine(destDir, destName);

                if (VfsShared.TryCreateLink(sub, destPath, Logger))
                {
                    planned++;
                }
                else
                {
                    skipped++;
                    errors.Add($"Failed to link subtitle {sub} -> {destPath}");
                }
            }
        }

        // Helper used by BuildSeries/PruneSeries to reuse MapHelper results for the duration of a VFS build
        private MapHelper.SeriesFileData GetSeriesFileDataCached(Shoko.Abstractions.Metadata.Shoko.IShokoSeries series)
        {
            if (_seriesFileDataCacheForBuild == null)
                return MapHelper.GetSeriesFileData(series);

            return _seriesFileDataCacheForBuild.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series));
        }
    }
}
