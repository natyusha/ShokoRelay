using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using System.Text.RegularExpressions;

namespace ShokoRelay.Helpers
{
    public static class TextHelper
    {
        #region Static
        // https://github.com/ShokoAnime/ShokoServer/blob/9c0ae9208479420dea3b766156435d364794e809/Shoko.Server/Utilities/TagFilter.cs#L37
        private static readonly HashSet<string> TagBlacklistAniDBHelpers = new(StringComparer.OrdinalIgnoreCase)
        {
            "asia", "awards", "body and host", "breasts", "cast missing", "cast", "complete manga adaptation", "content indicators", "delayed 16-9 broadcast",
            "description missing", "description needs improvement", "development hell", "dialogue driven", "dynamic", "earth", "elements", "ending", "ensemble cast",
            "family life", "fast-paced", "fetishes", "maintenance tags", "meta tags", "motifs", "no english subs available", "origin", "pic needs improvement",
            "place", "pornography", "season", "setting", "some weird shit goin' on", "source material", "staff missing", "storytelling", "tales", "target audience",
            "technical aspects", "themes", "time", "to be moved to character", "to be moved to episode", "translation convention", "tropes", "unsorted"
        };

        private static readonly Regex _seriesPrefixRegex                 = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
        private static readonly Regex _movieDescriptorRegex              = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);
        private static readonly Regex _defaultTitleRegex                 = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
        private static readonly Regex _sourceNoteSummaryRegex            = new(@"(?m)^\(?\b((Modified )?Sour?ces?|Note( [1-9])?|Summ?ary|From|See Also):(?!$| a daikon)([^\r\n]+|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _listIndicatorRegex                = new(@"(?m)^(\*|[\u2014~-] (adapted|source|description|summary|translated|written):?) ([^\r\n]+|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _wordRegex                         = new(@"[\'\w\d-]+\b", RegexOptions.Compiled);
        private static readonly Regex _aniDBLinkRegex                    = new(@"(?:http:\/\/anidb\.net\/(?:ch|co|cr|[feast]|(?:character|creator|file|episode|anime|tag)\/)(?:\d+)) \[([^\]]+)]", RegexOptions.Compiled);
        private static readonly Regex _bbCodeItalicBugRegex              = new(@"(?is)\[i\](?!" + Regex.Escape("\"The Sasami") + @"|" + Regex.Escape("\"Stellar") + @"|In the distant| occurred in)(.*?)\[\/i\]", RegexOptions.Compiled);
        private static readonly Regex _bbCodeSolitaryRegex               = new(@"\[\/?i\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _condenseLinesRegex                = new(@"(\r?\n\s*){2,}", RegexOptions.Compiled);
        private static readonly Regex _condenseSpacesRegex               = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly HashSet<string> _ambiguousTitles         = new(["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"], StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _forceLower              = new(["a", "an", "the", "and", "but", "or", "nor", "at", "by", "for", "from", "in", "into", "of", "off", "on", "onto", "out", "over", "per", "to", "up", "with", "as", "4-koma", "-hime", "-kei", "-kousai", "-sama", "-warashi", "no", "vs", "x"], StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _forceUpper              = new(["3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "npc", "op", "rpg", "tbs", "tv"], StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _forceSpecial = new(new Dictionary<string, string> { { "comicfesta", "ComicFesta" }, { "d'etat", "d'Etat" }, { "noitamina", "noitaminA" } }, StringComparer.OrdinalIgnoreCase);
        #endregion

        public static object[] GetFilteredTags(ISeries series)
        {
            if (series.Tags == null) return Array.Empty<object>();

            var userBlacklist = ShokoRelay.Settings.TagBlacklist
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            return series.Tags
                .Select(t => t.Name)
                .Where(tagName => !string.IsNullOrWhiteSpace(tagName) && 
                                  !TagBlacklistAniDBHelpers.Contains(tagName) && 
                                  !userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tagName => new { tag = TitleCase(tagName) })
                .ToArray<object>();
        }

        public static string GetTitleByLanguage(IWithTitles item, string languageSetting)
        {
            if (string.IsNullOrWhiteSpace(languageSetting)) 
                return item.PreferredTitle;

            var languages = languageSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (var lang in languages)
            {
                if (lang.Equals("shoko", StringComparison.OrdinalIgnoreCase)) 
                    return item.PreferredTitle;

                var titles = item.Titles;
                for (int i = 0; i < titles.Count; i++)
                {
                    var t = titles[i];
                    if (t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(t.Title))
                    {
                        return t.Title;
                    }
                }
            }

            return item.PreferredTitle;
        }

        public static (string DisplayTitle, string SortTitle, string? OriginalTitle) ResolveFullSeriesTitles(ISeries series)
        {
            // Get Title according to the language preference
            string rawTitle = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesTitleLanguage) ?? "";
            
            // Move common title prefixes to the end of the title
            string displayTitle = (ShokoRelay.Settings.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(rawTitle))
                    ? _seriesPrefixRegex.Replace(rawTitle, "$2 — $1")
                    : rawTitle;

            // Get Alternate Title according to the language preference
            string? altTitle = GetTitleByLanguage(series, ShokoRelay.Settings.SeriesAltTitleLanguage);

            // Duplicate check
            bool isDuplicate = string.IsNullOrEmpty(altTitle) ||
                               altTitle.Equals(rawTitle, StringComparison.OrdinalIgnoreCase) ||
                               altTitle.Equals(displayTitle, StringComparison.OrdinalIgnoreCase);

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
                tmdbEpTitle = shokoEp.TmdbEpisodes.FirstOrDefault()?.PreferredTitle;
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
            if (!string.IsNullOrEmpty(tmdbEpTitle) && 
                _defaultTitleRegex.IsMatch(rawEpTitle) && 
                !_defaultTitleRegex.IsMatch(tmdbEpTitle))
            {
                return tmdbEpTitle;
            }

            // Fallback to the raw title
            return rawEpTitle;
        }

        public static string SummarySanitizer(string? summary, int mode)
        {
            if (string.IsNullOrWhiteSpace(summary)) return string.Empty;

            if (mode != 4)
            {
                if (mode == 1 || mode == 3)
                    summary = _sourceNoteSummaryRegex.Replace(summary, "");      // Remove the line if it starts with ("Source: ", "Note: ", "Summary: ")
                if (mode == 1 || mode == 2)
                    summary = _listIndicatorRegex.Replace(summary, "");          // Remove the line if it starts with ("* ", "— ", "- ", "~ ")
            }

            summary = _aniDBLinkRegex.Replace(summary, "$1");                    // Replace AniDB links with text
            summary = _bbCodeItalicBugRegex.Replace(summary, "");                // Remove BBCode [i][/i] tags and contents (AniDB API Bug)
            summary = _bbCodeSolitaryRegex.Replace(summary, "");                 // Remove solitary leftover BBCode [i] or [/i] tags
            summary = _condenseLinesRegex.Replace(summary, Environment.NewLine); // Condense stacked empty lines
            summary = _condenseSpacesRegex.Replace(summary, " ");                // Remove double spaces and strip spaces and newlines

            return summary.Trim(' ', '\r', '\n');
        }

        public static string TitleCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 1. Primary Pass: Capitalize words and apply Upper/Lower lists
            string result = _wordRegex.Replace(text.ToLower(), m =>
            {
                string word = m.Value;
                if (_forceLower.Contains(word)) return word.ToLower(); // Convert words from force_lower to lowercase (follows AniDB capitalisation rules: https://wiki.anidb.net/Capitalisation)
                if (_forceUpper.Contains(word)) return word.ToUpper(); // Convert words from force_upper to uppercase (abbreviations or acronyms that should be fully capitalised)

                // Capitalise all words accounting for apostrophes first
                return char.ToUpper(word[0]) + word.Substring(1);
            });

            // Force capitalise the first character no matter what
            result = char.ToUpper(result[0]) + result.Substring(1);

            // Force capitalise the first character of the last word no matter what
            int lastSpaceIndex = result.LastIndexOf(' ');
            if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            {
                result = result.Substring(0, lastSpaceIndex + 1) + 
                         char.ToUpper(result[lastSpaceIndex + 1]) + 
                         result.Substring(lastSpaceIndex + 2);
            }

            // Apply special cases as a last step (where a specific capitalisation style is preferred)
            result = _wordRegex.Replace(result, m =>
            {
                if (_forceSpecial.TryGetValue(m.Value, out var special)) return special;
                return m.Value;
            });

            return result;
        }
    }
}