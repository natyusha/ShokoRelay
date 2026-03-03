using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Config;

namespace ShokoRelay.Helpers
{
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
        private static readonly Regex _animeThemesTagRegex = new(@"\[([^\]]+)\](?=\.webm$)", RegexOptions.Compiled);
        private static readonly Regex _bdDvdRegex = new(@"BD|DVD", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _numbersRegex = new(@"\d+", RegexOptions.Compiled);
        private static readonly Regex _opEdHyphenRegex = new(@"^((?:OP|ED)\d+(?:v\d+)?)-(\w+)", RegexOptions.Compiled);
        private static readonly Regex _themeVersionRegex = new(@"(?:OP|ED)\d+(v\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region General Text Helpers
        /// <summary>
        /// Replace runs of two or more whitespace characters with a single space.
        /// </summary>
        /// <param name="input">The string to condense.</param>
        /// <returns>The input with consecutive whitespace collapsed to a single space.</returns>
        public static string CondenseSpaces(string input) => _condenseSpacesRegex.Replace(input, " ");

        /// <summary>
        /// Extracts the first sequence of digits from a string and returns it as an optional integer.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <returns>The first numeric sequence as an int, or null if not found or not parseable.</returns>
        public static int? ExtractFirstNumber(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var match = _numbersRegex.Match(text);
            if (match.Success && int.TryParse(match.Value, out var id))
                return id;

            return null;
        }

        private static readonly Regex _unicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

        /// <summary>
        /// Replace literal commas with the unicode escape \u002C so that values containing commas can be stored in a simple comma-separated file.
        /// </summary>
        /// <param name="value">The string to escape.</param>
        /// <returns>The escaped string, or <see cref="string.Empty"/> if <paramref name="value"/> is null or empty.</returns>
        public static string EscapeCsvCommas(string value) => string.IsNullOrEmpty(value) ? string.Empty : value.Replace(",", "\\u002C");

        /// <summary>
        /// Decode \uXXXX escape sequences back into their actual Unicode characters.
        /// </summary>
        /// <param name="value">The string potentially containing escape sequences.</param>
        /// <returns>The unescaped string, or the original value if no escapes are found.</returns>
        public static string UnescapeUnicode(string value) => string.IsNullOrEmpty(value) ? value : _unicodeEscapeRegex.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        /// <summary>
        /// Splits a CSV line on commas. Caller must escape commas inside values using <see cref="EscapeCsvCommas"/> when generating.
        /// </summary>
        /// <param name="line">The CSV line to split.</param>
        /// <returns>An array of field values, or an empty array if <paramref name="line"/> is null.</returns>
        public static string[] SplitCsvLine(string line) => line?.Split(',') ?? Array.Empty<string>();

        /// <summary>
        /// For aesthetic reasons, convert the first hyphen in a filename to a right-pointing chevron character. Used with extras filenames.
        /// </summary>
        /// <param name="name">Original filename.</param>
        /// <returns>Modified string with replacement or empty string on null/whitespace.</returns>
        public static string ReplaceFirstHyphenWithChevron(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            int index = name.IndexOf('-');
            if (index < 0)
                return name;

            return string.Concat(name.AsSpan(0, index), "\u276F", name.AsSpan(index + 1)); // Unicode Character "❯" (U+276F) - Heavy Right-Pointing Angle Quotation Mark Ornament
        }

        #endregion

        #region Titles

        private static readonly IReadOnlySet<string> _ambiguousTitles = new HashSet<string>(
            ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>
        /// Return an item's title according to a comma-separated list of preferred language codes. Falls back to the item's preferred title if no match is found or the language setting is empty.
        /// </summary>
        /// <param name="item">Object that exposes a <see cref="IWithTitles.Titles"/> collection.</param>
        /// <param name="languageSetting">Comma-separated preferred language codes.</param>
        /// <returns>The best matching title string, or the item's preferred title as a fallback.</returns>
        public static string GetTitleByLanguage(IWithTitles item, string languageSetting)
        {
            if (string.IsNullOrWhiteSpace(languageSetting))
                return item.PreferredTitle?.Value ?? string.Empty;

            var languages = languageSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (var lang in languages)
            {
                if (lang.Equals("shoko", StringComparison.OrdinalIgnoreCase))
                    return item.PreferredTitle?.Value ?? string.Empty;

                var titles = item.Titles;
                for (int i = 0; i < titles.Count; i++)
                {
                    var t = titles[i];
                    if (t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(t.Value))
                    {
                        return t.Value;
                    }
                }
            }

            return item.PreferredTitle?.Value ?? string.Empty;
        }

        /// <summary>
        /// Determine display, sortable, and original titles for a series based on language preferences and optional prefix reordering settings.
        /// </summary>
        /// <param name="series">Series metadata.</param>
        /// <returns>A tuple of (DisplayTitle, SortTitle, OriginalTitle) where OriginalTitle is null when it duplicates the display title.</returns>
        public static (string DisplayTitle, string SortTitle, string? OriginalTitle) ResolveFullSeriesTitles(ISeries series)
        {
            // Get Title according to the language preference
            string rawTitle = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesTitleLanguage) ?? "";

            // Move common title prefixes to the end of the title
            string displayTitle = (ShokoRelay.Settings.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(rawTitle)) ? _seriesPrefixRegex.Replace(rawTitle, "$2 — $1") : rawTitle;

            // Get Alternate Title according to the language preference
            string? altTitle = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesAltTitleLanguage);

            // Duplicate check
            bool isDuplicate = string.IsNullOrEmpty(altTitle) || altTitle.Equals(rawTitle, StringComparison.OrdinalIgnoreCase) || altTitle.Equals(displayTitle, StringComparison.OrdinalIgnoreCase);

            // Set the final alternate title if it is not a duplicate
            string? finalAlt = isDuplicate ? null : altTitle;

            // Append the Alternate title to the Sort Title to make it searchable (padded with an en dash)
            string sortTitle = string.IsNullOrWhiteSpace(finalAlt) ? displayTitle : $"{displayTitle} – {finalAlt}";

            return (displayTitle, sortTitle, finalAlt);
        }

        /// <summary>
        /// Compute the best title to display for an episode, taking into account language settings, TMDB overrides, and ambiguous names that should default to the series title.
        /// </summary>
        /// <param name="ep">Episode metadata.</param>
        /// <param name="displaySeriesTitle">Resolved series title to use as fallback.</param>
        /// <returns>The resolved episode title string.</returns>
        public static string ResolveEpisodeTitle(IEpisode ep, string displaySeriesTitle)
        {
            string rawEpTitle = GetTitleByLanguage(ep, ShokoRelay.Settings.EpisodeTitleLanguage) ?? "";

            string? tmdbEpTitle = null;
            bool hasMultipleTmdbLinks = false;
            if (ep is IShokoEpisode shokoEp)
            {
                tmdbEpTitle = shokoEp.TmdbEpisodes.FirstOrDefault()?.PreferredTitle?.Value;
                hasMultipleTmdbLinks = shokoEp.TmdbEpisodes.Count > 1;
            }

            // Replace ambiguous single entry titles with the series title
            if (ep.EpisodeNumber == 1 && _ambiguousTitles.Contains(rawEpTitle))
            {
                // Get series title according to the language preference
                string epTitle = displaySeriesTitle;

                // Fallback to the TMDB title if there is a TMDB Episodes match
                if (epTitle == rawEpTitle && !string.IsNullOrEmpty(tmdbEpTitle))
                    epTitle = tmdbEpTitle;

                // If not found, fallback to EN series title as a last resort
                if (epTitle == rawEpTitle)
                    epTitle = (ep.Series != null) ? GetTitleByLanguage(ep.Series, "en") ?? rawEpTitle : rawEpTitle;

                // Append ambiguous title to series title if a replacement title was found and it doesn't contain it
                if (epTitle != rawEpTitle && !epTitle.Contains(rawEpTitle))
                {
                    // Reduce redundant movie descriptors
                    if (rawEpTitle == "Complete Movie")
                        epTitle = _movieDescriptorRegex.Replace(epTitle, "").Trim();

                    return $"{epTitle} — {rawEpTitle}";
                }
                return epTitle;
            }

            // If TMDB episode group names are enabled and a group is present override the title
            if (ShokoRelay.Settings.TmdbEpGroupNames && hasMultipleTmdbLinks && !string.IsNullOrEmpty(tmdbEpTitle))
                rawEpTitle = tmdbEpTitle;

            // TMDB episode title override if the episode title is ambiguous and enumerated on AniDB (excluding number 0) and there is a TMDB match
            if (!string.IsNullOrEmpty(tmdbEpTitle) && _defaultTitleRegex.IsMatch(rawEpTitle) && !_defaultTitleRegex.IsMatch(tmdbEpTitle))
            {
                return tmdbEpTitle;
            }

            // Fallback to the raw title
            return rawEpTitle;
        }

        #endregion

        #region Summaries

        /// <summary>
        /// Sanitize <paramref name="summary"/> and, if the result is empty, fall back to <paramref name="tmdbSummary"/>.
        /// </summary>
        /// <param name="summary">Primary (AniDB) summary text.</param>
        /// <param name="tmdbSummary">Optional TMDB fallback summary.</param>
        /// <param name="mode">Sanitization mode to apply.</param>
        /// <returns>The sanitized summary, or the sanitized TMDB fallback if the primary result is empty.</returns>
        public static string SanitizeSummaryWithFallback(string? summary, string? tmdbSummary, SummaryMode mode)
        {
            var result = SummarySanitizer(summary, mode);
            if (string.IsNullOrWhiteSpace(result) && !string.IsNullOrWhiteSpace(tmdbSummary))
                result = SummarySanitizer(tmdbSummary, mode);
            return result;
        }

        /// <summary>
        /// Clean up a summary string according to the configured <paramref name="mode"/> which may strip source notes, condense spacing, or leave the text alone.
        /// </summary>
        /// <param name="summary">Original summary text.</param>
        /// <param name="mode">Sanitization mode to apply.</param>
        /// <returns>The cleaned summary text, trimmed of leading/trailing whitespace and newlines.</returns>
        public static string SummarySanitizer(string? summary, SummaryMode mode)
        {
            if (string.IsNullOrWhiteSpace(summary))
                return string.Empty;

            switch (mode)
            {
                case SummaryMode.FullySanitize:
                    summary = _sourceNoteSummaryRegex.Replace(summary, "");
                    summary = _listIndicatorRegex.Replace(summary, "");
                    break;
                case SummaryMode.AllowInfoLines:
                    summary = _listIndicatorRegex.Replace(summary, "");
                    break;
                case SummaryMode.AllowMiscLines:
                    summary = _sourceNoteSummaryRegex.Replace(summary, "");
                    break;
                case SummaryMode.AllowBoth:
                default:
                    break;
            }

            summary = _aniDBLinkRegex.Replace(summary, "$1"); // Replace AniDB links with text
            summary = _bbCodeItalicBugRegex.Replace(summary, ""); // Remove BBCode [i][/i] tags and contents (AniDB API Bug)
            summary = _bbCodeSolitaryRegex.Replace(summary, ""); // Remove solitary leftover BBCode [i] or [/i] tags
            summary = _condenseLinesRegex.Replace(summary, Environment.NewLine); // Condense stacked empty lines
            summary = _condenseSpacesRegex.Replace(summary, " "); // Remove double spaces and strip spaces and newlines

            return summary.Trim(' ', '\r', '\n');
        }

        #endregion

        #region Plex

        /// <summary>
        /// Maps invalid Windows filename characters to visually similar Unicode replacements.
        /// The dictionary keys define the set of characters considered invalid.
        /// </summary>
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

        /// <summary>
        /// Remove characters that are invalid in Windows filenames by replacing them with visually similar unicode characters. Useful when generating filesystem-friendly strings.
        /// </summary>
        /// <param name="value">The string to sanitize.</param>
        /// <returns>A sanitized copy of <paramref name="value"/> with invalid characters removed and whitespace condensed.</returns>
        public static string StripInvalidWindowsChars(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (ReplacementCharMap.ContainsKey(c))
                    continue;
                sb.Append(c);
            }

            return _condenseSpacesRegex.Replace(sb.ToString().Trim(), " ");
        }

