using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers;

#region Data Models

/// <summary>Represents a metadata tag item for Plex responses.</summary>
public sealed class TagItem
{
    /// <summary>The display string for the tag.</summary>
    [JsonProperty("tag")]
    public string Tag { get; init; } = "";
}

#endregion

/// <summary>Helper utilities for generating cast, crew and studio tag information suitable for Plex metadata responses.</summary>
public static class CastHelper
{
    #region Static Configuration

    // csharpier-ignore
    /// <summary>TMDB crew departments used to strip category prefixes from role names.</summary>
    private static readonly FrozenSet<string> s_tmdbDepartments = new[] {
        "Production", "Costume & Make-Up", "Lighting", "Actors", "Crew", "Directing", "Writing", "Editing", "Sound", "Visual Effects", "Camera", "Art"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default blacklisted credit and studio keys loaded from the embedded CSV resource.</summary>
    private static readonly FrozenSet<string> s_creditBlacklist;

    /// <summary>Explicit studio and credit name merges mapping variant names to canonical names.</summary>
    private static readonly FrozenDictionary<string, string> s_creditMerges;

    /// <summary>Global cache mapping normalized AniDB credit keys (separated by Person vs. Company) to their preferred display names.</summary>
    private static readonly ConcurrentDictionary<(bool IsPerson, string NormKey), CacheHelper.GlobalNameEntry> s_anidbCreditNames = new();

    /// <summary>Global cache mapping normalized TMDB-only credit keys (separated by Person vs. Company) to their preferred display names.</summary>
    private static readonly ConcurrentDictionary<(bool IsPerson, string NormKey), CacheHelper.GlobalNameEntry> s_tmdbCreditNames = new();

    /// <summary>Tracks Shoko series IDs already recorded into the global credit cache to prevent repeated-request count inflation.</summary>
    private static readonly ConcurrentDictionary<int, byte> s_recordedSeriesIds = new();

    /// <summary>Lock object for synchronizing initial credit cache warm-up.</summary>
    private static readonly Lock s_warmLock = new();

    /// <summary>Tracks whether the global credit cache has been warmed across all series.</summary>
    private static volatile bool s_isWarmed;

    /// <summary>Global cache mapping normalized studio keys to resolved TMDB logo URLs to avoid AniDB image requests.</summary>
    private static readonly ConcurrentDictionary<string, string> s_globalStudioThumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Statically initializes default credit blacklist and merges from the embedded resource and pre-seeds canonical names.</summary>
    static CastHelper()
    {
        var (blacklist, merges, _) = LoadDefaultCreditOverrides();
        s_creditBlacklist = blacklist.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        s_creditMerges = merges.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Credit Overrides

    /// <summary>Loads default credit blacklist and merge mappings directly from the embedded assembly resource.</summary>
    /// <returns>A tuple containing default blacklisted keys, merge dictionary, and mentioned key set.</returns>
    private static (HashSet<string> Blacklist, Dictionary<string, string> Merges, HashSet<string> MergeMentioned) LoadDefaultCreditOverrides()
    {
        try
        {
            var asm = typeof(CastHelper).Assembly;
            if (
                asm.GetManifestResourceNames().FirstOrDefault(resName => resName.EndsWith(ShokoRelayConstants.FileDefaultCreditOverrides, StringComparison.OrdinalIgnoreCase)) is { } resName
                && asm.GetManifestResourceStream(resName) is { } stream
            )
                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return TextHelper.ParseOverrides(reader, NormalizeCreditKey);
        }
        catch { }

        return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Thread-local accumulation state used during parallel credit cache warming.</summary>
    private sealed class WarmThreadState
    {
        /// <summary>Memoized normalization results per raw credit name.</summary>
        public readonly Dictionary<string, (string Trimmed, string NormKey, bool Skip)> ResolveCache = new(StringComparer.Ordinal);

        /// <summary>Observed series counts per credit variant tuple.</summary>
        public readonly Dictionary<(bool IsPerson, bool IsAnidb, string NormKey, string Trimmed), int> Counts = [];

        /// <summary>Deduplication set ensuring each normalized credit is counted once per series.</summary>
        public readonly HashSet<(bool IsPerson, bool IsAnidb, string NormKey)> SeenInSeries = [];
    }

    /// <summary>Pre-warms the global credit, cast, crew, and studio display name cache across all series in Shoko.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static void WarmCache(IMetadataService metadataService, CancellationToken cancellationToken = default) => EnsureWarmed(metadataService, cancellationToken);

    /// <summary>Ensures the global credit cache has been populated across all Shoko series.</summary>
    /// <param name="metadataService">Optional metadata service to enumerate all series.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    private static void EnsureWarmed(IMetadataService? metadataService, CancellationToken cancellationToken = default)
    {
        if (s_isWarmed || metadataService == null)
            return;

        lock (s_warmLock)
        {
            if (s_isWarmed)
                return;

            CacheHelper.WarmSeriesCache<WarmThreadState>(
                metadataService,
                s_recordedSeriesIds,
                (series, local) =>
                {
                    local.SeenInSeries.Clear();
                    EnumerateSeriesCredits(series, null, (rawName, isPerson, isAnidb) =>
                    {
                        if (string.IsNullOrWhiteSpace(rawName))
                            return;

                        if (!local.ResolveCache.TryGetValue(rawName, out var resolved))
                        {
                            string trimmed = rawName.Trim();
                            string norm = NormalizeCreditKey(trimmed);
                            bool skip = norm.Length == 0 || s_creditMerges.ContainsKey(norm) || IsCreditBlacklisted(trimmed);
                            resolved = local.ResolveCache[rawName] = (trimmed, norm, skip);
                        }

                        if (resolved.Skip || !local.SeenInSeries.Add((isPerson, isAnidb, resolved.NormKey)))
                            return;

                        var key = (isPerson, isAnidb, resolved.NormKey, resolved.Trimmed);
                        local.Counts[key] = local.Counts.GetValueOrDefault(key, 0) + 1;
                    });
                },
                local =>
                {
                    foreach (var ((isPerson, isAnidb, normKey, trimmed), count) in local.Counts)
                    {
                        var targetCache = isAnidb ? s_anidbCreditNames : s_tmdbCreditNames;
                        targetCache.GetOrAdd((isPerson, normKey), _ => new CacheHelper.GlobalNameEntry()).ResolveDisplayName(trimmed, isAnidb ? 1 : 2, record: true, count);
                    }
                },
                cancellationToken
            );

            s_isWarmed = true;
        }
    }

    /// <summary>Records all cast, crew, and studio display name variants from a single series into the global cache once per series.</summary>
    /// <param name="series">The series metadata to inspect.</param>
    /// <param name="tmdbSeries">Optional pre-resolved TMDB series metadata.</param>
    private static void RecordSeriesCredits(ISeries series, ISeries? tmdbSeries)
    {
        var seenInSeries = new HashSet<(bool IsPerson, string NormKey, int Priority)>();

        EnumerateSeriesCredits(series, tmdbSeries, (rawName, isPerson, isAnidb) =>
        {
            if (string.IsNullOrWhiteSpace(rawName) || IsCreditBlacklisted(rawName))
                return;

            string trimmed = rawName.Trim();
            string norm = NormalizeCreditKey(trimmed);
            if (norm.Length == 0 || s_creditMerges.ContainsKey(norm))
                return;

            int priority = isAnidb ? 1 : 2;
            if (seenInSeries.Add((isPerson, norm, priority)))
            {
                var targetCache = isAnidb ? s_anidbCreditNames : s_tmdbCreditNames;
                targetCache.GetOrAdd((isPerson, norm), _ => new CacheHelper.GlobalNameEntry()).ResolveDisplayName(trimmed, priority, record: true);
            }
        });
    }

    /// <summary>Enumerates all valid cast, crew, and studio credits for a series, yielding raw name, person flag, and AniDB ownership.</summary>
    /// <param name="series">The series metadata to inspect.</param>
    /// <param name="tmdbSeries">Optional pre-resolved TMDB series metadata.</param>
    /// <param name="onCredit">Callback invoked with (rawName, isPerson, isAnidb) for each credit entry.</param>
    private static void EnumerateSeriesCredits(ISeries series, ISeries? tmdbSeries, Action<string?, bool, bool> onCredit)
    {
        foreach (var cast in series.Cast ?? [])
            if (cast.CreatorID != null && cast.Creator is { } creator)
                onCredit(creator.Title, creator.Type == CreatorType.Person, cast.Source != MetadataSource.TMDB);

        foreach (var crew in series.Crew ?? [])
            if (crew.Creator is { } creator)
                onCredit(creator.Title, creator.Type == CreatorType.Person, crew.Source != MetadataSource.TMDB);

        foreach (var studio in series.Studios ?? [])
            onCredit(studio.Name, false, studio.ID.Source != MetadataSource.TMDB);

        var resolvedTmdb = tmdbSeries ?? (series as IShokoSeries)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault();
        foreach (var studio in resolvedTmdb?.Studios ?? [])
            onCredit(studio.Name, false, false);
    }

    /// <summary>Builds a diagnostic CSV report of all cast, crew, and studio credits across Shoko series after applying default credit overrides.</summary>
    /// <param name="metadataService">The Shoko metadata service used to enumerate all series.</param>
    /// <returns>A CSV-formatted string listing source, credit, and series count, with merged variants appended after semicolons.</returns>
    public static string BuildCreditReport(IMetadataService metadataService) =>
        new ReportHelper.CsvReportBuilder("Credit", "credit", NormalizeCreditKey, s_creditMerges, s_creditBlacklist, allowSamePrimaryMerge: false).Build(
            metadataService.GetAllShokoSeries() ?? [],
            (series, record) =>
            {
                var tmdbSeries = series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault();
                var networkNames = tmdbSeries?.Networks?.Select(network => network.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

                void Record(MetadataSource source, string? name, bool isPerson)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return;

                    bool isTmdb = source == MetadataSource.TMDB;
                    record(isTmdb ? "TMDB" : "AniDB", isTmdb ? 1 : 0, name, isPerson ? "Person" : "Company");
                }

                foreach (var cast in series.Cast ?? [])
                    if (cast.Creator != null)
                        Record(cast.Source, cast.Creator.Title, cast.Creator.Type == CreatorType.Person);

                foreach (var crew in series.Crew ?? [])
                    if (crew.Creator != null)
                        Record(crew.Source, crew.Creator.Title, crew.Creator.Type == CreatorType.Person);

                foreach (var studio in series.Studios ?? [])
                    if (!string.IsNullOrWhiteSpace(studio.Name) && !networkNames.Contains(studio.Name))
                        Record(studio.ID.Source, studio.Name, false);

                foreach (var studio in tmdbSeries?.Studios ?? [])
                    if (!string.IsNullOrWhiteSpace(studio.Name) && !networkNames.Contains(studio.Name))
                        Record(MetadataSource.TMDB, studio.Name, false);
            }
        );

    #endregion

    #region Cast and Crew Logic

    /// <summary>Return an array of objects representing cast (and optionally crew, studios, and production companies) for the specified media item.</summary>
    /// <param name="item">Item implementing <see cref="IWithCastAndCrew"/>.</param>
    /// <param name="metadataService">Optional metadata service to resolve animation studios and cross-references.</param>
    /// <param name="tmdbSeries">Optional TMDB series metadata to resolve companies and broadcasting networks.</param>
    /// <returns>An array of anonymous cast, crew, studio, and company objects suitable for Plex metadata responses.</returns>
    public static object[] GetCastAndCrew(IWithCastAndCrew item, IMetadataService? metadataService = null, ISeries? tmdbSeries = null)
    {
        EnsureWarmed(metadataService);

        if (item is ISeries seriesItem)
        {
            if (seriesItem is IShokoSeries shokoSeries && s_recordedSeriesIds.TryAdd(shokoSeries.LocalID, 0))
                RecordSeriesCredits(seriesItem, tmdbSeries);

            // Pre-populate global studio thumbs from this series' TMDB studios so both crew and company listings can reference them
            foreach (var studio in (seriesItem.Studios ?? []).Concat(tmdbSeries?.Studios ?? []))
                if (studio.ID.Source == MetadataSource.TMDB && studio is IWithImages withImages && !string.IsNullOrWhiteSpace(studio.Name))
                {
                    string studioKey = NormalizeCreditKey(ResolvePreferredCreditName(studio.Name, false, 2));
                    if (!s_globalStudioThumbs.ContainsKey(studioKey) && withImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, forceRemote: true) is { } logoUrl)
                        s_globalStudioThumbs[studioKey] = logoUrl;
                }
        }

        int order = 1;
        var finalRoles = new List<object>();

        void ProcessRoleGroup<T>(IEnumerable<T> roles, Func<T, ICreator?> getCreator, Func<T, MetadataSource> getSource, Func<T, string?> getRole)
            where T : class
        {
            var allRoles = roles.Where(role => getCreator(role) != null).ToList();
            if (allRoles.Count == 0)
                return;

            var tmdbRoles = allRoles.Where(role => getSource(role) == MetadataSource.TMDB).ToList();
            var anidbRoles = allRoles.Where(role => getSource(role) != MetadataSource.TMDB).ToList();
            var index = tmdbRoles.Count > 0 ? new TmdbRoleIndex<T>(tmdbRoles, getCreator, role => getCreator(role)?.Title) : null;
            var matchedTmdb = index != null ? new HashSet<T>() : null;
            var seenRolePairs = new HashSet<(string Key, string Role)>();

            foreach (var role in anidbRoles)
            {
                var creator = getCreator(role)!;
                bool isPerson = creator.Type == CreatorType.Person;
                string? rawTag = creator.Title;
                string? roleName = getRole(role);

                if (string.IsNullOrWhiteSpace(rawTag) || IsCreditBlacklisted(rawTag))
                    continue;

                string tag = ResolvePreferredCreditName(rawTag, isPerson, 1);
                if (IsCreditBlacklisted(tag) || !seenRolePairs.Add((NormalizeCreditKey(tag), (roleName ?? string.Empty).Trim().ToUpperInvariant())))
                    continue;

                var thumbImg = creator.PrimaryImage;
                if (index?.FindMatch(creator, creator.Title) is { } match)
                {
                    matchedTmdb!.Add(match);
                    if (Settings.TmdbCastImages && getCreator(match)?.PrimaryImage != null)
                        thumbImg = getCreator(match)!.PrimaryImage;
                }

                string? thumb = null;
                if (Settings.TmdbCastImages && !isPerson && s_globalStudioThumbs.TryGetValue(NormalizeCreditKey(tag), out var cachedLogo))
                    thumb = cachedLogo;
                else if (thumbImg != null)
                    thumb = ImageHelper.GetImageUrl(thumbImg, forceRemote: true);

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role = roleName,
                        thumb,
                    }
                );
            }

            if (matchedTmdb == null)
                return;

            foreach (var role in tmdbRoles)
            {
                if (matchedTmdb.Contains(role))
                    continue;

                var creator = getCreator(role)!;
                bool isPerson = creator.Type == CreatorType.Person;
                string? rawTag = creator.Title;
                string? roleName = getRole(role);

                if (string.IsNullOrWhiteSpace(rawTag) || IsCreditBlacklisted(rawTag))
                    continue;

                string tag = ResolvePreferredCreditName(rawTag, isPerson, 2);
                if (IsCreditBlacklisted(tag) || !seenRolePairs.Add((NormalizeCreditKey(tag), (roleName ?? string.Empty).Trim().ToUpperInvariant())))
                    continue;

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role = roleName,
                        thumb = creator.PrimaryImage is { } img ? ImageHelper.GetImageUrl(img, forceRemote: true) : null,
                    }
                );
            }
        }

        if (item.Cast != null)
            ProcessRoleGroup(item.Cast, cast => cast.Creator, cast => cast.Source, cast => cast.Character?.Title ?? cast.Name);

        if (Settings.CrewListings && item.Crew != null)
            foreach (var group in item.Crew.GroupBy(crew => crew.RoleType))
                ProcessRoleGroup(group, crew => crew.Creator, crew => crew.Source, crew => crew.RoleType == CrewRoleType.Music ? "Composer" : CleanCrewRole(crew.Name));

        if (Settings.CompanyListings && item is ISeries series)
        {
            var anidbMergedStudios = (metadataService != null && series is IShokoSeries { AnidbAnimeID: > 0 } shokoSeries) ? metadataService.GetMergedStudios(shokoSeries.AnidbAnimeID) : [];

            var animationStudioNames = anidbMergedStudios.Select(studio => studio.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var networkNames = tmdbSeries?.Networks?.Select(network => network.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            // Process TMDB studios first so their high-resolution logos are cached and preferred over AniDB images
            var allStudios = (series.Studios ?? [])
                .Concat(tmdbSeries?.Studios ?? [])
                .Concat(anidbMergedStudios)
                .Where(studio => !string.IsNullOrWhiteSpace(studio.Name) && !networkNames.Contains(studio.Name) && !IsCreditBlacklisted(studio.Name))
                .OrderByDescending(studio => studio.ID.Source == MetadataSource.TMDB);

            var studioGroups = new Dictionary<string, (string Name, bool IsAnimationStudio, string? Thumb)>(StringComparer.OrdinalIgnoreCase);

            foreach (var studio in allStudios)
            {
                string rawName = studio.Name.Trim();
                bool isAniDb = studio.ID.Source == MetadataSource.AniDB;
                int priority = isAniDb ? 1 : 2;
                string preferredName = ResolvePreferredCreditName(rawName, false, priority);
                bool isAnimation = animationStudioNames.Contains(rawName) || animationStudioNames.Contains(preferredName);
                string key = NormalizeCreditKey(preferredName);

                if (!studioGroups.TryGetValue(key, out var existing))
                {
                    string? thumb = studio is IWithImages withImages ? withImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, forceRemote: true) : null;
                    studioGroups[key] = (preferredName, isAnimation, thumb);
                }
                else
                {
                    string? thumb = existing.Thumb ?? (studio is IWithImages withImages ? withImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, forceRemote: true) : null);
                    studioGroups[key] = (preferredName, existing.IsAnimationStudio || isAnimation, thumb);
                }
            }

            var ordered = studioGroups.Values.OrderByDescending(studioItem => studioItem.IsAnimationStudio).ThenBy(studioItem => studioItem.Name);

            foreach (var (name, isAnimation, thumb) in ordered)
                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag = name,
                        role = isAnimation ? "Studio" : "Production",
                        thumb,
                    }
                );
        }

        return [.. finalRoles];
    }

    /// <summary>Return director credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <param name="metadataService">Optional metadata service used to pre-warm cross-series deduplication.</param>
    /// <returns>An array of anonymous objects containing director names.</returns>
    public static object[] GetDirectors(IWithCastAndCrew item, IMetadataService? metadataService = null) => FilterCrew(item, crew => crew.RoleType == CrewRoleType.Director, metadataService);

    /// <summary>Retrieve writing credits (series composers or source work) for the item.</summary>
    /// <param name="item">Source item.</param>
    /// <param name="metadataService">Optional metadata service used to pre-warm cross-series deduplication.</param>
    /// <returns>An array of anonymous objects containing writer names.</returns>
    public static object[] GetWriters(IWithCastAndCrew item, IMetadataService? metadataService = null) =>
        FilterCrew(item, crew => crew.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork, metadataService);

    /// <summary>Retrieve producer credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <param name="metadataService">Optional metadata service used to pre-warm cross-series deduplication.</param>
    /// <returns>An array of anonymous objects containing producer names.</returns>
    public static object[] GetProducers(IWithCastAndCrew item, IMetadataService? metadataService = null) => FilterCrew(item, crew => crew.RoleType == CrewRoleType.Producer, metadataService);

    #endregion

    #region Studio Logic

    /// <summary>Determines if a credit or studio name is blacklisted.</summary>
    /// <param name="name">The credit or studio name to evaluate.</param>
    /// <returns>True if the credit should be filtered out.</returns>
    private static bool IsCreditBlacklisted(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (s_creditBlacklist.Contains(NormalizeCreditKey(name)) || (s_creditMerges.TryGetValue(NormalizeCreditKey(name), out var target) && s_creditBlacklist.Contains(NormalizeCreditKey(target))));

    /// <summary>Resolves the preferred global credit or studio display name using explicit merges, AniDB priority, and TMDB frequency.</summary>
    /// <param name="rawName">The raw studio, company, or creator name.</param>
    /// <param name="isPerson">True for Person creators; false for Studios/Companies.</param>
    /// <param name="priority">The source priority (1 = AniDB, 2 = TMDB).</param>
    /// <returns>The globally preferred display name.</returns>
    private static string ResolvePreferredCreditName(string rawName, bool isPerson, int priority = 2)
    {
        string trimmed = rawName.Trim();
        string norm = NormalizeCreditKey(trimmed);
        return norm.Length == 0 ? trimmed
            : s_creditMerges.TryGetValue(norm, out var mapped) ? mapped
            : priority == 1 ? trimmed
            : s_anidbCreditNames.TryGetValue((isPerson, norm), out var anidbEntry) ? anidbEntry.ResolveDisplayName(trimmed, 1, record: false)
            : s_tmdbCreditNames.GetOrAdd((isPerson, norm), _ => new CacheHelper.GlobalNameEntry()).ResolveDisplayName(trimmed, 2, record: false);
    }

    /// <summary>Normalizes a studio, company, or creator name for comparison by stripping trailing (JP) tags, diacritics, spaces, and punctuation.</summary>
    /// <param name="name">The name to normalize.</param>
    /// <returns>A normalized key string.</returns>
    private static string NormalizeCreditKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        if (trimmed.EndsWith(" (JP)", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^5].TrimEnd();

        var normalized = trimmed.Normalize(NormalizationForm.FormD);
        var stringBuilderNorm = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                stringBuilderNorm.Append(char.ToLowerInvariant(character));

        return stringBuilderNorm.ToString();
    }

    /// <summary>Assemble an array of <see cref="TagItem"/> objects representing the series' studios, utilizing Shoko's native cross-source merging.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">The metadata service used to invoke cross-source merging.</param>
    /// <returns>An array of <see cref="TagItem"/> instances, one per distinct studio.</returns>
    public static TagItem[] GetStudioTags(ISeries series, IMetadataService metadataService)
    {
        EnsureWarmed(metadataService);

        if (series is not IShokoSeries { AnidbAnimeID: > 0 } shokoSeries || metadataService.GetMergedStudios(shokoSeries.AnidbAnimeID) is not { Count: > 0 } studios)
            return [];

        if (s_recordedSeriesIds.TryAdd(shokoSeries.LocalID, 0))
            RecordSeriesCredits(shokoSeries, null);

        return
        [
            .. studios
                .Where(studio => !string.IsNullOrWhiteSpace(studio.Name) && !IsCreditBlacklisted(studio.Name))
                .Select(studio => ResolvePreferredCreditName(studio.Name, false, studio.ID.Source == MetadataSource.AniDB ? 1 : 2))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .DistinctBy(NormalizeCreditKey)
                .Select(name => new TagItem { Tag = name }),
        ];
    }

    #endregion

    #region Internal Helpers

    /// <summary>Flips a two-part name (Given Family to Family Given) with zero allocations using memory spans.</summary>
    /// <param name="name">The raw name string.</param>
    /// <returns>The flipped name or null if unparseable.</returns>
    private static string? GetFlippedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        int spaceIdx = name.IndexOf(' ');
        return (spaceIdx <= 0 || spaceIdx != name.LastIndexOf(' ')) ? null : string.Concat(name.AsSpan(spaceIdx + 1), " ", name.AsSpan(0, spaceIdx));
    }

    /// <summary>Strips department category prefixes from TMDB crew role names.</summary>
    /// <param name="role">The raw crew role string.</param>
    /// <returns>The cleaned role string.</returns>
    private static string CleanCrewRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? ""
        : role.IndexOf(',') is var commaIdx && commaIdx > 0 && s_tmdbDepartments.Contains(role[..commaIdx].Trim()) ? role[(commaIdx + 1)..].Trim()
        : role.Trim();

    /// <summary>Filters the crew by a specific predicate and returns a formatted array of tags.</summary>
    /// <param name="item">The media item containing cast and crew.</param>
    /// <param name="predicate">Filter predicate to evaluate each crew member against.</param>
    /// <param name="metadataService">Optional metadata service used to pre-warm cross-series deduplication.</param>
    /// <returns>An array of anonymous objects containing crew tags.</returns>
    private static object[] FilterCrew(IWithCastAndCrew item, Func<ICrew, bool> predicate, IMetadataService? metadataService = null)
    {
        EnsureWarmed(metadataService);

        return
        [
            .. DeduplicateCrew(item.Crew?.Where(predicate))
                .Select(crew =>
                {
                    if (crew.Creator == null)
                        return null;
                    string? rawName = crew.Creator.Title;
                    return string.IsNullOrWhiteSpace(rawName) || IsCreditBlacklisted(rawName)
                        ? null
                        : ResolvePreferredCreditName(rawName, crew.Creator.Type == CreatorType.Person, crew.Source == MetadataSource.TMDB ? 2 : 1);
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .DistinctBy(name => NormalizeCreditKey(name!))
                .Select(name => (object)new { tag = name }),
        ];
    }

    /// <summary>Deduplicates a collection of crew members per role type, preferring AniDB entries over matching TMDB entries.</summary>
    /// <param name="crewItems">The raw collection of crew items.</param>
    /// <returns>A deduplicated collection of crew items.</returns>
    private static IEnumerable<ICrew> DeduplicateCrew(IEnumerable<ICrew>? crewItems)
    {
        if (crewItems == null)
            yield break;

        foreach (var group in crewItems.GroupBy(crew => crew.RoleType))
        {
            var tmdb = group.Where(crew => crew.Source == MetadataSource.TMDB && crew.Creator != null).ToList();
            var anidb = group.Where(crew => crew.Source != MetadataSource.TMDB && crew.Creator != null);

            if (tmdb.Count == 0)
            {
                foreach (var crewMember in anidb)
                    yield return crewMember;
                continue;
            }

            var index = new TmdbRoleIndex<ICrew>(tmdb, role => role.Creator, role => role.Creator?.Title);
            var matchedTmdbEntries = new HashSet<ICrew>();

            foreach (var crewMember in anidb)
            {
                yield return crewMember;
                if (crewMember.Creator != null && index.FindMatch(crewMember.Creator, crewMember.Creator.Title) is { } match)
                    matchedTmdbEntries.Add(match);
            }

            foreach (var crewMember in tmdb)
                if (!matchedTmdbEntries.Contains(crewMember))
                    yield return crewMember;
        }
    }

    /// <summary>High-performance lookup index for matching AniDB cast and crew against TMDB records.</summary>
    private sealed class TmdbRoleIndex<T>
        where T : class
    {
        /// <summary>Lookup index matching exact TMDB creator original titles without whitespace.</summary>
        private readonly Dictionary<string, T> _exactOriginal = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator alternative titles without whitespace.</summary>
        private readonly Dictionary<string, T> _altOriginal = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator primary and flipped names.</summary>
        private readonly Dictionary<string, T> _names = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator alternative names and their flipped variants.</summary>
        private readonly Dictionary<string, T> _altNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new lookup index by indexing all provided TMDB cast or crew entries.</summary>
        /// <param name="tmdbItems">Collection of TMDB role entries to index.</param>
        /// <param name="getCreator">Accessor function to retrieve the creator from a role entry.</param>
        /// <param name="getName">Accessor function to retrieve the fallback name from a role entry.</param>
        public TmdbRoleIndex(IEnumerable<T> tmdbItems, Func<T, ICreator?> getCreator, Func<T, string?> getName)
        {
            foreach (var item in tmdbItems)
            {
                var creator = getCreator(item);
                string? name = creator?.Title ?? getName(item);

                // Index Original Title without spaces
                string? orig = creator?.OriginalTitle?.Value;
                if (!string.IsNullOrEmpty(orig))
                    _exactOriginal.TryAdd(orig.Contains(' ') ? orig.Replace(" ", "", StringComparison.Ordinal) : orig, item);

                // Index Primary and Flipped Names
                if (!string.IsNullOrEmpty(name))
                {
                    _names.TryAdd(name, item);
                    if (GetFlippedName(name) is { } flipped)
                        _names.TryAdd(flipped, item);
                }

                // Index Alternative Titles / Aliases
                if (creator?.Titles is { Count: > 0 } titles)
                {
                    foreach (var title in titles)
                    {
                        if (string.IsNullOrWhiteSpace(title.Value))
                            continue;

                        string alt = title.Value.Trim();
                        string altNoSpace = alt.Replace(" ", "", StringComparison.Ordinal);
                        _altOriginal.TryAdd(altNoSpace, item);

                        _altNames.TryAdd(alt, item);
                        if (GetFlippedName(alt) is { } altFlipped)
                            _altNames.TryAdd(altFlipped, item);
                    }
                }
            }
        }

        /// <summary>Finds the best matching TMDB role entry for a given AniDB creator and name.</summary>
        /// <param name="anidbCreator">The AniDB creator entity, if available.</param>
        /// <param name="anidbName">The fallback AniDB creator or character name.</param>
        /// <returns>The matching TMDB entry, or null if no match is found.</returns>
        public T? FindMatch(ICreator? anidbCreator, string anidbName)
        {
            string? orig = anidbCreator?.OriginalTitle?.Value;
            string? origNoSpace = (!string.IsNullOrEmpty(orig) && orig.Contains(' ')) ? orig.Replace(" ", "", StringComparison.Ordinal) : orig;
            string name = anidbCreator?.Title ?? anidbName;
            string? flippedName = GetFlippedName(name);

            // OriginalName matches OriginalName (whitespace stripped)
            if (!string.IsNullOrEmpty(origNoSpace) && _exactOriginal.TryGetValue(origNoSpace, out var exactOriginalMatch))
                return exactOriginalMatch;

            // OriginalName matches TMDB AlternativeNames (whitespace stripped)
            if (!string.IsNullOrEmpty(origNoSpace) && _altOriginal.TryGetValue(origNoSpace, out var altOriginalMatch))
                return altOriginalMatch;

            // Name matches Name (romanised)
            if (!string.IsNullOrEmpty(name) && _names.TryGetValue(name, out var nameMatch))
                return nameMatch;

            // Flipped Name matches Name (romanised)
            if (flippedName != null && _names.TryGetValue(flippedName, out var flippedNameMatch))
                return flippedNameMatch;

            // AniDB Name matches TMDB AlternativeNames
            if (!string.IsNullOrEmpty(name) && _altNames.TryGetValue(name, out var altNameMatch))
                return altNameMatch;

            // Flipped AniDB Name matches TMDB AlternativeNames (and their flipped variants)
            return flippedName != null && _altNames.TryGetValue(flippedName, out var altFlippedMatch) ? altFlippedMatch : null;
        }
    }

    #endregion
}
