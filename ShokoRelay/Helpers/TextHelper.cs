using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Helpers;

/// <summary>Provides a centralized collection of text processing utilities including Regex-based cleaning, language-based title resolution, and summary sanitization.</summary>
public static partial class TextHelper
{
    #region Compiled Regex

    /// <summary>Regex for isolating common prefixes from series titles.</summary>
    [GeneratedRegex(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)")]
    private static partial Regex SeriesPrefixRegex();

    /// <summary>Regex for removing redundant movie descriptors from titles.</summary>
    [GeneratedRegex(@"(?i)(:? The)?( Movie| Motion Picture)")]
    private static partial Regex MovieDescriptorRegex();

    /// <summary>Regex for identifying default or ambiguous episode titles.</summary>
    [GeneratedRegex(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$")]
    private static partial Regex DefaultTitleRegex();

    /// <summary>Regex for isolating and removing source notes from summaries.</summary>
    [GeneratedRegex(@"(?m)^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceNoteSummaryRegex();

    /// <summary>Regex for isolating and removing list indicators from summaries.</summary>
    [GeneratedRegex(@"(?m)^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ListIndicatorRegex();

    /// <summary>Regex for cleaning up AniDB links in summaries.</summary>
    [GeneratedRegex(@"(?:http:\/\/anidb\.net\/(?:ch|co|cr|[feast]|(?:character|creator|file|episode|anime|tag)\/)(?:\d+)) \[([^\]]+)]")]
    private static partial Regex AniDBLinkRegex();

    /// <summary>Regex for stripping broken BBCode italic tags from specific AniDB summaries.</summary>
    [GeneratedRegex(@"(?is)\[i\](?!""The Sasami|""Stellar|In the distant| occurred in)(.*?)\[\/i\]")]
    private static partial Regex BbCodeItalicBugRegex();

    /// <summary>Regex for removing solitary BBCode italic tags.</summary>
    [GeneratedRegex(@"\[\/?i\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbCodeSolitaryRegex();

    /// <summary>Regex for condensing multiple newlines into a single line break.</summary>
    [GeneratedRegex(@"(\r?\n\s*){2,}")]
    private static partial Regex CondenseLinesRegex();

    /// <summary>Regex for condensing multiple spaces into a single space.</summary>
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CondenseSpacesRegex();

    /// <summary>Regex for decoding Unicode escape sequences.</summary>
    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    #endregion

    #region General Text Helpers

    /// <summary>Replace runs of two or more whitespace characters with a single space.</summary>
    /// <param name="input">The string to process.</param>
    /// <returns>The condensed string.</returns>
    public static string CondenseSpaces(string input) => CondenseSpacesRegex().Replace(input, " ");

    /// <summary>Replace literal commas with the unicode escape \u002C.</summary>
    /// <param name="value">The string to escape.</param>
    /// <returns>A CSV-safe string.</returns>
    public static string EscapeCsvCommas(string value) => value?.Replace(",", "\\u002C") ?? string.Empty;

    /// <summary>Decode \uXXXX escape sequences back into their actual Unicode characters.</summary>
    /// <param name="value">The string containing escape sequences.</param>
    /// <returns>A decoded string.</returns>
    public static string UnescapeUnicode(string value) =>
        string.IsNullOrEmpty(value) || !value.Contains(@"\u", StringComparison.Ordinal) ? value : UnicodeEscapeRegex().Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

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
    /// <param name="item">Object that exposes an Overviews collection.</param>
    /// <param name="languageSetting">Comma-separated preferred language codes.</param>
    /// <returns>The best matching description string.</returns>
    public static string GetDescriptionByLanguage(IWithOverviews item, string languageSetting) =>
        GetByLanguage(languageSetting, item.PreferredOverview?.Value, item.Overviews, d => d.LanguageCode, d => d.Value);

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
        string display = (Settings.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(raw)) ? SeriesPrefixRegex().Replace(raw, "$2 — $1") : raw;

        // Get Alternate Title according to the language preference
        string? alt = GetTitleByLanguage(series, Settings.SeriesAltTitleLanguage);

        // Duplicate check to avoid redundant metadata
        string? finalAlt = (string.IsNullOrEmpty(alt) || alt.Equals(raw, StringComparison.OrdinalIgnoreCase) || alt.Equals(display, StringComparison.OrdinalIgnoreCase)) ? null : alt;

        // Append Alternate to Sort Title to make it searchable in Plex UI
        string sortTitle = string.IsNullOrWhiteSpace(finalAlt) ? display : $"{display} – {finalAlt}";

        return (display, sortTitle, finalAlt);
    }

    /// <summary>Resolves the configured display title for a series respecting plugin language preferences.</summary>
    /// <param name="series">The series metadata.</param>
    /// <returns>The resolved display title string, or null if series is null.</returns>
    public static string? GetDisplayTitle(this ISeries? series) => series != null ? ResolveFullSeriesTitles(series).DisplayTitle : null;

    /// <summary>Compute the best title to display for an episode, handling ambiguous names and TMDB reassignments.</summary>
    /// <param name="ep">Episode metadata.</param>
    /// <param name="displaySeriesTitle">Resolved series title for fallback.</param>
    /// <param name="tmdbEp">Optional pre-resolved TMDB episode override.</param>
    /// <returns>The resolved episode title string.</returns>
    public static string ResolveEpisodeTitle(IEpisode ep, string displaySeriesTitle, IEpisode? tmdbEp = null)
    {
        string raw = GetTitleByLanguage(ep, Settings.EpisodeTitleLanguage);
        string? tmdbTitle = tmdbEp?.PreferredTitle?.Value ?? (ep as IShokoEpisode)?.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault()?.PreferredTitle?.Value;

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
                string result = (raw == "Complete Movie") ? MovieDescriptorRegex().Replace(title, "").Trim() : title;
                return $"{result} — {raw}";
            }
            return title;
        }

        // If TMDB episode group names enabled and multiple links exist, prefer TMDB titles
        if (Settings.TmdbEpGroupNames && ep is IShokoEpisode tmdbGrouped && tmdbGrouped.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 1 } && !string.IsNullOrEmpty(tmdbTitle))
            return tmdbTitle;

        // Standard enumeration override (e.g. "Episode 1" -> "Actual Title")
        return (!string.IsNullOrEmpty(tmdbTitle) && DefaultTitleRegex().IsMatch(raw) && !DefaultTitleRegex().IsMatch(tmdbTitle)) ? tmdbTitle : raw;
    }

    /// <summary>Compute the best title to display for a standalone movie, omitting the episode title if the series only contains one main episode or if the episode title is ambiguous.</summary>
    /// <param name="ep">The movie episode metadata.</param>
    /// <param name="series">The parent series metadata.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata.</param>
    /// <returns>The resolved movie title string.</returns>
    public static string ResolveMovieTitle(IEpisode ep, ISeries series, IMovie? tmdbMovie)
    {
        var (sTitle, _, _) = ResolveFullSeriesTitles(series);
        if (string.IsNullOrWhiteSpace(sTitle) && tmdbMovie is IWithTitles mt)
            sTitle = GetTitleByLanguage(mt, Settings.SeriesTitleLanguage);
        if (string.IsNullOrWhiteSpace(sTitle))
            sTitle = "Unknown";

        // Fast-path: Check if there is more than 1 main episode by skipping the first match. This avoids iterating over the entire episode list.
        bool hasMultipleMainEpisodes = series.Episodes.Where(e => e.Type == EpisodeType.Episode).Skip(1).Any();
        if (!hasMultipleMainEpisodes)
            return sTitle;

        string raw = GetTitleByLanguage(ep, Settings.EpisodeTitleLanguage);

        // Append non-ambiguous episode title to series/movie title if not already present
        return !string.IsNullOrWhiteSpace(raw) && !s_ambiguousTitles.Contains(raw) && sTitle != raw && !sTitle.Contains(raw) ? $"{sTitle} — {raw}" : sTitle;
    }

    /// <summary>Sanitize AniDB summary and, if the result is empty, fall back to TMDB.</summary>
    /// <param name="summary">Primary summary.</param>
    /// <param name="tmdbSummary">Fallback summary.</param>
    /// <param name="mode">Sanitization level.</param>
    /// <returns>The cleanest available summary string.</returns>
    public static string SanitizeSummaryWithFallback(string? summary, string? tmdbSummary, SummaryMode mode) =>
        SummarySanitizer(summary, mode) is var result && !string.IsNullOrWhiteSpace(result) ? result : SummarySanitizer(tmdbSummary, mode);

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
            SummaryMode.FullySanitize => ListIndicatorRegex().Replace(SourceNoteSummaryRegex().Replace(s, ""), ""),
            SummaryMode.AllowInfoLines => ListIndicatorRegex().Replace(s, ""),
            SummaryMode.AllowMiscLines => SourceNoteSummaryRegex().Replace(s, ""),
            _ => s,
        };

        // Remove AniDB-specific artifacts and bugs
        s = AniDBLinkRegex().Replace(s, "$1"); // Resolve [Link] tags
        s = BbCodeItalicBugRegex().Replace(s, ""); // Cleanup known AniDB API italic bug content
        s = BbCodeSolitaryRegex().Replace(s, ""); // Strip leftover BBCode tags

        return CondenseSpacesRegex().Replace(CondenseLinesRegex().Replace(s, Environment.NewLine), " ").Trim(' ', '\r', '\n');
    }

    #endregion

    #region Plex Utils

    /// <summary>Normalizes directory separators to forward slashes and trims trailing slashes for Plex-compatible path comparison.</summary>
    /// <param name="path">The filesystem path to normalize.</param>
    /// <returns>A normalized path string.</returns>
    public static string NormalizePathForPlex(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Extracts the numeric Series or Episode ID from a path or string value.</summary>
    /// <param name="text">The string or path to parse.</param>
    /// <returns>The extracted integer ID, or null if none found.</returns>
    public static int? ExtractSeriesId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var segments = NormalizePathForPlex(text).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
            if (int.TryParse(segments[i], out int id))
                return id;

        return null;
    }

    /// <summary>Checks if a Plex string value (which mimics a boolean, e.g. "1") represents true.</summary>
    /// <param name="value">The Plex string value to check.</param>
    /// <returns>True if the string equals "1"; otherwise, false.</returns>
    public static bool IsPlexTrue(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    #endregion
}
