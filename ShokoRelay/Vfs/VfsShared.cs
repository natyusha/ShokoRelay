using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;

namespace ShokoRelay.Vfs;

/// <summary>Shared logic for VFS operations including symlink creation and concurrency management.</summary>
internal static class VfsShared
{
    #region Consts & Concurrency

    /// <summary>Global semaphore used to prevent concurrent structural VFS operations (Builds, Mapping, and MP3 generation).</summary>
    public static readonly SemaphoreSlim VfsLock = new(1, 1);

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

    /// <summary>Resolves the list of physical VFS series directories associated with a series across all import roots. Respects Primary ID overrides.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">Metadata service used for override resolution.</param>
    /// <returns>An enumerable of absolute directory paths.</returns>
    public static IEnumerable<string> ResolveSeriesVfsPaths(IShokoSeries series, IMetadataService metadataService)
    {
        var roots = new HashSet<string>(PathComparer);
        string rootName = ResolveRootFolderName();
        int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;

        var fileData = MapHelper.GetSeriesFileData(series, metadataService);
        foreach (var mapping in fileData.Mappings)
        {
            var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
            if (location == null || location.ManagedFolder == null || location.ManagedFolder.DropFolderType.HasFlag(DropFolderType.Source))
                continue;

            string? importRoot = ResolveImportRootPath(location);
            if (string.IsNullOrWhiteSpace(importRoot))
                continue;

            string seriesPath = Path.Combine(importRoot, rootName, folderId.ToString());
            roots.Add(seriesPath);
        }

        return roots;
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
    public static string ResolveRootFolderName() => ResolveFolderName(Settings.Advanced.VfsRootPath, ShokoRelayConstants.FolderVfsDefault);

    /// <summary>Resolves the anime themes folder name.</summary>
    public static string ResolveAnimeThemesFolderName() => ResolveFolderName(Settings.Advanced.AnimeThemesRootPath, ShokoRelayConstants.FolderAnimeThemesDefault);

    /// <summary>Resolves the collection posters folder name.</summary>
    public static string ResolveCollectionImagesFolderName() => ResolveFolderName(Settings.Advanced.CollectionImagesRootPath, ShokoRelayConstants.FolderCollectionImagesDefault);

    /// <summary>Assembles a unique set of folder names that should be ignored by VFS and Link operations based on current settings.</summary>
    /// <param name="settings">The current relay configuration.</param>
    /// <returns>A HashSet of folder names.</returns>
    public static HashSet<string> GetIgnoredFolderNames(RelayConfig settings)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ResolveRootFolderName(), ResolveAnimeThemesFolderName(), ResolveCollectionImagesFolderName() };
        foreach (var folder in settings.Advanced.FolderExclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ignored.Add(folder);

        // If Plex Local Extras are enabled, automatically include the standard Plex extra subdirectories in the ignore list.
        // This prevents Shoko from attempting to index these files, removing the need for manual user intervention for show and season-level extras.
        if (settings.Advanced.PlexLocalExtras)
            foreach (var extraDir in PlexConstants.LocalExtraDirs)
                ignored.Add(extraDir);

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
            logger.Warn(ex, "VFS: Unable to remove existing link at -> {Dest}", dest);
            return false;
        }
        return TryCreateSymlink(dest, relativeTarget, logger);
    }

    /// <summary>Internal wrapper around the OS file APIs to create standard filesystem relative symbolic links.</summary>
    /// <param name="linkPath">The target location where the symlink should be created.</param>
    /// <param name="target">The relative or absolute target destination of the link.</param>
    /// <param name="logger">Logger reference.</param>
    /// <returns>True if the symlink was created successfully; otherwise false.</returns>
    private static bool TryCreateSymlink(string linkPath, string target, Logger logger)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var info = File.CreateSymbolicLink(linkPath, target);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 20)
                logger.Debug("VFS: Symlink created -> '{Link}' in {Elapsed}ms", linkPath, sw.ElapsedMilliseconds); // only log slow operations, to avoid spamming the logs
            return info.Exists;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "VFS: Symlink creation failed -> {Link}", linkPath);
            return false;
        }
    }

    #endregion

    #region Validate & Normalize

    /// <summary>Normalizes directory separators to the current platform's standard.</summary>
    /// <param name="path">The filesystem path to normalize.</param>
    /// <returns>A path string utilizing platform-specific directory separators.</returns>
    public static string NormalizeSeparators(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>Checks if a path is safe to delete by ensuring it is not a filesystem root.</summary>
    /// <param name="path">The absolute path to evaluate.</param>
    /// <returns><c>true</c> if the path is not a root directory and is safe for recursive deletion.</returns>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Determines if any segment of a path or the file itself should be ignored based on current settings.</summary>
    /// <param name="path">The absolute or relative path to evaluate.</param>
    /// <param name="ignoredNames">Optional pre-computed set of ignored folder names for performance.</param>
    /// <returns>True if any segment of the path or the filename matches an ignore rule.</returns>
    public static bool IsPathIgnored(string path, HashSet<string>? ignoredNames = null)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var names = ignoredNames ?? GetIgnoredFolderNames(Settings);
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var seg in segments)
            if (names.Contains(seg) || (Settings.Advanced.PlexLocalExtras && TextHelper.MatchLocalExtraDir(seg).Success))
                return true;

        var fileName = Path.GetFileName(path);
        var baseName = Path.GetFileNameWithoutExtension(path);

        return names.Contains(fileName) || (Settings.Advanced.PlexLocalExtras && (TextHelper.MatchLocalExtraDir(fileName).Success || TextHelper.MatchLocalExtraFile(baseName).Success));
    }

    #endregion
}

#region VFS Ignore Rule

/// <summary>Automatically ignores Shoko Relay's internal VFS and local asset directories during Shoko's import scans.</summary>
public class VfsIgnoreRule : IManagedFolderIgnoreRule
{
    /// <inheritdoc/>
    public string Name => "Shoko Relay Ignore Rule";

    /// <inheritdoc/>
    public bool ShouldIgnore(IManagedFolder folder, FileSystemInfo fileSystemInfo) => VfsShared.IsPathIgnored(fileSystemInfo.FullName);
}

#endregion
