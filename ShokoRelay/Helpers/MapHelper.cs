using System.Collections.Concurrent;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using ShokoRelay.Plex;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Helpers;

/// <summary>Provides utility methods for mapping Shoko series and video files to Plex-compatible structures.</summary>
public static class MapHelper
{
    #region Data Models

    /// <summary>Represents a mapping between a video file and one or more Shoko episodes.</summary>
    /// <param name="Video">The video object.</param>
    /// <param name="Episodes">All episodes in the file.</param>
    /// <param name="PrimaryEpisode">The episode used for coordinate resolution.</param>
    /// <param name="Coords">Plex coordinates.</param>
    /// <param name="FileName">Original filename.</param>
    /// <param name="PartIndex">Optional index for split files.</param>
    /// <param name="PartCount">Total parts for split files.</param>
    /// <param name="TmdbEpisode">Optional TMDB metadata override.</param>
    /// <param name="IsVariation">Indicates if this file is marked as a variation in Shoko.</param>
    public record FileMapping(IVideo Video, IReadOnlyList<IEpisode> Episodes, IEpisode PrimaryEpisode, PlexCoords Coords, string FileName, int? PartIndex, int PartCount, object? TmdbEpisode, bool IsVariation);

    /// <summary>Aggregates file mapping information and available seasons for a series.</summary>
    /// <param name="Mappings">List of individual file mappings.</param>
    /// <param name="Seasons">List of unique season numbers.</param>
    public record SeriesFileData(List<FileMapping> Mappings, List<int> Seasons)
    {
        /// <summary>Retrieves mappings specific to a given season.</summary>
        /// <param name="season">The season number.</param>
        /// <returns>A filtered list of mappings.</returns>
        public List<FileMapping> GetForSeason(int season) => [.. Mappings.Where(m => m.Coords.Season == season).OrderBy(m => m.Coords.Episode)];
    }

    #endregion

    #region Public API

    /// <summary>Generate SeriesFileData for the given series by building file mappings and seasons.</summary>
    /// <param name="series">The series to process.</param>
    /// <returns>A SeriesFileData object.</returns>
    public static SeriesFileData GetSeriesFileData(ISeries series)
    {
        var mappings = BuildFileMappings(series);
        return new SeriesFileData(mappings, [.. mappings.Select(m => m.Coords.Season).Distinct().OrderBy(s => s)]);
    }

    /// <summary>Returns the TMDB ordering ID that should be used for episode numbering.</summary>
    /// <param name="series">The series to inspect.</param>
    /// <returns>Ordering ID or null.</returns>
    public static string? GetPreferredTmdbOrderingId(ISeries series)
    {
        if (!ShokoRelay.Settings.TmdbEpNumbering)
            return null;
        var tmdbShow = series.Episodes.OfType<IShokoEpisode>().FirstOrDefault()?.Series?.TmdbShows?.FirstOrDefault();
        string? pref = tmdbShow?.PreferredOrdering?.OrderingID;
        return (string.IsNullOrWhiteSpace(pref) || string.Equals(pref, tmdbShow?.ID.ToString(), StringComparison.OrdinalIgnoreCase)) ? null : pref;
    }

    /// <summary>Return merged file data for a primary series and any additional series in the group.</summary>
    /// <param name="primary">The primary series.</param>
    /// <param name="extras">Secondary series to merge.</param>
    /// <returns>A combined SeriesFileData object.</returns>
    public static SeriesFileData GetSeriesFileDataMerged(ISeries primary, IEnumerable<ISeries> extras)
    {
        var all = BuildFileMappings(primary);
        foreach (var s in extras ?? [])
            if (s != null)
                all.AddRange(BuildFileMappings(s));
        return new SeriesFileData(all, [.. all.Select(m => m.Coords.Season).Distinct().OrderBy(s => s)]);
    }

    /// <summary>Indicates whether an episode should be treated as hidden.</summary>
    /// <param name="e">The episode to check.</param>
    /// <returns>True if hidden.</returns>
    public static bool IsHidden(IEpisode e) => e is IShokoEpisode shokoEp && shokoEp.IsHidden;

