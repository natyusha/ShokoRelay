using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;

namespace ShokoRelay.Plex
{
    public static class PlexMapping
    {
        public struct PlexCoords
        {
            public int Season;
            public int Episode;
            public int? EndEpisode;
        }

        public static bool TryGetExtraSeason(int seasonNumber, out (string Folder, string Prefix, string Subtype) info)
        {
            return PlexConstants.ExtraSeasons.TryGetValue(seasonNumber, out info);
        }

        public static string GetSeasonFolderName(int seasonNumber)
        {
            if (TryGetExtraSeason(seasonNumber, out var special))
                return special.Folder;

            if (seasonNumber == 0)
                return "Specials";
            return $"Season {seasonNumber}";
        }

        public static string GetSeasonTitle(int seasonNumber)
        {
            if (TryGetExtraSeason(seasonNumber, out var special))
                return special.Prefix;

            if (seasonNumber == 0)
                return "Specials";
            return $"Season {seasonNumber}";
        }

        public static PlexCoords GetPlexCoordinates(IEpisode e)
        {
            if (ShokoRelay.Settings.TMDBEpNumbering && e is IShokoEpisode shokoEpisode)
            {
                var tmdbEpisodes = shokoEpisode.TmdbEpisodes.OrderBy(te => te.EpisodeNumber).ToList();

                if (tmdbEpisodes.Count > 0)
                {
                    var first = tmdbEpisodes.First();
                    if (first.SeasonNumber.HasValue)
                    {
                        return new PlexCoords
                        {
                            Season = first.SeasonNumber.Value,
                            Episode = first.EpisodeNumber,
                            EndEpisode = tmdbEpisodes.Count > 1 ? tmdbEpisodes.Last().EpisodeNumber : null,
                        };
                    }
                }
            }

            int epNum = e.EpisodeNumber;
            int seasonNum = ResolveSeasonNumber(e);

            return e.Type switch
            {
                EpisodeType.Other => new PlexCoords { Season = PlexConstants.SeasonOther, Episode = epNum },
                EpisodeType.Credits => new PlexCoords { Season = PlexConstants.SeasonCredits, Episode = epNum },
                EpisodeType.Trailer => new PlexCoords { Season = PlexConstants.SeasonTrailers, Episode = epNum },
                EpisodeType.Parody => new PlexCoords { Season = PlexConstants.SeasonParody, Episode = epNum },
                _ => new PlexCoords { Season = seasonNum, Episode = epNum },
            };
        }

        public static PlexCoords GetPlexCoordinatesForFile(IEnumerable<IEpisode> episodes, int? fileIndexWithinEpisode = null)
        {
            var eps = (episodes ?? Enumerable.Empty<IEpisode>()).ToList();
            if (!eps.Any())
                return new PlexCoords
                {
                    Season = 1,
                    Episode = 1,
                    EndEpisode = null,
                };

            if (ShokoRelay.Settings.TMDBEpNumbering)
            {
                var tmdbEntries = eps.OfType<IShokoEpisode>()
                    .Where(se => se.TmdbEpisodes != null && se.TmdbEpisodes.Any())
                    .SelectMany(se => se.TmdbEpisodes)
                    .OrderBy(te => te.SeasonNumber ?? 0)
                    .ThenBy(te => te.EpisodeNumber)
                    .ToList();

                if (tmdbEntries.Any())
                {
                    var first = tmdbEntries.First();
                    if (first.SeasonNumber.HasValue)
                    {
                        // If fileIndex is provided and within range, pick the specific offset
                        // Otherwise, return the full range (for single files mapping to multiple TMDB episodes)
                        if (fileIndexWithinEpisode.HasValue && fileIndexWithinEpisode.Value < tmdbEntries.Count)
                        {
                            var tmdbEp = tmdbEntries[fileIndexWithinEpisode.Value];
                            return new PlexCoords
                            {
                                Season = tmdbEp.SeasonNumber ?? first.SeasonNumber.Value,
                                Episode = tmdbEp.EpisodeNumber,
                                EndEpisode = null,
                            };
                        }
                        else if (!fileIndexWithinEpisode.HasValue || tmdbEntries.Count == 1)
                        {
                            // No fileIndex provided (single file) or only one TMDB entry: return the range
                            var last = tmdbEntries.Last();
                            int? endEpisode = (tmdbEntries.Count > 1 && last.SeasonNumber == first.SeasonNumber) ? last.EpisodeNumber : (int?)null;
                            return new PlexCoords
                            {
                                Season = first.SeasonNumber.Value,
                                Episode = first.EpisodeNumber,
                                EndEpisode = endEpisode,
                            };
                        }
                    }
                }
            }

            if (eps.Count == 1)
            {
                return GetPlexCoordinates(eps[0]);
            }

            var start = GetPlexCoordinates(eps.First());
            var end = GetPlexCoordinates(eps.Last());
            int? endEpisodeFinal = start.Season == end.Season ? end.Episode : (int?)null;

            return new PlexCoords
            {
                Season = start.Season,
                Episode = start.Episode,
                EndEpisode = endEpisodeFinal,
            };
        }

        private static int ResolveSeasonNumber(IEpisode e)
        {
            // Prefer provider season numbers when available (covers regular episodes and specials).
            if (e.SeasonNumber.HasValue)
                return e.SeasonNumber.Value;

            return e.Type switch
            {
                EpisodeType.Episode => PlexConstants.SeasonStandard,
                EpisodeType.Special => PlexConstants.SeasonSpecials,
                EpisodeType.Credits => PlexConstants.SeasonCredits,
                EpisodeType.Trailer => PlexConstants.SeasonTrailers,
                EpisodeType.Parody => PlexConstants.SeasonParody,
                EpisodeType.Other => PlexConstants.SeasonOther,
                _ => PlexConstants.SeasonUnknown,
            };
        }
    }
}
