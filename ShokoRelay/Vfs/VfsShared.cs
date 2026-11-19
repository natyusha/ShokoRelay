using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
        && !(Settings.Advanced.ManagedFolderExclusions?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []).Any(ex =>
            string.Equals(ex, folder.ID.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(ex, folder.Name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>Determines the root import path for a video file.</summary>
    /// <param name="location">The video file location.</param>
    /// <returns>The resolved import root path, or <c>null</c> if unavailable.</returns>
    public static string? ResolveImportRootPath(IVideoFile location)
    {
        static string? TrimRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string norm = NormalizeSeparators(path);
            string? pathRoot = Path.GetPathRoot(norm);
            return !string.IsNullOrEmpty(pathRoot) && string.Equals(norm, pathRoot, PathComparison) ? norm : norm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (!string.IsNullOrWhiteSpace(location.ManagedFolder?.Path))
            return TrimRoot(location.ManagedFolder.Path);

        string path = location.Path;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string normalizedPath = NormalizeSeparators(path);
        string relative = location.RelativePath.TrimLeadingSlashes().Trim();
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string normalizedRel = NormalizeSeparators(relative);
            if (normalizedPath.EndsWith(normalizedRel, PathComparison))
                return TrimRoot(normalizedPath[..^normalizedRel.Length]);
        }
        return TrimRoot(Path.GetDirectoryName(normalizedPath));
    }

    /// <summary>Resolves the source file path.</summary>
    /// <param name="location">The video file location.</param>
    /// <param name="importRoot">The resolved import root path.</param>
    /// <returns>The existing physical file path, or <c>null</c> if not found.</returns>
    public static string? ResolveSourcePath(IVideoFile location, string importRoot)
    {
        string original = location.Path;
        if (!string.IsNullOrWhiteSpace(original) && File.Exists(original))
            return original;
        string relative = location.RelativePath.TrimLeadingSlashes();
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string candidate = Path.Combine(importRoot, NormalizeSeparators(relative));
            if (File.Exists(candidate))
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
        var mainEpIds = doMovie ? fileData.Mappings.Where(m => m.PrimaryEpisode.Type == EpisodeType.Episode).Select(m => m.PrimaryEpisode.ID.ID).Distinct().ToList() : [];

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
                foreach (var epId in mainEpIds)
                    roots.Add(Path.Combine(importRoot, movieRootName, epId.ToString()));
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
            if (s_ignoreCache is { } recheck && ReferenceEquals(recheck.Settings, settings))
                return recheck;

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

    /// <summary>Attempts to create a symlink.</summary>
    /// <param name="source">The physical source file.</param>
    /// <param name="dest">The destination link path.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="targetOverride">Optional specific target string.</param>
    /// <param name="useRelativeTarget">Whether to resolve the target path relatively.</param>
    /// <param name="skipExistenceCheck">If true, bypasses the filesystem check and writes the link directly.</param>
    /// <param name="checkVfsDisabled">Whether to skip link creation if VFS generation is disabled in settings.</param>
    /// <returns>True if the link exists and is correct, or was successfully created.</returns>
    public static bool TryCreateLink(string source, string dest, ILogger logger, string? targetOverride = null, bool useRelativeTarget = true, bool skipExistenceCheck = false, bool checkVfsDisabled = true)
    {
        if (checkVfsDisabled && Settings.Advanced.DisableVfsGeneration)
            return true;

        string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);

        if (!skipExistenceCheck)
        {
            try
            {
                var fi = new FileInfo(dest);
                if (fi.Exists || fi.LinkTarget != null)
                {
                    if (string.Equals(NormalizeSeparators(fi.LinkTarget ?? string.Empty), NormalizeSeparators(relativeTarget), PathComparison))
                        return true;

                    fi.Delete();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VFS: Unable to remove existing link at -> {Dest}", dest);
                return false;
            }
        }

        // Internal wrapper around the OS file APIs to create standard filesystem relative symbolic links.
        try
        {
            var sw = Stopwatch.StartNew();
            var info = File.CreateSymbolicLink(dest, relativeTarget);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 20)
                logger.LogDebug("VFS: Symlink created -> '{Link}' in {Elapsed}ms", dest, sw.ElapsedMilliseconds); // only log slow operations, to avoid spamming the logs
            return info.Exists;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "VFS: Symlink creation failed -> {Link}", dest);
            return false;
        }
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
    /// <returns>True if any segment of the path or the filename matches an ignore rule.</returns>
    public static bool IsPathIgnored(string path, IVideoService videoService, RelayConfig settings, HashSet<string>? ignoredNames = null, string? relativeTo = null, bool isDirectory = false)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        static bool TryMakeRelative(string rootPath, string fullPath, out string relative)
        {
            relative = string.Empty;
            try
            {
                string rel = Path.GetRelativePath(rootPath, fullPath);
                if (
                    rel != ".."
                    && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(rel)
                )
                {
                    relative = rel;
                    return true;
                }
            }
            catch { }
            return false;
        }

        string originalPath = path;
        if (!string.IsNullOrEmpty(relativeTo))
        {
            if (TryMakeRelative(relativeTo, path, out var rel))
                path = rel;
        }
        else if (Path.IsPathRooted(path))
        {
            string normPath = NormalizeSeparators(path);
            if (normPath.Length > 1)
                normPath = normPath.TrimEnd(Path.DirectorySeparatorChar);

            var matchedRoot = (videoService.GetAllManagedFolders() ?? [])
                .Select(mf => mf.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    string norm = NormalizeSeparators(path);
                    return norm.Length > 1 ? norm.TrimEnd(Path.DirectorySeparatorChar) : norm;
                })
                .OrderByDescending(p => p.Length)
                .FirstOrDefault(p => string.Equals(normPath, p, PathComparison) || normPath.StartsWith(p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar, PathComparison));

            if (matchedRoot != null && TryMakeRelative(matchedRoot, path, out var rel))
                path = rel;
        }

        var plexLocalExtras = settings.Advanced.PlexLocalExtras;
        var names = ignoredNames ?? GetIgnoredFolderNames(settings);
        var alternateLookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        int lastSlash = -1;

        int end;
        for (int start = 0; start < path.Length; start = end + 1)
        {
            int relEnd = path.AsSpan(start).IndexOfAny(s_pathSeparators);
            end = relEnd < 0 ? path.Length : start + relEnd;

            if (end > start)
            {
                var seg = path.AsSpan(start, end - start);
                if (alternateLookup.Contains(seg) || (plexLocalExtras && VfsHelper.IsLocalExtraDir(seg)))
                    return true;
            }

            if (relEnd >= 0)
                lastSlash = end;
        }

        var multiSegments = GetIgnoredCache(settings).MultiSegments;
        if (multiSegments.Length > 0)
        {
            string normRel = NormalizeSeparators(path).Trim(Path.DirectorySeparatorChar);
            foreach (var multi in multiSegments)
                if (
                    normRel.Equals(multi, StringComparison.OrdinalIgnoreCase)
                    || normRel.StartsWith(multi + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || normRel.EndsWith(Path.DirectorySeparatorChar + multi, StringComparison.OrdinalIgnoreCase)
                    || normRel.Contains(Path.DirectorySeparatorChar + multi + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
        }

        if (plexLocalExtras && !isDirectory)
        {
            var fileSpan = path.AsSpan(lastSlash >= 0 ? lastSlash + 1 : 0);
            int lastDot = fileSpan.LastIndexOf('.');
            var nameWithoutExt = lastDot >= 0 ? fileSpan[..lastDot] : fileSpan;

            if (nameWithoutExt.Length > 0 && VfsHelper.IsLocalExtraFile(nameWithoutExt, out int suffixIndex))
            {
                string? dir = Path.GetDirectoryName(originalPath);
                if (string.IsNullOrEmpty(dir))
                    dir = ".";

                string searchPattern = string.Concat(nameWithoutExt[..suffixIndex], ".*");

                try
                {
                    var expectedBase = nameWithoutExt[..suffixIndex];
                    foreach (var file in Directory.EnumerateFiles(dir, searchPattern))
                        if (Path.GetFileNameWithoutExtension(file.AsSpan()).Equals(expectedBase, PathComparison) && videoService.IsAllowedVideoExtension(file))
                            return true;
                }
                catch { }
            }
        }

        return false;
    }

    #endregion

    #region File Writes & Blueprint

    /// <summary>Atomically writes a file via a temporary path move using the supplied writer callback.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="writeTmp">Callback that writes the contents to the temporary file path.</param>
    public static void WriteAtomic(string path, Action<string> writeTmp)
    {
        string tmpPath = path + ".tmp";
        writeTmp(tmpPath);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>Asynchronously and atomically writes a file via a temporary path move using the supplied writer callback.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="writeTmp">Asynchronous callback that writes the contents to the temporary file path.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static async Task WriteAtomicAsync(string path, Func<string, CancellationToken, Task> writeTmp, CancellationToken cancellationToken = default)
    {
        string tmpPath = path + ".tmp";
        await writeTmp(tmpPath, cancellationToken).ConfigureAwait(false);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>Atomically writes text content to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="content">The text content to write.</param>
    public static void WriteTextAtomic(string path, string content) => WriteAtomic(path, tmp => File.WriteAllText(tmp, content));

    /// <summary>Asynchronously and atomically writes text content to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="content">The text content to write.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(path, (tmp, ct) => File.WriteAllTextAsync(tmp, content, ct), cancellationToken);

    /// <summary>Atomically writes lines of text to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="lines">The lines to write.</param>
    public static void WriteLinesAtomic(string path, IEnumerable<string> lines) => WriteAtomic(path, tmp => File.WriteAllLines(tmp, lines));

    /// <summary>Asynchronously and atomically writes lines of text to a target file via a temporary file move.</summary>
    /// <param name="path">The target file path.</param>
    /// <param name="lines">The lines to write.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public static Task WriteLinesAtomicAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(path, (tmp, ct) => File.WriteAllLinesAsync(tmp, lines, ct), cancellationToken);

    /// <summary>Loads the VFS blueprint cache from disk.</summary>
    /// <returns>The deserialized concurrent dictionary representing the blueprint cache.</returns>
    public static ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>> LoadBlueprint()
    {
        var blueprint = new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(PathComparer);
        if (File.Exists(BlueprintFilePath))
        {
            try
            {
                using var fs = new FileStream(BlueprintFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, VfsBlueprintSeries>>>(fs) is { } existing)
                    foreach (var rKvp in existing)
                    {
                        var rootDict = blueprint.GetOrAdd(rKvp.Key, _ => new());
                        foreach (var sKvp in rKvp.Value)
                            rootDict.TryAdd(sKvp.Key, sKvp.Value);
                    }
            }
            catch { }
        }
        return blueprint;
    }

    /// <summary>Saves the VFS blueprint cache to disk atomically.</summary>
    /// <param name="blueprint">The blueprint data to persist.</param>
    public static void SaveBlueprint(ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>> blueprint)
    {
        try
        {
            WriteAtomic(
                BlueprintFilePath,
                tmp =>
                {
                    using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                    JsonSerializer.Serialize(fs, blueprint);
                }
            );
            Interlocked.Increment(ref s_blueprintVersion);
        }
        catch { }
    }

    #endregion
}
