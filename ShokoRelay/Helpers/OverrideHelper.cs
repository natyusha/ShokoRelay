using System.Collections.Concurrent;
using System.Globalization;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Helpers;

/// <summary>Defines a mapping rule for custom VFS coordinate overrides.</summary>
/// <param name="Type">The source episode type to match.</param>
/// <param name="StartEp">The starting episode number to match.</param>
/// <param name="EndEp">The ending episode number to match (inclusive).</param>
/// <param name="TargetSeason">The target Plex season number.</param>
/// <param name="TargetEpisodeStart">The starting target Plex episode number.</param>
public sealed record CustomCoordRule(EpisodeType Type, int StartEp, int EndEp, int TargetSeason, int TargetEpisodeStart);

/// <summary>Parses an optional <see cref="ShokoRelayConstants.FileVfsOverrides"/> file to group multiple Shoko series IDs together.</summary>
public static class OverrideHelper
{
    #region Setup & Constants

    /// <summary>Cached dictionary of primary to secondary series ID manual mappings.</summary>
    private static IReadOnlyDictionary<int, List<int>> s_groups = new Dictionary<int, List<int>>();

    /// <summary>Cached dictionary of custom episode coordinate overrides for specific secondary series.</summary>
    private static IReadOnlyDictionary<int, List<CustomCoordRule>> s_coordOverrides = new Dictionary<int, List<CustomCoordRule>>();

    /// <summary>JIT Cache for automatically merged TMDB series groups.</summary>
    private static readonly ConcurrentDictionary<string, List<int>> s_tmdbGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks whether the override groups have been initialized.</summary>
    private static bool s_isInitialized;

    /// <summary>Lock object for synchronizing override initialization.</summary>
    private static readonly Lock s_loadLock = new();

