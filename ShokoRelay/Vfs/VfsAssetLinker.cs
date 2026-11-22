using System.Collections.Concurrent;
using System.Collections.Frozen;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Handles the discovery and linking of local media assets (posters, themes) and non-Shoko Plex extras.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="logger">Logger instance.</param>
public class VfsAssetLinker(IVideoService videoService, ILogger<VfsAssetLinker> logger)
{
    #region Setup

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
    /// <param name="destinationDir">The target VFS series directory.</param>
    /// <param name="videoBaseNames">A set of base names for video files to exclude from series-level linking.</param>
    /// <param name="cache">Build-session cache for directory enumeration results.</param>
    /// <param name="planned">Reference to the planned links counter.</param>
    /// <param name="created">Reference to the successful links created counter.</param>
    /// <param name="expectedFiles">Optional set of VFS destination paths already claimed for this build.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    public void LinkSeriesMetadata(
        string sourceDir,
        string destinationDir,
        HashSet<string> videoBaseNames,
        ConcurrentDictionary<string, Lazy<string[]>> cache,
        ref int planned,
        ref int created,
        HashSet<string>? expectedFiles = null,
        Action<string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(file => s_seriesMetadataExtensions.Contains(Path.GetExtension(file)))])).Value;
        foreach (var file in candidates)
        {
            string name = Path.GetFileName(file);
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (videoBaseNames.Contains(baseName))
                continue;
            string destName = baseName.Equals("Specials", StringComparison.OrdinalIgnoreCase) ? "Season-Specials-Poster" + Path.GetExtension(name) : name;
            string destPath = Path.Combine(destinationDir, destName);
            if (expectedFiles?.Contains(destPath) == true)
                continue;
            if (VfsShared.TryCreateLink(file, destPath, logger, skipExistenceCheck: skipExistenceCheck))
            {
                planned++;
                created++;
                onLink?.Invoke(destName, file);
            }
        }
    }

    /// <summary>Links and renames episode-level metadata into the season VFS directory.</summary>
    /// <param name="sourceFile">Path to the original video file used for base name matching.</param>
    /// <param name="sourceDir">The physical directory containing the sidecars.</param>
    /// <param name="destinationBase">The new base filename in the VFS.</param>
    /// <param name="destinationDir">The target VFS season directory.</param>
    /// <param name="cache">Build-session cache for file enumeration results.</param>
    /// <param name="dirCache">Build-session cache for directory enumeration results.</param>
    /// <param name="planned">Reference to the planned links counter.</param>
    /// <param name="skipped">Reference to the skipped links counter.</param>
    /// <param name="errors">List of encountered error messages.</param>
    /// <param name="created">Reference to the successful links created counter.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    public void LinkEpisodeMetadata(
        string sourceFile,
        string sourceDir,
        string destinationBase,
        string destinationDir,
        ConcurrentDictionary<string, Lazy<string[]>> cache,
        ConcurrentDictionary<string, Lazy<string[]>> dirCache,
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
        var candidates = cache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateFiles(dir).Where(file => s_episodeMetadataExtensions.Contains(Path.GetExtension(file)))])).Value;
        var mappings = Settings.Advanced.SubtitleLanguageMappings;
        bool requireLangCode = Settings.Advanced.RequireSubtitleLanguageCodes;

        // Fast-path for unmapped sidecars: bypasses target checks, sorting, and tuple allocations
        if (mappings is not { Count: > 0 })
        {
            foreach (var sidecarFile in candidates)
            {
                string name = Path.GetFileName(sidecarFile);
                if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase) || (name.Length > originalBase.Length && char.IsLetterOrDigit(name[originalBase.Length])))
                    continue;

                string suffix = name[originalBase.Length..];
                if (requireLangCode && PlexConstants.LocalMediaAssets.SubtitleExtensions.Contains(Path.GetExtension(sidecarFile)) && !HasSubtitleLanguageCode(suffix))
                    continue;

                string destName = destinationBase + suffix;
                if (VfsShared.TryCreateLink(sidecarFile, Path.Combine(destinationDir, destName), logger, skipExistenceCheck: skipExistenceCheck))
                {
                    planned++;
                    created++;
                    onLink?.Invoke(destName, sidecarFile);
                }
                else
                {
                    skipped++;
                    errors.Add($"Metadata sidecar link failed: {sidecarFile}");
                }
            }
            LinkAttachmentFolder(sourceDir, originalBase, destinationDir, destinationBase, dirCache, ref planned, ref skipped, errors, ref created, logger, onLink, skipExistenceCheck);
            return;
        }

        var pendingLinks = new List<(string Source, string Name, int Priority)>();
        foreach (var sidecarFile in candidates)
        {
            string name = Path.GetFileName(sidecarFile);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase) || (name.Length > originalBase.Length && char.IsLetterOrDigit(name[originalBase.Length])))
                continue;

            string extension = Path.GetExtension(sidecarFile);
            string suffix = name[originalBase.Length..];

            // Exclude unavailable subtitle sources before they can win a destination collision
            if (PlexConstants.LocalMediaAssets.SubtitleExtensions.Contains(extension))
            {
                if (!File.Exists(sidecarFile))
                    continue;
                if (requireLangCode && !HasSubtitleLanguageCode(suffix))
                    continue;
            }

            var (mappedSuffix, priority) = PlexConstants.LocalMediaAssets.SubtitleExtensions.Contains(extension) ? RenameSubtitleSuffix(suffix, mappings) : (suffix, -1);

            pendingLinks.Add((sidecarFile, destinationBase + mappedSuffix, priority));
        }

        var linkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, name, priority) in pendingLinks.OrderBy(link => link.Priority).ThenBy(link => link.Source, StringComparer.Ordinal))
        {
            // Unchanged originals precede conversions; converted collisions follow mapping order.
            if (priority >= 0 && linkedNames.Contains(name))
                continue;

            string destName = name;
            bool linked = VfsShared.TryCreateLink(source, Path.Combine(destinationDir, destName), logger, skipExistenceCheck: skipExistenceCheck);

            if (!linked && priority >= 0)
            {
                destName = destinationBase + Path.GetFileName(source)[originalBase.Length..];
                logger.LogWarning("VFS: Subtitle conversion failed -> {Name}; keeping original suffix -> {OriginalName}", name, destName);
                linked = !linkedNames.Contains(destName) && VfsShared.TryCreateLink(source, Path.Combine(destinationDir, destName), logger);
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

        LinkAttachmentFolder(sourceDir, originalBase, destinationDir, destinationBase, dirCache, ref planned, ref skipped, errors, ref created, logger, onLink, skipExistenceCheck);
    }

    /// <summary>Links episode-level attachment directories into the VFS.</summary>
    /// <param name="sourceDir">The physical directory containing the attachment folder.</param>
    /// <param name="originalBase">Original video file base name.</param>
    /// <param name="destinationDir">The target VFS season or movie directory.</param>
    /// <param name="destinationBase">The new base filename in the VFS.</param>
    /// <param name="dirCache">Build-session cache for directory enumeration results.</param>
    /// <param name="planned">Reference to the planned links counter.</param>
    /// <param name="skipped">Reference to the skipped links counter.</param>
    /// <param name="errors">List of encountered error messages.</param>
    /// <param name="created">Reference to the successful links created counter.</param>
    /// <param name="logger">The logger to use for logging messages.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    private static void LinkAttachmentFolder(
        string sourceDir,
        string originalBase,
        string destinationDir,
        string destinationBase,
        ConcurrentDictionary<string, Lazy<string[]>> dirCache,
        ref int planned,
        ref int skipped,
        List<string> errors,
        ref int created,
        ILogger logger,
        Action<string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
    {
        var subDirs = dirCache.GetOrAdd(sourceDir, dir => new Lazy<string[]>(() => [.. Directory.EnumerateDirectories(dir)])).Value;
        string? attachSourceDir = subDirs.FirstOrDefault(dir =>
            Path.GetFileName(dir) is var name
            && name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase)
            && PlexConstants.LocalMediaAssets.AttachmentFolderSuffixes.Contains(name[originalBase.Length..])
        );

        if (attachSourceDir == null)
            return;

        string destAttachDir = Path.Combine(destinationDir, destinationBase + "_attach");
        if (!Settings.Advanced.DisableVfsGeneration)
            Directory.CreateDirectory(destAttachDir);

        foreach (var file in Directory.EnumerateFiles(attachSourceDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(attachSourceDir, file);
            string destFile = Path.Combine(destAttachDir, relativePath);
            string? subDir = Path.GetDirectoryName(destFile);

            if (!Settings.Advanced.DisableVfsGeneration && !string.IsNullOrEmpty(subDir))
                Directory.CreateDirectory(subDir);

            if (VfsShared.TryCreateLink(file, destFile, logger, skipExistenceCheck: skipExistenceCheck))
            {
                planned++;
                created++;
                onLink?.Invoke(Path.Combine(destinationBase + "_attach", relativePath), file);
            }
            else
            {
                skipped++;
                errors.Add($"Attachment link failed: {file}");
            }
        }
    }

    /// <summary>Discovers and links physical files matching Plex Local Extra conventions that are not managed by Shoko.</summary>
    /// <param name="fileData">Mapping data for the current series.</param>
    /// <param name="vfsSeriesPaths">The target VFS series root paths to link the discovered files into.</param>
    /// <param name="videoBaseNames">A set of base names for indexed video files to prevent naming collisions.</param>
    /// <param name="episodePad">The episode number padding used for naming consistency.</param>
    /// <param name="session">Active build session context containing cached recognized video paths.</param>
    /// <param name="onLink">Optional callback to record the created link for the VFS Browser blueprint.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    public void LinkLocalExtras(
        MapHelper.SeriesFileData fileData,
        HashSet<string> vfsSeriesPaths,
        HashSet<string> videoBaseNames,
        int episodePad,
        VfsBuildSession session,
        Action<string, string, string, string?>? onLink = null,
        bool skipExistenceCheck = false
    )
    {
        var sourceDirs = fileData
            .Mappings.SelectMany(mapping => mapping.Video.Files)
            .Select(file => Path.GetDirectoryName(file.Path))
            .Where(dir => !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            .Distinct(VfsShared.PathComparer);
        foreach (var sourceDir in sourceDirs)
        {
            // Show and Season-Level Extras (Subdirectory pattern matching)
            foreach (var subDir in Directory.EnumerateDirectories(sourceDir!))
            {
                var match = VfsHelper.MatchLocalExtraDir(Path.GetFileName(subDir));
                if (!match.Success)
                    continue;

                var (type, seasonNum) = (match.Groups[1].Value, match.Groups[3].Value);
                int parsedSeason = 0;
                bool hasSeason = !string.IsNullOrEmpty(seasonNum);
                if (hasSeason && !int.TryParse(seasonNum, out parsedSeason))
                    continue;

                string plexDirName = PlexConstants.LocalExtraDirs.First(dirName => string.Equals(dirName, type, StringComparison.OrdinalIgnoreCase));
                string seasonFolder = hasSeason ? VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(parsedSeason)) : "";

                foreach (var file in Directory.EnumerateFiles(subDir).Where(videoService.IsAllowedVideoExtension))
                {
                    if (session.KnownRecognizedVideoPaths.Contains(file))
                        continue;

                    foreach (var vfsSeriesPath in vfsSeriesPaths)
                    {
                        string destinationDir = string.IsNullOrEmpty(seasonFolder) ? Path.Combine(vfsSeriesPath, plexDirName) : Path.Combine(vfsSeriesPath, seasonFolder, plexDirName);
                        if (!Settings.Advanced.DisableVfsGeneration)
                            Directory.CreateDirectory(destinationDir);
                        if (VfsShared.TryCreateLink(file, Path.Combine(destinationDir, Path.GetFileName(file)), logger, skipExistenceCheck: skipExistenceCheck))
                            onLink?.Invoke(Path.GetDirectoryName(Path.GetDirectoryName(vfsSeriesPath))!, seasonFolder, Path.Combine(plexDirName, Path.GetFileName(file)), file);
                    }
                }
            }

            // Episode-Level Inline Extras
            foreach (var file in Directory.EnumerateFiles(sourceDir!).Where(videoService.IsAllowedVideoExtension))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (videoBaseNames.Contains(name) || session.KnownRecognizedVideoPaths.Contains(file))
                    continue;

                if (!VfsHelper.IsLocalExtraFile(name, out int suffixIndex) || suffixIndex <= 0)
                    continue;

                string parentBase = name[..suffixIndex];
                if (!videoBaseNames.Contains(parentBase))
                    continue;

                foreach (var mapping in fileData.Mappings.Where(map => Path.GetFileNameWithoutExtension(map.FileName).Equals(parentBase, StringComparison.OrdinalIgnoreCase)))
                {
                    string seasonFolder = VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(mapping.Coords.Season));
                    string destName = Path.GetFileNameWithoutExtension(VfsHelper.BuildStandardFileName(mapping, episodePad, "", mapping.Video.LocalID)) + name[parentBase.Length..] + Path.GetExtension(file);

                    foreach (var vfsSeriesPath in vfsSeriesPaths)
                    {
                        string vfsSeasonDir = Path.Combine(vfsSeriesPath, seasonFolder);
                        if (!Settings.Advanced.DisableVfsGeneration)
                            Directory.CreateDirectory(vfsSeasonDir);
                        if (VfsShared.TryCreateLink(file, Path.Combine(vfsSeasonDir, destName), logger, skipExistenceCheck: skipExistenceCheck))
                            onLink?.Invoke(Path.GetDirectoryName(Path.GetDirectoryName(vfsSeriesPath))!, seasonFolder, destName, file);
                    }
                }
            }
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Checks if a subtitle suffix contains a valid language code token.</summary>
    /// <param name="suffix">The filename suffix (e.g. .en.forced.srt).</param>
    /// <returns>True if a non-modifier language token is present.</returns>
    private static bool HasSubtitleLanguageCode(string suffix)
    {
        var parts = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int partIndex = 0; partIndex < parts.Length - 1; partIndex++)
            if (!PlexConstants.LocalMediaAssets.SubtitleModifiers.Contains(parts[partIndex]))
                return true;
        return false;
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

        for (int partIndex = 1; partIndex < parts.Length - 1; partIndex++)
        {
            if (PlexConstants.LocalMediaAssets.SubtitleModifiers.Contains(parts[partIndex]))
                continue;

            for (int mapIndex = 0; mapIndex < mappings.Count; mapIndex++)
            {
                var mapping = mappings.GetAt(mapIndex);
                if (!parts[partIndex].Equals(mapping.Key.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                string? replacement = mapping.Value?.Trim();
                // Reuse the existing filename sanitizer to reject unsafe replacements, without changing other settings.
                if (!string.IsNullOrEmpty(replacement) && !replacement.Contains('.') && VfsHelper.SanitizeName(replacement) == replacement)
                {
                    parts[partIndex] = replacement;
                    priority = Math.Min(priority, mapIndex);
                    modified = true;
                }
                break;
            }
        }
        return modified ? (string.Join('.', parts), priority) : (suffix, -1);
    }

    #endregion
}
