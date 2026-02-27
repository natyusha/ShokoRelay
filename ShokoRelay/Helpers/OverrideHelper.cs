using System.Globalization;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers; // for CsvHelper

namespace ShokoRelay.Helpers
{
    /// <summary>
    /// Parses an optional "anidb_vfs_overrides.csv" file placed in the plugin directory.
    /// The file allows a user to group multiple Shoko series IDs together so that
    /// one primary ID is treated as the canonical series and the rest are merged
    /// for VFS and metadata operations.
    ///
    /// Format:
    ///   # comment lines are ignored
    ///   1234,5678,9012   <-- 1234 is primary, the others are secondaries
    ///   2222,3333        <-- 2222 primary for a second group
    ///
    /// Only active when TMDB episode numbering is enabled; when disabled the file is
    /// ignored and all ids behave independently.
    /// </summary>
    // original helper for series merge overrides; kept in separate file for backwards compatibility
    // renamed back to OverrideHelper as requested by user
    public static class OverrideHelper
    {
        // map AniDB id -> list of AniDB ids in group (first is primary AniDB id)
        private static readonly Dictionary<int, List<int>> _groups = new();
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static string? _loadedPath;

        public static void EnsureLoaded(string? pluginDir = null)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                var asmPath = typeof(OverrideHelper).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(asmPath))
                {
                    pluginDir = Path.GetDirectoryName(asmPath);
                }
            }
            if (string.IsNullOrWhiteSpace(pluginDir))
                return;

            string path = Path.Combine(pluginDir, "anidb_vfs_overrides.csv");
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
                    {
                        if (!_groups.ContainsKey(id))
                            _groups[id] = parts;
                    }
                }
                var info = new FileInfo(path);
                _lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch { }
        }

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

        public static IReadOnlyList<int> GetGroup(int shokoSeriesId, IMetadataService metadataService)
        {
            if (!ShokoRelay.Settings.TmdbEpNumbering || metadataService == null)
                return new List<int> { shokoSeriesId };
            var s = metadataService.GetShokoSeriesByID(shokoSeriesId);
            if (s == null || s.AnidbAnimeID <= 0)
                return new List<int> { shokoSeriesId };
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
            return new List<int> { shokoSeriesId };
        }
    }
}
