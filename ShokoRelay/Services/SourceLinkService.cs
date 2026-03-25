using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.Services;

/// <summary>Automates the creation of relative symlinks from source folders to library locations based on a mapping file provided via API.</summary>
/// <param name="videoService">Shoko video service for import root discovery.</param>
public class SourceLinkService(IVideoService videoService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Scans all import roots for the specified mapping file and processes pending entries, or purges existing links.</summary>
    /// <param name="mapFile">The relative path to the mapping file.</param>
    /// <param name="purgeLinks">If true, removes all symlinks and generated _attach folders in the import roots.</param>
    /// <returns>The number of links created or removed.</returns>
    public async Task<int> ProcessLinksAsync(string mapFile, bool purgeLinks = false)
    {
        var roots = (videoService.GetAllManagedFolders() ?? []).Select(mf => mf.Path).Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct().ToList();
        int count = 0;

        if (purgeLinks)
        {
            // Resolve all configured folder names that should be protected from the purge
            var protectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                VfsShared.ResolveRootFolderName(),
                VfsShared.ResolveAnimeThemesFolderName(),
                VfsShared.ResolveCollectionPostersFolderName(),
            };

            foreach (var root in roots)
                count += PurgeDirectoryLinks(root!, protectedFolders);
            return count;
        }

        if (string.IsNullOrWhiteSpace(mapFile))
            return 0;
        string normMapFile = mapFile.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        foreach (var root in roots)
        {
            string txtPath = Path.Combine(root!, normMapFile);
            if (!File.Exists(txtPath))
                continue;

            string mappingFileDir = Path.GetDirectoryName(txtPath)!;
            string[] lines = await File.ReadAllLinesAsync(txtPath);
            bool modified = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var pipeParts = line.Split('|');
                if (pipeParts.Length < 2)
                    continue;

                var srcInfo = ExtractPathAndTags(pipeParts[0]);
                var destInfo = ExtractPathAndTags(pipeParts[1]);

                if (ProcessFileGroup(root!, mappingFileDir, srcInfo.Path, destInfo.Path, srcInfo.Tags))
                {
                    lines[i] = "#" + lines[i];
                    modified = true;
                    count++;
                }
            }
            if (modified)
                await File.WriteAllLinesAsync(txtPath, lines);
        }
        return count;
    }

    /// <summary>Recursively removes symlinks and _attach folders from a directory, skipping protected system folders.</summary>
    /// <param name="path">The directory path to scan.</param>
    /// <param name="protectedFolders">A set of folder names to exclude from the purge.</param>
    /// <returns>The number of items deleted.</returns>
    private static int PurgeDirectoryLinks(string path, HashSet<string> protectedFolders)
    {
        int deleted = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
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
                    deleted++;
                }
                else if (Directory.Exists(entry))
                {
                    // Specifically target the sidecar attachment folders created by the plugin
                    if (name.EndsWith("_attach", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(entry, true);
                        deleted++;
                    }
                    else
                        deleted += PurgeDirectoryLinks(entry, protectedFolders);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Trace(ex, "Purge failed for {0}", path);
        }
        return deleted;
    }

    /// <summary>Processes a primary video file and all associated sidecar files/folders, renaming sidecars to match the destination naming convention.</summary>
    private bool ProcessFileGroup(string root, string mappingFileDir, string relSrc, string relDest, List<string> tags)
    {
        try
        {
            string fullSrc = Path.Combine(mappingFileDir, relSrc);
            string fullDest = Path.Combine(root, relDest);
            if (!File.Exists(fullSrc))
            {
                Logger.Warn("SourceLink: Source file not found: {0}", fullSrc);
                return false;
            }

            string srcDir = Path.GetDirectoryName(fullSrc)!;
            string destDir = Path.GetDirectoryName(fullDest)!;
            string srcBase = Path.GetFileNameWithoutExtension(fullSrc);
            string destBase = Path.GetFileNameWithoutExtension(fullDest);

            string tagSuffix = tags.Count > 0 ? " " + string.Join(" ", tags.Select(t => $"[{t}]")) : string.Empty;
            string finalDestBase = destBase + tagSuffix;

            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            var candidates = Directory.EnumerateFileSystemEntries(srcDir, srcBase + "*").ToList();
            bool mainLinked = false;

            foreach (var entry in candidates)
            {
                string name = Path.GetFileName(entry);
                bool isDir = Directory.Exists(entry);

                // Logic: Filter for the primary video, any file starting with the base name, or the designated attachments folder
                if (!name.Equals(Path.GetFileName(fullSrc)) && !(!isDir && name.StartsWith(srcBase)) && !(isDir && name.Equals(srcBase + "_attachments", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string suffix = name[srcBase.Length..];
                if (isDir)
                    suffix = "_attach";

                string targetPath = Path.Combine(destDir, finalDestBase + suffix);

                if (isDir)
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    Directory.CreateDirectory(targetPath);
                    foreach (var subFile in Directory.EnumerateFiles(entry))
                        VfsShared.TryCreateLink(subFile, Path.Combine(targetPath, Path.GetFileName(subFile)), Logger);
                }
                else if (VfsShared.TryCreateLink(entry, targetPath, Logger) && name.Equals(Path.GetFileName(fullSrc)))
                    mainLinked = true;
            }
            return mainLinked;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SourceLink failed for {0}", relSrc);
            return false;
        }
    }

    /// <summary>Isolates paths and tags within segments by splitting on semicolons first, then stripping quotes and normalizing slashes.</summary>
    /// <param name="rawSegment">The raw string segment from the pipe-delimited file.</param>
    /// <returns>A tuple containing the cleaned relative path and a list of extracted tags.</returns>
    private static (string Path, List<string> Tags) ExtractPathAndTags(string rawSegment)
    {
        var parts = rawSegment.Split(';');
        string path = parts[0].Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var tags = parts.Length > 2 ? [.. parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : (List<string>)[];
        return (path, tags);
    }
}
