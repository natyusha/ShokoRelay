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
        "-Christian", "-Japanese", "AnimeFesta", "ComicFesta", "d'Etat", "JDrama", "noitaminA",                                          // AniDB
        "VTuber", "YouTuber"                                                                                                             // TMDB
    }.ToFrozenDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);

    /// <summary>Default blacklisted tag keys loaded from the embedded CSV resource.</summary>
    private static readonly FrozenSet<string> s_defaultTagBlacklist;

    /// <summary>Default tag merges mapping synonymous keywords/tags to a primary tag representation loaded from the embedded CSV resource.</summary>
    private static readonly FrozenDictionary<string, string> s_defaultTagMerges;

    /// <summary>Statically initializes the default tag blacklist and merges from the embedded resource.</summary>
    static TagHelper()
    {
        var (blacklist, merges, _) = LoadDefaultTagOverrides();
        s_defaultTagBlacklist = blacklist.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        s_defaultTagMerges = merges.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

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

    #region Tag Override Loading

    /// <summary>Loads default tag blacklist and merge mappings directly from the embedded assembly resource.</summary>
    /// <returns>A tuple containing default blacklisted keys, merge dictionary, and mentioned tag set.</returns>
    private static (HashSet<string> Blacklist, Dictionary<string, string> Merges, HashSet<string> MergeMentioned) LoadDefaultTagOverrides()
    {
        try
        {
            var asm = typeof(TagHelper).Assembly;
            if (
                asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ShokoRelayConstants.FileDefaultTagOverrides, StringComparison.OrdinalIgnoreCase)) is { } resName
                && asm.GetManifestResourceStream(resName) is { } stream
            )
            {
                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return TextHelper.ParseOverrides(reader, NormalizeTagKey);
            }
        }
        catch { }

        return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

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

            using var reader = new StringReader(rawConfig);
            var (userBlacklist, userMerges, userMergeMentioned) = TextHelper.ParseOverrides(reader, NormalizeTagKey);

            var activeMerges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in s_defaultTagMerges)
            {
                var normKey = NormalizeTagKey(def.Key);
                var normVal = NormalizeTagKey(def.Value);
                // If a default source or target is mentioned on a user merge line, drop the default rule entirely to allow the user to redefine it
                if (!userMergeMentioned.Contains(normKey) && !userMergeMentioned.Contains(normVal))
                    activeMerges[normKey] = def.Value;
            }

            foreach (var (normSource, target) in userMerges)
                activeMerges[normSource] = target;

            // Start with default blacklist, dropping any tag mentioned on a user merge line so user merges can unblock default blacklisted tags
            var activeBlacklist = new HashSet<string>(s_defaultTagBlacklist.Where(b => !userMergeMentioned.Contains(b)), StringComparer.OrdinalIgnoreCase);

            foreach (var b in userBlacklist)
                activeBlacklist.Add(b);

            s_cachedBlacklist = activeBlacklist;
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
            .Where(t => !string.IsNullOrWhiteSpace(t.CleanName))
            // 2. Resolve merge rules using normalized keys
            .Select(t =>
            {
                var normKey = NormalizeTagKey(t.CleanName);
                var isMerged = merges.TryGetValue(normKey, out var target);
                var effectiveName = isMerged ? target! : t.CleanName;
                var effectiveKey = isMerged ? NormalizeTagKey(effectiveName) : normKey;
                return (CleanName: effectiveName, SourceKey: normKey, NormKey: effectiveKey, t.Priority);
            })
            // 3. Blacklist filtering (checks both source and merged keys)
            .Where(t => t.NormKey.Length > 0 && !blacklistKeys.Contains(t.NormKey) && !blacklistKeys.Contains(t.SourceKey))
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
