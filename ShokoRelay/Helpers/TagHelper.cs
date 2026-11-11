using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
        // TMDB genres/keywords which are too broad
        "animation", "anime", "hentai",
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
        "3d", "bdsm", "cg", "cgi", "ed", "fff", "ffm", "ii", "milf", "mmf", "mmm", "neet", "npc", "op", "rpg", "tbs", "tv",              // AniDB
        "a.i.", "bl", "diy", "gl", "jsdf", "lgbt", "mma", "mmorpg", "ntr", "ol", "ona", "ova", "pov", "ptsd", "ufo", "us", "vr", "vrmmo" // TMDB
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Special cases where a specific capitalisation style is preferred</summary>
    private static readonly FrozenDictionary<string, string> s_forceSpecial = new[] {
        "-Japanese", "AnimeFesta", "ComicFesta", "d'Etat", "JDrama", "noitaminA",                                                        // AniDB
        "VTuber", "YouTuber"                                                                                                             // TMDB
    }.ToFrozenDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);

    // csharpier-ignore
    /// <summary>Explicit tag merges mapping synonymous keywords/tags to a primary tag representation.</summary>
    /// <remarks>
    /// - This section will likely be expanded fairly often and can be quite subjective
    /// - If AniDB groups the tags via aliases they will also be grouped here by the rules below
    /// - Outside of the stock AniDB groupings it is best to avoid merging things here if there is any ambiguity
    /// </remarks>
    private static readonly FrozenDictionary<string, string> s_defaultTagMerges = new string[][]
    {
        // AniDB <- TMDB Mappings                                                                                                        // Generally uses the primary tag entry
        ["3d cg animation", "3d animation", "cg animated"],
        ["absurdist humour", "absurd", "absurdist"],
        ["afterlife", "life after death"],
        ["android", "humanoid robot"],
        ["artificial intelligence", "artificial intelligence (a.i.)"],
        ["assassin", "hitman"],
        ["association football", "football (soccer)"],
        ["attempted rape", "rape attempt"],
        ["bisexual", "bisexuality"],
        ["bishoujo", "bishojo"],
        ["black humour", "black humor"],
        ["body swap", "body exchange"],
        ["boobjob", "tit fucking", "titty fuck"],
        ["brainwashing", "mind control", "hypnosis", "mesmerization"],
        ["bukkake", "bukakke"],
        ["cafe", "coffee shop"],
        ["cat person", "cat people"],
        ["cheating", "cheat"],
        ["chinese production", "donghua"],
        ["combat", "fight", "fighting"],
        ["coming of age", "growing up"],
        ["corrupt", "corruption"],
        ["cosplaying", "cosplay", "cosplayer"],
        ["countryside", "rural", "rural area", "rural town"],                                                                            // 'rural town' is also a separate series tag on AniDB
        ["creampie", "nakadashi"],
        ["cross-dressing", "cross dresser"],
        ["curse", "cursed"],
        ["cute girls doing cute things", "cgdct"],
        ["cyberpunk", "cyber"],
        ["daikaijuu", "kaiju", "giant monster"],
        ["dancing", "dance"],
        ["dark-skinned girl", "dark skin girl"],
        ["deception", "deceit"],
        ["deflowering", "defloration"],
        ["deity", "god", "goddess"],                                                                                                     // AniDB groups both genders
        ["delinquent", "delinquency"],
        ["demon", "youkai", "Oni", "Ogre"],
        ["dildos - vibrators", "dildo", "dildos", "vibrator"],
        ["dominatrix", "femdom", "female domination"],
        ["dystopia", "dystopian future"],
        ["elf", "elves"],
        ["fellatio", "blowjob", "blow job"],
        ["genetic modification", "genetic engineering"],
        ["hairdresser", "hairstylist"],
        ["heavy metal music", "heavy metal"],
        ["hokkaido", "hokkaido, japan"],
        ["homemaker", "housewife", "housewives"],
        ["horse riding", "horseback riding"],
        ["human cannibalism", "eating of humans"],
        ["immortality", "immortal"],
        ["isekai", "another world", "other world", "otherworld"],
        ["ishikawa", "ishikawa, japan"],
        ["journey", "travel", "traveling", "voyage"],
        ["juujin", "furry"],
        ["kidnapping", "kidnapped"],
        ["kitsune", "fox spirit"],
        ["korean-japanese co-production", "japanese-korean coproduction"],
        ["kumamoto", "kumamoto, japan"],
        ["kyoto", "kyoto, japan"],
        ["large breasts", "big tits", "big boobs", "big breast"],
        ["loli", "lolis", "loli type"],
        ["london", "london, england"],
        ["lonely", "loneliness"],
        ["love between enemies", "enemies to lovers"],
        ["mafia", "yakuza", "organized crime"],
        ["magic", "wizard", "sorcerer", "mage", "magician"],
        ["magical girl", "magic girl"],
        ["maze", "labyrinth"],
        ["mecha", "mechs"],
        ["mobile phone", "cellphone", "smartphone"],
        ["multiple personality disorder", "multiple personality", "double personality", "split personality", "dissociative identities"],
        ["nagasaki", "nagasaki, japan"],
        ["nazis", "nazi"],
        ["netorare", "netorare (ntr)", "ntr"],
        ["ninja", "shinobi", "female ninja", "kunoichi"],                                                                                // AniDB groups both genders
        ["office lady", "office lady (ol)"],
        ["oral", "oral sex"],
        ["osaka", "osaka, japan"],
        ["pantsu", "panties"],
        ["parallel world", "parallel universe"],
        ["paramilitary", "paramilitary organization"],
        ["parenting", "parenthood"],
        ["paris", "paris, france"],
        ["perverted", "pervert", "pervertion"],
        ["point of view", "pov (point of view)"],
        ["police", "cop", "police officer"],
        ["post-apocalyptic", "post-apocalyptic future"],
        ["racism", "racist"],
        ["reincarnation", "rebirth"],
        ["revenge", "vengeance"],
        ["rpg", "role playing game (rpg)"],
        ["salaryman", "office worker"],
        ["science fiction", "sci-fi", "sci fi"],
        ["scissoring", "tribadism"],
        ["secret organisation", "secret organization"],
        ["seiyuu", "voice acting"],
        ["sengoku period", "warring states period"],
        ["sex", "sexual intercourse"],
        ["sex change", "gender swap"],
        ["sexual humour", "sexual humor"],
        ["shintoism", "shinto"],
        ["shizuoka", "shizuoka, japan", "numazu, shizuoka, japan"],
        ["shougi", "shogi"],
        ["shoujo ai", "girls' love (gl)"],
        ["shounen ai", "boys' love (bl)"],
        ["slapstick", "slapstick comedy"],
        ["small breasts", "small tits", "flat chest", "pettanko"],
        ["space", "outer space"],
        ["spaceship", "spacecraft"],
        ["sumo wrestling", "sumo"],
        ["super power", "special ability", "special power", "superhuman", "superhuman abilities"],
        ["swordplay", "swordsplay"],
        ["taishou period", "taisho period"],
        ["tarot", "tarot cards"],
        ["teacher x student", "teacher student relationship", "teacher-student love"],
        ["thievery", "thief"],
        ["time stop", "time freeze"],
        ["tokugawa period", "edo period"],
        ["tokyo", "tokyo, japan"],
        ["treasure hunting", "treasure hunt"],
        ["unidentified flying object", "ufo"],
        ["university", "college"],
        ["urination", "peeing", "pissing"],
        ["virgin", "loss of virginity", "virginity"],
        ["virtual world", "virtual reality", "vr"],
        ["voyeurism", "voyeur"],
        ["water scarcity", "water shortage"],
        ["wolf", "wolves"],
        ["yokohama", "yokohama, Kanagawa, Japan"],
        // TMDB <- TMDB Mappings                                                                                                         // generally in order of popularity/usage
        ["car racing", "car race"],
        ["censored", "censorship"],
        ["classmates", "schoolmates"],
        ["disability", "disabled person"],
        ["eroticism", "erotism"],
        ["gay theme", "male homosexuality"],
        ["hobby", "hobbies"],
        ["horse racing", "horse race"],
        ["juvenile delinquent", "juvenile delinquency"],
        ["kiss", "kissing"],
        ["lesbian", "female homosexuality"],
        ["medieval", "medieval times"],
        ["megami tensei", "megaten", "shin megami tensei"],
        ["memories", "memory"],
        ["mixed martial arts (mma)", "mixed martial arts"],
        ["mother", "mom"],
        ["mourning", "grief"],
        ["near future", "not too distant future"],
        ["online game", "online gaming"],
        ["otome game", "otome"],
        ["paranormal", "paranormal activity", "paranormal events", "paranormal investigation", "paranormal phenomena"],
        ["past life", "past lives"],
        ["romcom", "love comedy"],
        ["roommates", "flatmates", "shared house", "cohabitation", "living together"],                                                   // 'under one roof' is also an AniDB tag with many matches
        ["schoolgirl", "student girl", "female student", "high school girl", "jk"],                                                      // AniDB has a character tag called 'student' for these
        ["semen", "cum"],
        ["shapeshifting", "shapeshifter"],
        ["street racing", "street race"],
        ["supernatural power", "supernatural ability"],
        ["sword fight", "sword duel"],
        ["teenager", "teenage", "adolescence", "adolescent", "youth"],                                                                   // AniDB has a character tag called 'adolescent' for these
        ["transsexual", "transsexuality"],
        ["university student", "college student"],
        ["wealthy family", "rich family"]
    }.SelectMany(rule => rule.Skip(1).Select(t => (Key: t, Target: rule[0]))).ToFrozenDictionary(x => x.Key, x => x.Target, StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks tag variants and series counts across multiple series for cross-series deduplication.</summary>
    private sealed class TagCacheEntry
    {
        public readonly Lock Lock = new();
        public int Priority = int.MaxValue;
        public string BestDisplayName = string.Empty;
        public int BestCount = 0;
        public readonly Dictionary<string, int> VariantCounts = new(StringComparer.Ordinal);

        /// <summary>Records an incoming tag variant and returns the preferred display name based on source priority and frequency.</summary>
        /// <param name="variant">The title-cased tag variant string.</param>
        /// <param name="priority">The source priority value (lower numerical values indicate higher priority).</param>
        /// <returns>The preferred display name for the normalized tag key.</returns>
        public string RecordAndGetDisplayName(string variant, int priority)
        {
            lock (Lock)
            {
                // Higher-priority source (e.g. AniDB 1 beats TMDB 2)
                if (priority < Priority)
                {
                    Priority = priority;
                    VariantCounts.Clear();
                    VariantCounts[variant] = 1;
                    BestDisplayName = variant;
                    BestCount = 1;
                    return BestDisplayName;
                }

                // Same priority (e.g. TMDB vs TMDB): choose the variant used on the highest number of series
                if (priority == Priority)
                {
                    int count = VariantCounts.GetValueOrDefault(variant, 0) + 1;
                    VariantCounts[variant] = count;
                    if (count > BestCount)
                    {
                        BestDisplayName = variant;
                        BestCount = count;
                    }
                    return BestDisplayName;
                }

                // Lower-priority source arriving when a higher-priority display name is already cached
                return BestDisplayName;
            }
        }
    }

    /// <summary>Global cache mapping normalized tag keys to their preferred display representations and priorities to ensure cross-series deduplication.</summary>
    private static readonly ConcurrentDictionary<string, TagCacheEntry> s_globalTagCache = new();

    /// <summary>State variable caching the raw configuration string to detect changes. Initialized to null so default rules are loaded on startup.</summary>
    private static string? s_lastTagConfig;

    /// <summary>Cached set of normalized blacklisted tag keys.</summary>
    private static HashSet<string> s_cachedBlacklist = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached dictionary of active tag merges (combining defaults with user overrides), keyed by normalized source keys.</summary>
    private static Dictionary<string, string> s_cachedMerges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lock object for synchronizing tag configuration compilation.</summary>
    private static readonly Lock s_configLock = new();

    #endregion

    #region Tag Filtering

    /// <summary>Parses and caches the user's blacklist and merge rules, invalidating when the configuration changes.</summary>
    /// <returns>A tuple containing the active normalized blacklist and active merge dictionary.</returns>
    private static (HashSet<string> Blacklist, Dictionary<string, string> Merges) GetTagConfig()
    {
        var rawConfig = Settings.TagOverrides ?? string.Empty;
        if (s_lastTagConfig != null && (ReferenceEquals(s_lastTagConfig, rawConfig) || string.Equals(s_lastTagConfig, rawConfig, StringComparison.Ordinal)))
            return (s_cachedBlacklist, s_cachedMerges);

        lock (s_configLock)
        {
            if (s_lastTagConfig != null && string.Equals(s_lastTagConfig, rawConfig, StringComparison.Ordinal))
                return (s_cachedBlacklist, s_cachedMerges);

            var blacklistKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var userMerges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var userMentionedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in rawConfig.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith(';'))
                {
                    var parts = line.TrimStart(';').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 0)
                    {
                        string target = parts[0];
                        if (NormalizeTagKey(target) is { Length: > 0 } normTarget)
                            userMentionedTags.Add(normTarget);

                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (NormalizeTagKey(parts[i]) is { Length: > 0 } normSource)
                            {
                                userMerges[normSource] = target;
                                userMentionedTags.Add(normSource);
                            }
                        }
                    }
                }
                else
                {
                    foreach (var tag in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (NormalizeTagKey(tag) is { Length: > 0 } normKey)
                            blacklistKeys.Add(normKey);
                }
            }

            var activeMerges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in s_defaultTagMerges)
            {
                var normKey = NormalizeTagKey(def.Key);
                var normVal = NormalizeTagKey(def.Value);
                // If a default source or target is mentioned in user overrides, drop the default rule entirely to allow the user to redefine it
                if (!userMentionedTags.Contains(normKey) && !userMentionedTags.Contains(normVal))
                    activeMerges[normKey] = def.Value;
            }

            foreach (var (normSource, target) in userMerges)
                activeMerges[normSource] = target;

            s_cachedBlacklist = blacklistKeys;
            s_cachedMerges = activeMerges;
            s_lastTagConfig = rawConfig;
            s_globalTagCache.Clear(); // Invalidate cross-series cache when configuration changes

            return (s_cachedBlacklist, s_cachedMerges);
        }
    }

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

        var (blacklistKeys, merges) = GetTagConfig();
        var sourceSetting = Settings.TagSources;

        var shokoNames = shokoTags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n, Priority: 3));

        if (sourceSetting == TagSources.UserOnly)
            return [.. FilterAndFormat(shokoNames, blacklistKeys, merges)];

        IEnumerable<(string Name, int Priority)> anidbNames = [];
        int minWeight = (int)Settings.MinimumTagWeight;
        if ((sourceSetting is TagSources.Combined or TagSources.AniDB) && shokoSeries?.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
            anidbNames = anidbTags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (minWeight <= 0 || t.Weight >= minWeight)).Select(t => (t.Name, Priority: 1));

        IEnumerable<(string Name, int Priority)> tmdbNames = [];
        if ((sourceSetting is TagSources.Combined or TagSources.TMDB) && (tmdbSeries ?? shokoSeries?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()) is { } tmdb)
            tmdbNames = tmdb
                .Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (t.Kind is TagKind.Genre || (t.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(t.Name))))
                .Select(t => (t.Name, Priority: 2));

        return [.. FilterAndFormat(anidbNames.Concat(tmdbNames).Concat(shokoNames), blacklistKeys, merges)];
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
    /// <param name="blacklistKeys">The set of normalized blacklisted tag keys to filter out.</param>
    /// <param name="merges">The active dictionary of tag merges mapping normalized source keys to primary target names.</param>
    /// <returns>An enumerable collection of formatted anonymous objects containing the sanitized tags.</returns>
    private static IEnumerable<object> FilterAndFormat(IEnumerable<(string Name, int Priority)> tags, HashSet<string> blacklistKeys, Dictionary<string, string> merges)
    {
        return tags
        // 1. Strip moderator notes / AniDB comments first
        .Select(t => (CleanName: TagCleanupRegex().Replace(t.Name, "").Trim(), t.Priority))
            .Where(t => !string.IsNullOrWhiteSpace(t.CleanName) && !s_tagBlacklistHelper.Contains(t.CleanName))
            // 2. Resolve merge rules using normalized keys
            .Select(t =>
            {
                var normKey = NormalizeTagKey(t.CleanName);
                var effectiveName = merges.TryGetValue(normKey, out var target) ? target : t.CleanName;
                var effectiveKey = merges.ContainsKey(normKey) ? NormalizeTagKey(effectiveName) : normKey;
                return (CleanName: effectiveName, NormKey: effectiveKey, t.Priority);
            })
            // 3. Blacklist filtering (checks both clean and merged keys) and AniDB broad blacklist helper
            .Where(t => t.NormKey.Length > 0 && !s_tagBlacklistHelper.Contains(t.CleanName) && (blacklistKeys.Count == 0 || !blacklistKeys.Contains(t.NormKey)))
            // 4. Deduplicate within this series (AniDB tags will beat TMDB because anidbNames precedes tmdbNames)
            .DistinctBy(t => t.NormKey)
            // 5. Update and retrieve preferred representation across series
            .Select(t =>
            {
                string titleCased = TitleCase(t.CleanName);
                var entry = s_globalTagCache.GetOrAdd(t.NormKey, _ => new TagCacheEntry());
                string displayName = entry.RecordAndGetDisplayName(titleCased, t.Priority);
                return (object)new { tag = displayName };
            });
    }

    #endregion

    #region Title Casing Logic

    /// <summary>Applies special case preferences for whole words and hyphenated suffixes.</summary>
    /// <param name="word">The word token to format.</param>
    /// <returns>The properly formatted word string.</returns>
    private static string ApplySpecialCasing(string word)
    {
        if (s_forceSpecial.TryGetValue(word, out var special))
            return special;

        if (word.Contains('-'))
        {
            var parts = word.Split('-');
            for (int i = 1; i < parts.Length; i++)
                if (!string.IsNullOrEmpty(parts[i]) && s_forceSpecial.TryGetValue("-" + parts[i], out var hyphenSpecial))
                    parts[i] = hyphenSpecial.TrimStart('-');

            return string.Join('-', parts);
        }

        return word;
    }

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
                text.ToLowerInvariant(),
                m =>
                    s_forceLower.Contains(m.Value) ? m.Value.ToLowerInvariant()
                    : s_forceUpper.Contains(m.Value) ? m.Value.ToUpperInvariant()
                    // Capitalise all words accounting for apostrophes first
                    : char.ToUpperInvariant(m.Value[0]) + m.Value[1..]
            );

        // Force capitalise the first character no matter what
        result = char.ToUpperInvariant(result[0]) + result[1..];

        // Force capitalise the first character of the last word no matter what
        int lastSpaceIndex = result.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            result = result[..(lastSpaceIndex + 1)] + char.ToUpperInvariant(result[lastSpaceIndex + 1]) + result[(lastSpaceIndex + 2)..];

        // Apply special case preferences for whole words and hyphenated suffixes as the absolute final step
        result = WordRegex().Replace(result, m => ApplySpecialCasing(m.Value));
        return result;
    }

    #endregion
}
