using System.Globalization;

namespace ShokoRelay.Helpers;

/// <summary>Parses an optional <see cref="ShokoRelayConstants.FileVfsOverrides"/> file to group multiple Shoko series IDs together.</summary>
public static class OverrideHelper
{
    #region Fields & Constants

    private static readonly Dictionary<int, List<int>> s_groups = [];
    private static bool s_isInitialized;
    private static readonly Lock s_loadLock = new();
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

    private static void LoadInternal(IMetadataService? metadataService)
    {
        // Use a local dictionary to build the groups, then perform an atomic swap to prevent race conditions where other threads see a cleared or partially-loaded static dictionary.
        var newGroups = new Dictionary<int, List<int>>();
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
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    var parts = TextHelper
                        .SplitCsvLine(line)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToList();
                    if (parts.Count >= 2)
                        AddGroup(parts);
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
                    ?.Select(s => new
                    {
                        s.AnidbAnimeID,
                        s.AirDate,
                        TmdbId = s.TmdbShows?.FirstOrDefault()?.ID,
                    })
                    .Where(x => x.TmdbId.HasValue)
                    .GroupBy(x => x.TmdbId!.Value)
                    .Where(g => g.Count() > 1)
                ?? [];

            foreach (var group in tmdbGroups)
            {
                if (group.Any(x => newGroups.ContainsKey(x.AnidbAnimeID)))
                    continue;
                var ids = group.OrderBy(x => x.AirDate ?? DateTime.MaxValue).ThenBy(x => x.AnidbAnimeID).Select(x => x.AnidbAnimeID).ToList();
                AddGroup(ids);
            }
        }

        // Atomic swap
        lock (s_loadLock)
        {
            s_groups.Clear();
            foreach (var kvp in newGroups)
                s_groups.Add(kvp.Key, kvp.Value);
        }
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
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0 && metadataService.GetShokoSeriesByAnidbID(grp[0]) is { } ps) ? ps.ID
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
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0) ? [.. grp.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.ID)]
            : [shokoSeriesId];
    }

    #endregion
}
