using System.Globalization;
using Shoko.Abstractions.Services;

namespace ShokoRelay.Helpers
{
    /// <summary>
    /// Parses an optional "anidb_vfs_overrides.csv" file placed in the plugin's config directory.
    /// The file allows a user to group multiple Shoko series IDs together so that one primary ID is treated as the canonical series and the rest are merged for VFS and metadata operations.
    /// </summary>
    public static class OverrideHelper
    {
        // map AniDB id -> list of AniDB ids in group (first is primary AniDB id)
        private static readonly Dictionary<int, List<int>> _groups = new();
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static string? _loadedPath;
        private const string OverridesFileName = "anidb_vfs_overrides.csv";
        private static string OverridesPath => Path.Combine(ShokoRelay.ConfigDirectory, OverridesFileName);

        /// <summary>
        /// Load override groups from the optional <c>anidb_vfs_overrides.csv</c> file located in the config directory. Refreshes if the file has changed since the last load.
        /// </summary>
        public static void EnsureLoaded()
        {
            var configDir = ShokoRelay.ConfigDirectory;
            // require config directory; nothing to do otherwise
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

        /// <summary>
        /// Return the primary series id for the given <paramref name="shokoSeriesId"/> according to the loaded override groups.
        /// If numbering overrides are disabled or no mapping exists, the original id is returned.
        /// </summary>
        /// <param name="shokoSeriesId">Input series ID.</param>
        /// <param name="metadataService">Service used to resolve series/anidb mappings.</param>
        /// <returns>The primary Shoko series ID for the override group, or <paramref name="shokoSeriesId"/> if no override exists.</returns>
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

        /// <summary>
        /// Retrieve the full group of series IDs associated with the specified <paramref name="shokoSeriesId"/>, including the primary and any secondaries.
        /// Returns a singleton list if no override applies.
        /// </summary>
        /// <param name="shokoSeriesId">Series id to look up.</param>
        /// <param name="metadataService">Metadata service.</param>
        /// <returns>A list of Shoko series IDs in the group (primary first), or a singleton list if no override applies.</returns>
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
