using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Services;

#region Data Models

/// <summary>Result returned by the source link processing operation.</summary>
/// <param name="Count">Number of links created or purged.</param>
/// <param name="IsPurge">True if the operation was a purge rather than a map.</param>
/// <param name="Details">List of specific paths that were mapped.</param>
public record SourceLinkResult(int Count, bool IsPurge, List<string> Details);

#endregion

/// <summary>Automates the creation of relative symlinks from source folders to library locations based on a mapping file provided via API.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="logger">Logger instance.</param>
public class SourceLinkService(IVideoService videoService, IFileSystem fileSystem, ILogger<SourceLinkService> logger)
{
    #region Public API

    /// <summary>Scans all import roots for the specified mapping file and processes pending entries, or purges existing links.</summary>
    /// <param name="mapFile">The relative path to the mapping file.</param>
    /// <param name="purgeLinks">If true, removes all symlinks and generated _attach folders in the import roots.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A result object detailing the operation's outcome.</returns>
    public async Task<SourceLinkResult> ProcessLinksAsync(string mapFile, bool purgeLinks = false, CancellationToken cancellationToken = default)
    {
        var managedFolders = videoService.GetAllManagedFolders() ?? [];
        var roots = VfsShared.GetManagedFolderPaths(videoService, requireExists: true, fileSystem: fileSystem);
        int count = 0;
        var details = new List<string>();

        if (purgeLinks)
        {
            // Use the centralized ignored folder set for purge safety
            var protectedFolders = VfsShared.GetIgnoredFolderNames(Settings);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count += PurgeDirectoryLinks(root, protectedFolders, details);
            }

            logger.LogInformation("SourceLinkService: Finished purge operation -> {Count} links removed.", count);
            return new SourceLinkResult(count, true, details);
        }

        if (string.IsNullOrWhiteSpace(mapFile))
            return new SourceLinkResult(0, false, details);

