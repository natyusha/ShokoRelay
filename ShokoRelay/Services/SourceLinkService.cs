using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Services;

#region Interface & Models

/// <summary>Result returned by the source link processing operation.</summary>
/// <param name="Count">Number of links created or purged.</param>
/// <param name="IsPurge">True if the operation was a purge rather than a map.</param>
/// <param name="Details">List of specific paths that were mapped.</param>
public record SourceLinkResult(int Count, bool IsPurge, List<string> Details);

#endregion

/// <summary>Automates the creation of relative symlinks from source folders to library locations based on a mapping file provided via API.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="logger">Logger instance.</param>
public class SourceLinkService(IVideoService videoService, ILogger<SourceLinkService> logger)
{
    #region Public API

    /// <summary>Scans all import roots for the specified mapping file and processes pending entries, or purges existing links.</summary>
    /// <param name="mapFile">The relative path to the mapping file.</param>
    /// <param name="purgeLinks">If true, removes all symlinks and generated _attach folders in the import roots.</param>
    /// <returns>A result object detailing the operation's outcome.</returns>
    public async Task<SourceLinkResult> ProcessLinksAsync(string mapFile, bool purgeLinks = false)
    {
        var managedFolders = videoService.GetAllManagedFolders() ?? [];
        var roots = managedFolders.Select(mf => mf.Path).Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct(VfsShared.PathComparer).ToList();
        int count = 0;
        var details = new List<string>();

        if (purgeLinks)
        {
            // Use the centralized ignored folder set for purge safety
            var protectedFolders = VfsShared.GetIgnoredFolderNames(Settings);
            foreach (var root in roots)
                count += PurgeDirectoryLinks(root!, protectedFolders, details, logger);

            logger.LogInformation("SourceLinkService: Finished purge operation -> {Count} links removed.", count);
            return new SourceLinkResult(count, true, details);
        }

        if (string.IsNullOrWhiteSpace(mapFile))
            return new SourceLinkResult(0, false, details);

        string safeMapFile = VfsShared.NormalizeSeparators(mapFile).TrimStart(Path.DirectorySeparatorChar);
        if (!safeMapFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return new SourceLinkResult(0, false, details);

        foreach (var mf in managedFolders)
        {
            string root = mf.Path;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            string fullRoot = Path.GetFullPath(root);
            string fullRootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            string fullTxtPath = Path.GetFullPath(Path.Combine(fullRoot, safeMapFile));
            if (!fullTxtPath.StartsWith(fullRootWithSep, VfsShared.PathComparison) || !File.Exists(fullTxtPath))
                continue;

            string mappingFileDir = Path.GetDirectoryName(fullTxtPath)!;
            string[] lines = await File.ReadAllLinesAsync(fullTxtPath).ConfigureAwait(false);
            bool modified = false;
            var dirsToScan = new HashSet<string>(VfsShared.PathComparer);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                    continue;

                var pipeParts = line.Split('|');
                if (pipeParts.Length < 2)
                    continue;

                var srcInfo = ExtractPathAndTags(pipeParts[0]);
                var destInfo = ExtractPathAndTags(pipeParts[1]);

                // Process a primary video file and all associated sidecar files/folders, renaming sidecars to match the destination naming convention
                try
                {
                    string fullSrc = Path.GetFullPath(Path.Combine(mappingFileDir, srcInfo.Path));
                    string fullDest = Path.GetFullPath(Path.Combine(fullRoot, destInfo.Path));

                    if (!fullDest.StartsWith(fullRootWithSep, VfsShared.PathComparison))
                    {
                        logger.LogWarning("SourceLinkService: Destination path escapes import root -> {Path}", fullDest);
                        continue;
                    }

                    if (!File.Exists(fullSrc))
                    {
                        logger.LogWarning("SourceLinkService: Source file not found -> {Path}", fullSrc);
                        continue;
                    }

                    string srcDir = Path.GetDirectoryName(fullSrc)!;
                    string destDir = Path.GetDirectoryName(fullDest)!;
                    string srcBase = Path.GetFileNameWithoutExtension(fullSrc);
                    string destBase = Path.GetFileNameWithoutExtension(fullDest);

                    string tagSuffix = srcInfo.Tags.Count > 0 ? " " + string.Join(" ", srcInfo.Tags.Select(t => $"[{t}]")) : string.Empty;
                    string finalDestBase = destBase + tagSuffix;

                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    var candidates = Directory.EnumerateFileSystemEntries(srcDir, srcBase + "*").ToList();
                    bool mainLinked = false;
                    var cmp = VfsShared.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

                    foreach (var entry in candidates)
                    {
                        string name = Path.GetFileName(entry);
                        bool isDir = Directory.Exists(entry);
                        bool isAttachDir = isDir && name.StartsWith(srcBase, cmp) && PlexConstants.LocalMediaAssets.AttachmentFolderSuffixes.Contains(name[srcBase.Length..]);

                        // Logic: Filter for the primary video, any file starting with the base name, or the designated attachments folder
                        if (!name.Equals(Path.GetFileName(fullSrc), cmp) && (isDir || !name.StartsWith(srcBase, cmp)) && !isAttachDir)
                            continue;

                        string suffix = isDir ? "_attach" : name[srcBase.Length..];
                        string targetPath = Path.Combine(destDir, finalDestBase + suffix);

                        if (isDir)
                        {
                            if (File.Exists(targetPath))
                                File.Delete(targetPath);
                            Directory.CreateDirectory(targetPath);
                            foreach (var subFile in Directory.EnumerateFiles(entry))
                                VfsShared.TryCreateLink(subFile, Path.Combine(targetPath, Path.GetFileName(subFile)), logger);
                        }
                        else if (VfsShared.TryCreateLink(entry, targetPath, logger))
                        {
                            if (videoService.IsAllowedVideoExtension(targetPath))
                                dirsToScan.Add(destDir);

                            if (name.Equals(Path.GetFileName(fullSrc), cmp))
                                mainLinked = true;
                        }
                    }

                    if (mainLinked)
                    {
                        lines[i] = "#" + lines[i];
                        modified = true;
                        count++;
                        string logMsg = $"{srcInfo.Path} -> {destInfo.Path}";
                        details.Add(logMsg);
                        logger.LogDebug("SourceLinkService: Created link -> {Link}", logMsg);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SourceLinkService: SourceLink failed for source -> {Path}", srcInfo.Path);
                }
            }

            if (modified)
                await File.WriteAllLinesAsync(fullTxtPath, lines).ConfigureAwait(false);

            foreach (var dir in dirsToScan)
            {
                try
                {
                    await videoService.NotifyVideoFileChangeDetected(dir).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SourceLinkService: Failed to notify directory change for {Dir}", dir);
                }
            }
        }

        logger.LogInformation("SourceLinkService: Finished mapping operation -> {Count} links created.", count);
        return new SourceLinkResult(count, false, details);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Recursively removes symlinks and _attach folders from a directory, skipping protected system folders.</summary>
    /// <param name="path">The directory path to scan.</param>
    /// <param name="protectedFolders">A set of folder names to exclude from the purge.</param>
    /// <param name="details">A list to record the paths of purged items.</param>
    /// <param name="logger">Logger instance for tracing and warnings.</param>
    /// <returns>The number of items deleted.</returns>
    private static int PurgeDirectoryLinks(string path, HashSet<string> protectedFolders, List<string> details, ILogger logger)
    {
        int deleted = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                try
                {
                    var name = Path.GetFileName(entry);
                    if (protectedFolders.Contains(name))
                        continue;

                    var attr = File.GetAttributes(entry);
                    if (attr.HasFlag(FileAttributes.ReparsePoint))
                    {
                        if (Directory.Exists(entry))
                            Directory.Delete(entry);
                        else
                            File.Delete(entry);

                        details.Add(entry);
                        logger.LogDebug("SourceLinkService: Purged link -> {Entry}", entry);
                        deleted++;
                    }
                    else if (Directory.Exists(entry))
                    {
                        // Specifically target the sidecar attachment folders created by the plugin
                        if (name.EndsWith("_attach", StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(entry, true);
                            details.Add(entry);
                            logger.LogDebug("SourceLinkService: Purged attachment folder -> {Entry}", entry);
                            deleted++;
                        }
                        else
                            deleted += PurgeDirectoryLinks(entry, protectedFolders, details, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SourceLinkService: Failed to purge entry -> {Entry}", entry);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "SourceLinkService: Purge failed for -> {Path}", path);
        }
        return deleted;
    }

    /// <summary>Isolates paths and tags within segments by splitting on semicolons first, then stripping quotes and normalizing slashes.</summary>
    /// <param name="rawSegment">The raw string segment from the pipe-delimited file.</param>
    /// <returns>A tuple containing the cleaned relative path and a list of extracted tags.</returns>
    private static (string Path, List<string> Tags) ExtractPathAndTags(string rawSegment) =>
        rawSegment.Split(';') is var parts
            ? (
                VfsShared.NormalizeSeparators(parts[0].Trim().Trim('"')).TrimStart(Path.DirectorySeparatorChar),
                parts.Length > 2 ? [.. parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : []
            )
            : (string.Empty, []);

    #endregion
}