        /// <summary>
        /// Determine if a filename contains a Plex-style split tag (e.g. "pt1"). Used when mapping multi-disc releases.
        /// Info: https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/
        /// </summary>
        /// <param name="fileName">The filename to inspect.</param>
        /// <returns><c>true</c> if a Plex split tag pattern is detected; otherwise <c>false</c>.</returns>
        public static bool HasPlexSplitTag(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            // Plex ignores square-bracketed text when parsing, so we treat bracketed tags as valid
            string name = Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' ');

            return _plexSplitTagRegex.IsMatch(name);
        }

        /// <summary>
        ///  Files are always placed inside a subfolder which itself lives directly under the numeric series ID folder.
        /// </summary>
        /// <param name="rawPath">Full file path to extract the series ID from.</param>
        /// <returns>The extracted series ID, or null if not found.</returns>
        public static int? ExtractSeriesId(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            string? dir = Path.GetDirectoryName(rawPath);
            for (int depth = 0; depth < 2 && !string.IsNullOrEmpty(dir); depth++)
            {
                string folder = Path.GetFileName(dir);
                if (int.TryParse(folder, out var id))
                    return id;
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        #endregion

        #region AnimeThemes

        /// <summary>
        /// Transform an AnimeThemes filename for Plex compatibility by inserting zero-width spaces in OP/ED prefixes (preventing Plex auto-rename), converting hyphens to middle dots, and cleaning bracketed tags.
        /// </summary>
        /// <param name="name">The original AnimeThemes filename.</param>
        /// <returns>A Plex-friendly filename string, or <see cref="string.Empty"/> if <paramref name="name"/> is null or whitespace.</returns>
        public static string AnimeThemesPlexFileNames(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string result = name;

            // change hyphen following OP/ED number into middle dot per regex
            result = _opEdHyphenRegex.Replace(result, "$1·$2");

            // Handle OP/ED at the start
            const string zws = "\u200B"; // Zero-width space to prevent Plex from renaming files with OP/ED tags
            if (result.StartsWith("OP", StringComparison.Ordinal))
                result = $"O{zws}P{result[2..]}";
            else if (result.StartsWith("ED", StringComparison.Ordinal))
                result = $"E{zws}D{result[2..]}";

            // Tag cleaning: Process bracketed tag before the .webm extension (always .webm)
            var tagMatch = _animeThemesTagRegex.Match(result);
            if (tagMatch.Success)
            {
                string tagContent = tagMatch.Groups[1].Value;
                string prefix = "";

                // If "NC" is present, prepend to the name and remove from tag
                if (tagContent.Contains("NC", StringComparison.Ordinal))
                {
                    prefix = "NC";
                    tagContent = tagContent.Replace("NC", "", StringComparison.Ordinal).Trim();
                }

                // Remove "BD" and "DVD"
                tagContent = _bdDvdRegex.Replace(tagContent, "");

                // Remove any numbers (resolutions)
                tagContent = _numbersRegex.Replace(tagContent, "");

                // Trim whitespace
                tagContent = tagContent.Trim();

                // If tag content is empty, don't add parentheses; otherwise, use parentheses
                string cleanedTag = string.IsNullOrEmpty(tagContent) ? "" : $" ({tagContent})";

                // Reconstruct: Prepend prefix to the base name, append cleaned tag, then .webm
                string baseName = result.Substring(0, tagMatch.Index).TrimEnd(); // Trim trailing space before tag
                result = prefix + baseName + cleanedTag + ".webm";
            }

            return result;
        }

        /// <summary>
        /// Extract the version suffix (e.g. "v2", "v3") from an anime theme filename such as "OP1v2" or "ED3v4".
        /// </summary>
        /// <param name="fileNameWithoutExtension">Filename stem (no extension) to scan for a version suffix.</param>
        /// <returns>The version suffix (e.g. "v2"), or <see cref="string.Empty"/> if none is present.</returns>
        public static string ExtractThemeVersionSuffix(string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                return string.Empty;
            var match = _themeVersionRegex.Match(fileNameWithoutExtension);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        #endregion
    }
}
