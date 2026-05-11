using System.Globalization;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;

namespace ShokoRelay.Helpers;

/// <summary>Parses an optional <see cref="ShokoRelayConstants.FileVfsOverrides"/> file to group multiple Shoko series IDs together.</summary>
public static class OverrideHelper
{
    #region Fields & Constants

    private static readonly Dictionary<int, List<int>> s_groups = [];
    private static bool s_isInitialized;
    private static readonly Lock s_loadLock = new();
    private static string OverridesPath => Path.Combine(ShokoRelay.ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);

    #endregion

    #region Loading Logic

    /// <summary>Ensures the override groups are loaded into the memory cache. Returns immediately if already initialized.</summary>
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

    private static void LoadInternal()
    {
        s_groups.Clear();
        string path = OverridesPath;
        if (!File.Exists(path))
            return;

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
                if (parts.Count < 2)
                    continue;
                foreach (var id in parts)
                    s_groups.TryAdd(id, parts);
            }
        }
        catch { }
    }

    #endregion

    #region Series Resolution

    /// <summary>Return the primary series id for the given Shoko series ID according to overrides.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>The primary Shoko series ID, or the original ID if no override exists.</returns>
    public static int GetPrimary(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded();
        return (!ShokoRelay.Settings.TmdbEpNumbering || metadataService == null || metadataService.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } s) ? shokoSeriesId
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0 && metadataService.GetShokoSeriesByAnidbID(grp[0]) is { } ps) ? ps.ID
            : shokoSeriesId;
    }

    /// <summary>Retrieve the full group of series IDs associated with the specified series ID.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>A list of Shoko series IDs in the group (primary first).</returns>
    public static IReadOnlyList<int> GetGroup(int shokoSeriesId, IMetadataService metadataService)
    {
        EnsureLoaded();
        return (!ShokoRelay.Settings.TmdbEpNumbering || metadataService == null || metadataService.GetShokoSeriesByID(shokoSeriesId) is not { AnidbAnimeID: > 0 } s) ? [shokoSeriesId]
            : (s_groups.TryGetValue(s.AnidbAnimeID, out var grp) && grp.Count > 0) ? [.. grp.Select(metadataService.GetShokoSeriesByAnidbID).OfType<IShokoSeries>().Select(ss => ss.ID)]
            : [shokoSeriesId];
    }

    #endregion
}
