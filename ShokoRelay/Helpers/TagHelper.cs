using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using ShokoRelay.Config;

namespace ShokoRelay.Helpers;

/// <summary>Utilities for filtering and formatting tag strings from Shoko metadata.</summary>
public static class TagHelper
{
    #region Static Configuration

    /// <summary>Regex which matches alphanumeric text including single quotes and hyphens until a space or other special character.</summary>
    private static readonly Regex _wordRegex = new(@"[\'\w\d-]+\b", RegexOptions.Compiled);

    // csharpier-ignore-start
    /// <summary><c>TagBlacklistAniDBHelpers</c>: https://github.com/ShokoAnime/ShokoServer/blob/d7c7f6ecdd883c714b15dbef385e19428c8d29cf/Shoko.Server/Utilities/TagFilter.cs#L37C44-L37C68</summary>
    private static readonly FrozenSet<string> TagBlacklistAniDBHelpers = new[] {
        "asia", "awards", "body and host", "breasts", "cast missing", "cast", "complete manga adaptation", "content indicators", "delayed 16-9 broadcast", "description missing",
        "description needs improvement", "development hell", "dialogue driven", "dynamic", "earth", "elements", "ending", "ensemble cast", "family life", "fast-paced", "fetishes", "maintenance tags",
        "meta tags", "motifs", "no english subs available", "origin", "pic needs improvement", "place", "pornography", "season", "setting", "source material", "staff missing", "storytelling",
        "tales", "target audience", "technical aspects", "themes", "time", "to be moved to character", "to be moved to episode", "translation convention", "tropes", "unsorted"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words to force lowercase in tags to follow AniDB capitalisation rules: https://wiki.anidb.net/Capitalisation</summary>
    private static readonly FrozenSet<string> _forceLower = new[] {
        "a", "an", "the", "and", "but", "or", "nor", "at", "by", "for", "from", "in", "into", "of", "off", "on", "onto", "out", "over", "per", "to", "up", "with", "as", "4-koma",
        "-hime", "-kei", "-kousai", "-sama", "-warashi", "no", "vs", "x"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Abbreviations or acronyms that should be fully capitalised</summary>
    private static readonly FrozenSet<string> _forceUpper = new[] {
        "3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "npc", "op", "rpg", "tbs", "tv"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Special cases where a specific capitalisation style is preferred</summary>
    private static readonly FrozenDictionary<string, string> _forceSpecial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "comicfesta", "ComicFesta" }, { "d'etat", "d'Etat" }, { "noitamina", "noitaminA" }
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    // csharpier-ignore-end

    #endregion

    #region Tag Filtering

    /// <summary>Return an array of tag objects derived from a series, applying filters and sources.</summary>
    /// <param name="series">The series to extract tags from.</param>
    /// <returns>An array of tag metadata objects.</returns>
    public static object[] GetFilteredTags(ISeries series)
    {
        var shokoSeries = series as Shoko.Abstractions.Metadata.Shoko.IShokoSeries;
        var shokoTags = shokoSeries?.Tags;
        if (shokoTags == null)
            return [];
        var userBlacklist = ShokoRelay.Settings.TagBlacklist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sourceSetting = ShokoRelay.Settings.TagSources;
        var shokoNames = shokoTags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList();

        if (sourceSetting == TagSources.UserOnly)
            return
            [
                .. shokoNames
                    .Where(tagName => !TagBlacklistAniDBHelpers.Contains(tagName) && !userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(tagName => new { tag = TitleCase(tagName) }),
            ];

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
                if (string.IsNullOrWhiteSpace(t.Name) || (minWeight > 0 && t.Weight < minWeight))
                    continue;
                anidbNames.Add(t.Name);
            }
        }
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
        var combined = anidbNames.Concat(tmdbNames).Concat(shokoNames);
        return
        [
            .. combined
                .Where(tagName => !string.IsNullOrWhiteSpace(tagName) && !TagBlacklistAniDBHelpers.Contains(tagName) && !userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tagName => new { tag = TitleCase(tagName) }),
        ];
    }

    #endregion

    #region Title Casing Logic

    /// <summary>Convert text to title case, honouring special word list logic.</summary>
    /// <param name="text">Input text.</param>
    /// <returns>Formatted string.</returns>
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
                    return word.ToLower();
                if (_forceUpper.Contains(word))
                    return word.ToUpper();

                // Capitalise all words accounting for apostrophes first
                return char.ToUpper(word[0]) + word[1..];
            }
        );

        // Force capitalise the first character no matter what
        result = char.ToUpper(result[0]) + result[1..];

        // Force capitalise the first character of the last word no matter what
        int lastSpaceIndex = result.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            result = result[..(lastSpaceIndex + 1)] + char.ToUpper(result[lastSpaceIndex + 1]) + result[(lastSpaceIndex + 2)..];
        result = _wordRegex.Replace(result, m => _forceSpecial.TryGetValue(m.Value, out var special) ? special : m.Value);
        return result;
    }

    #endregion
}
