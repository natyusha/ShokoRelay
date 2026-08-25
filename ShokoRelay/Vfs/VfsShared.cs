using System.Buffers;
using System.Diagnostics;
using Shoko.Abstractions.Metadata.Enums;
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

    /// <summary>The absolute path to the VFS blueprint cache file.</summary>
    public static string BlueprintFilePath => Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsBlueprintCache);

    /// <summary>The cached set of ignored folder names resolved during the last lookup.</summary>
    private static HashSet<string>? s_lastIgnoredNames;

    /// <summary>Reference to the last evaluated configuration settings instance used for cache invalidation.</summary>
    private static RelayConfig? s_lastSettingsForIgnore;

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

        var (doTv, doMovie) = MapHelper.GetGenerationModes(MapHelper.IsMovie(series), Settings.Advanced.MovieGenerationMode);

        var fileData = MapHelper.GetConsolidatedSeriesFileData(series, metadataService);
        foreach (var mapping in fileData.Mappings)
        {
            var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
            if (location == null || !IsVfsEnabledFolder(location.ManagedFolder))
                continue;

            string? importRoot = ResolveImportRootPath(location);
            if (string.IsNullOrWhiteSpace(importRoot))
                continue;

            if (doTv)
                roots.Add(Path.Combine(importRoot, rootName, folderId.ToString()));

            if (doMovie)
            {
                var mainEps = fileData.Mappings.Where(m => m.PrimaryEpisode.Type == EpisodeType.Episode).Select(m => m.PrimaryEpisode).DistinctBy(e => e.ID);
                foreach (var ep in mainEps)
                    roots.Add(Path.Combine(importRoot, movieRootName, ep.ID.ToString()));
            }
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

    /// <summary>Resolves the standalone Movies VFS root folder name.</summary>
    public static string ResolveMovieRootFolderName() => ResolveFolderName(Settings.Advanced.MovieVfsRootPath, ShokoRelayConstants.FolderMoviesDefault);

    /// <summary>Resolves the anime themes folder name.</summary>
    public static string ResolveAnimeThemesFolderName() => ResolveFolderName(Settings.Advanced.AnimeThemesRootPath, ShokoRelayConstants.FolderAnimeThemesDefault);

    /// <summary>Resolves the collection posters folder name.</summary>
    public static string ResolveCollectionImagesFolderName() => ResolveFolderName(Settings.Advanced.CollectionImagesRootPath, ShokoRelayConstants.FolderCollectionImagesDefault);

    /// <summary>Assembles a unique set of folder names that should be ignored by VFS and Link operations based on current settings.</summary>
    /// <param name="settings">The current relay configuration.</param>
    /// <returns>A HashSet of folder names.</returns>
    public static HashSet<string> GetIgnoredFolderNames(RelayConfig settings)
    {
        if (ReferenceEquals(s_lastSettingsForIgnore, settings) && s_lastIgnoredNames != null)
            return s_lastIgnoredNames;

        lock (s_ignoreLock)
        {
            if (ReferenceEquals(s_lastSettingsForIgnore, settings) && s_lastIgnoredNames != null)
                return s_lastIgnoredNames;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ResolveRootFolderName(), ResolveMovieRootFolderName(), ResolveAnimeThemesFolderName(), ResolveCollectionImagesFolderName() };
            foreach (var folder in settings.Advanced.FolderExclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ignored.Add(folder);

            // If Plex Local Extras are enabled, automatically include the standard Plex extra subdirectories in the ignore list.
            // This prevents Shoko from attempting to index these files, removing the need for manual user intervention for show and season-level extras.
            if (settings.Advanced.PlexLocalExtras)
                foreach (var extraDir in PlexConstants.LocalExtraDirs)
                    ignored.Add(extraDir);

            s_lastSettingsForIgnore = settings;
            return s_lastIgnoredNames = ignored;
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
    /// <returns>True if the link exists and is correct, or was successfully created.</returns>
    public static bool TryCreateLink(string source, string dest, Logger logger, string? targetOverride = null, bool useRelativeTarget = true, bool skipExistenceCheck = false)
    {
        string linkDir = Path.GetDirectoryName(dest) ?? string.Empty;
        string relativeTarget = targetOverride ?? source;
        if (targetOverride == null && useRelativeTarget && !string.IsNullOrWhiteSpace(linkDir))
            relativeTarget = Path.GetRelativePath(linkDir, source);

        if (!skipExistenceCheck)
        {
            try
            {
                var fi = new FileInfo(dest);
                if (fi.Exists || fi.LinkTarget != null) // Accurately captures both valid files and broken symlinks
                {
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) && string.Equals(fi.LinkTarget, relativeTarget, StringComparison.Ordinal))
                        return true;

                    fi.Delete();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "VFS: Unable to remove existing link at -> {Dest}", dest);
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
                logger.Debug("VFS: Symlink created -> '{Link}' in {Elapsed}ms", dest, sw.ElapsedMilliseconds); // only log slow operations, to avoid spamming the logs
            return info.Exists;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "VFS: Symlink creation failed -> {Link}", dest);
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
    /// <param name="videoService">Shoko video service to check for valid extensions when validating inline extras.</param>
    /// <param name="ignoredNames">Optional pre-computed set of ignored folder names for performance.</param>
    /// <returns>True if any segment of the path or the filename matches an ignore rule.</returns>
    public static bool IsPathIgnored(string path, IVideoService videoService, HashSet<string>? ignoredNames = null)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var settings = Settings;
        var plexLocalExtras = settings.Advanced.PlexLocalExtras;
        var names = ignoredNames ?? GetIgnoredFolderNames(settings);
        var alternateLookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        int lastSlash = -1;

        for (int start = 0, end; start < path.Length; start = end + 1)
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

        if (plexLocalExtras)
        {
            var fileSpan = path.AsSpan(lastSlash >= 0 ? lastSlash + 1 : 0);
            int lastDot = fileSpan.LastIndexOf('.');
            var nameWithoutExt = lastDot >= 0 ? fileSpan[..lastDot] : fileSpan;

            if (nameWithoutExt.Length > 0 && VfsHelper.IsLocalExtraFile(nameWithoutExt, out int suffixIndex))
            {
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir))
                    dir = ".";

                string searchPattern = string.Concat(nameWithoutExt[..suffixIndex], ".*");

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, searchPattern))
                        if (videoService.IsAllowedVideoExtension(file))
                            return true;
                }
                catch { }
            }
        }

        return false;
    }

    #endregion

    #region Blueprint Cache

    /// <summary>Loads the VFS blueprint cache from disk.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<int, VfsBlueprintSeries>> LoadBlueprint()
    {
        var blueprint = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<int, VfsBlueprintSeries>>(PathComparer);
        if (File.Exists(BlueprintFilePath))
        {
            try
            {
                var existing = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, VfsBlueprintSeries>>>(File.ReadAllText(BlueprintFilePath));
                if (existing != null)
                {
                    foreach (var rKvp in existing)
                    {
                        var rootDict = blueprint.GetOrAdd(rKvp.Key, _ => new());
                        foreach (var sKvp in rKvp.Value)
                            rootDict.TryAdd(sKvp.Key, sKvp.Value);
                    }
                }
            }
            catch { }
        }
        return blueprint;
    }

    /// <summary>Saves the VFS blueprint cache to disk atomically.</summary>
    public static void SaveBlueprint(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<int, VfsBlueprintSeries>> blueprint)
    {
        try
        {
            string tmpPath = BlueprintFilePath + ".tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                System.Text.Json.JsonSerializer.Serialize(fs, blueprint);
            File.Move(tmpPath, BlueprintFilePath, overwrite: true);
        }
        catch { }
    }

    #endregion
}

#region VFS Ignore Rule

/// <summary>Automatically ignores Shoko Relay's internal VFS and local asset directories during Shoko's import scans.</summary>
public class VfsIgnoreRule(IVideoService videoService) : IManagedFolderIgnoreRule
{
    /// <inheritdoc/>
    public string Name => "Shoko Relay Ignore Rule";

    /// <inheritdoc/>
    public bool ShouldIgnore(IManagedFolder folder, FileSystemInfo fileSystemInfo) => VfsShared.IsPathIgnored(fileSystemInfo.FullName, videoService);
}

#endregion
