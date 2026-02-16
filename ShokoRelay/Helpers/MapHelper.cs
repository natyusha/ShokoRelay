using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using ShokoRelay.Plex;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Helpers
{
    public static class MapHelper
    {
        public record FileMapping(IVideo Video, IReadOnlyList<IEpisode> Episodes, IEpisode PrimaryEpisode, PlexCoords Coords, string FileName, int? PartIndex, int PartCount, object? TmdbEpisode);

        public record SeriesFileData(List<FileMapping> Mappings, List<int> Seasons)
        {
            public List<FileMapping> GetForSeason(int season) => Mappings.Where(m => m.Coords.Season == season).OrderBy(m => m.Coords.Episode).ToList();
        }

        public static SeriesFileData GetSeriesFileData(ISeries series)
        {
            var mappings = BuildFileMappings(series);
            var seasons = mappings.Select(m => m.Coords.Season).Distinct().OrderBy(s => s).ToList();
            return new SeriesFileData(mappings, seasons);
        }

        private static List<FileMapping> BuildFileMappings(ISeries series)
        {
            var result = new List<FileMapping>();

            // Cache VideoList and filter once
            var seriesEpisodes = series.Episodes.Select(e => (Episode: e, Videos: e.VideoList.ToList())).Where(x => x.Videos.Count > 0 && !IsHidden(x.Episode)).ToList();

            if (seriesEpisodes.Count == 0)
                return result;

            // Build episode file lists from cached videos
            var episodeFileLists = seriesEpisodes.ToDictionary(x => x.Episode.ID, x => x.Videos.OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList());

            // Build episode lookup by video ID
            var videoToEpisodes = new Dictionary<int, List<(IEpisode Episode, PlexCoords Coords)>>();
            foreach (var (episode, videos) in seriesEpisodes)
            {
                var coords = GetPlexCoordinates(episode); // Compute once per episode
                foreach (var video in videos)
                {
                    if (!videoToEpisodes.TryGetValue(video.ID, out var list))
                    {
                        list = [];
                        videoToEpisodes[video.ID] = list;
                    }
                    list.Add((episode, coords));
                }
            }

            // Get unique videos sorted by filename
            var allVideos = seriesEpisodes.SelectMany(x => x.Videos).GroupBy(v => v.ID).Select(g => g.First()).OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList();

            foreach (var video in allVideos)
            {
                if (!videoToEpisodes.TryGetValue(video.ID, out var epList) || epList.Count == 0)
                    continue;

                // Sort by pre-computed coords
                var sortedEps = epList.OrderBy(x => x.Coords.Season).ThenBy(x => x.Coords.Episode).ToList();

                // Use shared helper for primary-type selection + dedupe (same logic used by the debug path).
                var (filteredEps, deduped) = FilterAndDedupeByPrimaryType(sortedEps, video);

                if (deduped.Count == 0)
                    continue;

                var (firstEp, _) = deduped[0];
                var epId = firstEp.ID;
                var episodes = deduped.Select(x => x.Episode).ToList();

                int fileIndex = episodeFileLists.TryGetValue(epId, out var files) ? files.FindIndex(x => x.ID == video.ID) : 0;
                int fileCount = files?.Count ?? 1;

                string fileName = Path.GetFileName(video.Locations.FirstOrDefault()?.Path ?? "");

                bool allowPartSuffix = ComputeAllowPartSuffix(deduped, fileCount, fileName);

                int? fileIndexParam = allowPartSuffix && fileCount > 1 ? fileIndex : null;

                var coords = GetPlexCoordinatesForFile(episodes, fileIndexParam);
                // Apply Featurettes fallback rule: if coords point to SeasonOther, try Season 1 -> Season 0 -> keep Featurettes
                coords = ApplyFeaturettesSeasonFallback(series, coords);
                int? partIndex = allowPartSuffix && fileCount > 1 ? fileIndex + 1 : null;
                int partCount = allowPartSuffix ? fileCount : 1;

                object? tmdbEpisode = null;
                if (fileCount > 1 && ShokoRelay.Settings.TMDBEpNumbering && firstEp is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    var tmdbEps = shokoEp.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList();

                    if (fileIndex < tmdbEps.Count)
                        tmdbEpisode = tmdbEps[fileIndex];
                }

                result.Add(new FileMapping(video, episodes, firstEp, coords, fileName, partIndex, partCount, tmdbEpisode));
            }

            return result;
        }

        public static bool IsHidden(IEpisode e)
        {
            return e is IShokoEpisode shokoEp && shokoEp.IsHidden;
        }

        // Returns a map of ShokoEpisodeId -> summed cross-reference percentage for the given video.
        // If video is null or has no cross-references, returns an empty dictionary.
        private static Dictionary<int, int> GetCrossRefMap(IVideo? video)
        {
            return video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).GroupBy(cr => cr.ShokoEpisode!.ID).ToDictionary(g => g.Key, g => g.Sum(cr => cr.Percentage))
                ?? new Dictionary<int, int>();
        }

        // Select primary episode type (using cross-ref percentages when available) and deduplicate coordinates.
        // Returns (filteredEps, dedupedEps). Accepts an optional precomputed crossRefMap to avoid recomputing.
        private static (List<(IEpisode Episode, PlexCoords Coords)> Filtered, List<(IEpisode Episode, PlexCoords Coords)> Deduped) FilterAndDedupeByPrimaryType(
            List<(IEpisode Episode, PlexCoords Coords)> sortedEps,
            IVideo? video,
            Dictionary<int, int>? crossRefMap = null
        )
        {
            var crMap = crossRefMap ?? GetCrossRefMap(video);

            // episodeId -> minimum cross-ref Order (if present on the video). Lower = higher priority.
            var orderMap =
                video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).GroupBy(cr => cr.ShokoEpisode!.ID).ToDictionary(g => g.Key, g => g.Min(cr => cr.Order)) ?? new Dictionary<int, int>();

            List<(IEpisode Episode, PlexCoords Coords)> filteredEps;

            var distinctTypes = sortedEps.Select(x => x.Episode.Type).Distinct().ToList();
            if (distinctTypes.Count > 1)
            {
                filteredEps = ChoosePrimaryTypeAndFilter(sortedEps, orderMap, crMap);
            }
            else
            {
                filteredEps = new List<(IEpisode Episode, PlexCoords Coords)>(sortedEps);
            }

            var deduped = DeduplicateByCoords(filteredEps);
            return (filteredEps, deduped);
        }

        // Determine whether Plex-style part suffix is allowed for this file
        private static bool ComputeAllowPartSuffix(List<(IEpisode Episode, PlexCoords Coords)> deduped, int fileCount, string fileName)
        {
            if (fileCount > 1 && TextHelper.HasPlexSplitTag(fileName))
            {
                var types = deduped.Select(d => d.Episode.Type).Distinct().ToList();
                return types.Count <= 1;
            }
            return false;
        }

        // Extracted helpers to simplify primary-type selection and deduplication logic.
        private static List<(IEpisode Episode, PlexCoords Coords)> DeduplicateByCoords(List<(IEpisode Episode, PlexCoords Coords)> filteredEps)
        {
            var deduped = new List<(IEpisode Episode, PlexCoords Coords)>();
            var seenCoords = new HashSet<(int Season, int Episode)>();
            foreach (var entry in filteredEps)
            {
                var key = (entry.Coords.Season, entry.Coords.Episode);
                if (!seenCoords.Add(key))
                    continue;
                deduped.Add(entry);
            }
            return deduped;
        }

        private static List<(IEpisode Episode, PlexCoords Coords)> ChoosePrimaryTypeAndFilter(
            List<(IEpisode Episode, PlexCoords Coords)> sortedEps,
            Dictionary<int, int> orderMap,
            Dictionary<int, int> crMap
        )
        {
            // Build type -> list of Order values (if present)
            var typeOrders = sortedEps
                .GroupBy(x => x.Episode.Type)
                .ToDictionary(g => g.Key, g => g.Select(se => orderMap.TryGetValue(se.Episode.ID, out var o) ? (int?)o : null).Where(v => v.HasValue).Select(v => v!.Value).ToList());

            var anyOrderPresent = typeOrders.Any(kv => kv.Value.Count > 0);
            if (anyOrderPresent)
            {
                // Order-first logic (use Order; tie-break by percentage sums)
                var typeMinOrder = typeOrders.ToDictionary(kv => kv.Key, kv => kv.Value.Count > 0 ? kv.Value.Min() : int.MaxValue);
                var lowestOrder = typeMinOrder.Values.Min();
                var orderWinners = typeMinOrder.Where(kv => kv.Value == lowestOrder).Select(kv => kv.Key).ToList();

                if (orderWinners.Count == 1)
                {
                    var primaryType = orderWinners[0];
                    return sortedEps.Where(x => x.Episode.Type == primaryType).ToList();
                }

                // tie between types with same best Order -> break tie using percentage sums
                var typeScores = sortedEps.GroupBy(x => x.Episode.Type).ToDictionary(g => g.Key, g => g.Sum(entry => crMap.TryGetValue(entry.Episode.ID, out var p) ? p : 0));
                var bestScore = typeScores.Where(kv => orderWinners.Contains(kv.Key)).Max(kv => kv.Value);
                var winners = typeScores.Where(kv => orderWinners.Contains(kv.Key) && kv.Value == bestScore).Select(kv => kv.Key).ToList();
                if (winners.Count == 1)
                    return sortedEps.Where(x => x.Episode.Type == winners[0]).ToList();

                // fallback: prefer earliest coordinate's type
                var primaryFallback = sortedEps[0].Episode.Type;
                return sortedEps.Where(x => x.Episode.Type == primaryFallback).ToList();
            }

            // No Order present -> use summed percentage per type (existing behavior)
            var scores = sortedEps.GroupBy(x => x.Episode.Type).ToDictionary(g => g.Key, g => g.Sum(entry => crMap.TryGetValue(entry.Episode.ID, out var p) ? p : 0));
            var maxScore = scores.Values.DefaultIfEmpty(0).Max();
            var winnersByScore = scores.Where(kv => kv.Value == maxScore).Select(kv => kv.Key).ToList();

            if (maxScore > 0 && winnersByScore.Count == 1)
                return sortedEps.Where(x => x.Episode.Type == winnersByScore[0]).ToList();

            // fallback: earliest coordinate's type
            var fallbackType = sortedEps[0].Episode.Type;
            return sortedEps.Where(x => x.Episode.Type == fallbackType).ToList();
        }

        // If a file would be placed into Featurettes (SeasonOther), prefer to place it into Season 1 if Season 1 is empty;
        // otherwise, if Season 1 has files, prefer Season 0 (Specials) if empty; otherwise leave in Featurettes.
        private static PlexCoords ApplyFeaturettesSeasonFallback(ISeries series, PlexCoords coords)
        {
            if (coords.Season != PlexConstants.SeasonOther)
                return coords;

            // Determine whether season 1 and season 0 currently have any episodes with files (non-hidden)
            bool season1HasFiles = series.Episodes.Any(e => !IsHidden(e) && (e.VideoList?.Any() ?? false) && GetPlexCoordinates(e).Season == PlexConstants.SeasonStandard);
            if (!season1HasFiles)
            {
                coords.Season = PlexConstants.SeasonStandard; // move Other -> Season 1 (normal episodes)
                return coords;
            }

            bool season0HasFiles = series.Episodes.Any(e => !IsHidden(e) && (e.VideoList?.Any() ?? false) && GetPlexCoordinates(e).Season == PlexConstants.SeasonSpecials);
            if (!season0HasFiles)
            {
                coords.Season = PlexConstants.SeasonSpecials; // move Other -> Specials
                return coords;
            }

            // Both Season 1 and Specials have files — keep in Featurettes (SeasonOther)
            return coords;
        }

        // Detailed debug information about how a single file is mapped by BuildFileMappings.
        public record EpisodeDebugInfo(
            int Id,
            string Title,
            EpisodeType Type,
            int? SeasonNumber,
            int EpisodeNumber,
            bool IsHidden,
            int? CrossRefPercentage,
            int? CrossRefOrder,
            PlexCoords EpisodeCoords,
            object? TmdbEpisodes,
            List<string> FileNames
        );

        public record TmdbEpisodeSummary(int? SeasonNumber, int EpisodeNumber, string? PreferredTitle);

        public record FileMappingSummary(
            int VideoId,
            string FileName,
            int? PartIndex,
            int PartCount,
            PlexCoords Coords,
            int? PrimaryEpisodeId,
            string? PrimaryEpisodeTitle,
            List<int> EpisodeIds,
            TmdbEpisodeSummary? TmdbEpisode
        );

        public record FileMappingDebugInfo(
            int VideoId,
            string FileName,
            string? FullPath,
            bool HasPlexSplitTag,
            int FileIndex,
            int FileCount,
            bool AllowPartSuffix,
            int? FileIndexParam,
            PlexCoords FinalCoords,
            int? PartIndex,
            int PartCount,
            TmdbEpisodeSummary? TmdbEpisodeSelected,
            EpisodeDebugInfo? PrimaryEpisode,
            List<EpisodeDebugInfo> VideoEpisodes,
            List<EpisodeDebugInfo> SortedEps,
            List<EpisodeDebugInfo> FilteredEps,
            List<(int Season, int Episode)> DedupedCoords,
            Dictionary<int, List<string>> EpisodeFileLists,
            FileMappingSummary? FinalMapSummary,
            bool TmdbEpNumberingSetting
        );

        public static FileMappingDebugInfo? GetFileMappingDebug(ISeries series, int videoId)
        {
            if (series == null)
                return null;

            // Snapshot of episodes (preserve original ordering for file lists)
            var allSeriesEpisodes = series.Episodes.Select(e => (Episode: e, Videos: e.VideoList.ToList())).ToList();

            // Which episodes would BuildFileMappings include (non-hidden and with >=1 video)
            var includedEpisodes = allSeriesEpisodes.Where(x => x.Videos.Count > 0 && !IsHidden(x.Episode)).ToList();

            // Per-episode ordered file lists (same ordering used by BuildFileMappings)
            var episodeFileLists = includedEpisodes.ToDictionary(x => x.Episode.ID, x => x.Videos.OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList());

            // Helper to get episode debug data
            static EpisodeDebugInfo ToEpDebug(IEpisode ep, int? crossRefPercentage = null, int? crossRefOrder = null)
            {
                var coords = GetPlexCoordinates(ep);
                object? tmdb = null;
                if (ep is IShokoEpisode sh)
                    tmdb = sh
                        .TmdbEpisodes?.Select(te => new
                        {
                            te.SeasonNumber,
                            te.EpisodeNumber,
                            te.PreferredTitle,
                        })
                        .ToList();

                var files = ep.VideoList.Select(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList();

                return new EpisodeDebugInfo(
                    ep.ID,
                    ep.PreferredTitle ?? string.Empty,
                    ep.Type,
                    ep.SeasonNumber,
                    ep.EpisodeNumber,
                    ep is IShokoEpisode s && s.IsHidden,
                    crossRefPercentage,
                    crossRefOrder,
                    coords,
                    tmdb,
                    files
                );
            }

            // Find the IVideo instance for the requested videoId (if present anywhere in the series)
            var allVideos = allSeriesEpisodes.SelectMany(x => x.Videos).GroupBy(v => v.ID).Select(g => g.First()).OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList();
            var video = allVideos.FirstOrDefault(v => v.ID == videoId);
            if (video == null)
                return null;

            // Build a map of ShokoEpisodeId -> summed cross-ref percentage for this video (used for primary-type selection)
            var crossRefMap =
                video.CrossReferences?.Where(cr => cr.ShokoEpisode != null).GroupBy(cr => cr.ShokoEpisode!.ID).ToDictionary(g => g.Key, g => g.Sum(cr => cr.Percentage)) ?? new Dictionary<int, int>();

            // Build a map of ShokoEpisodeId -> minimum cross-ref Order for this video (used as a tie-breaker)
            var crossRefOrderMap =
                video.CrossReferences?.Where(cr => cr.ShokoEpisode != null).GroupBy(cr => cr.ShokoEpisode!.ID).ToDictionary(g => g.Key, g => g.Min(cr => cr.Order)) ?? new Dictionary<int, int>();

            // Build video->episode matches (only from included episodes)
            var videoToEpisodes = new List<(IEpisode Episode, PlexCoords Coords)>();
            foreach (var (episode, videos) in includedEpisodes)
            {
                var coords = GetPlexCoordinates(episode);
                if (videos.Any(v => v.ID == videoId))
                    videoToEpisodes.Add((episode, coords));
            }

            // Sort / filter / dedupe (mirrors BuildFileMappings)
            var sortedEps = videoToEpisodes.OrderBy(x => x.Coords.Season).ThenBy(x => x.Coords.Episode).ToList();

            // Use shared helper (pass precomputed crossRefMap so it isn't recomputed internally)
            var (filteredEps, deduped) = FilterAndDedupeByPrimaryType(sortedEps, video, crossRefMap);

            if (deduped.Count == 0)
            {
                // No mapping for this file under current BuildFileMappings rules — still return diagnostics.
                return new FileMappingDebugInfo(
                    VideoId: video.ID,
                    FileName: Path.GetFileName(video.Locations.FirstOrDefault()?.Path ?? ""),
                    FullPath: video.Locations.FirstOrDefault()?.Path,
                    HasPlexSplitTag: TextHelper.HasPlexSplitTag(Path.GetFileName(video.Locations.FirstOrDefault()?.Path ?? "")),
                    FileIndex: 0,
                    FileCount: 0,
                    AllowPartSuffix: false,
                    FileIndexParam: null,
                    FinalCoords: new PlexCoords
                    {
                        Season = PlexConstants.SeasonStandard,
                        Episode = 1,
                        EndEpisode = null,
                    },
                    PartIndex: null,
                    PartCount: 1,
                    TmdbEpisodeSelected: null,
                    PrimaryEpisode: null,
                    VideoEpisodes: new List<EpisodeDebugInfo>(),
                    SortedEps: new List<EpisodeDebugInfo>(),
                    FilteredEps: new List<EpisodeDebugInfo>(),
                    DedupedCoords: new List<(int, int)>(),
                    EpisodeFileLists: episodeFileLists.ToDictionary(k => k.Key, v => v.Value.Select(x => Path.GetFileName(x.Locations.FirstOrDefault()?.Path ?? "")).ToList()),
                    FinalMapSummary: null,
                    TmdbEpNumberingSetting: ShokoRelay.Settings.TMDBEpNumbering
                );
            }

            var (firstEp, _) = deduped[0];
            var epId = firstEp.ID;
            var episodes = deduped.Select(x => x.Episode).ToList();

            int fileIndex = episodeFileLists.TryGetValue(epId, out var filesForEp) ? filesForEp.FindIndex(x => x.ID == video.ID) : 0;
            int fileCount = filesForEp?.Count ?? 1;

            string fileName = Path.GetFileName(video.Locations.FirstOrDefault()?.Path ?? "");

            bool allowPartSuffix = ComputeAllowPartSuffix(deduped, fileCount, fileName);

            int? fileIndexParam = allowPartSuffix && fileCount > 1 ? fileIndex : null;

            var coordsForFile = GetPlexCoordinatesForFile(episodes, fileIndexParam);
            // Mirror BuildFileMappings behavior so debug shows the effective placement after Featurettes fallback
            var adjustedCoordsForFile = ApplyFeaturettesSeasonFallback(series, coordsForFile);
            int? partIndex = allowPartSuffix && fileCount > 1 ? fileIndex + 1 : null;
            int partCount = allowPartSuffix ? fileCount : 1;

            // Debug-safe TMDB episode summary (avoid serializing full TMDB objects with circular refs)
            TmdbEpisodeSummary? tmdbEpisodeSelected = null;
            if (fileCount > 1 && ShokoRelay.Settings.TMDBEpNumbering && firstEp is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
            {
                var tmdbEps = shokoEp.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList();
                if (fileIndex < tmdbEps.Count)
                    tmdbEpisodeSelected = new TmdbEpisodeSummary(tmdbEps[fileIndex].SeasonNumber, tmdbEps[fileIndex].EpisodeNumber, tmdbEps[fileIndex].PreferredTitle);
            }

            // Compose debug payloads (include cross-ref percentage where available)
            int? pctFor(int id) => crossRefMap.TryGetValue(id, out var p) ? p : (int?)null;
            int? orderFor(int id) => crossRefOrderMap.TryGetValue(id, out var o) ? o : (int?)null;

            var videoEpisodesDebug = videoToEpisodes.Select(x => ToEpDebug(x.Episode, pctFor(x.Episode.ID), orderFor(x.Episode.ID))).ToList();
            var sortedDebug = sortedEps.Select(x => ToEpDebug(x.Episode, pctFor(x.Episode.ID), orderFor(x.Episode.ID))).ToList();
            var filteredDebug = filteredEps.Select(x => ToEpDebug(x.Episode, pctFor(x.Episode.ID), orderFor(x.Episode.ID))).ToList();
            var dedupedCoords = deduped.Select(x => (x.Coords.Season, x.Coords.Episode)).ToList();

            var finalMap = GetSeriesFileData(series).Mappings.FirstOrDefault(m => m.Video.ID == video.ID);
            FileMappingSummary? finalMapSummary = null;
            if (finalMap != null)
            {
                TmdbEpisodeSummary? fmTmdb = null;
                if (finalMap.TmdbEpisode != null)
                {
                    var raw = finalMap.TmdbEpisode;
                    var rt = raw.GetType();
                    int? sNum = null;
                    int eNum = 0;
                    string? pTitle = null;

                    var piSeason = rt.GetProperty("SeasonNumber");
                    if (piSeason != null)
                        sNum = (int?)piSeason.GetValue(raw);

                    var piEpisode = rt.GetProperty("EpisodeNumber");
                    if (piEpisode != null && piEpisode.GetValue(raw) != null)
                        eNum = Convert.ToInt32(piEpisode.GetValue(raw));

                    var piTitle = rt.GetProperty("PreferredTitle") ?? rt.GetProperty("Title") ?? rt.GetProperty("Name");
                    if (piTitle != null)
                        pTitle = piTitle.GetValue(raw) as string;

                    fmTmdb = new TmdbEpisodeSummary(sNum, eNum, pTitle);
                }

                finalMapSummary = new FileMappingSummary(
                    VideoId: finalMap.Video.ID,
                    FileName: finalMap.FileName,
                    PartIndex: finalMap.PartIndex,
                    PartCount: finalMap.PartCount,
                    Coords: finalMap.Coords,
                    PrimaryEpisodeId: finalMap.PrimaryEpisode?.ID,
                    PrimaryEpisodeTitle: finalMap.PrimaryEpisode?.PreferredTitle,
                    EpisodeIds: finalMap.Episodes.Select(e => e.ID).ToList(),
                    TmdbEpisode: fmTmdb
                );
            }

            return new FileMappingDebugInfo(
                VideoId: video.ID,
                FileName: fileName,
                FullPath: video.Locations.FirstOrDefault()?.Path,
                HasPlexSplitTag: TextHelper.HasPlexSplitTag(fileName),
                FileIndex: fileIndex,
                FileCount: fileCount,
                AllowPartSuffix: allowPartSuffix,
                FileIndexParam: fileIndexParam,
                FinalCoords: coordsForFile,
                PartIndex: partIndex,
                PartCount: partCount,
                TmdbEpisodeSelected: tmdbEpisodeSelected,
                PrimaryEpisode: ToEpDebug(firstEp, pctFor(firstEp.ID), orderFor(firstEp.ID)),
                VideoEpisodes: videoEpisodesDebug,
                SortedEps: sortedDebug,
                FilteredEps: filteredDebug,
                DedupedCoords: dedupedCoords,
                EpisodeFileLists: episodeFileLists.ToDictionary(k => k.Key, v => v.Value.Select(x => Path.GetFileName(x.Locations.FirstOrDefault()?.Path ?? "")).ToList()),
                FinalMapSummary: finalMapSummary,
                TmdbEpNumberingSetting: ShokoRelay.Settings.TMDBEpNumbering
            );
        }
    }
}