        string safeMapFile = VfsShared.NormalizeSeparators(mapFile).TrimStart(Path.DirectorySeparatorChar);
        if (!safeMapFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return new SourceLinkResult(0, false, details);

        foreach (var managedFolder in managedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = managedFolder.Path;
            if (string.IsNullOrWhiteSpace(root) || !fileSystem.DirectoryExists(root))
                continue;

            string fullRoot = Path.GetFullPath(root);
            string fullTxtPath = Path.GetFullPath(Path.Combine(fullRoot, safeMapFile));
            if (!VfsShared.IsPathUnderDirectory(fullTxtPath, fullRoot) || !fileSystem.FileExists(fullTxtPath))
                continue;

            string mappingFileDir = Path.GetDirectoryName(fullTxtPath)!;
            var linesList = new List<string>();
            await using (var txtStream = fileSystem.OpenFile(fullTxtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var txtReader = new StreamReader(txtStream))
                while (await txtReader.ReadLineAsync().ConfigureAwait(false) is { } txtLine)
                    linesList.Add(txtLine);

            string[] lines = [.. linesList];
            bool modified = false;
            var dirsToScan = new HashSet<string>(VfsShared.PathComparer);

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[lineIndex].Trim();
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                    continue;

                var pipeParts = line.Split('|');
                if (pipeParts.Length < 2)
                    continue;

                var sourceInfo = ExtractPathAndTags(pipeParts[0]);
                var destinationInfo = ExtractPathAndTags(pipeParts[1]);

                // Process a primary video file and all associated sidecar files/folders, renaming sidecars to match the destination naming convention
                try
                {
                    string fullSource = Path.GetFullPath(Path.Combine(mappingFileDir, sourceInfo.Path));
                    string fullDestination = Path.GetFullPath(Path.Combine(fullRoot, destinationInfo.Path));

                    if (!VfsShared.IsPathUnderDirectory(fullDestination, fullRoot))
                    {
                        logger.LogWarning("SourceLinkService: Destination path escapes import root -> {Path}", fullDestination);
                        continue;
                    }

                    if (!fileSystem.FileExists(fullSource))
                    {
                        logger.LogWarning("SourceLinkService: Source file not found -> {Path}", fullSource);
                        continue;
                    }

                    string sourceDir = Path.GetDirectoryName(fullSource)!;
                    string destinationDir = Path.GetDirectoryName(fullDestination)!;
                    string sourceBase = Path.GetFileNameWithoutExtension(fullSource);
                    string destinationBase = Path.GetFileNameWithoutExtension(fullDestination);

                    string tagSuffix = sourceInfo.Tags.Count > 0 ? " " + string.Join(" ", sourceInfo.Tags.Select(tag => $"[{tag}]")) : string.Empty;
                    string finalDestinationBase = destinationBase + tagSuffix;

                    if (!string.IsNullOrEmpty(destinationDir))
                        fileSystem.CreateDirectory(destinationDir);

                    bool mainLinked = false;
                    var comparison = VfsShared.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

                    foreach (var entry in fileSystem.GetFileSystemEntries(sourceDir))
                    {
                        string name = entry.Name;
                        bool isDirectory = entry.IsDirectory;
                        bool isAttachmentDir = isDirectory && name.StartsWith(sourceBase, comparison) && PlexConstants.LocalMediaAssets.AttachmentFolderSuffixes.Contains(name[sourceBase.Length..]);
                        bool isSidecarFile = !isDirectory && name.StartsWith(sourceBase, comparison) && (name.Length == sourceBase.Length || !char.IsLetterOrDigit(name[sourceBase.Length]));

                        // Logic: Filter for the primary video, any file starting with the base name (guarded against prefix collisions), or the designated attachments folder
                        if (!name.Equals(Path.GetFileName(fullSource), comparison) && !isSidecarFile && !isAttachmentDir)
                            continue;

                        string suffix = isDirectory ? "_attach" : name[sourceBase.Length..];
                        string targetPath = Path.Combine(destinationDir, finalDestinationBase + suffix);

                        if (isDirectory)
                        {
                            if (fileSystem.FileExists(targetPath))
                                fileSystem.DeleteFile(targetPath);
                            fileSystem.CreateDirectory(targetPath);
                            foreach (var subFile in fileSystem.GetFilePaths(entry.Path))
                                VfsShared.TryCreateLink(subFile, Path.Combine(targetPath, Path.GetFileName(subFile)), logger, fileSystem, checkVfsDisabled: false);
                        }
                        else if (VfsShared.TryCreateLink(entry.Path, targetPath, logger, fileSystem, checkVfsDisabled: false))
                        {
                            if (videoService.IsAllowedVideoExtension(targetPath))
                                dirsToScan.Add(destinationDir);

                            if (name.Equals(Path.GetFileName(fullSource), comparison))
                                mainLinked = true;
                        }
                    }

                    if (mainLinked)
                    {
                        lines[lineIndex] = "#" + lines[lineIndex];
                        modified = true;
                        count++;
                        string logMessage = $"{sourceInfo.Path} -> {destinationInfo.Path}";
                        details.Add(logMessage);
                        logger.LogDebug("SourceLinkService: Created link -> {Link}", logMessage);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "SourceLinkService: SourceLink failed for source -> {Path}", sourceInfo.Path);
                }
            }

            if (modified)
                await VfsShared.WriteLinesAtomicAsync(fullTxtPath, lines, fileSystem: fileSystem).ConfigureAwait(false);

            foreach (var directory in dirsToScan)
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await videoService.NotifyVideoFileChangeDetected(directory).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "SourceLinkService: Failed to notify directory change for {Directory}", directory);
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
    /// <returns>The number of items deleted.</returns>
    private int PurgeDirectoryLinks(string path, HashSet<string> protectedFolders, List<string> details)
    {
        int deleted = 0;
        try
        {
            foreach (var entry in fileSystem.GetFileSystemEntries(path))
                try
                {
                    if (protectedFolders.Contains(entry.Name))
                        continue;

                    if (entry.IsSymbolicLink)
                    {
                        VfsShared.DeleteSymbolicLink(fileSystem, entry.Path);
                        details.Add(entry.Path);
                        logger.LogDebug("SourceLinkService: Purged link -> {Entry}", entry.Path);
                        deleted++;
                    }
                    else if (entry.IsDirectory)
                    {
                        // Specifically target the sidecar attachment folders created by the plugin
                        if (entry.Name.EndsWith("_attach", StringComparison.OrdinalIgnoreCase))
                        {
                            fileSystem.DeleteDirectory(entry.Path, true);
                            details.Add(entry.Path);
                            logger.LogDebug("SourceLinkService: Purged attachment folder -> {Entry}", entry.Path);
                            deleted++;
                        }
                        else
                            deleted += PurgeDirectoryLinks(entry.Path, protectedFolders, details);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "SourceLinkService: Failed to purge entry -> {Entry}", entry.Path);
                }
        }
        catch (Exception exception)
        {
            logger.LogTrace(exception, "SourceLinkService: Purge failed for -> {Path}", path);
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
