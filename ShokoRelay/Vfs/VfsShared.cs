using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video;

namespace ShokoRelay.Vfs;

internal static class VfsShared
{
    private const string DefaultVfsRootName = "!ShokoRelayVFS";
    private const string DefaultCollectionPostersRootName = "!CollectionPosters";

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

    public static string NormalizeSeparators(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

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

    public static bool TryCreateLink(string source, string dest, Logger logger, string? targetOverride = null, bool useRelativeTarget = true)
    {
        try
        {
            if (File.Exists(dest))
                File.Delete(dest);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Unable to remove existing link at {Dest}", dest);
            return false;
        }

        string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);

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

    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }
}
