using System.Globalization;
using Shoko.Abstractions.Services;

namespace ShokoRelay.Helpers;

/// <summary>Parses an optional "anidb_vfs_overrides.csv" file to group multiple Shoko series IDs together.</summary>
public static class OverrideHelper
{
    #region Fields & Constants

    private static readonly Dictionary<int, List<int>> _groups = [];
    private static DateTime _lastWriteUtc = DateTime.MinValue;
    private static string? _loadedPath;
    private static string OverridesPath => Path.Combine(ShokoRelay.ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);

    #endregion

    #region Loading Logic

    /// <summary>Load override groups from the CSV file if it has changed since the last load.</summary>
    public static void EnsureLoaded()
    {
        var configDir = ShokoRelay.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir))
            return;

        string path = OverridesPath;
        if (_loadedPath == path)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc <= _lastWriteUtc)
                    return;
            }
            else
            {
                _groups.Clear();
                _loadedPath = path;
                _lastWriteUtc = DateTime.MinValue;
                return;
            }
        }

        _groups.Clear();
        _loadedPath = path;
        if (!File.Exists(path))
            return;

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                var csvFields = TextHelper.SplitCsvLine(line);
                var parts = csvFields
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                if (parts.Count < 2)
                    continue;
                int primary = parts[0];
                foreach (var id in parts)
                    _groups.TryAdd(id, parts);
            }
            var info = new FileInfo(path);
            _lastWriteUtc = info.LastWriteTimeUtc;
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
        if (!ShokoRelay.Settings.TmdbEpNumbering || metadataService == null)
            return shokoSeriesId;
        var s = metadataService.GetShokoSeriesByID(shokoSeriesId);
        if (s == null || s.AnidbAnimeID <= 0)
            return shokoSeriesId;
        int anidb = s.AnidbAnimeID;
        if (_groups.TryGetValue(anidb, out var grp) && grp.Count > 0)
        {
            var primaryAni = grp[0];
            var primarySeries = metadataService.GetShokoSeriesByAnidbID(primaryAni);
            if (primarySeries != null)
                return primarySeries.ID;
        }
        return shokoSeriesId;
    }

    /// <summary>Retrieve the full group of series IDs associated with the specified series ID.</summary>
    /// <param name="shokoSeriesId">The input series ID.</param>
    /// <param name="metadataService">Service used to resolve series mappings.</param>
    /// <returns>A list of Shoko series IDs in the group (primary first).</returns>
    public static IReadOnlyList<int> GetGroup(int shokoSeriesId, IMetadataService metadataService)
    {
        if (!ShokoRelay.Settings.TmdbEpNumbering || metadataService == null)
            return [shokoSeriesId];
        var s = metadataService.GetShokoSeriesByID(shokoSeriesId);
        if (s == null || s.AnidbAnimeID <= 0)
            return [shokoSeriesId];
        int anidb = s.AnidbAnimeID;
        if (_groups.TryGetValue(anidb, out var grp) && grp.Count > 0)
        {
            var list = new List<int>();
            foreach (var ani in grp)
            {
                var ss = metadataService.GetShokoSeriesByAnidbID(ani);
                if (ss != null)
                    list.Add(ss.ID);
            }
            if (list.Count > 0)
                return list;
        }
        return [shokoSeriesId];
    }

    #endregion
}
