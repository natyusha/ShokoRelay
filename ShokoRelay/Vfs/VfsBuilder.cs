using System.Text;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs
{
    public record VfsBuildResult(string RootPath, int SeriesProcessed, int CreatedLinks, int Skipped, List<string> Errors, int PlannedLinks);

    public class VfsBuilder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IMetadataService _metadataService;
        private readonly string _programDataPath;

        private static readonly HashSet<string> MetadataExtensions = PlexConstants
            .LocalMediaAssets.Artwork.Union(PlexConstants.LocalMediaAssets.ThemeSongs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SubtitleExtensions = PlexConstants.LocalMediaAssets.Subtitles;

        public VfsBuilder(IMetadataService metadataService, IApplicationPaths applicationPaths)
        {
            _metadataService = metadataService;
            _programDataPath = applicationPaths.ProgramDataPath;
        }

        public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool pruneSeries = false)
        {
            return BuildInternal(seriesId.HasValue ? new[] { seriesId.Value } : null, cleanRoot, pruneSeries);
        }

        public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, bool pruneSeries = false)
        {
            return BuildInternal(seriesIds, cleanRoot, pruneSeries);
        }

        private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, bool pruneSeries)
        {
            var errors = new List<string>();
            int created = 0;
            int skipped = 0;
            int seriesProcessed = 0;
            int planned = 0;

            string rootName = VfsShared.ResolveRootFolderName();
            var cleanedRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var cleanedSeriesPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            bool filterApplied = seriesIds != null && seriesIds.Count > 0;

            IEnumerable<IShokoSeries> seriesList;
            if (seriesIds != null && seriesIds.Count > 0)
            {
                var list = new List<IShokoSeries>();
                foreach (var id in seriesIds.Distinct())
                {
                    var s = _metadataService.GetShokoSeriesByID(id);
                    if (s == null)
                    {
                        errors.Add($"Series {id} not found");
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
                seriesList = _metadataService.GetAllShokoSeries();
            }

            foreach (var series in seriesList)
            {
                if (series == null)
                    continue;

                try
                {
                    var (c, s, e, p) = BuildSeries(series, rootName, cleanRoot, cleanedRoots, cleanedSeriesPaths, filterApplied);
                    created += c;
                    skipped += s;
                    planned += p;
                    errors.AddRange(e);
                    seriesProcessed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to process series {series.PreferredTitle}: {ex.Message}");
                    Logger.Error(ex, "VFS build failed for series {SeriesId}", series.ID);
                }
            }

            return new VfsBuildResult(rootName, seriesProcessed, created, skipped, errors, planned);
        }

        private (int Created, int Skipped, List<string> Errors, int Planned) BuildSeries(
            IShokoSeries series,
            string rootFolderName,
            bool cleanRoot,
            HashSet<string> cleanedRoots,
            HashSet<string> cleanedSeriesPaths,
            bool filterApplied
        )
        {
            int created = 0;
            int skipped = 0;
            int planned = 0;
            var errors = new List<string>();
            var reportedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var titles = TextHelper.ResolveFullSeriesTitles(series);
            string seriesFolder = series.ID.ToString();

            var fileData = MapHelper.GetSeriesFileData(series);
            if (!fileData.Mappings.Any())
            {
                return (0, 0, errors, 0);
            }

            var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).ToDictionary(g => g.Key, g => g.Count());

            var versionCounters = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 1);

            int maxEpNum = fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1);
            int epPad = Math.Max(2, maxEpNum.ToString().Length);

            var extraPadBySeason = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

            foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
            {
                var location = mapping.Video?.Locations?.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video?.Locations?.FirstOrDefault();
                if (location == null)
                {
                    skipped++;
                    errors.Add($"No video locations for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                string importFolderNameRaw = location.ImportFolder?.Name ?? "ImportFolder";
                string importFolderSafe = VfsHelper.SanitizeName(importFolderNameRaw);

                if (location.ImportFolder?.DropFolderType == DropFolderType.Source)
                {
                    skipped++;
                    errors.Add($"Skipped source-only import folder for {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}: {importFolderNameRaw}");
                    continue;
                }

                string? importRoot = VfsShared.ResolveImportRootPath(location);
                if (string.IsNullOrWhiteSpace(importRoot))
                {
                    skipped++;
                    errors.Add($"No import root for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                if (!Directory.Exists(importRoot))
                {
                    skipped++;
                    errors.Add($"Import root not found for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}: {importRoot}");
                    continue;
                }

                string rootPath = Path.Combine(importRoot, rootFolderName);
                string seriesPath = Path.Combine(rootPath, seriesFolder);

                if (cleanRoot)
                {
                    if (filterApplied)
                    {
                        // When a filter is applied, only remove the series-specific VFS folder(s)
                        // for the filtered series (do not delete the entire root).
                        if (cleanedSeriesPaths.Add(seriesPath))
                        {
                            if (Directory.Exists(seriesPath))
                            {
                                if (!IsSafeToDelete(seriesPath))
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
                        }
                    }
                    else
                    {
                        // Legacy behavior: delete the entire VFS root once per import root.
                        if (cleanedRoots.Add(rootPath))
                        {
                            if (Directory.Exists(rootPath))
                            {
                                if (!IsSafeToDelete(rootPath))
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
                        }
                    }
                }

                Directory.CreateDirectory(rootPath);

                string? source = ResolveSourcePath(location, importRoot);

                if (string.IsNullOrWhiteSpace(source))
                {
                    skipped++;
                    errors.Add($"No accessible file for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                int fileId = mapping.Video?.ID ?? 0;

                string rootDirKey = $"/{importFolderSafe}/{rootFolderName}";
                string seriesDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}";
                Directory.CreateDirectory(seriesPath);
                // record that we've processed these directories so LinkMetadata/LinkSubtitles can de-duplicate actions
                reportedDirs.Add(rootDirKey);
                reportedDirs.Add(seriesDirKey);

                bool isExtra = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var specialInfo);
                string seasonFolder = VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(mapping.Coords.Season));
                string seasonPath = Path.Combine(seriesPath, seasonFolder);
                string seasonDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}/{seasonFolder}";
                Directory.CreateDirectory(seasonPath);

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

                string fileName = isExtra
                    ? VfsHelper.BuildExtrasFileName(mapping, specialInfo, padForExtra, extension, titles.DisplayTitle, effectivePartIndex, effectivePartCount, versionIndex)
                    : VfsHelper.BuildStandardFileName(mapping, epPad, extension, fileId, effectivePartIndex, effectivePartCount, versionIndex);

                fileName = VfsHelper.SanitizeName(fileName);
                string destPath = Path.Combine(seasonPath, fileName);
                string destBase = Path.GetFileNameWithoutExtension(destPath);
                string sourceDir = Path.GetDirectoryName(source) ?? string.Empty;

                // Define a "crossover" as a video that is cross-referenced to episodes in MORE THAN ONE
                // distinct AniDB/Shoko series. Only when a video's cross-references span multiple series
                // do we treat it as a crossover and skip copying local metadata/subtitles to avoid
                // contaminating one series with another series' metadata.
                var distinctSeriesCount = mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => cr.ShokoEpisode!.SeriesID).Distinct().Count() ?? 0;
                bool isCrossover = distinctSeriesCount > 1;

                if (VfsShared.TryCreateLink(source, destPath, Logger))
                {
                    created++;
                    planned++;
                    if (!isCrossover)
                    {
                        LinkMetadata(sourceDir, seriesPath, reportedDirs);
                        LinkSubtitles(source, sourceDir, destBase, seasonPath, reportedDirs, ref planned, ref skipped, errors);
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

            return (created, skipped, errors, planned);
        }

        private string? ResolveSourcePath(Shoko.Plugin.Abstractions.DataModels.IVideoFile location, string importRoot)
        {
            string original = location.Path;
            if (!string.IsNullOrWhiteSpace(original) && File.Exists(original))
                return original;

            string relative = location.RelativePath?.TrimStart('/', '\\') ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(relative))
            {
                string candidate = Path.Combine(importRoot, VfsShared.NormalizeSeparators(relative));
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private void LinkMetadata(string sourceDir, string destDir, HashSet<string> reportedDirs)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return;

            string dirKey = $"meta::{sourceDir}->{destDir}";
            if (!reportedDirs.Add(dirKey))
                return;

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                string ext = Path.GetExtension(file);
                if (!MetadataExtensions.Contains(ext))
                    continue;

                string name = Path.GetFileName(file);
                string destPath = Path.Combine(destDir, name);

                VfsShared.TryCreateLink(file, destPath, Logger);
            }
        }

        private void PruneSeries(string rootFolderName, IShokoSeries series)
        {
            string seriesFolder = series.ID.ToString();

            var fileData = MapHelper.GetSeriesFileData(series);
            var seriesPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var mapping in fileData.Mappings)
            {
                foreach (var location in mapping.Video.Locations)
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
                    }
                }
            }
        }

        private void LinkSubtitles(string sourceFile, string sourceDir, string destBaseName, string destDir, HashSet<string> reportedDirs, ref int planned, ref int skipped, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return;

            string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
            foreach (var sub in Directory.EnumerateFiles(sourceDir))
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

        private static bool IsSafeToDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
