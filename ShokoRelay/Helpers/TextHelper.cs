using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Config;

namespace ShokoRelay.Helpers
{
    public static class TextHelper
    {
        private static readonly Regex _seriesPrefixRegex = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
        private static readonly Regex _movieDescriptorRegex = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);
        private static readonly Regex _defaultTitleRegex = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
        private static readonly Regex _sourceNoteSummaryRegex = new(
            @"(?m)^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );
        private static readonly Regex _listIndicatorRegex = new(
            @"(?m)^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );
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
        private static readonly IReadOnlySet<string> _ambiguousTitles = new HashSet<string>(
            ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
            StringComparer.OrdinalIgnoreCase
        );

        // Windows filename sanitization helpers - Invalid windows characters are defined as the keys of the ReplacementCharMap
        public static readonly IReadOnlyDictionary<char, char> ReplacementCharMap = new Dictionary<char, char>
        {
            ['\\'] = '⧵',
            ['/'] = '⁄',
            [':'] = '꞉',
            ['*'] = '＊',
            ['?'] = '？',
            ['<'] = '＜',
            ['>'] = '＞',
            ['|'] = '｜',
        };

        public static readonly char[] InvalidWindowsChars = ReplacementCharMap.Keys.ToArray();

        public static string StripInvalidWindowsChars(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (Array.IndexOf(InvalidWindowsChars, c) >= 0)
                    continue;
                sb.Append(c);
            }

            var cleaned = sb.ToString().Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");

            return cleaned;
        }

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
            if (ShokoRelay.Settings.TMDBEpGroupNames && hasMultipleTmdbLinks && !string.IsNullOrEmpty(tmdbEpTitle))
                rawEpTitle = tmdbEpTitle;

            // TMDB episode title override if the episode title is ambiguous and enumerated on AniDB (excluding number 0) and there is a TMDB match
            if (!string.IsNullOrEmpty(tmdbEpTitle) && _defaultTitleRegex.IsMatch(rawEpTitle) && !_defaultTitleRegex.IsMatch(tmdbEpTitle))
            {
                return tmdbEpTitle;
            }

            // Fallback to the raw title
            return rawEpTitle;
        }

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

        // Check for plex split tags as this is the only reliable way to handle this - https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/
        public static bool HasPlexSplitTag(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            // Plex ignores square-bracketed text when parsing, so we treat bracketed tags as valid
            string name = Path.GetFileNameWithoutExtension(fileName).Replace('[', ' ').Replace(']', ' ');

            return _plexSplitTagRegex.IsMatch(name);
        }

        public static int? ExtractSeriesId(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            // Files are always placed inside a subfolder which itself lives directly under the numeric series ID folder.
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

        public static string AnimeThemesPlexFileNames(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string result = name;

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

        // Purely for aesthetic reasons, replace the first hyphen in extras filenames as they don't show an episode number
        public static string ReplaceFirstHyphenWithChevron(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            int index = name.IndexOf('-');
            if (index < 0)
                return name;

            return string.Concat(name.AsSpan(0, index), "\u276F", name.AsSpan(index + 1)); // Unicode Character "❯" (U+276F) - Heavy Right-Pointing Angle Quotation Mark Ornament
        }
    }
}
