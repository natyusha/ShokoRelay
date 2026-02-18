using System;
using System.Collections.Concurrent;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;

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

        public static bool TryGetExtraSeason(int seasonNumber, out (string Folder, string Subtype) info)
        {
            return PlexConstants.ExtraSeasons.TryGetValue(seasonNumber, out info);
        }

        public static string GetSeasonFolder(int seasonNumber)
        {
            if (TryGetExtraSeason(seasonNumber, out var special))
                return special.Folder;

            if (seasonNumber == 0)
                return "Specials";
            return $"Season {seasonNumber}";
        }

        public static PlexCoords GetPlexCoordinates(IEpisode e, string? seriesPreferredOrderingId = null)
        {
            if (e == null)
                return new PlexCoords { Season = PlexConstants.SeasonStandard, Episode = 1 };

            // Allow callers to pass the series-level preferred ordering id so it is resolved once per series
            // Do NOT probe the episode->series PreferredOrdering here — callers that are mapping many
            // episodes (MapHelper/VFS) should pass the seriesPreferredOrderingId to avoid repeated lookups.
            string? showPrefId = seriesPreferredOrderingId;

            // Compute coordinates (original logic)
            PlexCoords result;

            // Apply TMDB episode-numbering for any Shoko episode that has TMDB links when enabled.
            if (ShokoRelay.Settings.TMDBEpNumbering && e is IShokoEpisode shokoEpisode && shokoEpisode.TmdbEpisodes != null && shokoEpisode.TmdbEpisodes.Any())
            {
                // Only evaluate alternate-ordering logic when a series-level preferred ordering id is present.
                // Otherwise use the fast default ordering (season -> episode) to avoid touching AllOrderings.
                var tmdbEpisodes = string.IsNullOrWhiteSpace(showPrefId)
                    ? shokoEpisode.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList()
                    : SelectPreferredTmdbOrdering(shokoEpisode.TmdbEpisodes, showPrefId);

                if (tmdbEpisodes.Count > 0)
                {
                    var first = tmdbEpisodes.First();
                    var firstCoords = GetOrderingCoords(first, showPrefId);
                    if (firstCoords.Season.HasValue)
                    {
                        int? endEp = null;
                        if (tmdbEpisodes.Count > 1)
                        {
                            var lastCoords = GetOrderingCoords(tmdbEpisodes.Last(), showPrefId);
                            if (lastCoords.Season == firstCoords.Season)
                                endEp = lastCoords.Episode;
                        }

                        result = new PlexCoords
                        {
                            Season = firstCoords.Season.Value,
                            Episode = firstCoords.Episode,
                            EndEpisode = endEp,
                        };

                        return result;
                    }
                }
            }

            int epNum = e.EpisodeNumber;
            int seasonNum = ResolveSeasonNumber(e);

            result = e.Type switch
            {
                EpisodeType.Other => new PlexCoords { Season = PlexConstants.SeasonOther, Episode = epNum },
                EpisodeType.Credits => new PlexCoords { Season = PlexConstants.SeasonCredits, Episode = epNum },
                EpisodeType.Trailer => new PlexCoords { Season = PlexConstants.SeasonTrailers, Episode = epNum },
                EpisodeType.Parody => new PlexCoords { Season = PlexConstants.SeasonParody, Episode = epNum },
                _ => new PlexCoords { Season = seasonNum, Episode = epNum },
            };

            return result;
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

            // If TMDB numbering is enabled and all episodes in the file are the same type (e.g. all Episode, all Special, all Other, etc.),
            // collect any TMDB entries present among those episodes and apply TMDB-driven coordinates when available.
            // We do NOT apply TMDB numbering for mixed-type files (MapHelper filters mixed types earlier).
            if (ShokoRelay.Settings.TMDBEpNumbering && eps.Select(ep => ep.Type).Distinct().Count() == 1)
            {
                var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().Where(se => se.TmdbEpisodes != null && se.TmdbEpisodes.Any()).SelectMany(se => se.TmdbEpisodes).ToList();

                // If the series defines a preferred ordering, pass it so TMDB alternate orderings are respected.
                string? showPrefId = eps.OfType<IShokoEpisode>().Select(se => se.Series).FirstOrDefault()?.TmdbShows?.FirstOrDefault()?.PreferredOrdering?.OrderingID;

                var tmdbEntries = string.IsNullOrWhiteSpace(showPrefId)
                    ? tmdbEntriesRaw.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList()
                    : SelectPreferredTmdbOrdering(tmdbEntriesRaw, showPrefId);

                if (tmdbEntries.Any())
                {
                    var first = tmdbEntries.First();
                    var firstCoords = GetOrderingCoords(first, showPrefId);
                    if (firstCoords.Season.HasValue)
                    {
                        // If fileIndex is provided and within range, pick the specific offset
                        // Otherwise, return the full range (for single files mapping to multiple TMDB episodes)
                        if (fileIndexWithinEpisode.HasValue && fileIndexWithinEpisode.Value < tmdbEntries.Count)
                        {
                            var tmdbEp = tmdbEntries[fileIndexWithinEpisode.Value];
                            var epCoords = GetOrderingCoords(tmdbEp, showPrefId);
                            return new PlexCoords
                            {
                                Season = epCoords.Season ?? firstCoords.Season.Value,
                                Episode = epCoords.Episode,
                                EndEpisode = null,
                            };
                        }
                        else if (!fileIndexWithinEpisode.HasValue || tmdbEntries.Count == 1)
                        {
                            // No fileIndex provided (single file) or only one TMDB entry: return the range
                            var last = tmdbEntries.Last();
                            var lastCoords = GetOrderingCoords(last, showPrefId);
                            int? endEpisode = (tmdbEntries.Count > 1 && lastCoords.Season == firstCoords.Season) ? lastCoords.Episode : (int?)null;
                            return new PlexCoords
                            {
                                Season = firstCoords.Season.Value,
                                Episode = firstCoords.Episode,
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

        // Determine if a TMDB Alternate ordering is preferred for the show
        // Lightweight caches to avoid repeated expensive access to TMDB alternate-ordering data (AllOrderings)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int EpId, string? OrderingId), bool> _tmdbAllOrderingsContainsCache =
            new System.Collections.Concurrent.ConcurrentDictionary<(int, string?), bool>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int EpId, string? OrderingId), (int? Season, int Episode)> _orderingCoordsCache =
            new System.Collections.Concurrent.ConcurrentDictionary<(int, string?), (int?, int)>();

        public static List<ITmdbEpisode> SelectPreferredTmdbOrdering(IEnumerable<ITmdbEpisode>? entries, string? showPreferredOrderingId = null)
        {
            if (entries == null)
                return new List<ITmdbEpisode>();

            var list = entries.ToList();
            if (!list.Any())
                return list;

            // If the caller supplied a show-preferred ordering id, prefer episodes that include that ordering id in their AllOrderings.
            if (!string.IsNullOrWhiteSpace(showPreferredOrderingId))
            {
                // Fast-path: check the episode's own OrderingID (cheap) before probing AllOrderings (which can be expensive/lazy-loaded)
                var preferredDirect = list.Where(te => string.Equals(te.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(te => te.SeasonNumber ?? 0)
                    .ThenBy(te => te.EpisodeNumber)
                    .ToList();

                var remainder = list.Except(preferredDirect).ToList();

                // Only enumerate AllOrderings for the remainder (rare path). Use a small memo to avoid repeated DB enumerations.
                var preferredFromAll = remainder
                    .Where(te =>
                    {
                        try
                        {
                            var key = (EpId: te.ID, OrderingId: showPreferredOrderingId);
                            if (_tmdbAllOrderingsContainsCache.TryGetValue(key, out var cached))
                                return cached;

                            bool found = te.AllOrderings != null && te.AllOrderings.Any(o => string.Equals(o.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase));
                            _tmdbAllOrderingsContainsCache[key] = found;
                            return found;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .OrderBy(te => te.SeasonNumber ?? 0)
                    .ThenBy(te => te.EpisodeNumber)
                    .ToList();

                var preferred = preferredDirect.Concat(preferredFromAll).ToList();
                var others = list.Except(preferred).OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber);
                return preferred.Concat(others).ToList();
            }

            // Default: canonical season/episode ordering (fast and deterministic).
            return list.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList();
        }

        // If a TMDB Alternate ordering is preferred for the show, prioritize episodes that include that ordering id in their AllOrderings list.
        public static (int? Season, int Episode) GetOrderingCoords(ITmdbEpisode ep, string? showPreferredOrderingId = null)
        {
            if (ep == null)
                return (null, 0);

            if (!string.IsNullOrWhiteSpace(showPreferredOrderingId))
            {
                var key = (EpId: ep.ID, OrderingId: showPreferredOrderingId);
                if (_orderingCoordsCache.TryGetValue(key, out var cachedCoords))
                    return cachedCoords;

                if (ep.AllOrderings != null)
                {
                    var byAll = ep.AllOrderings.FirstOrDefault(o => string.Equals(o.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase));
                    if (byAll != null)
                    {
                        var result = (byAll.SeasonNumber, byAll.EpisodeNumber);
                        _orderingCoordsCache[key] = result;
                        return result;
                    }
                }

                // Cache the fallback to declared season/episode as well so subsequent calls are cheap
                var fallback = (ep.SeasonNumber, ep.EpisodeNumber);
                _orderingCoordsCache[key] = fallback;
                return fallback;
            }

            // Fallback -> use the episode's declared SeasonNumber/EpisodeNumber.
            return (ep.SeasonNumber, ep.EpisodeNumber);
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
