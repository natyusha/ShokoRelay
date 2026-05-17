using System.Collections.Concurrent;
using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Vfs;

/// <summary>Handles the discovery and linking of local media assets (posters, themes) and non-Shoko Plex extras.</summary>
/// <param name="videoService">Shoko video service used to verify if files are managed by the database.</param>
public class VfsAssetLinker(IVideoService videoService)
{
    #region Fields

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
    public void LinkSeriesMetadata(string sourceDir, string destDir, HashSet<string> videoBaseNames, ConcurrentDictionary<string, Lazy<string[]>> cache)
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
            VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), s_logger);
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
    public void LinkEpisodeMetadata(
        string sourceFile,
        string sourceDir,
        string destBase,
        string destDir,
        ConcurrentDictionary<string, Lazy<string[]>> cache,
        ref int planned,
        ref int skipped,
        List<string> errors,
        ref int created
    )
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.GetFiles(sourceDir).Where(f => s_episodeMetadataExtensions.Contains(Path.GetExtension(f)))])).Value;
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

    /// <summary>Discovers and links physical files matching Plex Local Extra conventions that are not managed by Shoko.</summary>
    /// <param name="fileData">Mapping data for the current series.</param>
    /// <param name="vfsSeriesPath">The target VFS series root path.</param>
    /// <param name="videoBaseNames">A set of base names for indexed video files to prevent naming collisions.</param>
    public void LinkLocalExtras(MapHelper.SeriesFileData fileData, string vfsSeriesPath, HashSet<string> videoBaseNames)
    {
        var sourceDirs = fileData
            .Mappings.SelectMany(m => m.Video.Files)
            .Select(f => Path.GetDirectoryName(f.Path))
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Distinct(VfsShared.PathComparer)
            .ToList();
        foreach (var srcDir in sourceDirs)
        {
            // A. Show and Season-Level Extras (Subdirectory pattern matching)
            foreach (var subDir in Directory.EnumerateDirectories(srcDir!))
            {
                var match = TextHelper.MatchLocalExtraDir(Path.GetFileName(subDir));
                if (!match.Success)
                    continue;

                string type = match.Groups[1].Value;
                string seasonNum = match.Groups[3].Value;

                // Ensure standard Plex casing for the VFS folder (e.g., "trailers" -> "Trailers")
                string plexDirName = PlexConstants.LocalExtraDirs.First(d => string.Equals(d, type, StringComparison.OrdinalIgnoreCase));

                string destDir = string.IsNullOrEmpty(seasonNum)
                    ? Path.Combine(vfsSeriesPath, plexDirName)
                    : Path.Combine(vfsSeriesPath, VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(int.Parse(seasonNum))), plexDirName);

                foreach (var file in Directory.EnumerateFiles(subDir).Where(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f))))
                {
                    if (videoService.GetVideoFileByAbsolutePath(file) != null)
                        continue;
                    Directory.CreateDirectory(destDir);
                    VfsShared.TryCreateLink(file, Path.Combine(destDir, Path.GetFileName(file)), s_logger);
                }
            }
            foreach (var file in Directory.EnumerateFiles(srcDir!).Where(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f))))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (videoBaseNames.Contains(name) || videoService.GetVideoFileByAbsolutePath(file) != null)
                    continue;
                string parentBase = PlexConstants.LocalExtraSuffixes.Select(s => name.Split(s)[0]).OrderByDescending(s => s.Length).FirstOrDefault(videoBaseNames.Contains) ?? string.Empty;
                if (string.IsNullOrEmpty(parentBase))
                    continue;
                foreach (var m in fileData.Mappings.Where(m => Path.GetFileNameWithoutExtension(m.FileName).Equals(parentBase, StringComparison.OrdinalIgnoreCase)))
                {
                    string vfsSeasonDir = Path.Combine(vfsSeriesPath, VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(m.Coords.Season)));
                    string vfsParentName = Path.GetFileNameWithoutExtension(VfsHelper.BuildStandardFileName(m, 2, "", m.Video.ID));
                    VfsShared.TryCreateLink(file, Path.Combine(vfsSeasonDir, vfsParentName + name[parentBase.Length..] + Path.GetExtension(file)), s_logger);
                }
            }
        }
    }

    #endregion
}
