using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Config;

namespace ShokoRelay.Helpers;

/// <summary>Provides a centralized collection of text processing utilities including Regex-based cleaning, language-based title resolution, and summary sanitization.</summary>
public static class TextHelper
{
    #region Compiled Regex

    private static readonly Regex _seriesPrefixRegex = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
    private static readonly Regex _movieDescriptorRegex = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);
    private static readonly Regex _defaultTitleRegex = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex _sourceNoteSummaryRegex = new(
        @"(?m)^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex _listIndicatorRegex = new(@"(?m)^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _aniDBLinkRegex = new(@"(?:http:\/\/anidb\.net\/(?:ch|co|cr|[feast]|(?:character|creator|file|episode|anime|tag)\/)(?:\d+)) \[([^\]]+)]", RegexOptions.Compiled);
    private static readonly Regex _bbCodeItalicBugRegex = new(
        @"(?is)\[i\](?!" + Regex.Escape("\"The Sasami") + @"|" + Regex.Escape("\"Stellar") + @"|In the distant| occurred in)(.*?)\[\/i\]",
        RegexOptions.Compiled
    );
    private static readonly Regex _bbCodeSolitaryRegex = new(@"\[\/?i\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _condenseLinesRegex = new(@"(\r?\n\s*){2,}", RegexOptions.Compiled);
    private static readonly Regex _condenseSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex _plexSplitTagRegex = new(@"(?ix)(?:^|[\s._-])(cd|disc|disk|dvd|part|pt)[\s._-]*([1-8])(?!\d)", RegexOptions.Compiled);
    private static readonly Regex _numbersRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex _unicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    #endregion

    #region General Text Helpers

    /// <summary>Replace runs of two or more whitespace characters with a single space.</summary>
    /// <param name="input">The string to process.</param>
    /// <returns>The condensed string.</returns>
    public static string CondenseSpaces(string input) => _condenseSpacesRegex.Replace(input, " ");

    /// <summary>Replace literal commas with the unicode escape \u002C.</summary>
    /// <param name="value">The string to escape.</param>
    /// <returns>A CSV-safe string.</returns>
    public static string EscapeCsvCommas(string value) => value?.Replace(",", "\\u002C") ?? string.Empty;

    /// <summary>Decode \uXXXX escape sequences back into their actual Unicode characters.</summary>
    /// <param name="value">The string containing escape sequences.</param>
    /// <returns>A decoded string.</returns>
    public static string UnescapeUnicode(string value) => string.IsNullOrEmpty(value) ? value : _unicodeEscapeRegex.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>Splits a CSV line on commas.</summary>
    /// <param name="line">The raw CSV line.</param>
    /// <returns>An array of fields.</returns>
    public static string[] SplitCsvLine(string line) => line?.Split(',') ?? [];

    #endregion

    #region Metadata Resolution

    private static readonly IReadOnlySet<string> _ambiguousTitles = new HashSet<string>(
        ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Return an item's title according to a comma-separated list of preferred language codes.</summary>
    /// <param name="item">Object that exposes a Titles collection.</param>
    /// <param name="languageSetting">Comma-separated preferred language codes.</param>
    /// <returns>The best matching title string.</returns>
    public static string GetTitleByLanguage(IWithTitles item, string languageSetting) => GetByLanguage(languageSetting, item.PreferredTitle?.Value, item.Titles, t => t.LanguageCode, t => t.Value);

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
    /// <param name="series">Series metadata.</param>
    /// <returns>A tuple of (DisplayTitle, SortTitle, OriginalTitle).</returns>
    public static (string DisplayTitle, string SortTitle, string? OriginalTitle) ResolveFullSeriesTitles(ISeries series)
    {
        // Get Title according to the language preference
        string raw = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesTitleLanguage);

        // Move common title prefixes to the end of the title (e.g. OVA, Eiga)
        string display = (ShokoRelay.Settings.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(raw)) ? _seriesPrefixRegex.Replace(raw, "$2 — $1") : raw;

        // Get Alternate Title according to the language preference
        string? alt = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesAltTitleLanguage);

        // Duplicate check to avoid redundant metadata
        bool isDup = string.IsNullOrEmpty(alt) || alt.Equals(raw, StringComparison.OrdinalIgnoreCase) || alt.Equals(display, StringComparison.OrdinalIgnoreCase);
        string? finalAlt = isDup ? null : alt;

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
        string raw = GetTitleByLanguage(ep, ShokoRelay.Settings.EpisodeTitleLanguage);
        string? tmdbTitle = (ep as IShokoEpisode)?.TmdbEpisodes.FirstOrDefault()?.PreferredTitle?.Value;

        // Replace ambiguous single entry titles (like "OVA") with the series title
        if (ep.EpisodeNumber == 1 && _ambiguousTitles.Contains(raw))
        {
            string title = displaySeriesTitle;

            // Fallback to TMDB title or English series title as a last resort
            if (title == raw)
                title = tmdbTitle ?? (ep.Series != null ? GetTitleByLanguage(ep.Series, "en") : raw);

            // Append ambiguous title to series title if not already present
            if (title != raw && !title.Contains(raw))
            {
                // Reduce redundant movie descriptors for cleaner Plex display
                string result = (raw == "Complete Movie") ? _movieDescriptorRegex.Replace(title, "").Trim() : title;
                return $"{result} — {raw}";
            }
            return title;
        }

        // If TMDB episode group names enabled and multiple links exist, prefer TMDB titles
        if (ShokoRelay.Settings.TmdbEpGroupNames && ep is IShokoEpisode { TmdbEpisodes.Count: > 1 } && !string.IsNullOrEmpty(tmdbTitle))
            return tmdbTitle;

        // Standard enumeration override (e.g. "Episode 1" -> "Actual Title")
        return (!string.IsNullOrEmpty(tmdbTitle) && _defaultTitleRegex.IsMatch(raw) && !_defaultTitleRegex.IsMatch(tmdbTitle)) ? tmdbTitle : raw;
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
            SummaryMode.FullySanitize => _listIndicatorRegex.Replace(_sourceNoteSummaryRegex.Replace(s, ""), ""),
            SummaryMode.AllowInfoLines => _listIndicatorRegex.Replace(s, ""),
            SummaryMode.AllowMiscLines => _sourceNoteSummaryRegex.Replace(s, ""),
            _ => s,
        };

        // Remove AniDB-specific artifacts and bugs
        s = _aniDBLinkRegex.Replace(s, "$1"); // Resolve [Link] tags
        s = _bbCodeItalicBugRegex.Replace(s, ""); // Cleanup known AniDB API italic bug content
        s = _bbCodeSolitaryRegex.Replace(s, ""); // Strip leftover BBCode tags

        return _condenseSpacesRegex.Replace(_condenseLinesRegex.Replace(s, Environment.NewLine), " ").Trim(' ', '\r', '\n');
    }

    #endregion

    #region VFS/Plex Utils

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
        string.IsNullOrWhiteSpace(value) ? "" : _condenseSpacesRegex.Replace(new string([.. value.Where(c => !ReplacementCharMap.ContainsKey(c))]).Trim(), " ");

    /// <summary>Determine if a filename contains a Plex-style split tag (e.g. "pt1").</summary>
    /// <param name="fileName">The filename to check.</param>
    /// <returns>True if a split tag is found.</returns>
    public static bool HasPlexSplitTag(string fileName) => !string.IsNullOrWhiteSpace(fileName) && _plexSplitTagRegex.IsMatch(Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' '));

    /// <summary>Extracts the first sequence of digits from a string (Series ID lookup).</summary>
    /// <param name="text">The string to parse.</param>
    /// <returns>The extracted integer, or null.</returns>
    public static int? ExtractSeriesId(string? text) => (text != null && _numbersRegex.Match(text) is { Success: true } m && int.TryParse(m.Value, out var id)) ? id : null;

    #endregion
}
