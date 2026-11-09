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
        "family life", "fetishes", "maintenance tags", "meta tags", "motifs", "old animetags - op ed", "origin", "place", "pornography", "season", "setting", "storytelling", "tales", "target audience",
        "technical aspects", "themes", "time", "tropes", "unsorted",
        // AniDB maintenance tags: https://anidb.net/tag/30/animetb
        "cast missing", "description missing", "description needs improvement", "no english subs available", "pic needs improvement", "staff missing", "to be moved to character", "to be moved to episode",
        // AniDB parent tags which are too broad
        "asia", "earth",
        // TMDB genres which are too broad
        "Animation", "Anime",
        // TMDB keywords which violate the rules (mainly they are not english words) and will make it through the language filter. These can be removed once they are fixed on TMDB
        "accion", "animacion", "art martial", "boxeo", "bukatsu", "chuanyue", "chuyển sinh", "dongfang", "donghua", "espionnage", "futuro postapocalíptico",
        "gufeng", "guzhuang", "internete", "inzest", "magie", "samourai", "seadex", "sekai togou", "serie de tv"
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
        "3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "neet", "npc", "op", "rpg", "tbs", "tv",                     // AniDB
        "a.i.", "bl", "diy", "gl", "jk", "jsdf", "lgbt", "mma", "mmorpg", "ntr", "ol", "ona", "ova", "pov", "ptsd", "ufo", "us", "vr", "vrmmo"  // TMDB
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Special cases where a specific capitalisation style is preferred</summary>
    private static readonly FrozenDictionary<string, string> s_forceSpecial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // AniDB
        { "-japanese", "-Japanese" }, { "animefesta", "AnimeFesta" }, { "comicfesta", "ComicFesta" }, { "d'etat", "d'Etat" }, { "jdrama", "JDrama" }, { "noitamina", "noitaminA" },
        // TMDB
        { "vtuber", "VTuber" },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Explicit tag merges mapping synonymous TMDB keywords/tags to a primary TMDB tag and an optional canonical AniDB tag.</summary>
    /// <remarks>This section will likely be expanded fairly often and can be quite subjective. It is best to avoid merging things here if there is any ambiguity.</remarks>
    private static readonly FrozenDictionary<string, (string TmdbPrimary, string? AnidbTarget)> s_tagMerges = new List<(string? Anidb, string[] Tmdb)>
    {
        // AniDB <- TMDB Mappings
        ("3d cg animation", ["3d animation", "cg animated"]),
        ("absurdist humour", ["absurd", "absurdist"]),
        ("artificial intelligence", ["artificial intelligence (a.i.)"]),
        ("association football", ["football (soccer)"]),
        ("bishoujo", ["bishojo"]),
        ("bukkake", ["bukakke"]),
        ("chinese production", ["donghua"]),
        ("creampie", ["nakadashi"]),
        ("cross-dressing", ["cross dresser"]),
        ("cute girls doing cute things", ["cgdct"]),
        ("cyberpunk", ["cyber"]),
        ("demon", ["youkai"]),
        ("elf", ["elves"]),
        ("fellatio", ["blowjob", "blow job"]),
        ("isekai", ["another world", "other world", "otherworld"]),
        ("korean-japanese co-production", ["japanese-korean coproduction"]),
        ("juujin", ["furry"]),
        ("large breasts", ["big tits"]),
        ("loli", ["lolis", "loli type"]),
        ("magical girl", ["magic girl"]),
        ("mecha", ["mechs"]),
        ("netorare", ["netorare (ntr)", "ntr"]),
        ("office lady", ["office lady (ol)"]),
        ("pantsu", ["panties"]),
        ("paramilitary", ["paramilitary organization"]),
        ("parallel world", ["parallel universe"]),
        ("parenting", ["parenthood"]),
        ("paris", ["paris, france"]),
        ("police", ["cop", "police officer"]),
        ("racism", ["racist"]),
        ("science fiction", ["sci-fi", "sci fi"]),
        ("shougi", ["shogi"]),
        ("shoujo ai", ["girls' love (gl)"]),
        ("shounen ai", ["boys' love (bl)"]),
        ("small breasts", ["small tits"]),
        ("taishou period", ["taisho period"]),
        ("unidentified flying object", ["ufo"]),
        ("virtual world", ["virtual reality", "vr"]),
        ("wolf", ["wolves"]),
        // TMDB <- TMDB Mappings
        (null, ["eroticism", "erotism"]),
        (null, ["mixed martial arts (mma)", "mixed martial arts"]),
        (null, ["online game", "online gaming"]),
        (null, ["paranormal", "paranormal activity", "paranormal events", "paranormal investigation", "paranormal phenomena"]),
        (null, ["otome game", "otome"]),
        (null, ["role playing game (rpg)", "rpg"]),
        (null, ["romcom", "love comedy"])
    }.SelectMany(rule => (rule.Anidb != null ? rule.Tmdb.Append(rule.Anidb) : rule.Tmdb).Select(t => (Key: t, Rule: (TmdbPrimary: rule.Tmdb[0], AnidbTarget: rule.Anidb)))).ToFrozenDictionary(x => x.Key, x => x.Rule, StringComparer.OrdinalIgnoreCase);

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
        if ((sourceSetting is TagSources.Combined or TagSources.AniDB) && shokoSeries?.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
            anidbNames = anidbTags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (minWeight <= 0 || t.Weight >= minWeight)).Select(t => (t.Name, Priority: 1));

        IEnumerable<(string Name, int Priority)> tmdbNames = [];
        bool isCombined = sourceSetting == TagSources.Combined;
        if ((sourceSetting is TagSources.Combined or TagSources.TMDB) && (tmdbSeries ?? shokoSeries?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()) is { } tmdb)
        {
            var validTags = tmdb
                .Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (t.Kind is TagKind.Genre || (t.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(t.Name))))
                .Select(t => t.Name.Trim())
                .ToList();

            tmdbNames = validTags.Select(name =>
            {
                if (s_tagMerges.TryGetValue(name, out var rule))
                    name =
                        isCombined && rule.AnidbTarget != null ? rule.AnidbTarget
                        : rule.AnidbTarget != null && validTags.Any(vt => string.Equals(vt, rule.AnidbTarget, StringComparison.OrdinalIgnoreCase)) ? rule.AnidbTarget
                        : rule.TmdbPrimary;

                return (Name: name, Priority: 2);
            });
        }

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

        string key = sb.ToString();

        // Simple plural stemming for deduplication keys (preserves display names)
        if (key.Length > 3 && key.EndsWith('s') && key[^2] is not ('s' or 'u' or 'i' or 'o'))
            key = key[..^1];

        return key;
    }

    /// <summary>Filters the raw tag strings against the user blacklist, deduplicates across punctuation and diacritics, and normalizes them into title case.</summary>
    /// <param name="tags">The raw collection of tag name strings to process along with their source priority.</param>
    /// <param name="userBlacklist">The array of blacklisted tag names to filter out.</param>
    /// <returns>An enumerable collection of formatted anonymous objects containing the sanitized tags.</returns>
    private static IEnumerable<object> FilterAndFormat(IEnumerable<(string Name, int Priority)> tags, string[] userBlacklist)
    {
        var blacklistKeys = userBlacklist.Select(NormalizeTagKey).Where(k => k.Length > 0).ToHashSet();

        return tags.Select(t => (CleanName: TagCleanupRegex().Replace(t.Name, "").Trim(), t.Priority))
            .Where(t => !string.IsNullOrWhiteSpace(t.CleanName) && !s_tagBlacklistHelper.Contains(t.CleanName))
            .Select(t => (t.CleanName, NormKey: NormalizeTagKey(t.CleanName), t.Priority))
            .Where(t => blacklistKeys.Count == 0 || !blacklistKeys.Contains(t.NormKey))
            .Select(t =>
            {
                if (s_globalTagCache.TryGetValue(t.NormKey, out var cached) && cached.Priority <= t.Priority)
                    return (Key: t.NormKey, cached.DisplayName);

                string titleCased = TitleCase(t.CleanName);
                var (displayName, _) = s_globalTagCache.AddOrUpdate(t.NormKey, _ => (titleCased, t.Priority), (_, existing) => t.Priority <= existing.Priority ? (titleCased, t.Priority) : existing);
                return (Key: t.NormKey, DisplayName: displayName);
            })
            .DistinctBy(t => t.Key)
            .Select(t => (object)new { tag = t.DisplayName });
    }

    #endregion

    #region Title Casing Logic

    /// <summary>Formats an individual word or hyphenated compound according to capitalization, uppercase acronym, lowercase suffix, and special casing rules.</summary>
    /// <param name="word">The lowercase word token.</param>
    /// <returns>The properly cased word string.</returns>
    private static string FormatWord(string word)
    {
        if (s_forceSpecial.TryGetValue(word, out var special))
            return special;
        if (s_forceLower.Contains(word))
            return word;
        if (s_forceUpper.Contains(word))
            return word.ToUpperInvariant();

        if (word.Contains('-'))
        {
            var parts = word.Split('-');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                parts[i] =
                    i > 0 && s_forceLower.Contains("-" + parts[i]) ? parts[i].ToLowerInvariant()
                    : i > 0 && s_forceSpecial.TryGetValue("-" + parts[i], out var hyphenSpecial) ? hyphenSpecial.TrimStart('-')
                    : s_forceSpecial.TryGetValue(parts[i], out var partSpecial) ? partSpecial
                    : s_forceUpper.Contains(parts[i]) ? parts[i].ToUpperInvariant()
                    : char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
            }
            return string.Join('-', parts);
        }

        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>Convert text to title case, honouring special word list logic.</summary>
    /// <param name="text">Input text.</param>
    /// <returns>Formatted string.</returns>
    public static string TitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Primary Pass: Capitalize words, hyphenated compounds, and apply Upper/Lower/Special lists
        string result = WordRegex().Replace(text.ToLowerInvariant(), m => FormatWord(m.Value));

        // Force capitalise the first character no matter what
        result = char.ToUpperInvariant(result[0]) + result[1..];

        // Force capitalise the first character of the last word no matter what
        int lastSpaceIndex = result.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            result = result[..(lastSpaceIndex + 1)] + char.ToUpperInvariant(result[lastSpaceIndex + 1]) + result[(lastSpaceIndex + 2)..];

        return result;
    }

    #endregion
}
