using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Shared logic for VFS operations including symlink creation and concurrency management.</summary>
internal static class VfsShared
{
    #region Consts & Concurrency

    /// <summary>SIMD-accelerated search values for locating directory path separators rapidly.</summary>
    private static readonly SearchValues<char> s_pathSeparators = SearchValues.Create(['/', '\\']);

    /// <summary>Global semaphore used to prevent concurrent structural VFS operations (Builds, Mapping, and MP3 generation).</summary>
    public static readonly SemaphoreSlim VfsLock = new(1, 1);

    /// <summary>OS-aware path comparer.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>OS-aware string comparison type for paths.</summary>
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The absolute path to the VFS blueprint cache file.</summary>
    public static string BlueprintFilePath => Path.Combine(CacheDirectory, ShokoRelayConstants.FileVfsBlueprintCache);

    /// <summary>Monotonically incremented counter tracking blueprint saves for cache invalidation.</summary>
    private static long s_blueprintVersion;

    /// <summary>Gets the current monotonic blueprint save version.</summary>
    public static long BlueprintVersion => Volatile.Read(ref s_blueprintVersion);

    /// <summary>Cached ignored folder names and multi-segment path exclusions for a specific configuration instance.</summary>
    /// <param name="Settings">The configuration instance this cache entry was built from.</param>
    /// <param name="Names">Single-segment folder names to ignore.</param>
    /// <param name="MultiSegments">Multi-segment relative path exclusions normalized with platform separators.</param>
    private sealed record IgnoreCacheEntry(RelayConfig Settings, HashSet<string> Names, string[] MultiSegments);

    /// <summary>The cached ignored folder names and multi-segment path exclusions resolved during the last lookup.</summary>
    private static volatile IgnoreCacheEntry? s_ignoreCache;

    /// <summary>Exclusive lock used to ensure thread-safe ignored folder cache compilation.</summary>
    private static readonly Lock s_ignoreLock = new();

    #endregion

    #region Path Resolution

