using System.Collections.Concurrent;
using System.Collections.Frozen;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Handles the discovery and linking of local media assets (posters, themes) and non-Shoko Plex extras.</summary>
/// <param name="videoService">Shoko video service used to verify if files are managed by the database.</param>
public class VfsAssetLinker(IVideoService videoService)
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <summary>Combined set of recognized file extensions for series-level local metadata and artwork.</summary>
    private static readonly FrozenSet<string> s_seriesMetadataExtensions = PlexConstants
        .LocalMediaAssets.Artwork.Keys.Concat(PlexConstants.LocalMediaAssets.SeriesMetadata)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combined set of recognized file extensions for episode-level local metadata, subtitles, and sidecars.</summary>
    private static readonly FrozenSet<string> s_episodeMetadataExtensions = PlexConstants
        .LocalMediaAssets.Artwork.Keys.Concat(PlexConstants.LocalMediaAssets.EpisodeMetadata)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Asset Linking

    /// <summary>Links show-level metadata into the series VFS directory, excluding files that are identified as episode-level sidecars.</summary>
    /// <param name="sourceDir">The physical directory containing the assets.</param>
    /// <param name="destDir">The target VFS series directory.</param>
    /// <param name="videoBaseNames">A set of base names for video files to exclude from series-level linking.</param>
    /// <param name="cache">Build-session cache for directory enumeration results.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    public void LinkSeriesMetadata(
        string sourceDir,
        string destDir,
        HashSet<string> videoBaseNames,
        ConcurrentDictionary<string, Lazy<string[]>> cache,
        Action<string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
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
            string destName = baseName.Equals("Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name;
            if (VfsShared.TryCreateLink(file, Path.Combine(destDir, destName), s_logger, skipExistenceCheck: skipExistenceCheck))
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
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
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
        Action<string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;

        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(f => s_episodeMetadataExtensions.Contains(Path.GetExtension(f)))])).Value;
        var mappings = Settings.Advanced.SubtitleLanguageMappings;

        // Fast-path for unmapped sidecars: bypasses target checks, sorting, and tuple allocations
        if (mappings is not { Count: > 0 })
        {
            foreach (var sub in candidates)
            {
                string name = Path.GetFileName(sub);
                if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase) || (name.Length > originalBase.Length && char.IsLetterOrDigit(name[originalBase.Length])))
                    continue;

                string destName = destBase + name[originalBase.Length..];
                if (VfsShared.TryCreateLink(sub, Path.Combine(destDir, destName), s_logger, skipExistenceCheck: skipExistenceCheck))
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
            return;
        }

        var pendingLinks = new List<(string Source, string Name, int Priority)>();
        foreach (var sub in candidates)
        {
            string name = Path.GetFileName(sub);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase) || (name.Length > originalBase.Length && char.IsLetterOrDigit(name[originalBase.Length])) || !File.Exists(sub))
                continue;

            string ext = Path.GetExtension(sub);
            string suffix = name[originalBase.Length..];
            var (mappedSuffix, priority) = suffix.StartsWith('.') && PlexConstants.LocalMediaAssets.SubtitleExtensions.Contains(ext) ? RenameSubtitleSuffix(suffix, mappings) : (suffix, -1);

            pendingLinks.Add((sub, destBase + mappedSuffix, priority));
        }

        var linkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, name, priority) in pendingLinks.OrderBy(l => l.Priority).ThenBy(l => l.Source, StringComparer.Ordinal))
        {
            // Unchanged originals precede conversions; converted collisions follow mapping order.
            if (priority >= 0 && linkedNames.Contains(name))
                continue;

            string destName = name;
            bool linked = VfsShared.TryCreateLink(source, Path.Combine(destDir, destName), s_logger, skipExistenceCheck: skipExistenceCheck);

            if (!linked && priority >= 0)
            {
                destName = destBase + Path.GetFileName(source)[originalBase.Length..];
                s_logger.Warn("VFS: Subtitle conversion failed -> {Name}; keeping original suffix -> {OriginalName}", name, destName);
                linked = !linkedNames.Contains(destName) && VfsShared.TryCreateLink(source, Path.Combine(destDir, destName), s_logger);
            }

            if (linked)
            {
                linkedNames.Add(destName);
                planned++;
                created++;
                onLink?.Invoke(destName, source);
            }
            else
            {
                skipped++;
                errors.Add($"Metadata sidecar link failed: {source}");
            }
        }
    }

    /// <summary>Replaces language tokens once, preserving flags and ranking collisions by the earliest mapping used.</summary>
    /// <param name="suffix">Original subtitle suffix including leading dot and extension.</param>
    /// <param name="mappings">Ordered dictionary of token replacements.</param>
    /// <returns>A tuple of the renamed suffix and the priority rank.</returns>
    private static (string Suffix, int Priority) RenameSubtitleSuffix(string suffix, OrderedDictionary<string, string> mappings)
    {
        var parts = suffix.Split('.');
        int priority = int.MaxValue;
        bool modified = false;

        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (PlexConstants.LocalMediaAssets.SubtitleModifiers.Contains(parts[i]))
                continue;

            for (int j = 0; j < mappings.Count; j++)
            {
                var mapping = mappings.GetAt(j);
                if (!parts[i].Equals(mapping.Key.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                string? replacement = mapping.Value?.Trim();
                // Reuse the existing filename sanitizer to reject unsafe replacements, without changing other settings.
                if (!string.IsNullOrEmpty(replacement) && !replacement.Contains('.') && VfsHelper.SanitizeName(replacement) == replacement)
                {
                    parts[i] = replacement;
                    priority = Math.Min(priority, j);
                    modified = true;
                }
                break;
            }
        }
        return modified ? (string.Join('.', parts), priority) : (suffix, -1);
    }

    /// <summary>Discovers and links physical files matching Plex Local Extra conventions that are not managed by Shoko.</summary>
    /// <param name="fileData">Mapping data for the current series.</param>
    /// <param name="vfsSeriesPaths">The target VFS series root paths to link the discovered files into.</param>
    /// <param name="videoBaseNames">A set of base names for indexed video files to prevent naming collisions.</param>
    /// <param name="epPad">The episode number padding used for naming consistency.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    public void LinkLocalExtras(
        MapHelper.SeriesFileData fileData,
        HashSet<string> vfsSeriesPaths,
        HashSet<string> videoBaseNames,
        int epPad,
        Action<string, string, string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
    {
        var sourceDirs = fileData.Mappings.SelectMany(m => m.Video.Files).Select(f => Path.GetDirectoryName(f.Path)).Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(VfsShared.PathComparer);
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

                foreach (var file in Directory.EnumerateFiles(subDir).Where(videoService.IsAllowedVideoExtension))
                {
                    if (videoService.GetVideoFileByAbsolutePath(file)?.Video?.CrossReferences?.Any(cr => cr.ShokoEpisode != null) == true)
                        continue;

                    foreach (var vfsSeriesPath in vfsSeriesPaths)
                    {
                        string destDir = string.IsNullOrEmpty(seasonFolder) ? Path.Combine(vfsSeriesPath, plexDirName) : Path.Combine(vfsSeriesPath, seasonFolder, plexDirName);
                        if (!Settings.Advanced.DisableVfsGeneration)
                            Directory.CreateDirectory(destDir);
                        if (VfsShared.TryCreateLink(file, Path.Combine(destDir, Path.GetFileName(file)), s_logger, skipExistenceCheck: skipExistenceCheck))
                            onLink?.Invoke(Path.GetDirectoryName(Path.GetDirectoryName(vfsSeriesPath))!, seasonFolder, Path.Combine(plexDirName, Path.GetFileName(file)), file);
                    }
                }
            }

            // Episode-Level Inline Extras
            foreach (var file in Directory.EnumerateFiles(srcDir!).Where(videoService.IsAllowedVideoExtension))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (videoBaseNames.Contains(name) || videoService.GetVideoFileByAbsolutePath(file)?.Video?.CrossReferences?.Any(cr => cr.ShokoEpisode != null) == true)
                    continue;
                string parentBase = PlexConstants.LocalExtraSuffixes.Select(s => name.Split(s)[0]).OrderByDescending(s => s.Length).FirstOrDefault(videoBaseNames.Contains) ?? string.Empty;
                if (string.IsNullOrEmpty(parentBase))
                    continue;

                foreach (var m in fileData.Mappings.Where(m => Path.GetFileNameWithoutExtension(m.FileName).Equals(parentBase, StringComparison.OrdinalIgnoreCase)))
                {
                    string seasonFolder = VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(m.Coords.Season));
                    string destName = Path.GetFileNameWithoutExtension(VfsHelper.BuildStandardFileName(m, epPad, "", m.Video.ID)) + name[parentBase.Length..] + Path.GetExtension(file);

                    foreach (var vfsSeriesPath in vfsSeriesPaths)
                    {
                        string vfsSeasonDir = Path.Combine(vfsSeriesPath, seasonFolder);
                        if (VfsShared.TryCreateLink(file, Path.Combine(vfsSeasonDir, destName), s_logger, skipExistenceCheck: skipExistenceCheck))
                            onLink?.Invoke(Path.GetDirectoryName(Path.GetDirectoryName(vfsSeriesPath))!, seasonFolder, destName, file);
                    }
                }
            }
        }
    }

    #endregion
}
