using System.Collections.Concurrent;
using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Vfs;

/// <summary>Handles the discovery and linking of local media assets (posters, themes) and non-Shoko Plex extras.</summary>
/// <param name="videoService">Shoko video service used to verify if files are managed by the database.</param>
public class VfsAssetLinker(IVideoService videoService)
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly HashSet<string> s_seriesMetadataExtensions = [.. PlexConstants.LocalMediaAssets.Artwork, .. PlexConstants.LocalMediaAssets.SeriesMetadata];
    private static readonly HashSet<string> s_episodeMetadataExtensions = [.. PlexConstants.LocalMediaAssets.Artwork, .. PlexConstants.LocalMediaAssets.EpisodeMetadata];

    #endregion

    #region Asset Linking

    /// <summary>Links show-level metadata into the series VFS directory, excluding files that are identified as episode-level sidecars.</summary>
    /// <param name="sourceDir">The physical directory containing the assets.</param>
    /// <param name="destDir">The target VFS series directory.</param>
    /// <param name="videoBaseNames">A set of base names for video files to exclude from series-level linking.</param>
    /// <param name="cache">Build-session cache for directory enumeration results.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    public void LinkSeriesMetadata(string sourceDir, string destDir, HashSet<string> videoBaseNames, ConcurrentDictionary<string, Lazy<string[]>> cache, Action<string, string?>? onLink = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(f => s_seriesMetadataExtensions.Contains(Path.GetExtension(f)))])).Value;
        foreach (var file in candidates)
        {
            string name = Path.GetFileName(file);
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (videoBaseNames.Contains(baseName))
                continue;
            string destName = string.Equals(baseName, "Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name;
            if (VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), s_logger))
                onLink?.Invoke(destName, file);
        }
    }

    /// <summary>Links and renames episode-level metadata into the season VFS directory.</summary>
    /// <param name="sourceFile">Path to the original video file used for base name matching.</param>
    /// <param name="sourceDir">The physical directory containing the sidecars.</param>
    /// <param name="destBase">The new base filename in the VFS.</param>
    /// <param name="destDir">The target VFS season directory.</param>
    /// <param name="cache">Build-session cache for directory enumeration results.</param>
    /// <param name="planned">Reference to the planned links counter.</param>
    /// <param name="skipped">Reference to the skipped links counter.</param>
    /// <param name="errors">List of encountered error messages.</param>
    /// <param name="created">Reference to the successful links created counter.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    public void LinkEpisodeMetadata(
        string sourceFile,
        string sourceDir,
        string destBase,
        string destDir,
        ConcurrentDictionary<string, Lazy<string[]>> cache,
        ref int planned,
        ref int skipped,
        List<string> errors,
        ref int created,
        Action<string, string?>? onLink = null
    )
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(f => s_episodeMetadataExtensions.Contains(Path.GetExtension(f)))])).Value;
        foreach (var sub in candidates)
        {
            string name = Path.GetFileName(sub);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase))
                continue;
            string destName = destBase + name[originalBase.Length..];
            if (VfsShared.TryCreateLink(sub, Path.Combine(destDir, destName), s_logger))
            {
                planned++;
                created++;
                onLink?.Invoke(destName, sub);
            }
            else
            {
                skipped++;
                errors.Add($"Metadata sidecar link failed: {sub}");
            }
        }
    }

    /// <summary>Discovers and links physical files matching Plex Local Extra conventions that are not managed by Shoko.</summary>
    /// <param name="fileData">Mapping data for the current series.</param>
    /// <param name="vfsSeriesPath">The target VFS series root path.</param>
    /// <param name="videoBaseNames">A set of base names for indexed video files to prevent naming collisions.</param>
    /// <param name="epPad">The episode number padding used for naming consistency.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    public void LinkLocalExtras(MapHelper.SeriesFileData fileData, string vfsSeriesPath, HashSet<string> videoBaseNames, int epPad, Action<string, string, string?>? onLink = null)
    {
        var sourceDirs = fileData
            .Mappings.SelectMany(m => m.Video.Files)
            .Select(f => Path.GetDirectoryName(f.Path))
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Distinct(VfsShared.PathComparer)
            .ToList();
        foreach (var srcDir in sourceDirs)
        {
            // Show and Season-Level Extras (Subdirectory pattern matching)
            foreach (var subDir in Directory.EnumerateDirectories(srcDir!))
            {
                var match = VfsHelper.MatchLocalExtraDir(Path.GetFileName(subDir));
                if (!match.Success)
                    continue;

                string type = match.Groups[1].Value,
                    seasonNum = match.Groups[3].Value;
                string plexDirName = PlexConstants.LocalExtraDirs.First(d => string.Equals(d, type, StringComparison.OrdinalIgnoreCase));
                string seasonFolder = string.IsNullOrEmpty(seasonNum) ? "" : VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(int.Parse(seasonNum)));
                string destDir = string.IsNullOrEmpty(seasonFolder) ? Path.Combine(vfsSeriesPath, plexDirName) : Path.Combine(vfsSeriesPath, seasonFolder, plexDirName);

                foreach (var file in Directory.EnumerateFiles(subDir).Where(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f))))
                {
                    if (videoService.GetVideoFileByAbsolutePath(file)?.Video?.CrossReferences?.Any(cr => cr.ShokoEpisode != null) == true)
                        continue;
                    Directory.CreateDirectory(destDir);
                    if (VfsShared.TryCreateLink(file, Path.Combine(destDir, Path.GetFileName(file)), s_logger))
                        onLink?.Invoke(seasonFolder, Path.Combine(plexDirName, Path.GetFileName(file)), file);
                }
            }

            // Episode-Level Inline Extras
            foreach (var file in Directory.EnumerateFiles(srcDir!).Where(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f))))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (videoBaseNames.Contains(name) || videoService.GetVideoFileByAbsolutePath(file)?.Video?.CrossReferences?.Any(cr => cr.ShokoEpisode != null) == true)
                    continue;
                string parentBase = PlexConstants.LocalExtraSuffixes.Select(s => name.Split(s)[0]).OrderByDescending(s => s.Length).FirstOrDefault(videoBaseNames.Contains) ?? string.Empty;
                if (string.IsNullOrEmpty(parentBase))
                    continue;
                foreach (var m in fileData.Mappings.Where(m => Path.GetFileNameWithoutExtension(m.FileName).Equals(parentBase, StringComparison.OrdinalIgnoreCase)))
                {
                    string seasonFolder = VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(m.Coords.Season)),
                        vfsSeasonDir = Path.Combine(vfsSeriesPath, seasonFolder);
                    string destName = Path.GetFileNameWithoutExtension(VfsHelper.BuildStandardFileName(m, epPad, "", m.Video.ID)) + name[parentBase.Length..] + Path.GetExtension(file);
                    if (VfsShared.TryCreateLink(file, Path.Combine(vfsSeasonDir, destName), s_logger))
                        onLink?.Invoke(seasonFolder, destName, file);
                }
            }
        }
    }

    #endregion
}