    /// <summary>Determines if a managed folder is eligible for VFS generation (i.e., not strictly a source folder and not excluded in settings).</summary>
    /// <param name="folder">The managed folder to evaluate.</param>
    /// <returns>True if the folder should have a VFS generated inside it; otherwise, false.</returns>
    public static bool IsVfsEnabledFolder(IManagedFolder? folder) =>
        folder != null
        && (!folder.DropFolderType.HasFlag(DropFolderType.Source) || folder.DropFolderType.HasFlag(DropFolderType.Destination))
        && !(Settings.Advanced.ManagedFolderExclusions?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []).Any(exclusion =>
            string.Equals(exclusion, folder.ID.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(exclusion, folder.Name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>Normalizes a directory path and trims trailing separators unless the path is a filesystem or drive root.</summary>
    /// <param name="path">The directory path to normalize.</param>
    /// <param name="fullPath">When <c>true</c>, resolves the path via <see cref="Path.GetFullPath(string)"/> before trimming.</param>
    /// <returns>The normalized directory path, or <c>null</c> if the input is null or whitespace.</returns>
    public static string? TrimRoot(string? path, bool fullPath = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string normalizedInput = NormalizeSeparators(fullPath ? Path.GetFullPath(path) : path);
        string? pathRoot = Path.GetPathRoot(normalizedInput);
        return !string.IsNullOrEmpty(pathRoot) && string.Equals(normalizedInput, pathRoot, PathComparison)
            ? normalizedInput
            : normalizedInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Determines the root import path for a video file.</summary>
    /// <param name="location">The video file location.</param>
    /// <returns>The resolved import root path, or <c>null</c> if unavailable.</returns>
    public static string? ResolveImportRootPath(IVideoFile location)
    {
        if (!string.IsNullOrWhiteSpace(location.ManagedFolder?.Path))
            return TrimRoot(location.ManagedFolder.Path);

        string path = location.Path;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string normalizedPath = NormalizeSeparators(path);
        string relative = location.RelativePath.TrimLeadingSlashes().Trim();
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string normalizedRelativePath = NormalizeSeparators(relative);
            if (normalizedPath.EndsWith(normalizedRelativePath, PathComparison))
                return TrimRoot(normalizedPath[..^normalizedRelativePath.Length]);
        }
        return TrimRoot(Path.GetDirectoryName(normalizedPath));
    }

    /// <summary>Resolves the source file path.</summary>
    /// <param name="location">The video file location.</param>
    /// <param name="importRoot">The resolved import root path.</param>
    /// <param name="fileSystem">Optional Shoko file system service for long-path-safe file checks.</param>
    /// <returns>The existing physical file path, or <c>null</c> if not found.</returns>
    public static string? ResolveSourcePath(IVideoFile location, string importRoot, IFileSystem? fileSystem = null)
    {
        bool FileExists(string candidate) => fileSystem?.FileExists(candidate) ?? File.Exists(candidate);
        string original = location.Path;
        if (!string.IsNullOrWhiteSpace(original) && FileExists(original))
            return original;
        string relative = location.RelativePath.TrimLeadingSlashes();
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string candidate = Path.Combine(importRoot, NormalizeSeparators(relative));
            if (FileExists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Resolves the list of physical VFS series directories associated with a series across all import roots. Respects Primary ID overrides and movie structures.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">Metadata service used for override resolution.</param>
    /// <returns>An enumerable of absolute directory paths.</returns>
    public static IEnumerable<string> ResolveSeriesVfsPaths(IShokoSeries series, IMetadataService metadataService)
    {
        var roots = new HashSet<string>(PathComparer);
        string rootName = ResolveRootFolderName();
        string movieRootName = ResolveMovieRootFolderName();
        int folderId = series.GetPrimaryId(metadataService);

        var (doStandard, doMovie) = MapHelper.GetGenerationModes(MapHelper.IsMovie(series), Settings.Advanced.MovieGenerationMode);

        var fileData = MapHelper.GetConsolidatedSeriesFileData(series, metadataService);
        var mainEpisodeIds = doMovie ? fileData.Mappings.Where(mapping => mapping.PrimaryEpisode.Type == EpisodeType.Episode).Select(mapping => mapping.PrimaryEpisode.ID.ID).Distinct().ToList() : [];

        foreach (var mapping in fileData.Mappings)
        {
            var location = mapping.Video.Files.FirstOrDefault(file => IsVfsEnabledFolder(file.ManagedFolder) && !string.IsNullOrWhiteSpace(file.Path));
            if (location == null)
                continue;

            string? importRoot = ResolveImportRootPath(location);
            if (string.IsNullOrWhiteSpace(importRoot))
                continue;

            if (doStandard)
                roots.Add(Path.Combine(importRoot, rootName, folderId.ToString()));

            if (doMovie)
                foreach (var episodeId in mainEpisodeIds)
                    roots.Add(Path.Combine(importRoot, movieRootName, episodeId.ToString()));
        }

        return roots;
    }

    /// <summary>Returns distinct, non-empty managed folder paths configured in Shoko.</summary>
    /// <param name="videoService">Shoko video service.</param>
    /// <param name="onlyVfsEnabled">When <c>true</c>, restricts results to folders eligible for VFS generation.</param>
    /// <param name="requireExists">When <c>true</c>, filters out folders that do not currently exist on disk.</param>
    /// <param name="fileSystem">Optional Shoko file system service for long-path-safe directory checks.</param>
    /// <returns>A list of managed folder paths.</returns>
    public static List<string> GetManagedFolderPaths(IVideoService videoService, bool onlyVfsEnabled = false, bool requireExists = false, IFileSystem? fileSystem = null) =>
        [
            .. (videoService.GetAllManagedFolders() ?? [])
                .Where(folder => !onlyVfsEnabled || IsVfsEnabledFolder(folder))
                .Select(folder => folder.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && (!requireExists || (fileSystem?.DirectoryExists(path) ?? Directory.Exists(path))))
                .Distinct(PathComparer),
        ];

    /// <summary>Ensures the given directory path ends with the platform directory separator.</summary>
    /// <param name="path">The directory path to normalize.</param>
    /// <returns>The path guaranteed to end with a directory separator.</returns>
    public static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>Checks whether a candidate path resolves inside a base directory without escaping it via traversal.</summary>
    /// <param name="candidatePath">The path to check.</param>
    /// <param name="baseDirectory">The allowed root directory.</param>
    /// <param name="allowEqual">When <c>true</c>, also permits the candidate path to match <paramref name="baseDirectory"/> itself.</param>
    /// <returns><c>true</c> if the candidate path resides inside the base directory; otherwise, <c>false</c>.</returns>
    public static bool IsPathUnderDirectory(string candidatePath, string baseDirectory, bool allowEqual = false)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        string fullBase = Path.GetFullPath(baseDirectory);
        string fullCandidate = Path.GetFullPath(candidatePath);
        string baseWithSeparator = EnsureTrailingSeparator(fullBase);
        return allowEqual
            ? string.Equals(fullCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison)
                || fullCandidate.StartsWith(baseWithSeparator, PathComparison)
            : fullCandidate.StartsWith(baseWithSeparator, PathComparison);
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
    /// <param name="settings">Optional configuration settings override.</param>
    /// <returns>The sanitized VFS root folder name.</returns>
    public static string ResolveRootFolderName(RelayConfig? settings = null) => ResolveFolderName((settings ?? Settings).Advanced.VfsRootPath, ShokoRelayConstants.FolderVfsDefault);

    /// <summary>Resolves the standalone Movies VFS root folder name.</summary>
    /// <param name="settings">Optional configuration settings override.</param>
    /// <returns>The sanitized standalone movies VFS root folder name.</returns>
    public static string ResolveMovieRootFolderName(RelayConfig? settings = null) => ResolveFolderName((settings ?? Settings).Advanced.MovieVfsRootPath, ShokoRelayConstants.FolderMoviesDefault);

    /// <summary>Resolves the anime themes folder name.</summary>
    /// <param name="settings">Optional configuration settings override.</param>
    /// <returns>The sanitized AnimeThemes folder name.</returns>
    public static string ResolveAnimeThemesFolderName(RelayConfig? settings = null) => ResolveFolderName((settings ?? Settings).Advanced.AnimeThemesRootPath, ShokoRelayConstants.FolderAnimeThemesDefault);

    /// <summary>Resolves the collection posters folder name.</summary>
    /// <param name="settings">Optional configuration settings override.</param>
    /// <returns>The sanitized collection images folder name.</returns>
    public static string ResolveCollectionImagesFolderName(RelayConfig? settings = null) =>
        ResolveFolderName((settings ?? Settings).Advanced.CollectionImagesRootPath, ShokoRelayConstants.FolderCollectionImagesDefault);

    /// <summary>Assembles a unique set of folder names that should be ignored by VFS and Link operations based on current settings.</summary>
    /// <param name="settings">The current relay configuration.</param>
    /// <returns>A HashSet of folder names.</returns>
    public static HashSet<string> GetIgnoredFolderNames(RelayConfig settings) => GetIgnoredCache(settings).Names;

    /// <summary>Builds and caches single-segment folder names and multi-segment relative exclusion paths for the specified settings instance.</summary>
    /// <param name="settings">The current relay configuration.</param>
    /// <returns>The cached <see cref="IgnoreCacheEntry"/>.</returns>
    private static IgnoreCacheEntry GetIgnoredCache(RelayConfig settings)
    {
        if (s_ignoreCache is { } cached && ReferenceEquals(cached.Settings, settings))
            return cached;

        lock (s_ignoreLock)
        {
            if (s_ignoreCache is { } rechecked && ReferenceEquals(rechecked.Settings, settings))
                return rechecked;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ResolveRootFolderName(settings),
                ResolveMovieRootFolderName(settings),
                ResolveAnimeThemesFolderName(settings),
                ResolveCollectionImagesFolderName(settings),
            };
            var multi = new List<string>();
            foreach (var folder in (settings.Advanced.FolderExclusions ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string trimmed = NormalizeSeparators(folder).TrimSlashes();
                if (trimmed.Contains(Path.DirectorySeparatorChar))
                    multi.Add(trimmed);
                else if (trimmed.Length > 0)
                    ignored.Add(trimmed);
            }

            // If Plex Local Extras are enabled, automatically include the standard Plex extra subdirectories in the ignore list.
            // This prevents Shoko from attempting to index these files, removing the need for manual user intervention for show and season-level extras.
            if (settings.Advanced.PlexLocalExtras)
                foreach (var extraDir in PlexConstants.LocalExtraDirs)
                    ignored.Add(extraDir);

            return s_ignoreCache = new IgnoreCacheEntry(settings, ignored, [.. multi]);
        }
    }

    #endregion

    #region Symlink Operations

    /// <summary>Attempts to create a symlink via <see cref="IFileSystem"/> so changes are hidden from Shoko's file watcher and long paths are supported.</summary>
    /// <param name="source">The physical source file.</param>
    /// <param name="destination">The destination link path.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="fileSystem">Optional Shoko file system service.</param>
    /// <param name="targetOverride">Optional specific target string.</param>
    /// <param name="useRelativeTarget">Whether to resolve the target path relatively.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    /// <param name="checkVfsDisabled">Whether to skip link creation if VFS generation is disabled in settings.</param>
    /// <returns>True if the link exists and is correct, or was successfully created.</returns>
    public static bool TryCreateLink(
        string source,
        string destination,
        ILogger logger,
        IFileSystem? fileSystem = null,
        string? targetOverride = null,
        bool useRelativeTarget = true,
        bool skipExistenceCheck = false,
        bool checkVfsDisabled = true
    )
    {
        if (checkVfsDisabled && Settings.Advanced.DisableVfsGeneration)
            return true;

        string linkDir = Path.GetDirectoryName(destination) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);

        if (!skipExistenceCheck)
        {
            try
            {
                if (fileSystem != null)
                {
                    var existingStat = fileSystem.GetStat(destination, followSymbolicLinks: false);
                    if (existingStat is SymbolicLinkStat symlinkStat)
                    {
                        if (string.Equals(NormalizeSeparators(symlinkStat.Target ?? string.Empty), NormalizeSeparators(relativeTarget), PathComparison))
                            return true;

                        if (symlinkStat.IsDirectoryLink == true)
                            fileSystem.DeleteDirectory(destination, recursive: false);
                        else
                            fileSystem.DeleteFile(destination);
                    }
                    else if (existingStat is DirectoryStat)
                        fileSystem.DeleteDirectory(destination, recursive: true);
                    else if (existingStat is not null)
                        fileSystem.DeleteFile(destination);
                }
                else
                {
                    var fileInfo = new FileInfo(destination);
                    if (fileInfo.Exists || fileInfo.LinkTarget != null)
                    {
                        if (string.Equals(NormalizeSeparators(fileInfo.LinkTarget ?? string.Empty), NormalizeSeparators(relativeTarget), PathComparison))
                            return true;

                        fileInfo.Delete();
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "VFS: Unable to remove existing link at -> {Destination}", destination);
                return false;
            }
        }

        // Internal wrapper around the OS/Shoko file APIs to create standard filesystem relative symbolic links.
        try
        {
            var stopwatch = Stopwatch.StartNew();
            if (fileSystem != null)
            {
                fileSystem.CreateSymbolicLink(relativeTarget, destination, isDirectory: false);
                stopwatch.Stop();
                if (stopwatch.ElapsedMilliseconds > 20)
                    logger.LogDebug("VFS: Symlink created -> '{Link}' in {Elapsed}ms", destination, stopwatch.ElapsedMilliseconds);
                return true;
            }

            var info = File.CreateSymbolicLink(destination, relativeTarget);
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds > 20)
                logger.LogDebug("VFS: Symlink created -> '{Link}' in {Elapsed}ms", destination, stopwatch.ElapsedMilliseconds); // only log slow operations, to avoid spamming the logs
            return info.Exists;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "VFS: Symlink creation failed -> {Link}", destination);
            return false;
        }
    }

    /// <summary>Deletes a symbolic link without following its target, handling Windows directory symlinks safely.</summary>
    /// <param name="fileSystem">Shoko file system service.</param>
    /// <param name="path">The symbolic link path to delete.</param>
    public static void DeleteSymbolicLink(IFileSystem fileSystem, string path)
    {
        if (fileSystem.GetStat(path, followSymbolicLinks: false) is SymbolicLinkStat { IsDirectoryLink: true })
            fileSystem.DeleteDirectory(path, recursive: false);
        else
            fileSystem.DeleteFile(path);
    }

    #endregion

    #region Validate & Normalize

    /// <summary>Normalizes directory separators to the current platform's standard.</summary>
    /// <param name="path">The filesystem path to normalize.</param>
    /// <returns>A path string utilizing platform-specific directory separators.</returns>
    public static string NormalizeSeparators(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>Trims leading directory separators (both '/' and '\\') from a path string in a null-safe manner.</summary>
    /// <param name="path">The path string to trim.</param>
    /// <returns>A string without leading directory separators, or an empty string if null.</returns>
    public static string TrimLeadingSlashes(this string? path) => path?.TrimStart('/', '\\') ?? string.Empty;

    /// <summary>Trims leading and trailing directory separators (both '/' and '\\') from a path string in a null-safe manner.</summary>
    /// <param name="path">The path string to trim.</param>
    /// <returns>A string without leading or trailing directory separators, or an empty string if null.</returns>
    public static string TrimSlashes(this string? path) => path?.Trim('/', '\\') ?? string.Empty;

    /// <summary>Checks if a path is safe to delete by ensuring it is not a filesystem root.</summary>
    /// <param name="path">The absolute path to evaluate.</param>
    /// <returns><c>true</c> if the path is not a root directory and is safe for recursive deletion.</returns>
    public static bool IsSafeToDelete(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var full
        && Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var root
        && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Determines if any segment of a path or the file itself should be ignored based on current settings.</summary>
    /// <param name="path">The absolute or relative path to evaluate.</param>
    /// <param name="videoService">Shoko video service to check for valid extensions when validating inline extras.</param>
    /// <param name="settings">The current relay configuration.</param>
    /// <param name="ignoredNames">Optional pre-computed set of ignored folder names for performance.</param>
    /// <param name="relativeTo">Optional managed folder root used to isolate relative path segments.</param>
    /// <param name="isDirectory">Whether the path represents a directory rather than a file.</param>
    /// <param name="fileSystem">Optional Shoko file system service for long-path-safe sibling checks.</param>
    /// <param name="isDirectoryResolver">Optional lazy resolver evaluated only when inline extra matching needs to distinguish files from directories.</param>
    /// <returns>True if any segment of the path or the filename matches an ignore rule.</returns>
    public static bool IsPathIgnored(
        string path,
        IVideoService videoService,
        RelayConfig settings,
        HashSet<string>? ignoredNames = null,
        string? relativeTo = null,
        bool isDirectory = false,
        IFileSystem? fileSystem = null,
        Func<bool>? isDirectoryResolver = null
    )
    {
        if (string.IsNullOrEmpty(path))
            return false;

        static bool TryMakeRelative(string rootPath, string fullPath, out string relative)
        {
            relative = string.Empty;
            try
            {
                string relativePath = Path.GetRelativePath(rootPath, fullPath);
                if (
                    relativePath != ".."
                    && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(relativePath)
                )
                {
                    relative = relativePath;
                    return true;
                }
            }
            catch { }
            return false;
        }

        string originalPath = path;
        if (!string.IsNullOrEmpty(relativeTo))
        {
            if (TryMakeRelative(relativeTo, path, out var relativePath))
                path = relativePath;
        }
        else if (Path.IsPathRooted(path))
        {
            string fullNormalizedPath = TrimRoot(path) ?? string.Empty;

            var matchedRoot = (videoService.GetAllManagedFolders() ?? [])
                .Select(folder => folder.Path)
                .Where(folderPath => !string.IsNullOrWhiteSpace(folderPath))
                .Select(folderPath => TrimRoot(folderPath)!)
                .OrderByDescending(folderPath => folderPath.Length)
                .FirstOrDefault(folderPath =>
                    string.Equals(fullNormalizedPath, folderPath, PathComparison)
                    || fullNormalizedPath.StartsWith(folderPath.EndsWith(Path.DirectorySeparatorChar) ? folderPath : folderPath + Path.DirectorySeparatorChar, PathComparison)
                );

            if (matchedRoot != null && TryMakeRelative(matchedRoot, path, out var relativePath))
                path = relativePath;
        }

        var plexLocalExtras = settings.Advanced.PlexLocalExtras;
        var names = ignoredNames ?? GetIgnoredFolderNames(settings);
        var alternateLookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        int lastSeparatorIndex = -1;

        int end;
        for (int start = 0; start < path.Length; start = end + 1)
        {
            int relativeEnd = path.AsSpan(start).IndexOfAny(s_pathSeparators);
            end = relativeEnd < 0 ? path.Length : start + relativeEnd;

            if (end > start)
            {
                var segment = path.AsSpan(start, end - start);
                if (alternateLookup.Contains(segment) || (plexLocalExtras && VfsHelper.IsLocalExtraDir(segment)))
                    return true;
            }

            if (relativeEnd >= 0)
                lastSeparatorIndex = end;
        }

        var multiSegments = GetIgnoredCache(settings).MultiSegments;
        if (multiSegments.Length > 0)
        {
            string normalizedRelativePath = NormalizeSeparators(path).Trim(Path.DirectorySeparatorChar);
            foreach (var multi in multiSegments)
                if (
                    normalizedRelativePath.Equals(multi, StringComparison.OrdinalIgnoreCase)
                    || normalizedRelativePath.StartsWith(multi + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || normalizedRelativePath.EndsWith(Path.DirectorySeparatorChar + multi, StringComparison.OrdinalIgnoreCase)
                    || normalizedRelativePath.Contains(Path.DirectorySeparatorChar + multi + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
        }

        if (plexLocalExtras)
        {
            var fileSpan = path.AsSpan(lastSeparatorIndex >= 0 ? lastSeparatorIndex + 1 : 0);
            int lastDot = fileSpan.LastIndexOf('.');
            var nameWithoutExt = lastDot >= 0 ? fileSpan[..lastDot] : fileSpan;

            if (nameWithoutExt.Length > 0 && VfsHelper.IsLocalExtraFile(nameWithoutExt, out int suffixIndex) && !(isDirectoryResolver?.Invoke() ?? isDirectory))
            {
                string? dir = Path.GetDirectoryName(originalPath);
                if (string.IsNullOrEmpty(dir))
                    dir = ".";

                try
                {
                    var expectedBase = nameWithoutExt[..suffixIndex];
                    IEnumerable<string> siblingFiles = fileSystem != null ? fileSystem.GetFilePaths(dir) : Directory.EnumerateFiles(dir, "*");
                    foreach (var file in siblingFiles)
                        if (Path.GetFileNameWithoutExtension(file.AsSpan()).Equals(expectedBase, PathComparison) && videoService.IsAllowedVideoExtension(file))
                            return true;
                }
                catch { }
            }
        }

        return false;
    }

    #endregion

    #region File Writes & Cache

    /// <summary>Runs a fire-and-forget task without flowing the caller's <see cref="ExecutionContext"/> (preventing stale <c>HttpContext</c> or actor leaks).</summary>
    /// <param name="action">The asynchronous work to execute.</param>
    public static void RunDetached(Func<Task> action)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        _ = Task.Run(action);
    }

    /// <summary>Atomically writes a file via a temporary path move using the supplied writer callback.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="writeTemp">Callback that writes the contents to the temporary file path.</param>
    public static void WriteAtomic(string path, Action<string> writeTemp)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTemp(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
            throw;
        }
    }

    /// <summary>Asynchronously and atomically writes a file via a temporary path move using the supplied writer callback.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="writeTemp">Asynchronous callback that writes the contents to the temporary file path.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="fileSystem">Optional Shoko file system service for long-path-safe moves and deletions.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static async Task WriteAtomicAsync(string path, Func<string, CancellationToken, Task> writeTemp, CancellationToken cancellationToken = default, IFileSystem? fileSystem = null)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await writeTemp(tempPath, cancellationToken).ConfigureAwait(false);
            if (fileSystem != null)
                fileSystem.MoveFile(tempPath, path, overwrite: true);
            else
                File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (fileSystem != null)
                    fileSystem.DeleteFile(tempPath);
                else if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
            throw;
        }
    }

    /// <summary>Atomically writes text content to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="content">The text content to write.</param>
    public static void WriteTextAtomic(string path, string content) => WriteAtomic(path, temporaryPath => File.WriteAllText(temporaryPath, content));

    /// <summary>Asynchronously and atomically writes text content to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="content">The text content to write.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(path, (temporaryPath, token) => File.WriteAllTextAsync(temporaryPath, content, token), cancellationToken);

    /// <summary>Atomically writes lines of text to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="lines">The lines to write.</param>
    public static void WriteLinesAtomic(string path, IEnumerable<string> lines) => WriteAtomic(path, temporaryPath => File.WriteAllLines(temporaryPath, lines));

    /// <summary>Asynchronously and atomically writes lines of text to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="lines">The lines to write.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="fileSystem">Optional Shoko file system service for long-path-safe writes.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static Task WriteLinesAtomicAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default, IFileSystem? fileSystem = null) =>
        WriteAtomicAsync(
            path,
            async (temporaryPath, token) =>
            {
                if (fileSystem != null)
                {
                    await using var stream = fileSystem.OpenFile(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var writer = new StreamWriter(stream);
                    foreach (var line in lines)
                        await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                }
                else
                    await File.WriteAllLinesAsync(temporaryPath, lines, token).ConfigureAwait(false);
            },
            cancellationToken,
            fileSystem
        );

    /// <summary>Loads the VFS blueprint cache from disk.</summary>
    /// <returns>The deserialized concurrent dictionary representing the blueprint cache.</returns>
    public static ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>> LoadBlueprint()
    {
        var blueprint = new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(PathComparer);
        if (File.Exists(BlueprintFilePath))
            try
            {
                using var stream = new FileStream(BlueprintFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, VfsBlueprintSeries>>>(stream) is { } existing)
                    foreach (var rootPair in existing)
                    {
                        var rootDict = blueprint.GetOrAdd(rootPair.Key, _ => new());
                        foreach (var seriesPair in rootPair.Value)
                            rootDict.TryAdd(seriesPair.Key, seriesPair.Value);
                    }
            }
            catch { }
        return blueprint;
    }

    /// <summary>Saves the VFS blueprint cache to disk atomically.</summary>
    /// <param name="blueprint">The blueprint data to persist.</param>
    public static void SaveBlueprint(ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>> blueprint)
    {
        try
        {
            WriteAtomic(BlueprintFilePath, temporaryPath =>
            {
                using var fileStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(fileStream, blueprint);
            });
            Interlocked.Increment(ref s_blueprintVersion);
        }
        catch { }
    }

    /// <summary>Reads all text from a file using permissive file-sharing flags so concurrent readers/writers do not collide.</summary>
    /// <param name="path">The file path to read.</param>
    /// <returns>The full text content of the file.</returns>
    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Asynchronously reads all text from a file using permissive file-sharing flags so concurrent readers/writers do not collide.</summary>
    /// <param name="path">The file path to read.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The full text content of the file.</returns>
    public static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads non-empty trimmed lines from a file using permissive file-sharing flags.</summary>
    /// <param name="path">The file path to read.</param>
    /// <returns>A list of non-empty lines read from the file.</returns>
    public static List<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } rawLine)
            if (!string.IsNullOrWhiteSpace(rawLine))
                lines.Add(rawLine);
        return lines;
    }

    #endregion
}
