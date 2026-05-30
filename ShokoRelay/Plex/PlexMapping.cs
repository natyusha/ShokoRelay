using System.Collections.Concurrent;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Tmdb;

namespace ShokoRelay.Plex;

/// <summary>Maps Shoko episode/season data to Plex-style coordinates.</summary>
public static class PlexMapping
{
    #region Data Types

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

    #endregion

    #region Season & Folder Logic

    /// <summary>Look up info for an extra/special season number.</summary>
    /// <param name="seasonNumber">The season ID.</param>
    /// <param name="info">Result folder/subtype tuple.</param>
    /// <returns>True if special.</returns>
    public static bool TryGetExtraSeason(int seasonNumber, out (string Folder, string Subtype) info) => PlexConstants.ExtraSeasons.TryGetValue(seasonNumber, out info);

    /// <summary>Obtain the Plex folder name for a season.</summary>
    /// <param name="seasonNumber">The season ID.</param>
    /// <returns>Folder name string.</returns>
    public static string GetSeasonFolder(int seasonNumber) =>
        TryGetExtraSeason(seasonNumber, out var special) ? special.Folder
        : seasonNumber == 0 ? "Specials"
        : $"Season {seasonNumber}";

    #endregion

    #region Coordinate Calc

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

        if (EnforceTmdbNumbering && e is IShokoEpisode shokoEpisode && shokoEpisode.TmdbEpisodes != null && shokoEpisode.TmdbEpisodes.Any())
        {
            var tmdbEpisodes = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. shokoEpisode.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(shokoEpisode.TmdbEpisodes, showPrefId);
            if (tmdbEpisodes.Count > 0)
            {
                var first = tmdbEpisodes.First();
                var (season, episode) = GetOrderingCoords(first, showPrefId);
                if (season.HasValue)
                {
                    int? endEp = null;
                    if (tmdbEpisodes.Count > 1)
                    {
                        var (lastSeason, lastEpisode) = GetOrderingCoords(tmdbEpisodes.Last(), showPrefId);
                        if (lastSeason == season)
                            endEp = lastEpisode;
                    }
                    result = new PlexCoords
                    {
                        Season = season.Value,
                        Episode = episode,
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
        if (EnforceTmdbNumbering && eps.Select(ep => ep.Type).Distinct().Count() == 1)
        {
            var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().Where(se => se.TmdbEpisodes != null && se.TmdbEpisodes.Any()).SelectMany(se => se.TmdbEpisodes).ToList();
            string? showPrefId = eps.OfType<IShokoEpisode>().Select(se => se.Series).FirstOrDefault()?.TmdbShows?.FirstOrDefault()?.PreferredOrdering?.OrderingID;
            var tmdbEntries = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. tmdbEntriesRaw.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(tmdbEntriesRaw, showPrefId);
            if (tmdbEntries.Any())
            {
                var first = tmdbEntries.First();
                var (season, episode) = GetOrderingCoords(first, showPrefId);
                if (season.HasValue)
                {
                    if (fileIndexWithinEpisode.HasValue && fileIndexWithinEpisode.Value < tmdbEntries.Count)
                    {
                        var tmdbEp = tmdbEntries[fileIndexWithinEpisode.Value];
                        var (epSeason, epEpisode) = GetOrderingCoords(tmdbEp, showPrefId);
                        return new PlexCoords
                        {
                            Season = epSeason ?? season.Value,
                            Episode = epEpisode,
                            EndEpisode = null,
                        };
                    }
                    else if (!fileIndexWithinEpisode.HasValue || tmdbEntries.Count == 1)
                    {
                        var last = tmdbEntries.Last();
                        var (lastSeason, lastEpisode) = GetOrderingCoords(last, showPrefId);
                        int? endEpisode = (tmdbEntries.Count > 1 && lastSeason == season) ? lastEpisode : null;
                        return new PlexCoords
                        {
                            Season = season.Value,
                            Episode = episode,
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

    #endregion

    #region TMDB Order & Cache

    private static readonly ConcurrentDictionary<(int EpId, string? OrderingId), bool> s_tmdbAllOrderingsContainsCache = new();
    private static readonly ConcurrentDictionary<(int EpId, string? OrderingId), (int? Season, int Episode)> s_orderingCoordsCache = new();

    /// <summary>Filter a list of TMDB episode entries to the preferred ordering using a single-pass weighted sort.</summary>
    /// <param name="entries">The collection of TMDB episodes to filter.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A reordered and filtered list of TMDB episodes.</returns>
    public static List<ITmdbEpisode> SelectPreferredTmdbOrdering(IEnumerable<ITmdbEpisode>? entries, string? showPreferredOrderingId = null) =>
        entries == null ? []
        : entries.ToList() is var list && list.Count == 0 ? list
        : string.IsNullOrWhiteSpace(showPreferredOrderingId) ? [.. list.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
        :
        [
            .. list.OrderBy(te =>
                    string.Equals(te.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase) ? 0
                    : s_tmdbAllOrderingsContainsCache.GetOrAdd(
                        (te.ID, showPreferredOrderingId),
                        key => te.AllOrderings?.Any(o => string.Equals(o.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase)) == true
                    )
                        ? 1
                    : 2
                )
                .ThenBy(te => te.SeasonNumber ?? 0)
                .ThenBy(te => te.EpisodeNumber),
        ];

    /// <summary>Convert a TMDB episode into season/episode coordinates.</summary>
    /// <param name="ep">The TMDB episode to inspect.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A tuple containing the resolved season and episode numbers.</returns>
    public static (int? Season, int Episode) GetOrderingCoords(ITmdbEpisode ep, string? showPreferredOrderingId = null) =>
        ep == null ? (null, 0)
        : !string.IsNullOrWhiteSpace(showPreferredOrderingId)
            ? s_orderingCoordsCache.GetOrAdd(
                (ep.ID, showPreferredOrderingId),
                _ =>
                    ep.AllOrderings?.FirstOrDefault(o => string.Equals(o.OrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase)) is { } byAll
                        ? (byAll.SeasonNumber, byAll.EpisodeNumber)
                        : (ep.SeasonNumber, ep.EpisodeNumber)
            )
        : (ep.SeasonNumber, ep.EpisodeNumber);

    #endregion

    #region Private Helpers

    /// <summary>Resolves the season number for an episode, falling back to Plex extra season constants for non-standard episodes.</summary>
    /// <param name="e">The episode reference to evaluate.</param>
    /// <returns>The resolved numeric season index.</returns>
    private static int ResolveSeasonNumber(IEpisode e) =>
        e.SeasonNumber
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

    #endregion
}
