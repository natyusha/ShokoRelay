using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Helpers;

/// <summary>Provides a centralized collection of text processing utilities including Regex-based cleaning, language-based title resolution, and summary sanitization.</summary>
public static class TextHelper
{
    #region Compiled Regex

    private static readonly Regex s_seriesPrefixRegex = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
    private static readonly Regex s_movieDescriptorRegex = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);
    private static readonly Regex s_defaultTitleRegex = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex s_sourceNoteSummaryRegex = new(
        @"(?m)^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex s_listIndicatorRegex = new(@"(?m)^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_aniDBLinkRegex = new(@"(?:http:\/\/anidb\.net\/(?:ch|co|cr|[feast]|(?:character|creator|file|episode|anime|tag)\/)(?:\d+)) \[([^\]]+)]", RegexOptions.Compiled);
    private static readonly Regex s_bbCodeItalicBugRegex = new(
        @"(?is)\[i\](?!" + Regex.Escape("\"The Sasami") + @"|" + Regex.Escape("\"Stellar") + @"|In the distant| occurred in)(.*?)\[\/i\]",
        RegexOptions.Compiled
    );
    private static readonly Regex s_bbCodeSolitaryRegex = new(@"\[\/?i\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_condenseLinesRegex = new(@"(\r?\n\s*){2,}", RegexOptions.Compiled);
    private static readonly Regex s_condenseSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex s_plexSplitTagRegex = new(@"(?ix)(?:^|[\s._-])(cd|disc|disk|dvd|part|pt)[\s._-]*([1-8])(?!\d)", RegexOptions.Compiled);
    private static readonly Regex s_numbersRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex s_unicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);
    private static readonly Regex s_localExtraDirRegex = new($@"^({string.Join("|", PlexConstants.LocalExtraDirs)})(\s+[sS](\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_localExtraFileRegex = new($@"-(?:behindthescenes|deleted|featurette|interview|scene|short|trailer|other)\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    #endregion

    #region General Text Helpers

    /// <summary>Replace runs of two or more whitespace characters with a single space.</summary>
    /// <param name="input">The string to process.</param>
    /// <returns>The condensed string.</returns>
    public static string CondenseSpaces(string input) => s_condenseSpacesRegex.Replace(input, " ");

    /// <summary>Replace literal commas with the unicode escape \u002C.</summary>
    /// <param name="value">The string to escape.</param>
    /// <returns>A CSV-safe string.</returns>
    public static string EscapeCsvCommas(string value) => value?.Replace(",", "\\u002C") ?? string.Empty;

    /// <summary>Decode \uXXXX escape sequences back into their actual Unicode characters.</summary>
    /// <param name="value">The string containing escape sequences.</param>
    /// <returns>A decoded string.</returns>
    public static string UnescapeUnicode(string value) => string.IsNullOrEmpty(value) ? value : s_unicodeEscapeRegex.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>Splits a CSV line on commas.</summary>
    /// <param name="line">The raw CSV line.</param>
    /// <returns>An array of fields.</returns>
    public static string[] SplitCsvLine(string line) => line?.Split(',') ?? [];

    #endregion

    #region Metadata Resolution

    /// <summary>Set of ambiguous AniDB episode titles that should be overridden by the series title.</summary>
    private static readonly IReadOnlySet<string> s_ambiguousTitles = new HashSet<string>(
        ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Return an item's title according to preferred language codes, excluding short titles and prioritizing official/main types.</summary>
    /// <param name="item">Object that exposes a Titles collection.</param>
    /// <param name="languageSetting">Comma-separated preferred language codes.</param>
    /// <returns>The best matching title string.</returns>
    public static string GetTitleByLanguage(IWithTitles item, string languageSetting) =>
        GetByLanguage(
            languageSetting,
            item.PreferredTitle?.Value,
            item.Titles.Where(t => t.Type != TitleType.Short)
                .OrderBy(t =>
                    t.Type switch
                    {
                        TitleType.Main => 1,
                        TitleType.Official => 2,
                        TitleType.Synonym => 3,
                        _ => 4,
                    }
                ),
            t => t.LanguageCode,
            t => t.Value
        );

    /// <summary>Return an item's description according to a comma-separated list of preferred language codes.</summary>
    /// <param name="item">Object that exposes a Descriptions collection.</param>
    /// <param name="languageSetting">Comma-separated preferred language codes.</param>
    /// <returns>The best matching description string.</returns>
    public static string GetDescriptionByLanguage(IWithDescriptions item, string languageSetting) =>
        GetByLanguage(languageSetting, item.PreferredDescription?.Value, item.Descriptions, d => d.LanguageCode, d => d.Value);

    /// <summary>Selects the first non-empty value from a collection matching a priority list of language codes.</summary>
    /// <typeparam name="T">The type of items in the collection.</typeparam>
    /// <param name="languageSetting">Comma-separated preferred language codes.</param>
    /// <param name="preferredValue">The default value to return if "shoko" is selected or as a final fallback.</param>
    /// <param name="collection">The collection of metadata items to search.</param>
    /// <param name="getLangCode">Function to extract the language code from a collection item.</param>
    /// <param name="getValue">Function to extract the text value from a collection item.</param>
    /// <returns>The resolved text value string.</returns>
    private static string GetByLanguage<T>(string languageSetting, string? preferredValue, IEnumerable<T> collection, Func<T, string> getLangCode, Func<T, string> getValue)
    {
        if (string.IsNullOrWhiteSpace(languageSetting))
            return preferredValue ?? "";

        var languages = languageSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var lang in languages)
        {
            if (lang.Equals("shoko", StringComparison.OrdinalIgnoreCase))
                return preferredValue ?? "";

            var match = collection.FirstOrDefault(x => getLangCode(x).Equals(lang, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(getValue(x)));
            if (match != null)
                return getValue(match);
        }
        return preferredValue ?? "";
    }

    /// <summary>Determine display, sortable, and original titles for a series based on preferences and prefix reordering settings.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>A tuple of (DisplayTitle, SortTitle, OriginalTitle).</returns>
    public static (string DisplayTitle, string SortTitle, string? OriginalTitle) ResolveFullSeriesTitles(ISeries series)
    {
        // Get Title according to the language preference
        string raw = GetTitleByLanguage(series, Settings.SeriesTitleLanguage);

        // Move common title prefixes to the end of the title (e.g. OVA, Eiga)
        string display = (Settings.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(raw)) ? s_seriesPrefixRegex.Replace(raw, "$2 — $1") : raw;

        // Get Alternate Title according to the language preference
        string? alt = GetTitleByLanguage(series, Settings.SeriesAltTitleLanguage);

        // Duplicate check to avoid redundant metadata
        string? finalAlt = (string.IsNullOrEmpty(alt) || alt.Equals(raw, StringComparison.OrdinalIgnoreCase) || alt.Equals(display, StringComparison.OrdinalIgnoreCase)) ? null : alt;

        // Append Alternate to Sort Title to make it searchable in Plex UI
        string sortTitle = string.IsNullOrWhiteSpace(finalAlt) ? display : $"{display} – {finalAlt}";

        return (display, sortTitle, finalAlt);
    }

    /// <summary>Compute the best title to display for an episode, handling ambiguous names and TMDB reassignments.</summary>
    /// <param name="ep">Episode metadata.</param>
    /// <param name="displaySeriesTitle">Resolved series title for fallback.</param>
    /// <returns>The resolved episode title string.</returns>
    public static string ResolveEpisodeTitle(IEpisode ep, string displaySeriesTitle)
    {
        string raw = GetTitleByLanguage(ep, Settings.EpisodeTitleLanguage);
        string? tmdbTitle = (ep as IShokoEpisode)?.TmdbEpisodes.FirstOrDefault()?.PreferredTitle?.Value;

        // Replace ambiguous single entry titles (like "OVA") with the series title
        if (ep.EpisodeNumber == 1 && s_ambiguousTitles.Contains(raw))
        {
            string title = displaySeriesTitle;

            // Fallback to TMDB title or English series title as a last resort
            if (title == raw)
                title = tmdbTitle ?? (ep.Series != null ? GetTitleByLanguage(ep.Series, "en") : raw);

            // Append ambiguous title to series title if not already present
            if (title != raw && !title.Contains(raw))
            {
                // Reduce redundant movie descriptors for cleaner Plex display
                string result = (raw == "Complete Movie") ? s_movieDescriptorRegex.Replace(title, "").Trim() : title;
                return $"{result} — {raw}";
            }
            return title;
        }

        // If TMDB episode group names enabled and multiple links exist, prefer TMDB titles
        if (Settings.TmdbEpGroupNames && ep is IShokoEpisode { TmdbEpisodes.Count: > 1 } && !string.IsNullOrEmpty(tmdbTitle))
            return tmdbTitle;

        // Standard enumeration override (e.g. "Episode 1" -> "Actual Title")
        return (!string.IsNullOrEmpty(tmdbTitle) && s_defaultTitleRegex.IsMatch(raw) && !s_defaultTitleRegex.IsMatch(tmdbTitle)) ? tmdbTitle : raw;
    }

    /// <summary>Sanitize AniDB summary and, if the result is empty, fall back to TMDB.</summary>
    /// <param name="summary">Primary summary.</param>
    /// <param name="tmdbSummary">Fallback summary.</param>
    /// <param name="mode">Sanitization level.</param>
    /// <returns>The cleanest available summary string.</returns>
    public static string SanitizeSummaryWithFallback(string? summary, string? tmdbSummary, SummaryMode mode)
    {
        var result = SummarySanitizer(summary, mode);
        return string.IsNullOrWhiteSpace(result) ? SummarySanitizer(tmdbSummary, mode) : result;
    }

    /// <summary>Clean up a summary string according to the configured sanitization mode (stripping notes, indicators, etc).</summary>
    /// <param name="s">The string to sanitize.</param>
    /// <param name="mode">The sanitization mode.</param>
    /// <returns>A sanitized summary string.</returns>
    public static string SummarySanitizer(string? s, SummaryMode mode)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        s = mode switch
        {
            SummaryMode.FullySanitize => s_listIndicatorRegex.Replace(s_sourceNoteSummaryRegex.Replace(s, ""), ""),
            SummaryMode.AllowInfoLines => s_listIndicatorRegex.Replace(s, ""),
            SummaryMode.AllowMiscLines => s_sourceNoteSummaryRegex.Replace(s, ""),
            _ => s,
        };

        // Remove AniDB-specific artifacts and bugs
        s = s_aniDBLinkRegex.Replace(s, "$1"); // Resolve [Link] tags
        s = s_bbCodeItalicBugRegex.Replace(s, ""); // Cleanup known AniDB API italic bug content
        s = s_bbCodeSolitaryRegex.Replace(s, ""); // Strip leftover BBCode tags

        return s_condenseSpacesRegex.Replace(s_condenseLinesRegex.Replace(s, Environment.NewLine), " ").Trim(' ', '\r', '\n');
    }

    #endregion

    #region VFS/Plex Utils

    /// <summary>Normalizes directory separators to forward slashes and trims trailing slashes for Plex-compatible path comparison.</summary>
    /// <param name="path">The filesystem path to normalize.</param>
    /// <returns>A normalized path string.</returns>
    public static string NormalizePathForPlex(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');

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

    /// <summary>Remove characters that are invalid in Windows filenames by replacing them with visually similar unicode characters.</summary>
    /// <param name="value">The string to clean.</param>
    /// <returns>A filename-safe string.</returns>
    public static string StripInvalidWindowsChars(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : s_condenseSpacesRegex.Replace(new string([.. value.Where(c => !ReplacementCharMap.ContainsKey(c))]).Trim(), " ");

    /// <summary>Determine if a filename contains a Plex-style split tag (e.g. "pt1").</summary>
    /// <param name="fileName">The filename to check.</param>
    /// <returns>True if a split tag is found.</returns>
    public static bool HasPlexSplitTag(string fileName) => !string.IsNullOrWhiteSpace(fileName) && s_plexSplitTagRegex.IsMatch(Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' '));

    /// <summary>Extracts the first sequence of digits from a string (Series ID lookup).</summary>
    /// <param name="text">The string to parse.</param>
    /// <returns>The extracted integer, or null.</returns>
    public static int? ExtractSeriesId(string? text) => (text != null && s_numbersRegex.Match(text) is { Success: true } m && int.TryParse(m.Value, out var id)) ? id : null;

    /// <summary>Identifies local extra directories with optional season suffixes.</summary>
    /// <param name="name">The directory name to evaluate.</param>
    /// <returns>A Match object containing the extra type and optional season number.</returns>
    public static Match MatchLocalExtraDir(string name) => s_localExtraDirRegex.Match(name);

    /// <summary>Identifies local extra files based on Plex naming suffixes (e.g., "-trailer").</summary>
    /// <param name="fileNameWithoutExtension">The filename without its extension to evaluate.</param>
    /// <returns>A Match object indicating success or failure.</returns>
    public static Match MatchLocalExtraFile(string fileNameWithoutExtension) => s_localExtraFileRegex.Match(fileNameWithoutExtension);

    /// <summary>Checks if a Plex string value (which mimics a boolean, e.g. "1") represents true.</summary>
    /// <param name="value">The Plex string value to check.</param>
    /// <returns>True if the string equals "1"; otherwise, false.</returns>
    public static bool IsPlexTrue(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    #endregion
}