    #endregion

    #region Mapping Logic

    private static List<FileMapping> BuildFileMappings(ISeries series)
    {
        var result = new List<FileMapping>();
        var seriesEpisodes = series.Episodes.Select(e => (Episode: e, Videos: e.VideoList.ToList())).Where(x => x.Videos.Count > 0 && !IsHidden(x.Episode)).ToList();
        if (seriesEpisodes.Count == 0)
            return result;

        string? prefId = GetPreferredTmdbOrderingId(series);
        var coordsByEpisode = new ConcurrentDictionary<int, PlexCoords>();
        var videoToEps = new ConcurrentDictionary<int, ConcurrentBag<(IEpisode Episode, PlexCoords Coords)>>();
        var seasonsSet = new ConcurrentDictionary<int, byte>();

        Parallel.ForEach(
            seriesEpisodes,
            p =>
            {
                var coords = GetPlexCoordinates(p.Episode, prefId);
                coordsByEpisode[p.Episode.ID] = coords;
                seasonsSet.TryAdd(coords.Season, 0);
                foreach (var v in p.Videos)
                    videoToEps.GetOrAdd(v.ID, _ => []).Add((p.Episode, coords));
            }
        );

        bool s1 = seasonsSet.ContainsKey(PlexConstants.SeasonStandard),
            s0 = seasonsSet.ContainsKey(PlexConstants.SeasonSpecials);
        var episodeFileLists = seriesEpisodes.ToDictionary(x => x.Episode.ID, x => x.Videos.OrderBy(v => Path.GetFileName(v.Files.FirstOrDefault()?.Path ?? "")).ToList());
        var allVideos = seriesEpisodes.SelectMany(x => x.Videos).GroupBy(v => v.ID).Select(g => g.First()).OrderBy(v => Path.GetFileName(v.Files.FirstOrDefault()?.Path ?? "")).ToList();

        // Versioning Logic: Group counts and counters by Season, Episode, and IsVariation status
        var coordCounts = allVideos
            .Where(v => videoToEps.ContainsKey(v.ID))
            .Select(v =>
            {
                var eps = videoToEps[v.ID];
                var sortedEps = eps.OrderBy(x => x.Coords.Season).ThenBy(x => x.Coords.Episode);
                var deduped = DeduplicateByCoords([.. sortedEps], v);
                if (deduped.Count == 0)
                    return null;
                var c = (deduped.Count == 1) ? deduped[0].Coords : GetPlexCoordinatesForFile(deduped.Select(x => x.Episode));
                return new { Coords = c, v.IsVariation };
            })
            .Where(x => x != null)
            .GroupBy(x => (x!.Coords.Season, x.Coords.Episode, x.IsVariation))
            .ToDictionary(g => g.Key, g => g.Count());

        var versionCounters = coordCounts.Where(g => g.Value > 1).ToDictionary(g => g.Key, _ => 1);

        foreach (var video in allVideos)
        {
            if (!videoToEps.TryGetValue(video.ID, out var epList) || epList.Count == 0)
                continue;

            // Handle multi-type tiebreaks using cross-reference ordering
            var sortedEps = epList.OrderBy(x => x.Coords.Season).ThenBy(x => x.Coords.Episode).ToList();
            if (sortedEps.Count > 1 && sortedEps.Select(x => x.Episode.Type).Distinct().Count() > 1 && video.CrossReferences?.Count > 0)
            {
                var firstXrefId = video.CrossReferences.FirstOrDefault(cr => cr.ShokoEpisode != null && sortedEps.Any(e => e.Episode.ID == cr.ShokoEpisode.ID))?.ShokoEpisode?.ID;
                if (firstXrefId.HasValue)
                {
                    var primaryType = sortedEps.First(x => x.Episode.ID == firstXrefId.Value).Episode.Type;
                    sortedEps = [.. sortedEps.Where(x => x.Episode.Type == primaryType)];
                }
            }

            var deduped = DeduplicateByCoords(sortedEps, video);
            if (deduped.Count == 0)
                continue;
            var firstEp = deduped[0].Episode;
            var fileList = episodeFileLists.GetValueOrDefault(firstEp.ID);
            int fIdx = fileList?.FindIndex(x => x.ID == video.ID) ?? 0,
                fCount = fileList?.Count ?? 1;
            string fileName = Path.GetFileName(video.Files?.FirstOrDefault()?.Path ?? "");
            bool allowPt = fCount > 1 && TextHelper.HasPlexSplitTag(fileName) && deduped.Select(d => d.Episode.Type).Distinct().Count() <= 1;
            int? partIdx = allowPt ? fIdx + 1 : null;
            PlexCoords coords = (deduped.Count == 1) ? deduped[0].Coords : GetPlexCoordinatesForFile(deduped.Select(x => x.Episode), allowPt ? fIdx : null);
            if (coords.Season == PlexConstants.SeasonOther)
                coords = ApplyFeaturettesFallback(coords, s1, s0);

            // Versioning: Only calculate vIdx if there are multiple files within this specific variation status group
            var vKey = (coords.Season, coords.Episode, video.IsVariation);
            int? vIdx = (!partIdx.HasValue && versionCounters.TryGetValue(vKey, out var v)) ? (versionCounters[vKey] = v + 1) - 1 : null;

            // TMDB Episode metadata override for multi-part files
            object? tmdbEp = null;
            if (fCount > 1 && ShokoRelay.Settings.TmdbEpNumbering && firstEp is IShokoEpisode se && se.TmdbEpisodes?.Any() == true)
            {
                var sel = SelectPreferredTmdbOrdering(se.TmdbEpisodes, prefId).ElementAtOrDefault(fIdx);
                if (sel != null)
                {
                    var (Season, Episode) = GetOrderingCoords(sel, prefId);
                    tmdbEp = new
                    {
                        SeasonNumber = Season,
                        EpisodeNumber = Episode,
                        PreferredTitle = sel.PreferredTitle?.Value,
                    };
                }
            }
            result.Add(new FileMapping(video, [.. deduped.Select(x => x.Episode)], firstEp, coords, fileName, partIdx, allowPt ? fCount : 1, tmdbEp, video.IsVariation));
        }
        return result;
    }

