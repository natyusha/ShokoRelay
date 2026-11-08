using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Helpers;

/// <summary>Utilities for filtering and formatting tag strings from Shoko metadata.</summary>
public static partial class TagHelper
{
    #region Static Configuration

    /// <summary>Regex which matches alphanumeric text including single quotes and hyphens until a space or other special character.</summary>
    [GeneratedRegex(@"[\'\w\d-]+\b")]
    private static partial Regex WordRegex();

    /// <summary>Regex which strips moderator notes, trailing comments starting with double hyphens, and migration instructions from AniDB tags.</summary>
    [GeneratedRegex(@"\s*(?:--.*|-\s*to be\s+.*deleted)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagCleanupRegex();

    /// <summary>Regex matching characters outside basic Latin and punctuation blocks, used to filter out non-English TMDB keywords: https://www.themoviedb.org/bible/general#59f793929251413e9300000f.</summary>
    [GeneratedRegex(@"[^\u0020-\u024F\u1E00-\u1EFF\u2000-\u206F]")]
    private static partial Regex NonEnglishTagRegex();

    // csharpier-ignore
    /// <summary>A custom version of Shoko's default <c>TagBlacklistAniDBHelpers</c>: https://github.com/ShokoAnime/ShokoServer/blob/master/Shoko.Server/Utilities/TagFilter.cs#L37</summary>
    private static readonly FrozenSet<string> s_tagBlacklistHelper = new[] {
        // AniDB parent tags which only exist if a more specific child tag is present
        "awards", "body and host", "breasts", "cast", "character related tags which need deleting or merging", "content indicators", "dynamic", "elements", "ending", "ending tags that need merging",
        "family life", "fetishes", "maintenance tags", "meta tags", "motifs", "origin", "place", "pornography", "season", "setting", "storytelling", "tales", "target audience", "technical aspects",
        "themes", "time", "tropes", "unsorted",
        // AniDB maintenance tags: https://anidb.net/tag/30/animetb
        "cast missing", "description missing", "description needs improvement", "no english subs available", "pic needs improvement", "staff missing", "to be moved to character", "to be moved to episode",
        // AniDB parent tags which are too broad
        "asia", "earth",
        // TMDB tags which are too broad
        "Animation", "Anime"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Words to force lowercase in tags to follow AniDB capitalisation rules: https://wiki.anidb.net/Capitalisation</summary>
    private static readonly FrozenSet<string> s_forceLower = new[] {
        "a", "an", "the", "and", "but", "or", "nor", "at", "by", "for", "from", "in", "into", "of", "off", "on", "onto", "out",
        "over", "per", "to", "up", "with", "as", "4-koma", "-hime", "-kei", "-kousai", "-sama", "-warashi", "no", "vs", "x"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Abbreviations or acronyms that should be fully capitalised</summary>
    private static readonly FrozenSet<string> s_forceUpper = new[] {
        "3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "npc", "op", "rpg", "tbs", "tv",  // AniDB
        "bl", "gl", "jk", "jsdf", "mma", "ntr", "ol", "ona", "ova", "pov", "ptsd", "us", "vr", "vrmmo"               // TMDB
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Special cases where a specific capitalisation style is preferred</summary>
    private static readonly FrozenDictionary<string, string> s_forceSpecial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // AniDB
        { "-japanese", "-Japanese" }, { "animefesta", "AnimeFesta" }, { "comicfesta", "ComicFesta" },
        { "d'etat", "d'Etat" }, { "jdrama", "JDrama" }, { "noitamina", "noitaminA" },
        // TMDB
        { "vtuber", "VTuber" },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Explicit tag merges mapping canonical AniDB tags to one or more TMDB keywords/tags in Combined mode.</summary>
    private static readonly FrozenDictionary<string, string> s_tmdbTagMerges = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "artificial intelligence", ["artificial intelligence (a.i.)"] },
        { "large breasts", ["big tits"] },
        { "small breasts", ["small tits"] },
    }.SelectMany(kvp => kvp.Value.Select(tmdb => (Tmdb: tmdb, Anidb: kvp.Key))).ToFrozenDictionary(x => x.Tmdb, x => x.Anidb, StringComparer.OrdinalIgnoreCase);

    /// <summary>Global cache mapping normalized tag keys to their preferred display representations and priorities to ensure cross-series deduplication.</summary>
    private static readonly ConcurrentDictionary<string, (string DisplayName, int Priority)> s_globalTagCache = new();

    #endregion

    #region Tag Filtering

    /// <summary>Return an array of tag objects derived from a series, applying filters and sources.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="tmdbSeries">Optional pre-resolved TMDB series metadata object.</param>
    /// <returns>An array of tag metadata objects.</returns>
    public static object[] GetFilteredTags(ISeries series, ISeries? tmdbSeries = null)
    {
        var shokoSeries = series as IShokoSeries;
        var shokoTags = shokoSeries?.Tags;
        if (shokoTags == null)
            return [];

        var userBlacklist = string.IsNullOrWhiteSpace(Settings.TagBlacklist) ? [] : Settings.TagBlacklist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sourceSetting = Settings.TagSources;
        var shokoNames = shokoTags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n, Priority: 3));

        if (sourceSetting == TagSources.UserOnly)
            return [.. FilterAndFormat(shokoNames, userBlacklist)];

        IEnumerable<(string Name, int Priority)> anidbNames = [];
        int minWeight = (int)Settings.MinimumTagWeight;
        if ((sourceSetting == TagSources.Combined || sourceSetting == TagSources.AniDB) && shokoSeries?.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
            anidbNames = anidbTags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (minWeight <= 0 || t.Weight >= minWeight)).Select(t => (t.Name, Priority: 1));

        IEnumerable<(string Name, int Priority)> tmdbNames = [];
        bool isCombined = sourceSetting == TagSources.Combined;
        if ((sourceSetting == TagSources.Combined || sourceSetting == TagSources.TMDB) && (tmdbSeries ?? shokoSeries?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()) is { } tmdb)
            tmdbNames = tmdb
                .Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (t.Kind is TagKind.Genre || (t.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(t.Name))))
                .Select(t => (Name: isCombined && s_tmdbTagMerges.TryGetValue(t.Name.Trim(), out var target) ? target : t.Name, Priority: 2));

        return [.. FilterAndFormat(anidbNames.Concat(tmdbNames).Concat(shokoNames), userBlacklist)];
    }

