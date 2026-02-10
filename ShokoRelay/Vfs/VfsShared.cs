using System.Runtime.InteropServices;
using NLog;
using Shoko.Plugin.Abstractions.DataModels;

namespace ShokoRelay.Vfs;

internal static class VfsShared
{
    private const string DefaultRootName = "!ShokoRelayVFS";
    private const string DefaultCollectionPostersName = "!CollectionPosters";

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
            configured = DefaultRootName;

        configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Path.IsPathRooted(configured))
            configured = Path.GetFileName(configured);

        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultRootName;

        configured = VfsHelper.SanitizeName(configured);
        return string.IsNullOrWhiteSpace(configured) ? DefaultRootName : configured;
    }

    public static string ResolveCollectionPostersFolderName()
    {
        string configured = ShokoRelay.Settings.CollectionPostersRootFolder;
        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultCollectionPostersName;

        configured = configured.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Path.IsPathRooted(configured))
            configured = Path.GetFileName(configured);

        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultCollectionPostersName;

        configured = VfsHelper.SanitizeName(configured);
        return string.IsNullOrWhiteSpace(configured) ? DefaultCollectionPostersName : configured;
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

        if (OperatingSystem.IsWindows())
        {
            if (TryCreateSymlink(dest, relativeTarget, logger))
                return true;
            if (TryCreateHardLink(dest, source, logger))
                return true;
        }
        else
        {
            if (TryCreateSymlink(dest, relativeTarget, logger))
                return true;
            if (TryCreateHardLink(dest, source, logger))
                return true;
        }

        return false;
    }

    private static bool TryCreateSymlink(string linkPath, string target, Logger logger)
    {
        try
        {
            var info = File.CreateSymbolicLink(linkPath, target);
            return info.Exists;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Symlink creation failed for {Link}", linkPath);
            return false;
        }
    }

    private static bool TryCreateHardLink(string linkPath, string target, Logger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(linkPath, target, IntPtr.Zero))
                    return File.Exists(linkPath);
                return false;
            }

            int res = link(target, linkPath);
            return res == 0 && File.Exists(linkPath);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Hardlink creation failed for {Link}", linkPath);
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}
