using System.Runtime.InteropServices;
using System.Text;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Helpers;

namespace ShokoRelay.Meta
{
    public record VfsBuildResult(
        string RootPath,
        int SeriesProcessed,
        int CreatedLinks,
        int Skipped,
        List<string> Errors,
        bool DryRun,
        int PlannedLinks,
        string? ReportPath,
        string? ReportContent);

    public class VfsBuilder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string DefaultRootName = "!ShokoRelayVFS";

        private readonly IMetadataService _metadataService;
        private readonly string _programDataPath;

        private static readonly HashSet<string> MetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".gif", ".jpe", ".jpeg", ".jpg", ".png", ".tbn", ".tif", ".tiff", ".webp", ".mp3"
        };

        private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".smi", ".ssa", ".ass", ".vtt"
        };

        // Special buckets for fallback seasons
        private static readonly Dictionary<int, (string Folder, string Prefix)> SpecialSeasons = new()
        {
            { 95, ("Featurettes", "Other") },
            { 96, ("Shorts", "Credits") },
            { 97, ("Trailers", "Trailers") },
            { 98, ("Scenes", "Parody") },
            { 99, ("Other", "Unknown") }
        };

        public VfsBuilder(IMetadataService metadataService, IApplicationPaths applicationPaths)
        {
            _metadataService = metadataService;
            _programDataPath = applicationPaths.ProgramDataPath;
        }

        public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, bool dryRun = false, bool pruneSeries = false)
        {
            var errors = new List<string>();
            int created = 0;
            int skipped = 0;
            int seriesProcessed = 0;
            int planned = 0;

            var report = new StringBuilder();

            string rootName = ResolveRootFolderName();
            var cleanedRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            if (dryRun)
                cleanRoot = false;

            IEnumerable<IShokoSeries> seriesList;
            if (seriesId.HasValue)
            {
                var s = _metadataService.GetShokoSeriesByID(seriesId.Value);
                if (s == null)
                {
                    errors.Add($"Series {seriesId.Value} not found");
                    return new VfsBuildResult(rootName, 0, 0, 0, errors, dryRun, planned, null, null);
                }
                seriesList = new[] { s };

                if (pruneSeries && !dryRun)
                {
                    PruneSeries(rootName, s);
                }
            }
            else
            {
                seriesList = _metadataService.GetAllShokoSeries();
            }

            foreach (var series in seriesList)
            {
                if (series == null) continue;

                try
                {
                    var (c, s, e, p) = BuildSeries(series, rootName, cleanRoot, dryRun, cleanedRoots, report);
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

            string? reportPath = null;
            if (dryRun)
            {
                reportPath = Path.Combine(_programDataPath, "ShokoRelay-VFS-dryrun.txt");
                try
                {
                    File.WriteAllText(reportPath, report.ToString());
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to write dry-run report: {ex.Message}");
                    Logger.Warn(ex, "Dry-run report write failed at {Path}", reportPath);
                }
            }

            return new VfsBuildResult(rootName, seriesProcessed, created, skipped, errors, dryRun, planned, reportPath, dryRun ? report.ToString() : null);
        }

        private (int Created, int Skipped, List<string> Errors, int Planned) BuildSeries(
            IShokoSeries series,
            string rootFolderName,
            bool cleanRoot,
            bool dryRun,
            HashSet<string> cleanedRoots,
            StringBuilder report)
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

            int maxEpNum = fileData.Mappings.Max(m => m.Coords.EndEpisode ?? m.Coords.Episode);
            int epPad = Math.Max(2, maxEpNum.ToString().Length);

            foreach (var mapping in fileData.Mappings
                .OrderBy(m => m.Coords.Season)
                .ThenBy(m => m.Coords.Episode)
                .ThenBy(m => m.PartIndex ?? 0))
            {
                var location = mapping.Video.Locations.FirstOrDefault(l => File.Exists(l.Path)) ?? mapping.Video.Locations.FirstOrDefault();
                if (location == null)
                {
                    skipped++;
                    errors.Add($"No video locations for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                string importFolderNameRaw = location.ImportFolder?.Name ?? "ImportFolder";
                string importFolderSafe = SanitizeName(importFolderNameRaw);

                if (location.ImportFolder?.DropFolderType == DropFolderType.Source)
                {
                    skipped++;
                    errors.Add($"Skipped source-only import folder for {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}: {importFolderNameRaw}");
                    continue;
                }

                string? importRoot = ResolveImportRootPath(location);
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
                if (cleanRoot && !dryRun && cleanedRoots.Add(rootPath))
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

                if (!dryRun)
                {
                    Directory.CreateDirectory(rootPath);
                }

                string? source = ResolveSourcePath(location, importRoot);

                if (string.IsNullOrWhiteSpace(source))
                {
                    skipped++;
                    errors.Add($"No accessible file for mapping {series.PreferredTitle} S{mapping.Coords.Season}E{mapping.Coords.Episode}");
                    continue;
                }

                int fileId = mapping.Video.ID;

                string seriesPath = Path.Combine(rootPath, seriesFolder);
                string rootDirKey = $"/{importFolderSafe}/{rootFolderName}";
                string seriesDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}";
                if (!dryRun)
                {
                    Directory.CreateDirectory(seriesPath);
                }
                else
                {
                    if (reportedDirs.Add(rootDirKey))
                        report.AppendLine(rootDirKey);
                    if (reportedDirs.Add(seriesDirKey))
                        report.AppendLine(seriesDirKey);
                }

                bool isSpecial = SpecialSeasons.TryGetValue(mapping.Coords.Season, out var specialInfo);
                string seasonFolder = SanitizeName(isSpecial ? specialInfo.Folder : $"Season {mapping.Coords.Season}");
                string seasonPath = Path.Combine(seriesPath, seasonFolder);
                string seasonDirKey = $"/{importFolderSafe}/{rootFolderName}/{seriesFolder}/{seasonFolder}";
                if (!dryRun)
                    Directory.CreateDirectory(seasonPath);
                else if (reportedDirs.Add(seasonDirKey))
                    report.AppendLine(seasonDirKey);

                string extension = Path.GetExtension(source) ?? string.Empty;
                string fileName = isSpecial
                    ? BuildSpecialFileName(mapping, specialInfo, epPad, extension, titles.DisplayTitle, fileId)
                    : BuildStandardFileName(mapping, epPad, extension, fileId);

                fileName = SanitizeName(fileName);
                string destPath = Path.Combine(seasonPath, fileName);
                string destBase = Path.GetFileNameWithoutExtension(destPath);
                string sourceDir = Path.GetDirectoryName(source) ?? string.Empty;

                if (dryRun)
                {
                    planned++;
                    report.AppendLine($"/{importFolderSafe}/{rootFolderName}/{seriesFolder}/{seasonFolder}/{fileName} <- {source}");
                    LinkMetadata(sourceDir, seasonPath, reportedDirs, dryRun, report);
                    LinkSubtitles(source, sourceDir, destBase, seasonPath, reportedDirs, dryRun, report, ref planned, ref skipped, errors);
                }
                else
                {
                    if (TryCreateLink(source, destPath))
                    {
                        created++;
                        planned++;
                        LinkMetadata(sourceDir, seasonPath, reportedDirs, dryRun, report);
                        LinkSubtitles(source, sourceDir, destBase, seasonPath, reportedDirs, dryRun, report, ref planned, ref skipped, errors);
                    }
                    else
                    {
                        skipped++;
                        errors.Add($"Failed to link {source} -> {destPath}");
                    }
                }
            }

            return (created, skipped, errors, planned);
        }

        private string BuildSpecialFileName(MapHelper.FileMapping mapping, (string Folder, string Prefix) specialInfo, int pad, string extension, string displaySeriesTitle, int fileId)
        {
            string epPart = mapping.Coords.Episode.ToString($"D{pad}");
            string part = mapping.PartCount > 1 && mapping.PartIndex.HasValue ? $"-pt{mapping.PartIndex.Value}" : string.Empty;

            string epTitle = TextHelper.ResolveEpisodeTitle(mapping.PrimaryEpisode, displaySeriesTitle);
            epTitle = SanitizeName(epTitle);
            string fileIdPart = $"[{fileId}]";

            return $"{fileIdPart} {epTitle} - {epPart}{part}{extension}";
        }

        private string BuildStandardFileName(MapHelper.FileMapping mapping, int pad, string extension, int fileId)
        {
            string epPart = $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{pad}")}";
            if (mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode.Value != mapping.Coords.Episode)
            {
                epPart += $"-{mapping.Coords.EndEpisode.Value.ToString($"D{pad}")}";
            }

            if (mapping.PartCount > 1 && mapping.PartIndex.HasValue)
            {
                epPart += $"-pt{mapping.PartIndex.Value}";
            }

            string fileIdPart = $"[{fileId}]";
            return $"{fileIdPart} - {epPart}{extension}";
        }

        private string? ResolveImportRootPath(Shoko.Plugin.Abstractions.DataModels.IVideoFile location)
        {
            string path = location.Path;
            if (string.IsNullOrWhiteSpace(path)) return null;

            string normalizedPath = NormalizeSeparators(path);
            string relative = location.RelativePath?.TrimStart('/', '\\') ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(relative))
            {
                string normalizedRel = NormalizeSeparators(relative);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (normalizedPath.EndsWith(normalizedRel, comparison))
                {
                    string root = normalizedPath.Substring(0, normalizedPath.Length - normalizedRel.Length)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.IsNullOrWhiteSpace(root))
                        return root;
                }
            }

            string? dir = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeSeparators(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private string? ResolveSourcePath(Shoko.Plugin.Abstractions.DataModels.IVideoFile location, string importRoot)
        {
            string original = location.Path;
            if (!string.IsNullOrWhiteSpace(original) && File.Exists(original))
                return original;

            string relative = location.RelativePath?.TrimStart('/', '\\') ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(relative))
            {
                string candidate = Path.Combine(importRoot, NormalizeSeparators(relative));
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private void LinkMetadata(string sourceDir, string destDir, HashSet<string> reportedDirs, bool dryRun, StringBuilder report)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return;

            string dirKey = $"meta::{sourceDir}->{destDir}";
            if (!reportedDirs.Add(dirKey)) return;

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                string ext = Path.GetExtension(file);
                if (!MetadataExtensions.Contains(ext)) continue;

                string name = Path.GetFileName(file);
                string destPath = Path.Combine(destDir, name);

                if (dryRun)
                {
                    report.AppendLine($"META {destPath} <- {file}");
                    continue;
                }

                TryCreateLink(file, destPath);
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
                    string? importRoot = ResolveImportRootPath(location);
                    if (string.IsNullOrWhiteSpace(importRoot)) continue;

                    string seriesPath = Path.Combine(importRoot, rootFolderName, seriesFolder);
                    if (!seriesPaths.Add(seriesPath)) continue;

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

        private void LinkSubtitles(string sourceFile, string sourceDir, string destBaseName, string destDir, HashSet<string> reportedDirs, bool dryRun, StringBuilder report, ref int planned, ref int skipped, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return;

            string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
            foreach (var sub in Directory.EnumerateFiles(sourceDir))
            {
                string ext = Path.GetExtension(sub);
                if (!SubtitleExtensions.Contains(ext)) continue;

                string name = Path.GetFileName(sub);
                if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase)) continue;

                string suffix = name.Substring(originalBase.Length);
                string destName = destBaseName + suffix;
                string destPath = Path.Combine(destDir, destName);

                if (dryRun)
                {
                    planned++;
                    report.AppendLine($"SUB {destPath} <- {sub}");
                    continue;
                }

                if (TryCreateLink(sub, destPath))
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

        private string ResolveRootFolderName()
        {
            string configured = ShokoRelay.Settings.VfsRootPath;
            if (string.IsNullOrWhiteSpace(configured))
                configured = DefaultRootName;

            configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Path.IsPathRooted(configured))
            {
                configured = Path.GetFileName(configured);
            }

            if (string.IsNullOrWhiteSpace(configured))
                configured = DefaultRootName;

            configured = SanitizeName(configured);

            return string.IsNullOrWhiteSpace(configured) ? DefaultRootName : configured;
        }

        private static bool TryCreateLink(string source, string dest)
        {
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to remove existing link at {Dest}", dest);
                return false;
            }

            string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
            string relativeTarget = string.IsNullOrWhiteSpace(linkDir)
                ? source
                : Path.GetRelativePath(linkDir, source);

            if (OperatingSystem.IsWindows())
            {
                // Prefer symlink for clarity; fall back to hardlink if symlink creation is blocked
                if (TryCreateSymlink(dest, relativeTarget)) return true;
                if (TryCreateHardLink(dest, source)) return true;
            }
            else
            {
                if (TryCreateSymlink(dest, relativeTarget)) return true;
                if (TryCreateHardLink(dest, source)) return true;
            }

            return false;
        }

        private static bool TryCreateSymlink(string linkPath, string target)
        {
            try
            {
                var info = File.CreateSymbolicLink(linkPath, target);
                return info.Exists;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Symlink creation failed for {Link}", linkPath);
                return false;
            }
        }

        private static bool TryCreateHardLink(string linkPath, string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows Kernel32 CreateHardLink
                    if (CreateHardLinkW(linkPath, target, IntPtr.Zero))
                        return File.Exists(linkPath);
                    return false;
                }

                // POSIX link()
                int res = link(target, linkPath);
                return res == 0 && File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Hardlink creation failed for {Link}", linkPath);
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);

        private static bool IsSafeToDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? ' ' : c);
            }

            string cleaned = sb.ToString();
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            cleaned = cleaned.Trim().TrimEnd('.');

            return cleaned.Length == 0 ? "Unknown" : cleaned;
        }
    }
}
