using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using ShokoRelay.Config;

namespace ShokoRelay.Helpers
{
    /// <summary>
    /// Utilities for filtering and formatting tag strings obtained from Shoko series metadata. Includes logic for blacklists and title-casing.
    /// </summary>
    public static class TagHelper
    {
        // https://github.com/ShokoAnime/ShokoServer/blob/9c0ae9208479420dea3b766156435d364794e809/Shoko.Server/Utilities/TagFilter.cs#L37
        private static readonly FrozenSet<string> TagBlacklistAniDBHelpers = new[]
        {
            "asia",
            "awards",
            "body and host",
            "breasts",
            "cast missing",
            "cast",
            "complete manga adaptation",
            "content indicators",
            "delayed 16-9 broadcast",
            "description missing",
            "description needs improvement",
            "development hell",
            "dialogue driven",
            "dynamic",
            "earth",
            "elements",
            "ending",
            "ensemble cast",
            "family life",
            "fast-paced",
            "fetishes",
            "maintenance tags",
            "meta tags",
            "motifs",
            "no english subs available",
            "origin",
            "pic needs improvement",
            "place",
            "pornography",
            "season",
            "setting",
            "some weird shit goin' on",
            "source material",
            "staff missing",
            "storytelling",
            "tales",
            "target audience",
            "technical aspects",
            "themes",
            "time",
            "to be moved to character",
            "to be moved to episode",
            "translation convention",
            "tropes",
            "unsorted",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex _wordRegex = new(@"[\'\w\d-]+\b", RegexOptions.Compiled);
        private static readonly FrozenSet<string> _forceLower = new[]
        {
            "a",
            "an",
            "the",
            "and",
            "but",
            "or",
            "nor",
            "at",
            "by",
            "for",
            "from",
            "in",
            "into",
            "of",
            "off",
            "on",
            "onto",
            "out",
            "over",
            "per",
            "to",
            "up",
            "with",
            "as",
            "4-koma",
            "-hime",
            "-kei",
            "-kousai",
            "-sama",
            "-warashi",
            "no",
            "vs",
            "x",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        private static readonly FrozenSet<string> _forceUpper = new[] { "3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "npc", "op", "rpg", "tbs", "tv" }.ToFrozenSet(
            StringComparer.OrdinalIgnoreCase
        );
        private static readonly FrozenDictionary<string, string> _forceSpecial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "comicfesta", "ComicFesta" },
            { "d'etat", "d'Etat" },
            { "noitamina", "noitaminA" },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Return an array of tag objects derived from the provided <paramref name="series"/>, applying blacklist rules and combining sources (Shoko, AniDB, TMDB) based on configuration.
        /// </summary>
        /// <param name="series">Series to extract tags from.</param>
        /// <returns>An array of anonymous objects each containing a <c>tag</c> property with a title-cased tag name.</returns>
        public static object[] GetFilteredTags(ISeries series)
        {
            var shokoSeries = series as Shoko.Abstractions.Metadata.Shoko.IShokoSeries;
            var shokoTags = shokoSeries?.Tags;
            if (shokoTags == null)
                return Array.Empty<object>();

            var userBlacklist = ShokoRelay.Settings.TagBlacklist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var sourceSetting = ShokoRelay.Settings.TagSources;

            // compute list of custom shoko tag names once for reuse
            var shokoNames = shokoTags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList();

            // if user-only, skip all external sources
            if (sourceSetting == TagSources.UserOnly)
            {
                return shokoNames
                    .Where(tagName => !TagBlacklistAniDBHelpers.Contains(tagName) && !userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(tagName => new { tag = TitleCase(tagName) })
                    .ToArray<object>();
            }

            // build AniDB names list if source permits
            var anidbNames = new List<string>();
            int minWeight = (int)ShokoRelay.Settings.MinimumTagWeight;
            if (
                (sourceSetting == TagSources.Combined || sourceSetting == TagSources.AniDB)
                && shokoSeries != null
                && shokoSeries.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags
                && anidbTags.Count > 0
            )
            {
                foreach (var t in anidbTags)
                {
                    if (string.IsNullOrWhiteSpace(t.Name))
                        continue;
                    if (minWeight > 0 && t.Weight < minWeight)
                        continue;
                    anidbNames.Add(t.Name!);
                }
            }

            // build TMDB names list if requested
            var tmdbNames = new List<string>();
            if ((sourceSetting == TagSources.Combined || sourceSetting == TagSources.TMDB) && shokoSeries != null)
            {
                var tmdb = shokoSeries.TmdbShows?.FirstOrDefault();
                if (tmdb != null)
                {
                    if (tmdb.Keywords != null)
                        tmdbNames.AddRange(tmdb.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
                    if (tmdb.Genres != null)
                        tmdbNames.AddRange(tmdb.Genres.Where(g => !string.IsNullOrWhiteSpace(g)));
                }
            }

            // always include Shoko custom tags (already computed)
            var combined = anidbNames.Concat(tmdbNames).Concat(shokoNames);

            return combined
                .Where(tagName => !string.IsNullOrWhiteSpace(tagName) && !TagBlacklistAniDBHelpers.Contains(tagName) && !userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tagName => new { tag = TitleCase(tagName) })
                .ToArray<object>();
        }

        /// <summary>
        /// Convert the given <paramref name="text"/> to title case, honoring special words that should always be upper- or lowercase according to AniDB rules.
        /// </summary>
        /// <param name="text">Text to convert.</param>
        /// <returns>Title‑cased string; original input returned if null/whitespace.</returns>
        public static string TitleCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Primary Pass: Capitalize words and apply Upper/Lower lists
            string result = _wordRegex.Replace(
                text.ToLower(),
                m =>
                {
                    string word = m.Value;
                    if (_forceLower.Contains(word))
                        return word.ToLower(); // Convert words from force_lower to lowercase (follows AniDB capitalisation rules: https://wiki.anidb.net/Capitalisation)
                    if (_forceUpper.Contains(word))
                        return word.ToUpper(); // Convert words from force_upper to uppercase (abbreviations or acronyms that should be fully capitalised)

                    // Capitalise all words accounting for apostrophes first
                    return char.ToUpper(word[0]) + word.Substring(1);
                }
            );

            // Force capitalise the first character no matter what
            result = char.ToUpper(result[0]) + result.Substring(1);

            // Force capitalise the first character of the last word no matter what
            int lastSpaceIndex = result.LastIndexOf(' ');
            if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            {
                result = result.Substring(0, lastSpaceIndex + 1) + char.ToUpper(result[lastSpaceIndex + 1]) + result.Substring(lastSpaceIndex + 2);
            }

            // Apply special cases as a last step (where a specific capitalisation style is preferred)
            result = _wordRegex.Replace(
                result,
                m =>
                {
                    if (_forceSpecial.TryGetValue(m.Value, out var special))
                        return special;
                    return m.Value;
                }
            );

            return result;
        }
    }
}
