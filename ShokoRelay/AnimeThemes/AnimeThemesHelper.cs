using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

#region Data Models

/// <summary>Represents a single theme mapping between a local file and AnimeThemes identifiers.</summary>
/// <param name="FilePath">Relative path to the theme file.</param>
/// <param name="VideoId">AnimeThemes video identifier.</param>
/// <param name="AniDbId">AniDB anime identifier.</param>
/// <param name="NC">No Credits flag.</param>
/// <param name="Slug">Theme type slug (e.g., OP1, ED2).</param>
/// <param name="Version">Version number of the theme entry.</param>
/// <param name="ArtistName">Name of the performing artist.</param>
/// <param name="SongTitle">Title of the song.</param>
/// <param name="Lyrics">Presence of timed lyrics.</param>
/// <param name="Subbed">Presence of subtitles.</param>
/// <param name="Uncen">Uncensored status.</param>
/// <param name="NSFW">Not Safe For Work status.</param>
/// <param name="Spoiler">Spoiler status.</param>
/// <param name="Source">Media source type.</param>
/// <param name="Resolution">Vertical video resolution.</param>
/// <param name="Episodes">Episode range string.</param>
/// <param name="Overlap">Theme overlap status.</param>
public sealed record AnimeThemesMappingEntry(
    string FilePath,
    int VideoId,
    int AniDbId,
    bool NC,
    string Slug,
    int Version,
    string ArtistName,
    string SongTitle,
    bool Lyrics,
    bool Subbed,
    bool Uncen,
    bool NSFW,
    bool Spoiler,
    string Source,
    int Resolution,
    string Episodes,
    string Overlap
);

/// <summary>Result returned by a mapping file build operation.</summary>
/// <param name="MapPath">Absolute path to the generated mapping CSV.</param>
/// <param name="EntriesWritten">Count of entries written to the file.</param>
/// <param name="Reused">Count of entries reused from an existing mapping.</param>
/// <param name="Errors">Count of encountered errors.</param>
/// <param name="Messages">Detailed diagnostic messages.</param>
public sealed record AnimeThemesMappingBuildResult(string MapPath, int EntriesWritten, int Reused, int Errors, IReadOnlyList<string> Messages);

/// <summary>Represents an entry for the WebM VFS cache used by the video player.</summary>
/// <param name="VfsPath">Relative path within the VFS.</param>
/// <param name="VideoId">AnimeThemes video identifier.</param>
/// <param name="Bitmask">Attribute flags bitmask.</param>
public sealed record WebmCacheEntry(string VfsPath, int VideoId, int Bitmask);

/// <summary>Outcome of applying a mapping file to create VFS links.</summary>
/// <param name="LinksCreated">Count of successful symlinks.</param>
/// <param name="Skipped">Count of themes skipped due to missing files.</param>
/// <param name="SeriesMatched">Count of Shoko series that found theme matches.</param>
/// <param name="Errors">List of error messages.</param>
/// <param name="CacheEntries">Data generated for the standalone video player cache.</param>
/// <param name="Elapsed">Total time taken for the operation.</param>
public sealed record AnimeThemesMappingApplyResult(int LinksCreated, int Skipped, int SeriesMatched, IReadOnlyList<string> Errors, IReadOnlyList<WebmCacheEntry> CacheEntries, TimeSpan Elapsed);

/// <summary>Internal helper record used when looking up theme metadata by video identifier.</summary>
internal sealed record AnimeThemesVideoLookup(
    int VideoId,
    int ThemeId,
    int AniDbId,
    bool NC,
    string Slug,
    int Version,
    string ArtistName,
    string SongTitle,
    bool Lyrics,
    bool Subbed,
    bool Uncen,
    bool NSFW,
    bool Spoiler,
    string Source,
    int Resolution,
    string Episodes,
    string Overlap
);

#endregion

/// <summary>Shared constants and helper utilities used throughout the AnimeThemes subcomponent.</summary>
internal static class AnimeThemesHelper
{
    #region Constants & Fields

    internal const string AtApiBase = "https://api.animethemes.moe";
    internal const string AtMapFileName = "anidb_animethemes_xrefs.csv";
    internal const string AtFavsFileName = "favs_animethemes.cache";
    internal const string AtRawMapUrl = "https://gist.githubusercontent.com/natyusha/bb33a3b3bc95bc7a3869633e23d522bb/raw/";

