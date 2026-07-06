using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace ShokoRelay.Vfs;

/// <summary>Utilities used during VFS generation including filename sanitization.</summary>
public static class VfsHelper
{
    #region Regex and Mappings

    /// <summary>Regex to match standard quotes with the intention of turning them curly.</summary>
    private static readonly Regex s_quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);

    /// <summary>Regex to match whitespace except for Hair Space and Zero Width Spaceas they are part of some Plex Extra filename formatting.</summary>
    private static readonly Regex s_whitespaceRegex = new(@"((?![\u200A\u200B])\s)+", RegexOptions.Compiled);

    private static readonly Regex s_plexSplitTagRegex = new(@"(?ix)(?:^|[\s._-])(cd|disc|disk|dvd|part|pt)[\s._-]*([1-8])(?!\d)", RegexOptions.Compiled);

    private static readonly Regex s_localExtraDirRegex = new($@"^({string.Join("|", PlexConstants.LocalExtraDirs)})(\s+[sS](\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_localExtraFileRegex = new($@"-(?:behindthescenes|deleted|featurette|interview|scene|short|trailer|other)\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Find, string Replace)[] s_styledReplacements = [("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←")];

    private static readonly IReadOnlyDictionary<string, string> s_extraTypePrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trailer"] = "T",
        ["sceneOrSample"] = "P",
        ["featurette"] = "O",
        ["short"] = "C",
        ["other"] = "U",
    };

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

    #endregion

    #region Sanitization

    /// <summary>Sanitizes a string for use as a filename.</summary>
    /// <param name="name">The name to sanitize.</param>
    /// <returns>A sanitized filename string.</returns>
    public static string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Unknown"
        : TextHelper.CondenseSpaces(new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)])).Trim().TrimEnd('.') is var cleaned && cleaned.Length > 0 ? cleaned
        : "Unknown";

    /// <summary>Cleans episode titles for filename use.</summary>
    public static string CleanEpisodeTitleForFilename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        string c = title;
        foreach (var (f, r) in s_styledReplacements)
            c = c.Replace(f, r, StringComparison.Ordinal);
        c = s_quotedTextRegex.Replace(c, "“$1”");
        return s_whitespaceRegex.Replace(new string([.. c.Select(ch => ReplacementCharMap.TryGetValue(ch, out var m) ? m : ch)]), " ").Trim(' ');
    }

    #endregion

    #region Naming Logic

    /// <summary>Generates an orderable pairwise key where non-seasons are sorted alphabetically and standard seasons numerically.</summary>
    public static (bool IsSeason, int SeasonNumber, string Name) GetSeasonSortKey(string name) =>
        (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int num)) ? (true, num, string.Empty) : (false, 0, name);

    /// <summary>Resolves the Plex-compatible season number for any given VFS folder name.</summary>
    public static int? GetSeasonId(string name)
    {
        if (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out int num))
            return num;
        if (string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase))
            return PlexConstants.SeasonSpecials;

        var match = PlexConstants.ExtraSeasons.FirstOrDefault(kvp => string.Equals(kvp.Value.Folder, name, StringComparison.OrdinalIgnoreCase));
        return match.Value.Folder != null ? match.Key : null;
    }

    /// <summary>Builds a standard episode filename based on coordinates, versioning, and variation status.</summary>
    /// <param name="mapping">The mapping containing coordinates and video data.</param>
    /// <param name="pad">The number of digits to pad the episode number with.</param>
    /// <param name="ext">The file extension (including the dot).</param>
    /// <param name="fileId">The unique Shoko video identifier.</param>
    /// <param name="omitFileId">Whether to exclude the Shoko video identifier from the filename.</param>
    /// <param name="partIdx">The 1-based index for multi-part files.</param>
    /// <param name="partCount">The total number of parts for the file.</param>
    /// <param name="vIdx">The 1-based version index for duplicate items in the same variation bucket.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A formatted filename string (e.g., "S01E01-v1 [123][variation].mkv").</returns>
    public static string BuildStandardFileName(
        MapHelper.FileMapping mapping,
        int pad,
        string ext,
        int fileId,
        bool omitFileId = false,
        int? partIdx = null,
        int? partCount = null,
        int? vIdx = null,
        bool isVariation = false
    )
    {
        string name = $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{pad}")}";
        if (mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode != mapping.Coords.Episode)
            name += $"-E{mapping.Coords.EndEpisode.Value.ToString($"D{pad}")}";
        int totalParts = partCount ?? mapping.PartCount;
        if (totalParts > 1)
            name += $"-pt{partIdx ?? mapping.PartIndex}";
        else if (vIdx.HasValue)
            name += $"-v{vIdx}";

        string result = omitFileId ? name : $"{name} [{fileId}]";
        if (isVariation && totalParts <= 1)
            result += "[variation]";
        return result + ext;
    }

    /// <summary>Builds a filename for extra content (trailers, credits, etc.) using special type prefixes and title metadata.</summary>
    /// <param name="mapping">The mapping containing coordinates and metadata.</param>
    /// <param name="ex">A tuple containing the target folder and the Plex extra subtype.</param>
    /// <param name="pad">The number of digits to pad the extra number with.</param>
    /// <param name="ext">The file extension (including the dot).</param>
    /// <param name="seriesTitle">The display title of the parent series used for name resolution.</param>
    /// <param name="partIdx">Optional 1-based index for multi-part extras.</param>
    /// <param name="partCount">Optional total number of parts for multi-part extras.</param>
    /// <param name="vIdx">Optional version index for duplicate extras.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A sanitized and formatted filename string for Plex extras (e.g., "C01 ❯ Episode Title [variation].mkv").</returns>
    public static string BuildExtrasFileName(
        MapHelper.FileMapping mapping,
        (string Folder, string Subtype) ex,
        int pad,
        string ext,
        string seriesTitle,
        int? partIdx = null,
        int? partCount = null,
        int? vIdx = null,
        bool isVariation = false
    )
    {
        string ep = s_extraTypePrefixes.TryGetValue(ex.Subtype, out var pref) ? pref + mapping.Coords.Episode.ToString($"D{pad}") : mapping.Coords.Episode.ToString($"D{pad}");
        int totalParts = partCount ?? mapping.PartCount;
        string part = totalParts > 1 ? $"-pt{partIdx ?? mapping.PartIndex}" : (vIdx.HasValue ? $"-v{vIdx}" : "");

        string name = $"{ep}{part} ❯ {CleanEpisodeTitleForFilename(TextHelper.ResolveEpisodeTitle(mapping.PrimaryEpisode, seriesTitle))}";
        if (isVariation && totalParts <= 1)
            name += "[variation]";
        return SanitizeName(name + ext);
    }

    #endregion

    #region VFS/Plex Helpers

    /// <summary>Determine if a filename contains a Plex-style split tag (e.g. "pt1").</summary>
    /// <param name="fileName">The filename to check.</param>
    /// <returns>True if a split tag is found.</returns>
    public static bool HasPlexSplitTag(string fileName) => !string.IsNullOrWhiteSpace(fileName) && s_plexSplitTagRegex.IsMatch(Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' '));

    /// <summary>Identifies local extra directories with optional season suffixes.</summary>
    /// <param name="name">The directory name to evaluate.</param>
    /// <returns>A Match object containing the extra type and optional season number.</returns>
    public static Match MatchLocalExtraDir(string name) => s_localExtraDirRegex.Match(name);

    /// <summary>Identifies local extra files based on Plex naming suffixes (e.g., "-trailer").</summary>
    /// <param name="fileNameWithoutExtension">The filename without its extension to evaluate.</param>
    /// <returns>A Match object indicating success or failure.</returns>
    public static Match MatchLocalExtraFile(string fileNameWithoutExtension) => s_localExtraFileRegex.Match(fileNameWithoutExtension);

    #endregion

    #region File & Cleanup

    /// <summary>Perform diff-based cleanup of unexpected files and empty directories left behind by renames or moves.</summary>
    /// <param name="resolvedVfsSeriesPaths">The unique set of VFS paths involved in the build.</param>
    /// <param name="expectedFiles">The tracked set of files that should exist.</param>
    public static void CleanupOrphanedFilesAndFolders(HashSet<string> resolvedVfsSeriesPaths, HashSet<string> expectedFiles)
    {
        string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
        foreach (var seriesPath in resolvedVfsSeriesPaths)
        {
            if (!Directory.Exists(seriesPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(seriesPath, "*", SearchOption.AllDirectories))
            {
                if (!expectedFiles.Contains(file))
                {
                    try
                    {
                        var fi = new FileInfo(file);

                        // Allow AnimeThemesMapping to manage its own files
                        if (fi.LinkTarget != null && fi.LinkTarget.Contains(themeRootName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        File.Delete(file);
                    }
                    catch { }
                }
            }

            var dirs = Directory.EnumerateDirectories(seriesPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
                }
                catch { }
            }
        }
    }

    /// <summary>Checks if a mapped theme file physically exists within the import root, using an in-memory cache to prevent disk thrashing.</summary>
    /// <param name="relativePath">The relative path of the theme file inside the !AnimeThemes folder.</param>
    /// <param name="importRoot">The physical import root directory.</param>
    /// <param name="themeRootName">The name of the AnimeThemes directory.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <returns>The absolute path of the theme file if it exists; otherwise, null.</returns>
    public static string? GetThemeSourcePath(string relativePath, string importRoot, string themeRootName, VfsBuildSession session)
    {
        string themeRootPath = Path.Combine(importRoot, themeRootName);
        var cache = session
            .PhysicalThemeCaches.GetOrAdd(
                themeRootPath,
                root => new Lazy<Dictionary<string, string>>(() =>
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (Directory.Exists(root))
                    {
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(root, "*.webm", SearchOption.AllDirectories))
                                dict[Path.GetRelativePath(root, file).Replace('\\', '/').TrimStart('/')] = Path.GetFullPath(file);
                        }
                        catch { }
                    }
                    return dict;
                })
            )
            .Value;

        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        return cache.TryGetValue(normalizedRel, out string? absPath) ? absPath : null;
    }

    #endregion
}
