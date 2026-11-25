using System.Buffers;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.FileSystem;

namespace ShokoRelay.Vfs;

/// <summary>Utilities used during VFS generation including filename sanitization.</summary>
public static partial class VfsHelper
{
    #region Regex and Mappings

    /// <summary>Regex to match standard quotes with the intention of turning them curly.</summary>
    [GeneratedRegex("\"(.*?)\"")]
    private static partial Regex QuotedTextRegex();

    /// <summary>Regex to match whitespace except for Hair Space and Zero Width Spaceas they are part of some Plex Extra filename formatting.</summary>
    [GeneratedRegex(@"((?![\u200A\u200B])\s)+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Regex to identify Plex-style split tags in filenames.</summary>
    [GeneratedRegex(@"(?ix)(?:^|[\s._-])(cd|disc|disk|dvd|part|pt)[\s._-]*([1-8])(?!\d)")]
    private static partial Regex PlexSplitTagRegex();

    /// <summary>Regex to match local extra directories with optional season suffixes.</summary>
    [GeneratedRegex(@"^(Behind The Scenes|Deleted Scenes|Featurettes|Interviews|Scenes|Shorts|Trailers|Other)(\s+[sS](\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalExtraDirRegex();

    /// <summary>Regex to match local extra file suffixes.</summary>
    [GeneratedRegex(@"-(?:behindthescenes|deleted|featurette|interview|scene|short|trailer|other)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalExtraFileRegex();

    /// <summary>Regex to match standard season or specials subfolder names.</summary>
    [GeneratedRegex(@"^(?:Season\s*\d+|Specials|S\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSubfolderRegex();

    /// <summary>Array of styled text replacements for cleaning episode titles.</summary>
    private static readonly (string Find, string Replace)[] s_styledReplacements = [("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←")];

    /// <summary>Dictionary mapping local extra subtypes to their filename prefixes.</summary>
    private static readonly IReadOnlyDictionary<string, string> s_extraTypePrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trailer"] = "T",
        ["sceneOrSample"] = "P",
        ["featurette"] = "O",
        ["short"] = "C",
        ["other"] = "U",
    };

    /// <summary>Hardware-accelerated search values for locating invalid characters rapidly.</summary>
    public static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>Maps invalid Windows filename characters to visually similar Unicode replacements.</summary>
    public static readonly FrozenDictionary<char, char> ReplacementCharMap = new Dictionary<char, char>
    {
        ['\\'] = '⧵',
        ['/'] = '⁄',
        [':'] = '꞉',
        ['*'] = '＊',
        ['?'] = '？',
        ['<'] = '＜',
        ['>'] = '＞',
        ['|'] = '｜',
    }.ToFrozenDictionary();

    /// <summary>Hardware-accelerated search values for locating characters that require visual replacements.</summary>
    private static readonly SearchValues<char> s_replacementChars = SearchValues.Create([.. ReplacementCharMap.Keys]);

    #endregion

    #region Sanitization

    /// <summary>Sanitizes a string for use as a filename using fast hardware-accelerated SIMD instructions where possible.</summary>
    /// <param name="name">The name to sanitize.</param>
    /// <returns>A sanitized filename string.</returns>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown";

        string processed = name;
        if (name.AsSpan().IndexOfAny(InvalidFileNameChars) >= 0)
            processed = string.Create(name.Length, name, (chars, state) =>
            {
                for (int charIndex = 0; charIndex < state.Length; charIndex++)
                    chars[charIndex] = InvalidFileNameChars.Contains(state[charIndex]) ? ' ' : state[charIndex];
            });

        var cleaned = TextHelper.CondenseSpaces(processed).Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }

    /// <summary>Cleans episode titles for filename use.</summary>
    /// <param name="title">The title string to clean.</param>
    /// <returns>A cleaned episode title string.</returns>
    public static string CleanEpisodeTitleForFilename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        string cleanedTitle = title;
        foreach (var (find, replace) in s_styledReplacements)
            cleanedTitle = cleanedTitle.Replace(find, replace, StringComparison.Ordinal);
        cleanedTitle = QuotedTextRegex().Replace(cleanedTitle, "“$1”");

        // Zero-width space (\u200B) and hair space (\u200A) bypass Plex's undocumented behavior of automatically stripping OP/ED/extra prefixes from display titles while also sorting Openings before Endings
        if (cleanedTitle.StartsWith("Opening", StringComparison.OrdinalIgnoreCase))
            cleanedTitle = $"{TextHelper.HairSpace}O{TextHelper.ZeroWidthSpace}pening{cleanedTitle[7..]}";
        else if (cleanedTitle.StartsWith("Ending", StringComparison.OrdinalIgnoreCase))
            cleanedTitle = $"E{TextHelper.ZeroWidthSpace}nding{cleanedTitle[6..]}";

        if (cleanedTitle.AsSpan().IndexOfAny(s_replacementChars) >= 0)
            cleanedTitle = string.Create(cleanedTitle.Length, cleanedTitle, (chars, state) =>
            {
                for (int charIndex = 0; charIndex < state.Length; charIndex++)
                    chars[charIndex] = ReplacementCharMap.TryGetValue(state[charIndex], out var replacementChar) ? replacementChar : state[charIndex];
            });

        return WhitespaceRegex().Replace(cleanedTitle, " ").Trim(' ');
    }

    #endregion

    #region Naming Logic

    /// <summary>Generates an orderable pairwise key where non-seasons are sorted alphabetically and standard seasons numerically.</summary>
    /// <param name="name">The season folder name to parse.</param>
    /// <returns>A tuple sorting standard seasons numerically before named folders.</returns>
    public static (bool IsSeason, int SeasonNumber, string Name) GetSeasonSortKey(string name) =>
        (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int numberValue)) ? (true, numberValue, string.Empty) : (false, 0, name);

    /// <summary>Resolves the Plex-compatible season number for any given VFS folder name.</summary>
    /// <param name="name">The season folder name.</param>
    /// <returns>The numeric season ID, or <c>null</c> if unrecognized.</returns>
    public static int? GetSeasonId(string name) =>
        name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int numberValue) ? numberValue
        : string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase) ? PlexConstants.SeasonSpecials
        : PlexConstants.ExtraSeasons.FirstOrDefault(entry => string.Equals(entry.Value.Folder, name, StringComparison.OrdinalIgnoreCase)) is var match && match.Value.Folder != null ? match.Key
        : null;

    /// <summary>Builds a standard episode filename based on coordinates, versioning, and variation status.</summary>
    /// <param name="mapping">The mapping containing coordinates and video data.</param>
    /// <param name="padding">The number of digits to pad the episode number with.</param>
    /// <param name="extension">The file extension (including the dot).</param>
    /// <param name="fileId">The unique Shoko video identifier.</param>
    /// <param name="omitFileId">Whether to exclude the Shoko video identifier from the filename.</param>
    /// <param name="partIndex">The 1-based index for multi-part files.</param>
    /// <param name="partCount">The total number of parts for the file.</param>
    /// <param name="versionIndex">The 1-based version index for duplicate items in the same variation bucket.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A formatted filename string (e.g., "S01E01-dup1 [123][variation].mkv").</returns>
    public static string BuildStandardFileName(
        MapHelper.FileMapping mapping,
        int padding,
        string extension,
        int fileId,
        bool omitFileId = false,
        int? partIndex = null,
        int? partCount = null,
        int? versionIndex = null,
        bool isVariation = false
    ) =>
        FormatVfsFileName(
            mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode != mapping.Coords.Episode
                ? $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{padding}")}-E{mapping.Coords.EndEpisode.Value.ToString($"D{padding}")}"
                : $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{padding}")}",
            mapping,
            extension,
            fileId,
            omitFileId,
            partIndex,
            partCount,
            versionIndex,
            isVariation
        );

    /// <summary>Builds a movie filename based on part indexing, versioning, and variation status.</summary>
    /// <param name="mapping">The mapping containing coordinates and video data.</param>
    /// <param name="extension">The file extension (including the dot).</param>
    /// <param name="fileId">The unique Shoko video identifier.</param>
    /// <param name="omitFileId">Whether to exclude the Shoko video identifier from the filename.</param>
    /// <param name="partIndex">The 1-based index for multi-part files.</param>
    /// <param name="partCount">The total number of parts for the file.</param>
    /// <param name="versionIndex">The 1-based version index for duplicate items in the same variation bucket.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A formatted movie filename string (e.g., "Movie [{ShokoFileID}].mkv").</returns>
    public static string BuildMovieFileName(
        MapHelper.FileMapping mapping,
        string extension,
        int fileId,
        bool omitFileId = false,
        int? partIndex = null,
        int? partCount = null,
        int? versionIndex = null,
        bool isVariation = false
    ) => FormatVfsFileName("Movie", mapping, extension, fileId, omitFileId, partIndex, partCount, versionIndex, isVariation);

    /// <summary>Appends part indexing, versioning, file ID, and variation tags to a base VFS filename.</summary>
    /// <param name="baseName">The base prefix (e.g. "S01E01" or "Movie").</param>
    /// <param name="mapping">The mapping containing part and variation metadata.</param>
    /// <param name="extension">The file extension (including the dot).</param>
    /// <param name="fileId">The unique Shoko video identifier.</param>
    /// <param name="omitFileId">Whether to exclude the Shoko video identifier from the filename.</param>
    /// <param name="partIndex">The 1-based index for multi-part files.</param>
    /// <param name="partCount">The total number of parts for the file.</param>
    /// <param name="versionIndex">The 1-based version index for duplicate items in the same variation bucket.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A formatted VFS filename string.</returns>
    private static string FormatVfsFileName(string baseName, MapHelper.FileMapping mapping, string extension, int fileId, bool omitFileId, int? partIndex, int? partCount, int? versionIndex, bool isVariation)
    {
        string name = baseName;
        int totalParts = partCount ?? mapping.PartCount;
        if (totalParts > 1)
            name += $"-pt{partIndex ?? mapping.PartIndex}";
        else if (versionIndex.HasValue)
            name += $"-dup{versionIndex}";

        string result = omitFileId ? name : $"{name} [{fileId}]";
        if (isVariation && totalParts <= 1)
            result += "[variation]";
        return result + extension;
    }

    /// <summary>Builds a filename for extra content (trailers, credits, etc.) using special type prefixes and title metadata.</summary>
    /// <param name="mapping">The mapping containing coordinates and metadata.</param>
    /// <param name="extra">A tuple containing the target folder and the Plex extra subtype.</param>
    /// <param name="padding">The number of digits to pad the extra number with.</param>
    /// <param name="extension">The file extension (including the dot).</param>
    /// <param name="seriesTitle">The display title of the parent series used for name resolution.</param>
    /// <param name="partIndex">Optional 1-based index for multi-part extras.</param>
    /// <param name="partCount">Optional total number of parts for multi-part extras.</param>
    /// <param name="versionIndex">Optional version index for duplicate extras.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A sanitized and formatted filename string for Plex extras (e.g., "C01 ❯ Episode Title [variation].mkv").</returns>
    public static string BuildExtrasFileName(
        MapHelper.FileMapping mapping,
        (string Folder, string Subtype) extra,
        int padding,
        string extension,
        string seriesTitle,
        int? partIndex = null,
        int? partCount = null,
        int? versionIndex = null,
        bool isVariation = false
    )
    {
        string episode = s_extraTypePrefixes.TryGetValue(extra.Subtype, out var prefix) ? prefix + mapping.Coords.Episode.ToString($"D{padding}") : mapping.Coords.Episode.ToString($"D{padding}");
        int totalParts = partCount ?? mapping.PartCount;
        string part = totalParts > 1 ? $"-pt{partIndex ?? mapping.PartIndex}" : (versionIndex.HasValue ? $"-dup{versionIndex}" : "");

        string name = $"{episode}{part} ❯ {CleanEpisodeTitleForFilename(TextHelper.ResolveEpisodeTitle(mapping.PrimaryEpisode, seriesTitle))}";
        if (isVariation && totalParts <= 1)
            name += "[variation]";
        return SanitizeName(name + extension);
    }

    #endregion

    #region VFS/Plex Helpers

    /// <summary>Checks if a path resides within the VFS root directory.</summary>
    /// <param name="path">The folder path to evaluate.</param>
    /// <param name="vfsRoot">Outputs the resolved VFS root folder name.</param>
    /// <returns>True if the path intersects the VFS boundary.</returns>
    public static bool IsInVfsRoot(string path, out string vfsRoot)
    {
        vfsRoot = VfsShared.ResolveRootFolderName();
        string movieRoot = VfsShared.ResolveMovieRootFolderName();
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains(vfsRoot, StringComparer.OrdinalIgnoreCase))
            return true;
        if (segments.Contains(movieRoot, StringComparer.OrdinalIgnoreCase))
        {
            vfsRoot = movieRoot;
            return true;
        }
        return false;
    }

    /// <summary>Determine if a filename contains a Plex-style split tag (e.g. "pt1").</summary>
    /// <param name="fileName">The filename to check.</param>
    /// <returns>True if a split tag is found.</returns>
    public static bool HasPlexSplitTag(string fileName) => !string.IsNullOrWhiteSpace(fileName) && PlexSplitTagRegex().IsMatch(Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' '));

