using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;

namespace ShokoRelay.Plex;

/// <summary>Maps Shoko episode/season data to Plex-style coordinates.</summary>
public static class PlexMapping
{
    /// <summary>Simple struct representing season/episode coordinates.</summary>
    public struct PlexCoords
    {
        /// <summary>Plex season number.</summary>
        public int Season;

        /// <summary>Plex starting episode number.</summary>
        public int Episode;

        /// <summary>Optional ending episode number for ranges.</summary>
        public int? EndEpisode;
    }

    /// <summary>Look up info for an extra/special season number.</summary>
    /// <param name="seasonNumber">The season ID.</param>
    /// <param name="info">Result folder/subtype tuple.</param>
    /// <returns>True if special.</returns>
    public static bool TryGetExtraSeason(int seasonNumber, out (string Folder, string Subtype) info)
    {
        return PlexConstants.ExtraSeasons.TryGetValue(seasonNumber, out info);
    }

    /// <summary>Obtain the Plex folder name for a season.</summary>
    /// <param name="seasonNumber">The season ID.</param>
    /// <returns>Folder name string.</returns>
    public static string GetSeasonFolder(int seasonNumber)
    {
        return TryGetExtraSeason(seasonNumber, out var special) ? special.Folder
            : seasonNumber == 0 ? "Specials"
            : $"Season {seasonNumber}";
    }

    /// <summary>Calculate Plex coordinates for an episode.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <param name="seriesPreferredOrderingId">Optional TMDB ordering ID.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinates(IEpisode e, string? seriesPreferredOrderingId = null)
    {
        if (e == null)
            return new PlexCoords { Season = PlexConstants.SeasonStandard, Episode = 1 };
        string? showPrefId = seriesPreferredOrderingId;
        PlexCoords result;

        if (ShokoRelay.Settings.TmdbEpNumbering && e is IShokoEpisode shokoEpisode && shokoEpisode.TmdbEpisodes != null && shokoEpisode.TmdbEpisodes.Any())
        {
            var tmdbEpisodes = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. shokoEpisode.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(shokoEpisode.TmdbEpisodes, showPrefId);
            if (tmdbEpisodes.Count > 0)
            {
                var first = tmdbEpisodes.First();
                var (Season, Episode) = GetOrderingCoords(first, showPrefId);
                if (Season.HasValue)
                {
                    int? endEp = null;
                    if (tmdbEpisodes.Count > 1)
                    {
                        var lastCoords = GetOrderingCoords(tmdbEpisodes.Last(), showPrefId);
                        if (lastCoords.Season == Season)
                            endEp = lastCoords.Episode;
                    }
                    result = new PlexCoords
                    {
                        Season = Season.Value,
                        Episode = Episode,
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

    /// <summary>Determine Plex coordinates for episodes sharing a file.</summary>
    /// <param name="episodes">Episode list.</param>
    /// <param name="fileIndexWithinEpisode">Optional file index.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinatesForFile(IEnumerable<IEpisode> episodes, int? fileIndexWithinEpisode = null)
    {
        var eps = (episodes ?? []).ToList();
        if (!eps.Any())
            return new PlexCoords
            {
                Season = 1,
                Episode = 1,
                EndEpisode = null,
            };
        if (ShokoRelay.Settings.TmdbEpNumbering && eps.Select(ep => ep.Type).Distinct().Count() == 1)
        {
            var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().Where(se => se.TmdbEpisodes != null && se.TmdbEpisodes.Any()).SelectMany(se => se.TmdbEpisodes).ToList();
            string? showPrefId = eps.OfType<IShokoEpisode>().Select(se => se.Series).FirstOrDefault()?.TmdbShows?.FirstOrDefault()?.PreferredOrdering?.OrderingID;
            var tmdbEntries = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. tmdbEntriesRaw.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(tmdbEntriesRaw, showPrefId);
            if (tmdbEntries.Any())
            {
                var first = tmdbEntries.First();
                var (Season, Episode) = GetOrderingCoords(first, showPrefId);
                if (Season.HasValue)
                {
                    if (fileIndexWithinEpisode.HasValue && fileIndexWithinEpisode.Value < tmdbEntries.Count)
                    {
                        var tmdbEp = tmdbEntries[fileIndexWithinEpisode.Value];
                        var epCoords = GetOrderingCoords(tmdbEp, showPrefId);
                        return new PlexCoords
                        {
                            Season = epCoords.Season ?? Season.Value,
                            Episode = epCoords.Episode,
                            EndEpisode = null,
                        };
                    }
                    else if (!fileIndexWithinEpisode.HasValue || tmdbEntries.Count == 1)
                    {
                        var last = tmdbEntries.Last();
                        var lastCoords = GetOrderingCoords(last, showPrefId);
                        int? endEpisode = (tmdbEntries.Count > 1 && lastCoords.Season == Season) ? lastCoords.Episode : null;
                        return new PlexCoords
                        {
                            Season = Season.Value,
                            Episode = Episode,
                            EndEpisode = endEpisode,
                        };
                    }
                }
            }
        }
        if (eps.Count == 1)
            return GetPlexCoordinates(eps[0]);
        var start = GetPlexCoordinates(eps.First());
        var end = GetPlexCoordinates(eps.Last());
        int? endEpisodeFinal = start.Season == end.Season ? end.Episode : null;
        return new PlexCoords
        {
            Season = start.Season,
            Episode = start.Episode,
            EndEpisode = endEpisodeFinal,
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int EpId, string? OrderingId), bool> _tmdbAllOrderingsContainsCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int EpId, string? OrderingId), (int? Season, int Episode)> _orderingCoordsCache = new();

    /// <summary>Filter a list of TMDB episode entries to the preferred ordering.</summary>
    /// <param name="entries">TMDB episodes.</param>
    /// <param name="showPreferredOrderingId">Preferred ordering ID.</param>
    /// <returns>Reordered list.</returns>
    public static List<ITmdbEpisode> SelectPreferredTmdbOrdering(IEnumerable<ITmdbEpisode>? entries, string? showPreferredOrderingId = null)
    {
        if (entries == null)
            return [];
        var list = entries.ToList();
        if (!list.Any())
            return list;
        if (!string.IsNullOrWhiteSpace(showPreferredOrderingId))
        {
            var preferredDirect = list.Where(te => string.Equals(te.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(te => te.SeasonNumber ?? 0)
                .ThenBy(te => te.EpisodeNumber)
                .ToList();
            var remainder = list.Except(preferredDirect).ToList();
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
            return [.. preferred, .. others];
        }
        return [.. list.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)];
    }

    /// <summary>Convert a TMDB episode into season/episode coordinates.</summary>
    /// <param name="ep">TMDB episode.</param>
    /// <param name="showPreferredOrderingId">Ordering ID.</param>
    /// <returns>Season and Episode tuple.</returns>
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
        return e.SeasonNumber
            ?? e.Type switch
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
