using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
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
    [GeneratedRegex(@"(:? The)?( Movie| Motion Picture)", RegexOptions.IgnoreCase)]
    private static partial Regex MovieDescriptorRegex();

    /// <summary>Regex for identifying default or ambiguous episode titles.</summary>
    [GeneratedRegex(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$")]
    private static partial Regex DefaultTitleRegex();

    /// <summary>Regex for isolating and removing source notes from summaries.</summary>
    [GeneratedRegex(@"^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SourceNoteSummaryRegex();

    /// <summary>Regex for isolating and removing list indicators from summaries.</summary>
    [GeneratedRegex(@"^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ListIndicatorRegex();

    /// <summary>Regex for cleaning up AniDB links in summaries.</summary>
    [GeneratedRegex(@"(?:http:\/\/anidb\.net\/(?:ch|co|cr|[feast]|(?:character|creator|file|episode|anime|tag)\/)(?:\d+)) \[([^\]]+)]")]
    private static partial Regex AniDBLinkRegex();

    /// <summary>Regex for stripping broken BBCode italic tags from specific AniDB summaries.</summary>
    [GeneratedRegex(@"\[i\](?!""The Sasami|""Stellar|In the distant| occurred in)(.*?)\[\/i\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbCodeItalicBugRegex();

    /// <summary>Regex for removing solitary BBCode italic tags.</summary>
    [GeneratedRegex(@"\[\/?i\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbCodeSolitaryRegex();

    /// <summary>Regex for condensing multiple newlines into a single line break.</summary>
    [GeneratedRegex(@"(?:\r?\n[^\S\r\n]*){2,}")]
    private static partial Regex CondenseLinesRegex();

    /// <summary>Regex for condensing multiple spaces into a single space.</summary>
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CondenseSpacesRegex();

    /// <summary>Regex for decoding Unicode escape sequences.</summary>
    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    /// <summary>Regex for parsing custom episode coordinate assignments from VFS override entries.</summary>
    [GeneratedRegex(@"^(?:([CSTPO])?(\d+)(?:-(\d+))?)?[S](\d+)[E](\d+)$", RegexOptions.IgnoreCase)]
    public static partial Regex CustomCoordRegex();

    #endregion

    #region General Text Helpers

    /// <summary>Zero-width space character used to bypass Plex title stripping.</summary>
    public const string ZeroWidthSpace = "\u200B";

    /// <summary>Hair space character used to bypass Plex title stripping and force Openings before Endings.</summary>
    public const string HairSpace = "\u200A";

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

    /// <summary>Parses CSV lines from a reader stream into blacklist and merge mappings using a key normalization function.</summary>
    /// <param name="reader">The text reader stream containing the CSV content.</param>
    /// <param name="normalizeKey">Function used to generate normalized lookup keys.</param>
    /// <returns>A tuple containing blacklisted keys, merge mappings, and mentioned keys.</returns>
    public static (HashSet<string> Blacklist, Dictionary<string, string> Merges, HashSet<string> MergeMentioned) ParseOverrides(TextReader reader, Func<string, string> normalizeKey)
    {
        var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mergeMentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            int commentIdx = line.IndexOf("##", StringComparison.Ordinal);
            var trimmed = (commentIdx >= 0 ? line[..commentIdx] : line).Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith(';'))
            {
                var parts = SplitCsvLine(trimmed.TrimStart(';'));
                foreach (var part in parts)
                {
                    var item = UnescapeUnicode(part.Trim());
                    if (normalizeKey(item) is { Length: > 0 } normKey)
                        blacklist.Add(normKey);
                }
            }
            else
            {
                var parts = SplitCsvLine(trimmed);
                if (parts.Length < 2)
                    continue;

                var target = UnescapeUnicode(parts[0].Trim());
                if (string.IsNullOrWhiteSpace(target))
                    continue;

                var normTarget = normalizeKey(target);
                if (normTarget.Length > 0)
                    mergeMentioned.Add(normTarget);

                for (int i = 1; i < parts.Length; i++)
                {
                    var source = UnescapeUnicode(parts[i].Trim());
                    if (normalizeKey(source) is { Length: > 0 } normSource)
                    {
                        merges[normSource] = target;
                        mergeMentioned.Add(normSource);
                    }
                }
            }
        }

        return (blacklist, merges, mergeMentioned);
    }

    #endregion

    #region Global Name Cache

    /// <summary>Tracks display name variants and series counts across multiple series for global cross-series deduplication.</summary>
    internal sealed class GlobalNameEntry
    {
        /// <summary>Synchronization lock for thread-safe variant recording.</summary>
        private readonly Lock _lock = new();

        /// <summary>Lowest numerical priority recorded so far (1 = AniDB, 2 = TMDB, 3 = User).</summary>
        private int _priority = int.MaxValue;

        /// <summary>Current winning display name across all recorded series.</summary>
        private string _bestDisplayName = string.Empty;

        /// <summary>Number of series using the winning display name at the winning priority.</summary>
        private int _bestCount;

        /// <summary>Counts per distinct casing/spelling variant at the current priority level.</summary>
        private readonly Dictionary<string, int> _variantCounts = new(StringComparer.Ordinal);

        /// <summary>Resolves the preferred display name, optionally recording a new series observation or upgrading to a higher-priority source.</summary>
        /// <param name="variant">The formatted display variant string.</param>
        /// <param name="priority">The source priority value (lower numerical values indicate higher priority).</param>
        /// <param name="record">True to record a new series observation; false to read the cached winner unless a higher-priority source arrives.</param>
        /// <returns>The preferred display name for the normalized key.</returns>
        public string ResolveDisplayName(string variant, int priority, bool record)
        {
            lock (_lock)
                return (record || priority < _priority || string.IsNullOrEmpty(_bestDisplayName)) ? RecordLocked(variant, priority) : _bestDisplayName;
        }

        /// <summary>Records an incoming variant under lock and returns the updated winning display name.</summary>
        /// <param name="variant">The formatted display variant string.</param>
        /// <param name="priority">The source priority value (lower numerical values indicate higher priority).</param>
        /// <returns>The preferred display name for the normalized key.</returns>
        private string RecordLocked(string variant, int priority)
        {
            // Higher-priority source (e.g. AniDB 1 beats TMDB 2)
            if (priority < _priority)
            {
                _priority = priority;
                _variantCounts.Clear();
                _variantCounts[variant] = 1;
                _bestDisplayName = variant;
                _bestCount = 1;
                return _bestDisplayName;
            }

            // Same priority: choose the variant used on the highest number of series, breaking ties deterministically by length then ordinal order
            if (priority == _priority)
            {
                int count = _variantCounts.GetValueOrDefault(variant, 0) + 1;
                _variantCounts[variant] = count;
                if (
                    count > _bestCount
                    || (
                        count == _bestCount
                        && (variant.Length < _bestDisplayName.Length || (variant.Length == _bestDisplayName.Length && string.Compare(variant, _bestDisplayName, StringComparison.Ordinal) < 0))
                    )
                )
                {
                    _bestDisplayName = variant;
                    _bestCount = count;
                }
                return _bestDisplayName;
            }

            // Lower-priority source arriving when a higher-priority display name is already cached
            return _bestDisplayName;
        }
    }

    #endregion

    #region Report Generation

    /// <summary>Records a candidate item into the diagnostic report stats dictionary, applying normalization, merge rules, and blacklist checks.</summary>
    /// <param name="stats">Dictionary tracking unique entries and counts across all series.</param>
    /// <param name="seenInSeries">Set tracking unique entries already counted for the current series.</param>
    /// <param name="source">Display name of the metadata source (e.g., "AniDB", "TMDB", "User").</param>
    /// <param name="sourceOrder">Numerical sort order for the source.</param>
    /// <param name="rawName">The raw candidate name string.</param>
    /// <param name="normalizeKey">Function generating a normalized lookup key from a name string.</param>
    /// <param name="merges">Dictionary mapping alias keys to canonical target names.</param>
    /// <param name="blacklist">Set of normalized blacklisted keys to exclude.</param>
    /// <param name="formatDisplayName">Optional formatting function for the display name (e.g. TitleCase).</param>
    internal static void RecordReportItem(
        Dictionary<(int SourceOrder, string Source, string NormKey), (GlobalNameEntry Entry, int Count)> stats,
        HashSet<(string Source, string NormKey)> seenInSeries,
        string source,
        int sourceOrder,
        string? rawName,
        Func<string, string> normalizeKey,
        IReadOnlyDictionary<string, string> merges,
        IReadOnlySet<string> blacklist,
        Func<string, string>? formatDisplayName = null
    )
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return;

        string trimmed = rawName.Trim();
        string origNorm = normalizeKey(trimmed);
        bool isMerged = merges.TryGetValue(origNorm, out var target);
        string effectiveName = isMerged ? target! : trimmed;
        string normKey = isMerged ? normalizeKey(effectiveName) : origNorm;

        if (normKey.Length == 0 || blacklist.Contains(normKey) || blacklist.Contains(origNorm) || !seenInSeries.Add((source, normKey)))
            return;

        var key = (sourceOrder, source, normKey);
        if (!stats.TryGetValue(key, out var item))
            item = (new GlobalNameEntry(), 0);

        string displayName = formatDisplayName != null ? formatDisplayName(effectiveName) : effectiveName;
        item.Entry.ResolveDisplayName(displayName, 1, record: true);
        stats[key] = (item.Entry, item.Count + 1);
    }

    /// <summary>Formats aggregated name stats into a sorted CSV report string.</summary>
    /// <param name="title">Report title banner (e.g. "Tag" or "Credit").</param>
    /// <param name="columnName">Name of the item column (e.g. "tag" or "credit").</param>
    /// <param name="stats">The dictionary containing source order, source name, and name entries with series counts.</param>
    /// <returns>A CSV-formatted report string.</returns>
    internal static string FormatCsvReport(string title, string columnName, Dictionary<(int SourceOrder, string Source, string NormKey), (GlobalNameEntry Entry, int Count)> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title} Report ##").AppendLine();
        sb.AppendLine($"#source,{columnName},count");

        foreach (
            var (sourceOrder, source, name, count) in stats
                .Select(kvp => (kvp.Key.SourceOrder, kvp.Key.Source, Name: kvp.Value.Entry.ResolveDisplayName(string.Empty, int.MaxValue, false), kvp.Value.Count))
                .OrderBy(r => r.SourceOrder)
                .ThenByDescending(r => r.Count)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        )
            sb.AppendLine($"{source},{EscapeCsvCommas(name)},{count}");

        return sb.ToString();
    }

    #endregion

    #region Metadata Resolution

    /// <summary>Set of ambiguous AniDB episode titles that should be overridden by the series title.</summary>
    private static readonly FrozenSet<string> s_ambiguousTitles = new[] { "Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web" }.ToFrozenSet(
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

    /// <summary>Determines if a movie episode shares its physical video file with multiple non-hidden main episodes of the same series.</summary>
    /// <param name="ep">The movie episode to inspect.</param>
    /// <returns>True if at least one video file linked to this episode also contains other main episodes from the same series.</returns>
    public static bool IsMultiEpisodeMovieFile(IEpisode ep) =>
        ep is IShokoEpisode { Videos: { Count: > 0 } vids } shokoEp
        && vids.Any(v => v.Episodes.Where(e => e.Type == EpisodeType.Episode && e.ShokoSeriesID == shokoEp.ShokoSeriesID && !MapHelper.IsHidden(e)).Select(e => e.LocalID).Distinct().Skip(1).Any());

    /// <summary>Compute the best title to display for a standalone movie, omitting the episode title if the series only contains one main episode, the file spans multiple episodes, or the episode title is ambiguous.</summary>
    /// <param name="ep">The movie episode metadata.</param>
    /// <param name="series">The parent series metadata.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata.</param>
    /// <param name="isStandaloneMovie">True when resolving for a standalone Movie library entry where multi-episode files collapse into one item.</param>
    /// <param name="seriesTitle">Optional pre-resolved series display title to avoid redundant lookups.</param>
    /// <param name="isMultiEpFile">Optional pre-computed flag indicating whether the file spans multiple main episodes.</param>
    /// <returns>The resolved movie title string.</returns>
    public static string ResolveMovieTitle(IEpisode ep, ISeries series, IMovie? tmdbMovie, bool isStandaloneMovie = false, string? seriesTitle = null, bool? isMultiEpFile = null)
    {
        string sTitle = !string.IsNullOrWhiteSpace(seriesTitle) ? seriesTitle : ResolveFullSeriesTitles(series).DisplayTitle;
        if (string.IsNullOrWhiteSpace(sTitle) && tmdbMovie is IWithTitles mt)
            sTitle = GetTitleByLanguage(mt, Settings.SeriesTitleLanguage);
        if (string.IsNullOrWhiteSpace(sTitle))
            sTitle = "Unknown";

        // Fast-path: Check if there is more than 1 main episode by skipping the first match. This avoids iterating over the entire episode list.
        bool hasMultipleMainEpisodes = series.Episodes.Where(e => e.Type == EpisodeType.Episode).Skip(1).Any();
        if (!hasMultipleMainEpisodes)
            return sTitle;

        // In standalone movie libraries, omit the episode subtitle when a single video file spans multiple main episodes
        if (isStandaloneMovie && (isMultiEpFile ?? IsMultiEpisodeMovieFile(ep)))
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

    /// <summary>Checks if a Plex string value (which mimics a boolean, e.g. "1") represents true.</summary>
    /// <param name="value">The Plex string value to check.</param>
    /// <returns>True if the string equals "1"; otherwise, false.</returns>
    public static bool IsPlexTrue(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    #endregion
}
