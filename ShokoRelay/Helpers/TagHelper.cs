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

    /// <summary>Composite key identifying a unique tag configuration context (including per-library overrides).</summary>
    private readonly record struct TagCacheKey(string Overrides, TagSources Sources, MinimumTagWeight MinWeight);

    /// <summary>Holds compiled override rules and pre-warmed cross-series display name mappings for a single tag configuration.</summary>
    private sealed class TagCacheState(HashSet<string> blacklist, Dictionary<string, string> merges)
    {
        public readonly HashSet<string> Blacklist = blacklist;
        public readonly Dictionary<string, string> Merges = merges;
        public readonly ConcurrentDictionary<string, TextHelper.GlobalNameEntry> DisplayNames = new();
        public readonly ConcurrentDictionary<int, byte> RecordedSeriesIds = new();
        public readonly Lock WarmLock = new();
        public volatile bool IsWarmed;
    }

    /// <summary>Cache mapping active tag configuration contexts to their compiled rules and global display name caches.</summary>
    private static readonly ConcurrentDictionary<TagCacheKey, TagCacheState> s_tagCaches = new();

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

    /// <summary>Pre-warms the global tag display name cache across all series in Shoko for the current configuration.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    public static void WarmCache(IMetadataService metadataService)
    {
        var key = new TagCacheKey(Settings.TagOverrides ?? string.Empty, Settings.TagSources, Settings.MinimumTagWeight);
        if (key.Sources != TagSources.None)
            EnsureWarmed(GetOrCreateTagCacheState(key), key, metadataService);
    }

    /// <summary>Retrieves or compiles the cached blacklist, merge rules, and display-name state for the given configuration key.</summary>
    /// <param name="key">The active tag configuration key.</param>
    /// <returns>A <see cref="TagCacheState"/> containing active rules and display name caches.</returns>
    private static TagCacheState GetOrCreateTagCacheState(TagCacheKey key)
    {
        if (s_tagCaches.TryGetValue(key, out var existing))
            return existing;

        lock (s_configLock)
        {
            if (s_tagCaches.TryGetValue(key, out existing))
                return existing;

            // Cap cached configurations to prevent unbounded growth during iterative settings edits
            if (s_tagCaches.Count >= 4)
                s_tagCaches.Clear();

            using var reader = new StringReader(key.Overrides);
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

            return s_tagCaches[key] = new TagCacheState(activeBlacklist, activeMerges);
        }
    }

    /// <summary>Ensures the tag display name cache for the specified configuration has been populated across all Shoko series.</summary>
    /// <param name="state">The target cache state.</param>
    /// <param name="key">The configuration key governing tag collection.</param>
    /// <param name="metadataService">Optional metadata service to enumerate all series.</param>
    private static void EnsureWarmed(TagCacheState state, TagCacheKey key, IMetadataService? metadataService)
    {
        if (state.IsWarmed || metadataService == null)
            return;

        lock (state.WarmLock)
        {
            if (state.IsWarmed)
                return;

            foreach (var series in metadataService.GetAllShokoSeries() ?? [])
                if (state.RecordedSeriesIds.TryAdd(series.LocalID, 0))
                    foreach (var (cleanName, normKey, priority) in ExtractNormalizedTags(CollectRawTags(series, null, key.Sources, key.MinWeight), state))
                        state.DisplayNames.GetOrAdd(normKey, _ => new TextHelper.GlobalNameEntry()).ResolveDisplayName(TitleCase(cleanName), priority, record: true);

            state.IsWarmed = true;
        }
    }

    /// <summary>Return an array of tag objects derived from a series, applying filters, global deduplication, and configured sources.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="tmdbSeries">Optional pre-resolved TMDB series metadata object.</param>
    /// <param name="metadataService">Optional metadata service used to pre-warm cross-series deduplication.</param>
    /// <returns>An array of tag metadata objects.</returns>
    public static object[] GetFilteredTags(ISeries series, ISeries? tmdbSeries = null, IMetadataService? metadataService = null)
    {
        var key = new TagCacheKey(Settings.TagOverrides ?? string.Empty, Settings.TagSources, Settings.MinimumTagWeight);
        if (key.Sources == TagSources.None)
            return [];

        var state = GetOrCreateTagCacheState(key);
        EnsureWarmed(state, key, metadataService);

        bool shouldRecord = series is IShokoSeries ss && state.RecordedSeriesIds.TryAdd(ss.LocalID, 0);

        return
        [
            .. ExtractNormalizedTags(CollectRawTags(series, tmdbSeries, key.Sources, key.MinWeight), state)
                .Select(t => state.DisplayNames.GetOrAdd(t.NormKey, _ => new TextHelper.GlobalNameEntry()).ResolveDisplayName(TitleCase(t.CleanName), t.Priority, shouldRecord))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(displayName => (object)new { tag = displayName }),
        ];
    }

    /// <summary>Collects raw tag names and source priorities for a series according to the configured tag sources and minimum weight.</summary>
    /// <param name="series">The series metadata.</param>
    /// <param name="tmdbSeries">Optional pre-resolved TMDB series metadata object.</param>
    /// <param name="sources">Configured tag source flags.</param>
    /// <param name="minWeight">Minimum AniDB tag weight threshold.</param>
    /// <returns>An enumerable of raw tag names paired with source priorities.</returns>
    private static IEnumerable<(string Name, int Priority)> CollectRawTags(ISeries series, ISeries? tmdbSeries, TagSources sources, MinimumTagWeight minWeight)
    {
        if (sources == TagSources.None)
            return [];

        var shokoSeries = series as IShokoSeries;
        IEnumerable<(string Name, int Priority)> result = [];

        if (sources.HasFlag(TagSources.AniDB) && shokoSeries?.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
            result = result.Concat(anidbTags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && ((int)minWeight <= 0 || t.Weight >= (int)minWeight)).Select(t => (t.Name, Priority: 1)));

        if (sources.HasFlag(TagSources.TMDB) && (tmdbSeries ?? shokoSeries?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() ?? (series.ID.Source == MetadataSource.TMDB ? series : null)) is { } tmdb)
            result = result.Concat(
                tmdb.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name) && (t.Kind is TagKind.Genre || (t.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(t.Name)))).Select(t => (t.Name, Priority: 2))
            );

        if (sources.HasFlag(TagSources.User) && shokoSeries?.Tags is { } shokoTags)
            result = result.Concat(shokoTags.Where(t => t != null && t.ID.Source == MetadataSource.User && !string.IsNullOrWhiteSpace(t.Name)).Select(t => (t.Name, Priority: 3)));

        return result;
    }

    /// <summary>Generates a normalized comparison key ignoring case, spacing, punctuation, diacritics, and common English plurals.</summary>
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
        if (key.Length > 4 && key.EndsWith("ies", StringComparison.Ordinal))
            key = key[..^3] + "y";
        else if (key.Length > 4 && key.EndsWith("ie", StringComparison.Ordinal))
            key = key[..^2] + "y";
        else if (key.Length > 5 && key.EndsWith("sses", StringComparison.Ordinal))
            key = key[..^2];
        else if (key.Length > 3 && key.EndsWith('s') && key[^2] is not ('s' or 'u' or 'i' or 'o'))
            key = key[..^1];

        return key;
    }

    /// <summary>Strips notes, applies merge rules and blacklists, and deduplicates normalized tags within a single series.</summary>
    /// <param name="tags">The raw collection of tag names paired with source priority.</param>
    /// <param name="state">The active tag cache state containing blacklists and merge mappings.</param>
    /// <returns>An enumerable of cleaned, merged, and deduplicated tag entries.</returns>
    private static IEnumerable<(string CleanName, string NormKey, int Priority)> ExtractNormalizedTags(IEnumerable<(string Name, int Priority)> tags, TagCacheState state) =>
        tags
        // 1. Strip moderator notes / AniDB comments first
        .Select(t => (CleanName: TagCleanupRegex().Replace(t.Name, "").Trim(), t.Priority))
            .Where(t => !string.IsNullOrWhiteSpace(t.CleanName))
            // 2. Resolve merge rules using normalized keys
            .Select(t =>
            {
                var normKey = NormalizeTagKey(t.CleanName);
                var isMerged = state.Merges.TryGetValue(normKey, out var target);
                var effectiveName = isMerged ? target! : t.CleanName;
                var effectiveKey = isMerged ? NormalizeTagKey(effectiveName) : normKey;
                return (CleanName: effectiveName, SourceKey: normKey, NormKey: effectiveKey, t.Priority);
            })
            // 3. Blacklist filtering (checks both source and merged keys)
            .Where(t => t.NormKey.Length > 0 && !state.Blacklist.Contains(t.NormKey) && !state.Blacklist.Contains(t.SourceKey))
            // 4. Deduplicate within this series (AniDB tags will beat TMDB because anidbNames precedes tmdbNames)
            .DistinctBy(t => t.NormKey)
            .Select(t => (t.CleanName, t.NormKey, t.Priority));

    /// <summary>Builds a diagnostic CSV report of all tags across Shoko series after applying default tag overrides.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    /// <returns>A CSV-formatted string listing source, tag, and series count.</returns>
    public static string BuildTagReport(IMetadataService metadataService)
    {
        var stats = new Dictionary<(int SourceOrder, string Source, string NormKey), (TextHelper.GlobalNameEntry Entry, int Count)>();

        foreach (var series in metadataService.GetAllShokoSeries() ?? [])
        {
            var seenInSeries = new HashSet<(string Source, string NormKey)>();

            void RecordTag(string source, int sourceOrder, string? rawName) =>
                TextHelper.RecordReportItem(
                    stats,
                    seenInSeries,
                    source,
                    sourceOrder,
                    string.IsNullOrWhiteSpace(rawName) ? null : TagCleanupRegex().Replace(rawName, ""),
                    NormalizeTagKey,
                    s_defaultTagMerges,
                    s_defaultTagBlacklist,
                    TitleCase
                );

            if (series.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
                foreach (var t in anidbTags)
                    RecordTag("AniDB", 0, t.Name);

            if (series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { } tmdb)
                foreach (var t in tmdb.Tags)
                    if (t.Kind is TagKind.Genre || (t.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(t.Name)))
                        RecordTag("TMDB", 1, t.Name);

            foreach (var t in series.Tags ?? [])
                if (t != null && t.ID.Source == MetadataSource.User)
                    RecordTag("User", 2, t.Name);
        }

        return TextHelper.FormatCsvReport("Tag", "tag", stats);
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