    /// <summary>Identifies local extra directories with optional season suffixes.</summary>
    /// <param name="name">The directory name to evaluate.</param>
    /// <returns>A Match object containing the extra type and optional season number.</returns>
    public static Match MatchLocalExtraDir(string name) => LocalExtraDirRegex().Match(name);

    /// <summary>Identifies local extra directories directly from a string span.</summary>
    /// <param name="name">The directory name to evaluate.</param>
    /// <returns>True if it matches the pattern.</returns>
    public static bool IsLocalExtraDir(ReadOnlySpan<char> name) => LocalExtraDirRegex().IsMatch(name);

    /// <summary>Identifies local extra files from a string span and returns the start index of the suffix if matched.</summary>
    /// <param name="fileNameWithoutExtension">The filename without its extension to evaluate.</param>
    /// <param name="suffixIndex">The 0-based start index of the matched suffix, or -1 if not matched.</param>
    /// <returns>True if the filename matches a Plex local extra pattern; otherwise, false.</returns>
    public static bool IsLocalExtraFile(ReadOnlySpan<char> fileNameWithoutExtension, out int suffixIndex)
    {
        var enumerator = LocalExtraFileRegex().EnumerateMatches(fileNameWithoutExtension);
        return (suffixIndex = enumerator.MoveNext() ? enumerator.Current.Index : -1) >= 0;
    }