    /// <summary>The absolute path to the VFS overrides CSV file.</summary>
    private static string OverridesPath => Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);

    #endregion

    #region Loading Logic

    /// <summary>Ensures the override groups are loaded into the memory cache.</summary>
    public static void EnsureLoaded()
    {
        if (s_isInitialized)
            return;
        lock (s_loadLock)
        {
            if (s_isInitialized)
                return;
            LoadInternal();
            s_isInitialized = true;
        }
    }

    /// <summary>Forces a fresh reload of the overrides from the disk into the memory cache.</summary>
    public static void Reload()
    {
        lock (s_loadLock)
        {
            LoadInternal();
            s_isInitialized = true;
        }
    }

    /// <summary>Loads manual VFS series override groups from disk.</summary>
    private static void LoadInternal()
    {
        var newGroups = new Dictionary<int, List<int>>();
        var newCoordOverrides = new Dictionary<int, List<CustomCoordRule>>();
        string path = OverridesPath;
        void AddGroup(List<int> ids) => ids.ForEach(id => newGroups.TryAdd(id, ids));

        // Load Manual CSV Overrides
        if (File.Exists(path))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;

                    var parts = TextHelper.SplitCsvLine(line).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    if (parts.Count == 0)
                        continue;

                    var (primaryId, primaryRules) = ParsePart(parts[0]);
                    if (primaryId <= 0)
                        continue;

                    if (primaryRules != null)
                        newCoordOverrides[primaryId] = primaryRules;

                    var group = new List<int> { primaryId };

                    for (int i = 1; i < parts.Count; i++)
                    {
                        var (secId, secRules) = ParsePart(parts[i]);
                        if (secId <= 0)
                            continue;

                        if (secRules != null)
                            newCoordOverrides[secId] = secRules;

                        group.Add(secId);
                    }

                    if (group.Count >= 2)
                        AddGroup([.. group.Distinct()]);
                }
            }
            catch { }
        }

        // Atomic swap & cache clear
        s_groups = newGroups;
        s_coordOverrides = newCoordOverrides;
        s_tmdbGroups.Clear();
    }

    /// <summary>Parses an AniDB ID and optional custom coordinate rules from an override token.</summary>
    /// <param name="part">The raw CSV token to parse.</param>
    /// <returns>A tuple containing the AniDB ID and any parsed coordinate rules.</returns>
    private static (int Id, List<CustomCoordRule>? Rules) ParsePart(string part)
    {
        int anidbId = 0;
        List<CustomCoordRule>? ruleList = null;

        int sepIdx = part.IndexOf(';');
        if (sepIdx > 0)
        {
            var idStr = part[..sepIdx].Trim();
            var coordStr = part[(sepIdx + 1)..].Trim();
            if (int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out anidbId) && anidbId > 0)
            {
                ruleList = [];
                foreach (var rStr in coordStr.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var match = TextHelper.CustomCoordRegex().Match(rStr);
                    if (!match.Success)
                        continue;

                    string tStr = match.Groups[1].Value;
                    EpisodeType eType = EpisodeType.Episode;
                    if (!string.IsNullOrEmpty(tStr))
                    {
                        eType = tStr.ToUpperInvariant() switch
                        {
                            "C" => EpisodeType.Credits,
                            "S" => EpisodeType.Special,
                            "T" => EpisodeType.Trailer,
                            "P" => EpisodeType.Parody,
                            "O" => EpisodeType.Other,
                            _ => EpisodeType.Episode,
                        };
                    }

                    int startEp = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                    int endEp = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : (match.Groups[2].Success ? startEp : int.MaxValue);
                    int targetS = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                    int targetE = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);

                    ruleList.Add(new CustomCoordRule(eType, startEp, endEp, targetS, targetE));
                }
            }
        }
        else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            anidbId = parsedId;

        return (anidbId, ruleList?.Count > 0 ? ruleList : null);
    }

    #endregion

    #region Series Resolution

    /// <summary>Return the primary series id for the given Shoko series ID according to overrides and auto-merges.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>The primary Shoko series ID, or the original ID if no override exists.</returns>
    public static int GetPrimary(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded();

        if (metadataService?.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } series || !EnforceTmdbNumbering)
            return shokoSeriesId;

        // 1. Manual override takes precedence (O(1))
        if (s_groups.TryGetValue(series.AnidbAnimeID, out var manualGrp) && manualGrp.Count > 0)
            return metadataService.GetShokoSeriesByAnidbID(manualGrp[0])?.LocalID ?? shokoSeriesId;

        // 2. Auto TMDB Merge fast-path (O(1))
        if (Settings.Advanced.MergeTmdbSeries && series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { } tmdbSeries && !string.IsNullOrWhiteSpace(tmdbSeries.ID.ID))
        {
            if (s_tmdbGroups.TryGetValue(tmdbSeries.ID.ID, out var cachedGroup) && cachedGroup.Count > 0)
                return metadataService.GetShokoSeriesByAnidbID(cachedGroup[0])?.LocalID ?? shokoSeriesId;

            // Cache miss - resolve JIT via GetGroup
            var group = GetGroup(shokoSeriesId, metadataService);
            return group.Count > 0 ? group[0] : shokoSeriesId;
        }

        return shokoSeriesId;
    }

    /// <summary>Retrieve the full group of series IDs associated with the specified series ID.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>A list of Shoko series IDs in the group (primary first).</returns>
    public static IReadOnlyList<int> GetGroup(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded();

        if (metadataService?.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } series || !EnforceTmdbNumbering)
            return [shokoSeriesId];

        // 1. Manual override takes precedence
        if (s_groups.TryGetValue(series.AnidbAnimeID, out var manualGrp) && manualGrp.Count > 0)
            return [.. manualGrp.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.LocalID)];

        // 2. Auto TMDB Merge Just-In-Time Resolution
        if (Settings.Advanced.MergeTmdbSeries && series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { } tmdbSeries && !string.IsNullOrWhiteSpace(tmdbSeries.ID.ID))
        {
            string tmdbIdStr = tmdbSeries.ID.ID;
            if (s_tmdbGroups.TryGetValue(tmdbIdStr, out var cachedGroup))
                return [.. cachedGroup.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.LocalID)];

            var xrefs = metadataService.GetCrossReferencesForProviderEntry(tmdbSeries.ID).OfType<IMetadataSeriesCrossReference>().ToList();
            if (xrefs.Count > 1)
            {
                var groupIds = xrefs
                    .Select(x => x.AnidbAnimeID)
                    .Distinct()
                    .Select(metadataService.GetShokoSeriesByAnidbID)
                    .OfType<IShokoSeries>()
                    .OrderBy(s => s.AirDate ?? PartialDateOnly.MaxValue)
                    .ThenBy(s => s.AnidbAnimeID)
                    .Select(s => s.AnidbAnimeID)
                    .ToList();

                if (groupIds.Count > 1)
                {
                    s_tmdbGroups[tmdbIdStr] = groupIds;
                    return [.. groupIds.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.LocalID)];
                }
            }

            // Single-item result or isolated series: cache to prevent re-querying
            s_tmdbGroups[tmdbIdStr] = [series.AnidbAnimeID];
        }

        return [shokoSeriesId];
    }

    /// <summary>Retrieves manual VFS coordinates for a specific episode, if defined in the overrides file.</summary>
    /// <param name="anidbAnimeId">The AniDB Anime ID the episode belongs to.</param>
    /// <param name="type">The source episode type to match against.</param>
    /// <param name="episodeNumber">The numeric episode number of the specific type.</param>
    /// <returns>The overriden season and episode coordinates, or null if unconfigured.</returns>
    public static (int Season, int Episode)? GetCustomCoords(int anidbAnimeId, EpisodeType type, int episodeNumber)
    {
        EnsureLoaded();
        if (s_coordOverrides.TryGetValue(anidbAnimeId, out var rules))
        {
            foreach (var rule in rules)
            {
                if (rule.Type == type && episodeNumber >= rule.StartEp && episodeNumber <= rule.EndEp)
                {
                    int offset = episodeNumber - rule.StartEp;
                    return (rule.TargetSeason, rule.TargetEpisodeStart + offset);
                }
            }
        }
        return null;
    }

    #endregion
}
