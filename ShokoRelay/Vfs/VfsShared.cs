using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video;

namespace ShokoRelay.Vfs;

/// <summary>Shared logic for VFS operations including symlink creation.</summary>
internal static class VfsShared
{
    private const string DefaultVfsRootName = "!ShokoRelayVFS";
    private const string DefaultCollectionPostersRootName = "!CollectionPosters";
    private const string DefaultAnimeThemesRootName = "!AnimeThemes";

    /// <summary>OS-aware path comparer.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Determines the root import path for a video file.</summary>
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
            if (normalizedPath.EndsWith(normalizedRel, PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                string root = normalizedPath[..^normalizedRel.Length].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(root))
                    return root;
            }
        }
        string? dir = Path.GetDirectoryName(normalizedPath);
        return string.IsNullOrWhiteSpace(dir) ? null : dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Normalizes directory separators.</summary>
    public static string NormalizeSeparators(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>Resolves the VFS root folder name.</summary>
    public static string ResolveRootFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.VfsRootPath, DefaultVfsRootName);

    /// <summary>Resolves the collection posters folder name.</summary>
    public static string ResolveCollectionPostersFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.CollectionPostersRootPath, DefaultCollectionPostersRootName);

    /// <summary>Resolves the anime themes folder name.</summary>
    public static string ResolveAnimeThemesFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.AnimeThemesRootPath, DefaultAnimeThemesRootName);

    private static string ResolveFolderName(string configured, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(configured))
            configured = defaultName;
        configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(configured))
            configured = Path.GetFileName(configured);
        if (string.IsNullOrWhiteSpace(configured))
            configured = defaultName;
        configured = VfsHelper.SanitizeName(configured);
        return string.IsNullOrWhiteSpace(configured) ? defaultName : configured;
    }

    /// <summary>Attempts to create a symlink.</summary>
    public static bool TryCreateLink(string source, string dest, Logger logger, string? targetOverride = null, bool useRelativeTarget = true)
    {
        string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);
        try
        {
            if (File.Exists(dest))
            {
                var attr = File.GetAttributes(dest);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    var fi = new FileInfo(dest);
                    if (string.Equals(fi.LinkTarget, relativeTarget, StringComparison.Ordinal))
                        return true;
                }
                File.Delete(dest);
            }
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Unable to remove existing link at {Dest}", dest);
            return false;
        }
        return TryCreateSymlink(dest, relativeTarget, logger);
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

    /// <summary>Resolves the source file path.</summary>
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

    /// <summary>Checks if a path is safe to delete.</summary>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }
}