    /// <summary>Determines if a directory name matches a standard season or specials subfolder pattern.</summary>
    /// <param name="name">The directory name to evaluate.</param>
    /// <returns>True if the directory name is a season or specials folder.</returns>
    public static bool IsSeasonSubfolder(string name) => !string.IsNullOrWhiteSpace(name) && SeasonSubfolderRegex().IsMatch(name);

    #endregion

    #region File & Cleanup

    /// <summary>Perform diff-based cleanup of unexpected files and empty directories left behind by renames or moves.</summary>
    /// <param name="resolvedVfsSeriesPaths">The unique set of VFS paths involved in the build.</param>
    /// <param name="expectedFiles">The tracked set of files that should exist.</param>
    /// <param name="fileSystem">Shoko file system service.</param>
    public static void CleanupOrphanedFilesAndFolders(HashSet<string> resolvedVfsSeriesPaths, HashSet<string> expectedFiles, IFileSystem fileSystem)
    {
        if (Settings.Advanced.DisableVfsGeneration)
            return;

        var recursiveOptions = new FileSystemListingOptions { Recursive = true };
        string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
        foreach (var seriesPath in resolvedVfsSeriesPaths)
        {
            if (!fileSystem.DirectoryExists(seriesPath))
                continue;

            foreach (var file in fileSystem.GetFilePaths(seriesPath, recursiveOptions))
                if (!expectedFiles.Contains(file))
                    try
                    {
                        if (fileSystem.GetStat(file, followSymbolicLinks: false) is SymbolicLinkStat symlinkStat)
                        {
                            // Allow AnimeThemesMapping to manage its own files
                            if (symlinkStat.Target?.Contains(themeRootName, StringComparison.OrdinalIgnoreCase) == true)
                                continue;

                            if (symlinkStat.IsDirectoryLink == true)
                                fileSystem.DeleteDirectory(file, recursive: false);
                            else
                                fileSystem.DeleteFile(file);
                        }
                        else
                            fileSystem.DeleteFile(file);
                    }
                    catch { }

            var directories = fileSystem.GetDirectoryPaths(seriesPath, recursiveOptions).OrderByDescending(dir => dir.Length).ToList();
            foreach (var directory in directories)
                try
                {
                    if (fileSystem.IsDirectoryEmpty(directory))
                        fileSystem.DeleteDirectory(directory);
                }
                catch { }
        }
    }