    internal static readonly FrozenSet<string> VideoFileExtensions = FrozenSet.ToFrozenSet([".mkv", ".avi", ".mp4", ".mov", ".ogm", ".wmv", ".mpg", ".mpeg", ".mk3d", ".m4v"], StringComparer.OrdinalIgnoreCase);
    internal static readonly Regex SlugRegex = new("^(?:op|ed)(?!0)[0-9]{0,2}(?:-(?:bd|web|tv|original))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> SlugFormatting = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Animax", "Animax" },
        { "ATX", "AT-X" },
        { "BD", "Blu-ray" },
        { "BestSelection", "Best Selection" },
        { "EN", "English" },
        { "HD", "High Definition" },
        { "KO", "Korean" },
        { "NoitaminA", "noitaminA" },
        { "ORIGINAL", "Original" },
        { "Oversea", "Overseas" },
        { "Plus", "Plus" },
        { "RepeatShow", "Repeat Show" },
        { "ShounenHen", "Shounen-hen" },
        { "Sound", "Sound" },
        { "Theatrical2", "Theatrical 2" },
        { "Theatrical3", "Theatrical 3" },
        { "Theatrical4", "Theatrical 4" },
        { "Theatrical5", "Theatrical 5" },
        { "Theatrical6", "Theatrical 6" },
        { "Theatrical7", "Theatrical 7" },
        { "Theatrical8", "Theatrical 8" },
        { "TV", "Broadcast" },
        { "WEB", "Web" },
        { "YorinukiGintamaSan", "Yorinuki Gintama-san" },
    };

    #endregion

    #region Initialization

    /// <summary>Add a default User-Agent header to the client if none is present.</summary>
    /// <param name="client">The HttpClient to configure.</param>
    internal static void EnsureUserAgent(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShokoRelay", ShokoRelayInfo.Version));
    }

    #endregion

    #region CSV Logic

    /// <summary>Parses a CSV string into a list of mapping entries.</summary>
    /// <param name="content">The raw CSV content string.</param>
    /// <returns>A list of parsed entries.</returns>
    internal static List<AnimeThemesMappingEntry> ParseMappingContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        var result = new List<AnimeThemesMappingEntry>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            var f = TextHelper.SplitCsvLine(line);
            if (f.Length < 17 || !int.TryParse(f[1], out int vid) || !int.TryParse(f[2], out int aid) || !int.TryParse(f[5], out int ver))
                continue;

            result.Add(
                new AnimeThemesMappingEntry(
                    f[0],
                    vid,
                    aid,
                    f[3] == "1",
                    f[4],
                    ver,
                    TextHelper.UnescapeUnicode(f[7]),
                    TextHelper.UnescapeUnicode(f[6]),
                    f[8] == "1",
                    f[9] == "1",
                    f[10] == "1",
                    f[11] == "1",
                    f[12] == "1",
                    f[13],
                    int.TryParse(f[14], out var res) ? res : 0,
                    f[15],
                    f[16]
                )
            );
        }
        return result;
    }

    /// <summary>Serializes a list of mapping entries into a CSV string with standard header.</summary>
    /// <param name="entries">The list of entries to serialize.</param>
    /// <returns>A formatted CSV string.</returns>
    internal static string SerializeMapping(List<AnimeThemesMappingEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Shoko Relay AniDB AnimeThemes Xrefs ##\n");
        sb.AppendLine("# filepath, videoId, anidbId, nc, slug, version, songTitle, artistName, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap");
        foreach (var e in entries)
            sb.AppendLine(SerializeEntry(e));
        return sb.ToString();
    }

    /// <summary>Serialize a single AnimeThemesMappingEntry to a comma-separated line.</summary>
    /// <param name="e">The entry to serialize.</param>
    /// <returns>A CSV formatted string line.</returns>
    internal static string SerializeEntry(AnimeThemesMappingEntry e)
    {
        return $"{e.FilePath},{e.VideoId},{e.AniDbId},{(e.NC ? "1" : "0")},{e.Slug},{e.Version},"
            + $"{TextHelper.EscapeCsvCommas(e.SongTitle)},{TextHelper.EscapeCsvCommas(e.ArtistName)},"
            + $"{(e.Lyrics ? "1" : "0")},{(e.Subbed ? "1" : "0")},{(e.Uncen ? "1" : "0")},"
            + $"{(e.NSFW ? "1" : "0")},{(e.Spoiler ? "1" : "0")},{e.Source},{e.Resolution},"
            + $"{TextHelper.EscapeCsvCommas(e.Episodes)},{e.Overlap}";
    }

    #endregion

    #region Naming & Path Logic

    /// <summary>Calculates a bitmask for metadata flags used in the webm cache.</summary>
    /// <param name="e">The mapping entry.</param>
    /// <returns>An integer bitmask.</returns>
    internal static int CalculateBitmask(AnimeThemesMappingEntry e)
    {
        int flags = 0;
        if (e.NC)
            flags |= 1;
        if (e.Lyrics)
            flags |= 2;
        if (e.Subbed)
            flags |= 4;
        if (e.Uncen)
            flags |= 8;
        if (e.NSFW)
            flags |= 16;
        if (e.Spoiler)
            flags |= 32;
        if (e.Overlap == "Transition")
            flags |= 64;
        else if (e.Overlap == "Over")
            flags |= 128;
        return flags;
    }

    /// <summary>
    /// Constructs a sanitized filename from theme metadata.
    /// </summary>
    /// <param name="lookup">The metadata lookup object.</param>
    /// <param name="extension">The file extension to append.</param>
    /// <param name="overrideIndex">The 0-based index of the series within an override group.</param>
    /// <returns>A formatted and sanitized filename.</returns>
    internal static string BuildNewFileName(AnimeThemesVideoLookup lookup, string extension, int overrideIndex = 0)
    {
        var (baseSlug, slugSuffix) = ParseSlug(lookup.Slug ?? "");
        string overridePrefix = overrideIndex > 0 ? $"P{overrideIndex + 1} ❯ " : "";
        string nc = lookup.NC ? "NC" : "";
        string slug = string.IsNullOrWhiteSpace(baseSlug) ? "Theme" : baseSlug;
        const string zwsp = "\u200B",
            hsp = "\u200A";

        if (slug.StartsWith("OP", StringComparison.OrdinalIgnoreCase))
            slug = $"{hsp}O{zwsp}P{slug[2..]}";
        else if (slug.StartsWith("ED", StringComparison.OrdinalIgnoreCase))
            slug = $"E{zwsp}D{slug[2..]}";

        string ver = lookup.Version > 1 ? $"v{lookup.Version}" : "";
        string title = string.IsNullOrWhiteSpace(lookup.SongTitle) ? "" : " ❯ " + lookup.SongTitle;
        string slugTag = FormatSlugTag(slugSuffix);

        var artistList = !string.IsNullOrWhiteSpace(lookup.ArtistName) ? lookup.ArtistName.Split([" / "], StringSplitOptions.RemoveEmptyEntries) : [];
        string artistDisplay = artistList.Length switch
        {
            >= 4 => "Various Artists",
            3 => $"{artistList[0]}, {artistList[1]} & {artistList[2]}",
            2 => $"{artistList[0]} & {artistList[1]}",
            1 => artistList[0],
            _ => "",
        };
        string artistStr = string.IsNullOrWhiteSpace(artistDisplay) ? "" : " ❯ " + artistDisplay;

        var attr = new List<string>();
        if (lookup.Lyrics)
            attr.Add("LYRICS");
        if (lookup.Subbed)
            attr.Add("SUBS");
        if (lookup.Uncen)
            attr.Add("UNCEN");
        if (lookup.NSFW)
            attr.Add("NSFW");
        if (lookup.Spoiler)
            attr.Add("SPOIL");
        if (lookup.Overlap == "Transition")
            attr.Add("TRANS");
        else if (lookup.Overlap == "Over")
            attr.Add("OVER");

        string attrStr = (ShokoRelay.Settings.Advanced.AnimeThemesAppendTags && attr.Count > 0) ? $" [{string.Join(", ", attr)}]" : "";
        return VfsHelper.CleanEpisodeTitleForFilename($"{overridePrefix}{nc}{slug}{ver}{title}{slugTag}{artistStr}{attrStr}{extension}");
    }

    /// <summary>Parses a theme slug into base and suffix components.</summary>
    /// <param name="slug">The raw slug from AnimeThemes.</param>
    /// <returns>A tuple containing the base slug and suffix.</returns>
    internal static (string baseSlug, string? suffix) ParseSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return ("", null);
        int dash = slug.IndexOf('-');
        string b = dash < 0 ? slug : slug[..dash];
        if (b.Equals("OP1", StringComparison.OrdinalIgnoreCase) || b.Equals("ED1", StringComparison.OrdinalIgnoreCase))
            b = b[..2];
        return (b, dash < 0 ? null : slug[(dash + 1)..]);
    }

    /// <summary>Formats a slug variant suffix into a human-readable tag.</summary>
    /// <param name="suffix">The slug suffix (e.g. BD, TV).</param>
    /// <returns>A formatted string tag.</returns>
    internal static string FormatSlugTag(string? suffix) => string.IsNullOrWhiteSpace(suffix) ? "" : $" ({(SlugFormatting.TryGetValue(suffix.Trim(), out var f) ? f : suffix)})";

    /// <summary>Ensures a filename has the specified extension.</summary>
    /// <param name="fileName">The filename.</param>
    /// <param name="ext">The extension including dot.</param>
    /// <returns>The filename with extension.</returns>
    internal static string EnsureExtension(string fileName, string ext) => string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) ? fileName + ext : fileName;

    /// <summary>Resolves the absolute source path for a theme file.</summary>
    /// <param name="relativeFilePath">Relative path from the theme root.</param>
    /// <param name="importRoot">The Shoko import root.</param>
    /// <param name="themeRootFolder">The AnimeThemes folder name.</param>
    /// <returns>The full path or null if missing.</returns>
    internal static string? ResolveThemeSourcePath(string relativeFilePath, string importRoot, string themeRootFolder)
    {
        string path = Path.Combine(importRoot, themeRootFolder, relativeFilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>Builds the relative target path for a symlink to point back to the theme root.</summary>
    /// <param name="relativeFilePath">Relative path to the theme file.</param>
    /// <param name="themeRootFolder">The name of the AnimeThemes folder.</param>
    /// <returns>A relative path string.</returns>
    internal static string BuildThemeRelativeTarget(string relativeFilePath, string themeRootFolder) =>
        Path.Combine("..", "..", "..", themeRootFolder, relativeFilePath.TrimStart('/', '\\')).Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    #endregion
}