    private static List<(IEpisode Episode, PlexCoords Coords)> DeduplicateByCoords(List<(IEpisode Episode, PlexCoords Coords)> eps, IVideo video)
    {
        var deduped = new List<(IEpisode Episode, PlexCoords Coords)>();
        var index = new Dictionary<(int, int), int>();
        var xrefPos = video.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select((cr, i) => (id: cr.ShokoEpisode!.ID, i)).ToDictionary(t => t.id, t => t.i);
        foreach (var entry in eps)
        {
            var key = (entry.Coords.Season, entry.Coords.Episode);
            if (!index.TryGetValue(key, out var existingIdx))
            {
                index[key] = deduped.Count;
                deduped.Add(entry);
                continue;
            }
            if (xrefPos == null)
                continue;
            int exPos = xrefPos.GetValueOrDefault(deduped[existingIdx].Episode.ID, -1),
                enPos = xrefPos.GetValueOrDefault(entry.Episode.ID, -1);
            if (enPos >= 0 && (exPos == -1 || enPos < exPos))
                deduped[existingIdx] = entry;
        }
        // Reorder list so the first cross-referenced episode is the Primary
        if (xrefPos != null && deduped.Count > 1)
        {
            var best = deduped.Select((d, i) => (d, i, pos: xrefPos.GetValueOrDefault(d.Episode.ID, int.MaxValue))).OrderBy(x => x.pos).First();
            if (best.i > 0)
            {
                deduped.RemoveAt(best.i);
                deduped.Insert(0, best.d);
            }
        }
        return deduped;
    }

    private static PlexCoords ApplyFeaturettesFallback(PlexCoords coords, bool s1, bool s0)
    {
        if (!s1)
        {
            coords.Season = PlexConstants.SeasonStandard;
            return coords;
        }
        if (!s0)
        {
            coords.Season = PlexConstants.SeasonSpecials;
            return coords;
        }
        return coords;
    }

    #endregion
}
