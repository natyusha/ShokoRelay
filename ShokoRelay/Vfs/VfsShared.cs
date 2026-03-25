using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video;
using ShokoRelay.Config;

namespace ShokoRelay.Vfs;

/// <summary>Shared logic for VFS operations including symlink creation.</summary>
internal static class VfsShared
{
    #region Constants & Props

    /// <summary>OS-aware path comparer.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    #endregion

    #region Path Resolution

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

    #endregion

    #region Folder Resolution

    /// <summary>Resolves a finalized folder name by sanitizing user input and falling back to a default value if necessary.</summary>
    /// <param name="configured">The raw folder name string obtained from the configuration settings.</param>
    /// <param name="defaultName">The hardcoded default name to use as a fallback if the configured value is invalid.</param>
    /// <returns>A sanitized folder name string safe for use in filesystem paths.</returns>
    private static string ResolveFolderName(string configured, string defaultName)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? defaultName : configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(name))
            name = Path.GetFileName(name);
        var sanitized = VfsHelper.SanitizeName(name);
        return (string.IsNullOrWhiteSpace(sanitized) || sanitized == "Unknown") ? defaultName : sanitized;
    }

    /// <summary>Resolves the VFS root folder name.</summary>
    public static string ResolveRootFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.VfsRootPath, ShokoRelayConstants.FolderVfsDefault);

    /// <summary>Resolves the anime themes folder name.</summary>
    public static string ResolveAnimeThemesFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.AnimeThemesRootPath, ShokoRelayConstants.FolderAnimeThemesDefault);

    /// <summary>Resolves the collection posters folder name.</summary>
    public static string ResolveCollectionPostersFolderName() => ResolveFolderName(ShokoRelay.Settings.Advanced.CollectionPostersRootPath, ShokoRelayConstants.FolderCollectionPostersDefault);

    /// <summary>Assembles a unique set of folder names that should be ignored by VFS and Link operations based on current settings.</summary>
    /// <param name="settings">The current relay configuration.</param>
    /// <returns>A HashSet of folder names.</returns>
    public static HashSet<string> GetIgnoredFolderNames(RelayConfig settings)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ResolveRootFolderName(), ResolveAnimeThemesFolderName(), ResolveCollectionPostersFolderName() };
        foreach (var folder in settings.Advanced.FolderExclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ignored.Add(folder);
        return ignored;
    }

    #endregion

    #region Symlink Operations

    /// <summary>Attempts to create a symlink.</summary>
    /// <param name="source">The physical source file.</param>
    /// <param name="dest">The destination link path.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="targetOverride">Optional specific target string.</param>
    /// <param name="useRelativeTarget">Whether to resolve the target path relatively.</param>
    /// <returns>True if the link exists and is correct, or was successfully created.</returns>
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

    #endregion

    #region Validate & Normalize

    /// <summary>Normalizes directory separators.</summary>
    public static string NormalizeSeparators(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>Checks if a path is safe to delete.</summary>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
