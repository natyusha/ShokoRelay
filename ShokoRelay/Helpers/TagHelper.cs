using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata.Anidb;

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
    }.ToFrozenDictionary(text => text, text => text, StringComparer.OrdinalIgnoreCase);

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
    /// <param name="Overrides">The raw tag override lines.</param>
    /// <param name="Sources">The enabled tag sources.</param>
    /// <param name="MinWeight">The minimum AniDB tag weight threshold.</param>
    private readonly record struct TagCacheKey(string Overrides, TagSources Sources, MinimumTagWeight MinWeight);

    /// <summary>Holds compiled override rules and pre-warmed cross-series display name mappings for a single tag configuration.</summary>
    /// <param name="blacklist">Set of normalized blacklisted tag keys.</param>
    /// <param name="merges">Dictionary mapping source tag keys to target display names.</param>
    private sealed class TagCacheState(HashSet<string> blacklist, Dictionary<string, string> merges)
    {
        /// <summary>Set of normalized blacklisted tag keys.</summary>
        public readonly HashSet<string> Blacklist = blacklist;

        /// <summary>Dictionary mapping source tag keys to target display names.</summary>
        public readonly Dictionary<string, string> Merges = merges;

        /// <summary>Global display-name frequency entries keyed by normalized tag key.</summary>
        public readonly ConcurrentDictionary<string, CacheHelper.GlobalNameEntry> DisplayNames = new();

        /// <summary>Series IDs already recorded into <see cref="DisplayNames"/>.</summary>
        public readonly ConcurrentDictionary<int, byte> RecordedSeriesIds = new();

        /// <summary>Lock synchronizing initial cache warming.</summary>
        public readonly Lock WarmLock = new();

        /// <summary>Indicates whether <see cref="DisplayNames"/> has been warmed across all series.</summary>
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
            var assembly = typeof(TagHelper).Assembly;
            if (
                assembly.GetManifestResourceNames().FirstOrDefault(resourceName => resourceName.EndsWith(ShokoRelayConstants.FileDefaultTagOverrides, StringComparison.OrdinalIgnoreCase)) is { } resourceName
                && assembly.GetManifestResourceStream(resourceName) is { } stream
            )
                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return TextHelper.ParseOverrides(reader, NormalizeTagKey);
        }
        catch { }

        return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    #endregion

    #region Tag Filtering

    /// <summary>Thread-local accumulation state used during parallel tag cache warming.</summary>
    private sealed class WarmThreadState
    {
        /// <summary>Memoized cleanup and merge resolution results per raw tag name.</summary>
        public readonly Dictionary<string, (string NormKey, string TitleCased, bool Skip)> ResolveCache = new(StringComparer.Ordinal);

        /// <summary>Observed series counts per tag variant tuple.</summary>
        public readonly Dictionary<(string NormKey, int Priority, string TitleCased), int> Counts = [];

        /// <summary>Best-priority tag variant encountered within the current series.</summary>
        public readonly Dictionary<string, (int Priority, string TitleCased)> BestInSeries = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Pre-warms the global tag display name cache across all series in Shoko for the current configuration.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static void WarmCache(IMetadataService metadataService, CancellationToken cancellationToken = default)
    {
        var key = new TagCacheKey(Settings.TagOverrides ?? string.Empty, Settings.TagSources, Settings.MinimumTagWeight);
        if (key.Sources != TagSources.None)
            EnsureWarmed(GetOrCreateTagCacheState(key), key, metadataService, cancellationToken);
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
            foreach (var defaultEntry in s_defaultTagMerges)
            {
                var normalizedKey = NormalizeTagKey(defaultEntry.Key);
                var normalizedValue = NormalizeTagKey(defaultEntry.Value);
                // If a default source or target is mentioned on a user merge line, drop the default rule entirely to allow the user to redefine it
                if (!userMergeMentioned.Contains(normalizedKey) && !userMergeMentioned.Contains(normalizedValue))
                    activeMerges[normalizedKey] = defaultEntry.Value;
            }

            foreach (var (normSource, target) in userMerges)
                activeMerges[normSource] = target;

            // Start with default blacklist, dropping any tag mentioned on a user merge line so user merges can unblock default blacklisted tags
            var activeBlacklist = new HashSet<string>(s_defaultTagBlacklist.Where(tagKey => !userMergeMentioned.Contains(tagKey)), StringComparer.OrdinalIgnoreCase);

            foreach (var blacklistedTag in userBlacklist)
                activeBlacklist.Add(blacklistedTag);

            return s_tagCaches[key] = new TagCacheState(activeBlacklist, activeMerges);
        }
    }

    /// <summary>Ensures the tag display name cache for the specified configuration has been populated across all Shoko series.</summary>
    /// <param name="state">The target cache state.</param>
    /// <param name="key">The configuration key governing tag collection.</param>
    /// <param name="metadataService">Optional metadata service to enumerate all series.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    private static void EnsureWarmed(TagCacheState state, TagCacheKey key, IMetadataService? metadataService, CancellationToken cancellationToken = default)
    {
        if (state.IsWarmed || metadataService == null)
            return;

        lock (state.WarmLock)
        {
            if (state.IsWarmed)
                return;

            CacheHelper.WarmSeriesCache<WarmThreadState>(
                metadataService,
                state.RecordedSeriesIds,
                (series, local) =>
                {
                    local.BestInSeries.Clear();
                    foreach (var (rawName, priority) in CollectRawTags(series, null, key.Sources, key.MinWeight))
                    {
                        if (string.IsNullOrWhiteSpace(rawName))
                            continue;

                        if (!local.ResolveCache.TryGetValue(rawName, out var resolved))
                        {
                            string clean = TagCleanupRegex().Replace(rawName, "").Trim();
                            if (clean.Length == 0)
                            {
                                local.ResolveCache[rawName] = (string.Empty, string.Empty, true);
                                continue;
                            }

                            string origNorm = NormalizeTagKey(clean);
                            bool isMerged = state.Merges.TryGetValue(origNorm, out var target);
                            string effectiveName = isMerged ? target! : clean;
                            string normKey = isMerged ? NormalizeTagKey(effectiveName) : origNorm;
                            bool skip = normKey.Length == 0 || state.Blacklist.Contains(normKey) || state.Blacklist.Contains(origNorm);

                            resolved = local.ResolveCache[rawName] = (normKey, skip ? string.Empty : TitleCase(effectiveName), skip);
                        }

                        if (!resolved.Skip && !local.BestInSeries.ContainsKey(resolved.NormKey))
                            local.BestInSeries[resolved.NormKey] = (priority, resolved.TitleCased);
                    }

                    foreach (var (normKey, (priority, titleCased)) in local.BestInSeries)
                        local.Counts[(normKey, priority, titleCased)] = local.Counts.GetValueOrDefault((normKey, priority, titleCased), 0) + 1;
                },
                local =>
                {
                    foreach (var ((normKey, priority, titleCased), count) in local.Counts)
                        state.DisplayNames.GetOrAdd(normKey, _ => new CacheHelper.GlobalNameEntry()).ResolveDisplayName(titleCased, priority, record: true, count);
                },
                cancellationToken
            );

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

        bool shouldRecord = series is IShokoSeries shokoSeries && state.RecordedSeriesIds.TryAdd(shokoSeries.LocalID, 0);

        return
        [
            .. ExtractNormalizedTags(CollectRawTags(series, tmdbSeries, key.Sources, key.MinWeight), state)
                .Select(tag => state.DisplayNames.GetOrAdd(tag.NormKey, _ => new CacheHelper.GlobalNameEntry()).ResolveDisplayName(TitleCase(tag.CleanName), tag.Priority, shouldRecord))
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
            result = result.Concat(anidbTags.Where(tag => !string.IsNullOrWhiteSpace(tag.Name) && ((int)minWeight <= 0 || tag.Weight >= (int)minWeight)).Select(tag => (tag.Name, Priority: 1)));

        if (sources.HasFlag(TagSources.TMDB) && (tmdbSeries ?? shokoSeries?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() ?? (series.ID.Source == MetadataSource.TMDB ? series : null)) is { } tmdb)
            result = result.Concat(
                tmdb.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag.Name) && (tag.Kind is TagKind.Genre || (tag.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(tag.Name))))
                    .Select(tag => (tag.Name, Priority: 2))
            );

        if (sources.HasFlag(TagSources.User) && shokoSeries?.Tags is { } shokoTags)
            result = result.Concat(shokoTags.Where(tag => tag != null && tag.ID.Source == MetadataSource.User && !string.IsNullOrWhiteSpace(tag.Name)).Select(tag => (tag.Name, Priority: 3)));

        return result;
    }

    /// <summary>Generates a normalized comparison key ignoring case, spacing, punctuation, diacritics, and common English plurals.</summary>
    /// <param name="text">The raw tag name string.</param>
    /// <returns>A normalized alphanumeric ASCII lowercase string.</returns>
    private static string NormalizeTagKey(string text)
    {
        string key = TextHelper.NormalizeAlphanumericKey(text);
        if (key.Length == 0)
            return string.Empty;

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
        // Strip moderator notes / AniDB comments first
        .Select(tag => (CleanName: TagCleanupRegex().Replace(tag.Name, "").Trim(), tag.Priority))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.CleanName))
            // Resolve merge rules using normalized keys
            .Select(tag =>
            {
                var normKey = NormalizeTagKey(tag.CleanName);
                var isMerged = state.Merges.TryGetValue(normKey, out var target);
                var effectiveName = isMerged ? target! : tag.CleanName;
                var effectiveKey = isMerged ? NormalizeTagKey(effectiveName) : normKey;
                return (CleanName: effectiveName, SourceKey: normKey, NormKey: effectiveKey, tag.Priority);
            })
            // Blacklist filtering (checks both source and merged keys)
            .Where(tag => tag.NormKey.Length > 0 && !state.Blacklist.Contains(tag.NormKey) && !state.Blacklist.Contains(tag.SourceKey))
            // Deduplicate within this series (AniDB tags will beat TMDB because anidbNames precedes tmdbNames)
            .DistinctBy(tag => tag.NormKey)
            .Select(tag => (tag.CleanName, tag.NormKey, tag.Priority));

    /// <summary>Builds a diagnostic CSV report of all tags across Shoko series after applying default tag overrides.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    /// <returns>A CSV-formatted string listing source, tag, and series count, with merged variants appended after semicolons.</returns>
    public static string BuildTagReport(IMetadataService metadataService) =>
        new ReportHelper.CsvReportBuilder("Tag", "tag", NormalizeTagKey, s_defaultTagMerges, s_defaultTagBlacklist, TitleCase, rawTag => TagCleanupRegex().Replace(rawTag, "")).Build(
            metadataService.GetAllShokoSeries() ?? [],
            (series, record) =>
            {
                if (series.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> anidbTags)
                    foreach (var tag in anidbTags)
                        record("AniDB", 0, tag.Name);

                if (series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { } tmdb)
                    foreach (var tag in tmdb.Tags)
                        if (tag.Kind is TagKind.Genre || (tag.Kind is TagKind.Keyword && !NonEnglishTagRegex().IsMatch(tag.Name)))
                            record("TMDB", 1, tag.Name);

                foreach (var tag in series.Tags ?? [])
                    if (tag != null && tag.ID.Source == MetadataSource.User)
                        record("User", 2, tag.Name);
            }
        );

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
            for (int partIndex = 1; partIndex < parts.Length; partIndex++)
                if (!string.IsNullOrEmpty(parts[partIndex]) && s_forceSpecial.TryGetValue("-" + parts[partIndex], out var hyphenSpecial))
                    parts[partIndex] = hyphenSpecial.TrimStart('-');

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
            .Replace(text.ToLowerInvariant(), match => s_forceLower.Contains(match.Value) ? match.Value.ToLowerInvariant()
                : s_forceUpper.Contains(match.Value) ? match.Value.ToUpperInvariant()
                // Capitalise all words accounting for apostrophes first
                : char.ToUpperInvariant(match.Value[0]) + match.Value[1..]);

        // Force capitalise the first character no matter what
        result = char.ToUpperInvariant(result[0]) + result[1..];

        // Force capitalise the first character of the last word no matter what
        int lastSpaceIndex = result.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < result.Length - 1)
            result = result[..(lastSpaceIndex + 1)] + char.ToUpperInvariant(result[lastSpaceIndex + 1]) + result[(lastSpaceIndex + 2)..];

        // Apply special case preferences for whole words and hyphenated suffixes as the absolute final step
        result = WordRegex().Replace(result, match => ApplySpecialCasing(match.Value));
        return result;
    }

    #endregion
}
