using System.Globalization;
using Shoko.Abstractions.Metadata;

namespace ShokoRelay.Helpers;

/// <summary>Parses an optional <see cref="ShokoRelayConstants.FileVfsOverrides"/> file to group multiple Shoko series IDs together.</summary>
public static class OverrideHelper
{
    #region Setup & Constants

    /// <summary>Cached dictionary of primary to secondary series ID mappings.</summary>
    private static IReadOnlyDictionary<int, List<int>> s_groups = new Dictionary<int, List<int>>();

    /// <summary>Cached dictionary of custom episode coordinate overrides for specific secondary series.</summary>
    private static IReadOnlyDictionary<int, (int Season, int Episode)> s_coordOverrides = new Dictionary<int, (int Season, int Episode)>();

    /// <summary>Tracks whether the override groups have been initialized.</summary>
    private static bool s_isInitialized;

    /// <summary>Lock object for synchronizing override initialization.</summary>
    private static readonly Lock s_loadLock = new();

    /// <summary>The absolute path to the VFS overrides CSV file.</summary>
    private static string OverridesPath => Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);

    #endregion

    #region Loading Logic

    /// <summary>Ensures the override groups are loaded into the memory cache. Optionally performs TMDB series link discovery if enabled.</summary>
    /// <param name="metadataService">Service used to scan series for TMDB links.</param>
    public static void EnsureLoaded(IMetadataService? metadataService = null)
    {
        if (s_isInitialized)
            return;
        lock (s_loadLock)
        {
            if (s_isInitialized)
                return;
            LoadInternal(metadataService);
            s_isInitialized = true;
        }
    }

    /// <summary>Forces a fresh reload of the overrides from the disk into the memory cache.</summary>
    /// <param name="metadataService">Service used to scan series for TMDB links.</param>
    public static void Reload(IMetadataService? metadataService = null)
    {
        lock (s_loadLock)
        {
            LoadInternal(metadataService);
            s_isInitialized = true;
        }
    }

    /// <summary>Loads manual and automatic VFS series override groups from disk and metadata relationships.</summary>
    /// <param name="metadataService">Optional metadata service used to discover and resolve automatic TMDB series merges.</param>
    private static void LoadInternal(IMetadataService? metadataService)
    {
        // Use local dictionaries to build the groups, then perform an atomic swap to prevent race conditions where other threads see a cleared or partially-loaded static dictionary.
        var newGroups = new Dictionary<int, List<int>>();
        var newCoordOverrides = new Dictionary<int, (int Season, int Episode)>();
        string path = OverridesPath;
        void AddGroup(List<int> ids) => ids.ForEach(id => newGroups.TryAdd(id, ids)); // Helper to add a list of IDs to the group registry

        // Load Manual CSV Overrides (Priority)
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
                    if (parts.Count < 2)
                        continue;

                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var primaryId) || primaryId <= 0)
                        continue;

                    var group = new List<int> { primaryId };

                    for (int i = 1; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        int anidbId = 0;

                        int sepIdx = part.IndexOf(';');
                        if (sepIdx > 0)
                        {
                            var idStr = part[..sepIdx].Trim();
                            var coordStr = part[(sepIdx + 1)..].Trim();
                            if (int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out anidbId) && anidbId > 0)
                            {
                                var match = TextHelper.CustomCoordRegex().Match(coordStr);
                                if (match.Success)
                                {
                                    int s = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                                    int e = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                                    newCoordOverrides[anidbId] = (s, e);
                                }
                            }
                        }
                        else
                        {
                            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                                anidbId = parsedId;
                        }

                        if (anidbId > 0)
                            group.Add(anidbId);
                    }

                    if (group.Count >= 2)
                        AddGroup([.. group.Distinct()]);
                }
            }
            catch { }
        }

        // Automatic TMDB Merge Discovery (Only for Series links)
        if (Settings.Advanced.MergeTmdbSeries && metadataService != null)
        {
            var tmdbGroups =
                metadataService
                    .GetAllShokoSeries()
                    ?.Select(s => (s.AnidbAnimeID, s.AirDate, TmdbId: s.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()?.ID.ID))
                    .Where(x => !string.IsNullOrWhiteSpace(x.TmdbId))
                    .GroupBy(x => x.TmdbId)
                    .Where(g => g.Count() > 1)
                ?? [];

            foreach (var group in tmdbGroups)
            {
                if (group.Any(x => newGroups.ContainsKey(x.AnidbAnimeID)))
                    continue;
                var ids = group.OrderBy(x => x.AirDate ?? PartialDateOnly.MaxValue).ThenBy(x => x.AnidbAnimeID).Select(x => x.AnidbAnimeID).ToList();
                AddGroup(ids);
            }
        }

        // Atomic swap
        s_groups = newGroups;
        s_coordOverrides = newCoordOverrides;
    }

    #endregion

    #region Series Resolution

    /// <summary>Return the primary series id for the given Shoko series ID according to overrides.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>The primary Shoko series ID, or the original ID if no override exists.</returns>
    public static int GetPrimary(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded(metadataService);
        return (!EnforceTmdbNumbering || metadataService == null || metadataService.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } s) ? shokoSeriesId
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0 && metadataService.GetShokoSeriesByAnidbID(grp[0]) is { } ps) ? ps.LocalID
            : shokoSeriesId;
    }

    /// <summary>Retrieve the full group of series IDs associated with the specified series ID.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>A list of Shoko series IDs in the group (primary first).</returns>
    public static IReadOnlyList<int> GetGroup(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded(metadataService);
        return (!EnforceTmdbNumbering || metadataService == null || metadataService.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } s) ? [shokoSeriesId]
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0) ? [.. grp.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.LocalID)]
            : [shokoSeriesId];
    }

    /// <summary>Retrieves manual VFS coordinates for a specific episode, if defined in the overrides file.</summary>
    /// <param name="anidbAnimeId">The AniDB Anime ID the episode belongs to.</param>
    /// <param name="episodeNumber">The numeric episode number of the specific type.</param>
    /// <returns>The overriden season and episode coordinates, or null if unconfigured.</returns>
    public static (int Season, int Episode)? GetCustomCoords(int anidbAnimeId, int episodeNumber)
    {
        EnsureLoaded();
        return s_coordOverrides.Count > 0 && s_coordOverrides.TryGetValue(anidbAnimeId, out var coords) ? (coords.Season, coords.Episode + Math.Max(0, episodeNumber - 1)) : null;
    }

    #endregion
}