    /// <summary>Checks if a mapped theme file physically exists within the import root, using an in-memory cache to prevent disk thrashing.</summary>
    /// <param name="relativePath">The relative path of the theme file inside the !AnimeThemes folder.</param>
    /// <param name="importRoot">The physical import root directory.</param>
    /// <param name="themeRootName">The name of the AnimeThemes directory.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <param name="fileSystem">Shoko file system service.</param>
    /// <returns>The absolute path of the theme file if it exists; otherwise, null.</returns>
    public static string? GetThemeSourcePath(string relativePath, string importRoot, string themeRootName, VfsBuildSession session, IFileSystem fileSystem)
    {
        string themeRootPath = Path.Combine(importRoot, themeRootName);
        var cache = session
            .PhysicalThemeCaches.GetOrAdd(themeRootPath, root => new Lazy<Dictionary<string, string>>(() =>
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (fileSystem.DirectoryExists(root))
                {
                    try
                    {
                        var options = new FileSystemListingOptions { Recursive = true, Extensions = [".webm"] };
                        foreach (string file in fileSystem.GetFilePaths(root, options))
                            dict[Path.GetRelativePath(root, file).Replace('\\', '/').TrimLeadingSlashes()] = Path.GetFullPath(file);
                    }
                    catch { }
                }
                return dict;
            }))
            .Value;

        string normalizedRelativePath = relativePath.Replace('\\', '/').TrimLeadingSlashes();
        return cache.TryGetValue(normalizedRelativePath, out string? absPath) ? absPath : null;
    }

    #endregion
}
