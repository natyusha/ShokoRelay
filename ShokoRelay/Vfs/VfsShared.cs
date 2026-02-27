using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video;

namespace ShokoRelay.Vfs;

internal static class VfsShared
{
    private const string DefaultVfsRootName = "!ShokoRelayVFS";
    private const string DefaultCollectionPostersRootName = "!CollectionPosters";

    /// <summary>
    /// Determine the root import path for a video file based on its absolute and relative paths, falling back to the containing directory.
    /// </summary>
    public static string? ResolveImportRootPath(IVideoFile location)
    {
        string path = location.Path;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalizedPath = NormalizeSeparators(path);
        string relative = location.RelativePath?.TrimStart('/', '\\') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string normalizedRel = NormalizeSeparators(relative);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (normalizedPath.EndsWith(normalizedRel, comparison))
            {
                string root = normalizedPath.Substring(0, normalizedPath.Length - normalizedRel.Length).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(root))
                    return root;
            }
        }

        string? dir = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Normalize all directory separators in <paramref name="path"/> to the current platform's <see cref="Path.DirectorySeparatorChar"/>.
    /// </summary>
    public static string NormalizeSeparators(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Determine the name to use for the top‑level VFS root folder. Reads the <c>VfsRootPath</c> setting and sanitizes it; falls back to a default string if the configured value is empty or invalid.
    /// </summary>
    public static string ResolveRootFolderName()
    {
        string configured = ShokoRelay.Settings.VfsRootPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultVfsRootName;

        configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Path.IsPathRooted(configured))
            configured = Path.GetFileName(configured);

        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultVfsRootName;

        configured = VfsHelper.SanitizeName(configured);
        return string.IsNullOrWhiteSpace(configured) ? DefaultVfsRootName : configured;
    }

    /// <summary>
    /// Compute the folder name used for collection poster assets in the VFS. Sanitizes the <c>CollectionPostersRootPath</c> setting and falls back to a default if necessary.
    /// </summary>
    public static string ResolveCollectionPostersFolderName()
    {
        string configured = ShokoRelay.Settings.CollectionPostersRootPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultCollectionPostersRootName;

        configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Path.IsPathRooted(configured))
            configured = Path.GetFileName(configured);

        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultCollectionPostersRootName;

        configured = VfsHelper.SanitizeName(configured);
        return string.IsNullOrWhiteSpace(configured) ? DefaultCollectionPostersRootName : configured;
    }

    /// <summary>
    /// Attempt to create or update a symlink at <paramref name="dest"/> pointing to <paramref name="source"/>; an explicit <paramref name="targetOverride"/> may be supplied or a relative target calculated.
    /// Returns <c>true</c> on success.
    /// </summary>
    public static bool TryCreateLink(string source, string dest, Logger logger, string? targetOverride = null, bool useRelativeTarget = true)
    {
        string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);

        // if dest already exists and is a symlink pointing to exactly the same target string we intend to use, we can treat it as a success and avoid the expensive delete/recreate cycle.
        // This helps on slow filesystems where re-creating thousands of unchanged links would waste time.
        try
        {
            if (File.Exists(dest))
            {
                var attr = File.GetAttributes(dest);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    var fi = new FileInfo(dest);
                    // LinkTarget returns the string originally supplied when the link was created (relative or absolute). Compare verbatim so we don't attempt to rewrite an identical link.
                    if (string.Equals(fi.LinkTarget, relativeTarget, StringComparison.Ordinal))
                        return true;
                }
                // not the same target; fall through to delete below
                File.Delete(dest);
            }
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Unable to remove existing link at {Dest}", dest);
            return false;
        }

        if (TryCreateSymlink(dest, relativeTarget, logger))
            return true;

        return false;
    }

    private static bool TryCreateSymlink(string linkPath, string target, Logger logger)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var info = File.CreateSymbolicLink(linkPath, target);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 20)
                logger.Debug("Symlink creation for {Link} took {Elapsed}ms", linkPath, sw.ElapsedMilliseconds);
            return info.Exists;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Symlink creation failed for {Link}", linkPath);
            return false;
        }
    }

    /// <summary>
    /// Given a video <paramref name="location"/> and an <paramref name="importRoot"/>, attempt to resolve the existing source path if the original is missing.
    /// </summary>
    public static string? ResolveSourcePath(IVideoFile location, string importRoot)
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

    /// <summary>
    /// Determine whether deleting <paramref name="path"/> is safe, i.e. it is not a root directory. Helper used during clean operations to avoid removing the filesystem root by mistake.
    /// </summary>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }
}