    /// <summary>Generates a normalized comparison key ignoring case, spacing, punctuation, and diacritics.</summary>
    /// <param name="text">The raw tag name string.</param>
    /// <returns>A normalized alphanumeric ASCII lowercase string.</returns>
    private static string NormalizeTagKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));

        return sb.ToString();
    }

    /// <summary>Filters the raw tag strings against the user blacklist, deduplicates across punctuation and diacritics, and normalizes them into title case.</summary>
    /// <param name="tags">The raw collection of tag name strings to process along with their source priority.</param>
    /// <param name="userBlacklist">The array of blacklisted tag names to filter out.</param>
    /// <returns>An enumerable collection of formatted anonymous objects containing the sanitized tags.</returns>
    private static IEnumerable<object> FilterAndFormat(IEnumerable<(string Name, int Priority)> tags, string[] userBlacklist)
    {
        var blacklistKeys = userBlacklist.Select(NormalizeTagKey).Where(k => k.Length > 0).ToHashSet();

        return tags.Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => (Name: TagCleanupRegex().Replace(t.Name, "").Trim(), t.Priority))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name) && !s_tagBlacklistHelper.Contains(t.Name) && (blacklistKeys.Count == 0 || !blacklistKeys.Contains(NormalizeTagKey(t.Name))))
            .Select(t =>
            {
                string normKey = NormalizeTagKey(t.Name);
                string titleCased = TitleCase(t.Name);
                var (displayName, _) = s_globalTagCache.AddOrUpdate(normKey, _ => (titleCased, t.Priority), (_, existing) => t.Priority <= existing.Priority ? (titleCased, t.Priority) : existing);
                return (Key: normKey, DisplayName: displayName);
            })
            .DistinctBy(t => t.Key)
            .Select(t => (object)new { tag = t.DisplayName });
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
        string result = WordRegex()
            .Replace(
                text.ToLower(),
                m =>
                    s_forceLower.Contains(m.Value) ? m.Value.ToLower()
                    : s_forceUpper.Contains(m.Value) ? m.Value.ToUpper()
                    // Capitalise all words accounting for apostrophes first
                    : char.ToUpper(m.Value[0]) + m.Value[1..]
            );

        // Force capitalise the first character no matter what
        result = char.ToUpper(result[0]) + result[1..];

        // Force capitalise the first character of the last word no matter what
        int lastSpaceIndex = result.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            result = result[..(lastSpaceIndex + 1)] + char.ToUpper(result[lastSpaceIndex + 1]) + result[(lastSpaceIndex + 2)..];
        result = WordRegex().Replace(result, m => s_forceSpecial.TryGetValue(m.Value, out var special) ? special : m.Value);
        return result;
    }

    #endregion
}
