using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using static ShokoRelay.Meta.PlexMapping;

namespace ShokoRelay.Helpers
{
    public static class MapHelper
    {
        public record FileMapping(
            IVideo Video,
            IReadOnlyList<IEpisode> Episodes,
            IEpisode PrimaryEpisode,
            PlexCoords Coords,
            string FileName,
            int? PartIndex,
            int PartCount,
            object? TmdbEpisode
        );

        public record SeriesFileData(
            List<FileMapping> Mappings,
            List<int> Seasons
        )
        {
            public List<FileMapping> GetForSeason(int season)
                => Mappings.Where(m => m.Coords.Season == season).OrderBy(m => m.Coords.Episode).ToList();
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
            var seriesEpisodes = series.Episodes
                .Select(e => (Episode: e, Videos: e.VideoList.ToList()))
                .Where(x => x.Videos.Count > 0 && !IsHidden(x.Episode))
                .ToList();

            if (seriesEpisodes.Count == 0) return result;

            // Build episode file lists from cached videos
            var episodeFileLists = seriesEpisodes.ToDictionary(
                x => x.Episode.ID,
                x => x.Videos.OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? "")).ToList()
            );

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
            var allVideos = seriesEpisodes
                .SelectMany(x => x.Videos)
                .GroupBy(v => v.ID)
                .Select(g => g.First())
                .OrderBy(v => Path.GetFileName(v.Locations.FirstOrDefault()?.Path ?? ""))
                .ToList();

            foreach (var video in allVideos)
            {
                if (!videoToEpisodes.TryGetValue(video.ID, out var epList) || epList.Count == 0)
                    continue;

                // Sort by pre-computed coords
                var sortedEps = epList
                    .OrderBy(x => x.Coords.Season)
                    .ThenBy(x => x.Coords.Episode)
                    .ToList();

                // Filter multi-episode files of differing types (unless ThemeSong)
                var filteredEps = new List<(IEpisode Episode, PlexCoords Coords)>();
                string? prevType = null;

                foreach (var (ep, epCoords) in sortedEps)
                {
                    string epType = GetEpisodeTypeString(ep);
                    if (filteredEps.Count > 0 && epType != prevType && epType != "ThemeSong" && prevType != "ThemeSong")
                        continue;

                    filteredEps.Add((ep, epCoords));
                    prevType = epType;
                }

                if (filteredEps.Count == 0) continue;

                var (firstEp, _) = filteredEps[0];
                var epId = firstEp.ID;
                var episodes = filteredEps.Select(x => x.Episode).ToList();

                int fileIndex = episodeFileLists.TryGetValue(epId, out var files) ? files.FindIndex(x => x.ID == video.ID) : 0;
                int fileCount = files?.Count ?? 1;
                int? fileIndexParam = fileCount > 1 ? fileIndex : null;

                var coords = GetPlexCoordinatesForFile(episodes, fileIndexParam);
                var fileName = Path.GetFileName(video.Locations.FirstOrDefault()?.Path ?? "");
                int? partIndex = fileCount > 1 ? fileIndex + 1 : null;

                object? tmdbEpisode = null;
                if (fileCount > 1 && ShokoRelay.Settings.TMDBStructure && firstEp is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    var tmdbEps = shokoEp.TmdbEpisodes
                        .OrderBy(te => te.SeasonNumber ?? 0)
                        .ThenBy(te => te.EpisodeNumber)
                        .ToList();

                    if (fileIndex < tmdbEps.Count)
                        tmdbEpisode = tmdbEps[fileIndex];
                }

                result.Add(new FileMapping(video, episodes, firstEp, coords, fileName, partIndex, fileCount, tmdbEpisode));
            }

            return result;
        }

        public static bool IsHidden(IEpisode e)
        {
            try { return ((dynamic)e).IsHidden == true; }
            catch { return false; }
        }

        private static string GetEpisodeTypeString(IEpisode ep)
        {
            try { return ((dynamic)ep).Type?.ToString() ?? ""; }
            catch { return ""; }
        }
    }
}