using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;

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
    /// <param name="episode">The episode metadata.</param>
    /// <param name="seriesPreferredOrderingId">Optional TMDB ordering ID.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinates(IEpisode episode, string? seriesPreferredOrderingId = null)
    {
        if (episode == null)
            return new PlexCoords { Season = PlexConstants.SeasonStandard, Episode = 1 };
        string? showPrefId = seriesPreferredOrderingId;

        if (EnforceTmdbNumbering && episode is IShokoEpisode shokoEpisode && shokoEpisode.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 0 } tmdbEps)
        {
            var tmdbEpisodes = string.IsNullOrWhiteSpace(showPrefId) ? [.. tmdbEps.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)] : SelectPreferredTmdbOrdering(tmdbEps, showPrefId);
            if (tmdbEpisodes.Count > 0)
            {
                var first = tmdbEpisodes.First();
                var (season, epNum) = GetOrderingCoords(first, showPrefId);
                if (season.HasValue)
                {
                    int? endEp = null;
                    if (tmdbEpisodes.Count > 1)
                    {
                        var (lastSeason, lastEpisode) = GetOrderingCoords(tmdbEpisodes.Last(), showPrefId);
                        if (lastSeason == season)
                            endEp = lastEpisode;
                    }
                    return new PlexCoords
                    {
                        Season = season.Value,
                        Episode = epNum,
                        EndEpisode = endEp,
                    };
                }
            }
        }

        // Resolve season coordinate, falling back to Plex extra season constants for non-standard episodes
        int seasonNum = episode.Type switch
        {
            EpisodeType.Other => PlexConstants.SeasonOther,
            EpisodeType.Credits => PlexConstants.SeasonCredits,
            EpisodeType.Trailer => PlexConstants.SeasonTrailers,
            EpisodeType.Parody => PlexConstants.SeasonParody,
            _ => episode.SeasonNumber ?? (episode.Type == EpisodeType.Special ? PlexConstants.SeasonSpecials : PlexConstants.SeasonStandard),
        };

        return new PlexCoords { Season = seasonNum, Episode = episode.EpisodeNumber };
    }

    /// <summary>Determine Plex coordinates for episodes sharing a file.</summary>
    /// <param name="episodes">The collection of episodes.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinatesForFile(IEnumerable<IEpisode> episodes)
    {
        var eps = (episodes ?? []).ToList();
        if (eps.Count == 0)
            return new PlexCoords
            {
                Season = 1,
                Episode = 1,
                EndEpisode = null,
            };

        if (EnforceTmdbNumbering && eps.Select(ep => ep.Type).Distinct().Count() == 1)
        {
            var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().SelectMany(se => se.GetLinkedEpisodes(MetadataSource.TMDB) ?? []).ToList();
            string? showPrefId = eps.OfType<IShokoEpisode>().Select(se => se.Series).FirstOrDefault() is { } series ? MapHelper.GetPreferredTmdbOrderingId(series) : null;
            var tmdbEntries = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. tmdbEntriesRaw.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(tmdbEntriesRaw, showPrefId);

            if (tmdbEntries.Any())
            {
                var first = tmdbEntries.First();
                var (season, episode) = GetOrderingCoords(first, showPrefId);
                if (season.HasValue)
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
        if (eps.Count == 1)
            return GetPlexCoordinates(eps[0]);
        var start = GetPlexCoordinates(eps[0]);
        var end = GetPlexCoordinates(eps[^1]);
        int? endEpisodeFinal = start.Season == end.Season ? end.Episode : null;
        return new PlexCoords
        {
            Season = start.Season,
            Episode = start.Episode,
            EndEpisode = endEpisodeFinal,
        };
    }

    #endregion

    #region TMDB Order

    /// <summary>Filter a list of TMDB episode entries to the preferred ordering using a single-pass weighted sort.</summary>
    /// <param name="entries">The collection of episodes to filter.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A reordered and filtered list of TMDB episodes.</returns>
    public static List<IEpisode> SelectPreferredTmdbOrdering(IEnumerable<IEpisode>? entries, string? showPreferredOrderingId = null)
    {
        if (entries == null)
            return [];
        var list = entries.ToList();
        if (list.Count == 0)
            return list;

        var defaultOrderingId = IOrdering.DefaultOrderingID(list[0].SeriesID).ID;
        return string.IsNullOrWhiteSpace(showPreferredOrderingId) || string.Equals(defaultOrderingId, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase)
            ? [.. list.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
            :
            [
                .. list.Select(te => (Episode: te, Priority: te.Orderings.Any(o => IsTmdbOrdering(o, showPreferredOrderingId)) ? 0 : 1))
                    .OrderBy(x => x.Priority)
                    .ThenBy(x => x.Episode.SeasonNumber ?? 0)
                    .ThenBy(x => x.Episode.EpisodeNumber)
                    .Select(x => x.Episode),
            ];
    }

    /// <summary>Convert a TMDB episode into season/episode coordinates.</summary>
    /// <param name="episode">The TMDB episode to inspect.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A tuple containing the resolved season and episode numbers.</returns>
    public static (int? Season, int Episode) GetOrderingCoords(IEpisode episode, string? showPreferredOrderingId = null) =>
        episode == null ? (null, 0)
        : !string.IsNullOrWhiteSpace(showPreferredOrderingId) && episode.Orderings.FirstOrDefault(o => IsTmdbOrdering(o, showPreferredOrderingId)) is { } byAll ? (byAll.SeasonNumber, byAll.EpisodeNumber)
        : (episode.SeasonNumber, episode.EpisodeNumber);

    /// <summary>Indicates whether an episode's place is in the given TMDB ordering.</summary>
    /// <param name="place">The episode's place in one of its show's orderings.</param>
    /// <param name="orderingId">The TMDB ordering identifier.</param>
    /// <returns>True if the place belongs to that TMDB ordering.</returns>
    private static bool IsTmdbOrdering(IEpisodeOrderingInformation place, string? orderingId) =>
        place.OrderingID.Source == MetadataSource.TMDB && string.Equals(place.OrderingID.ID, orderingId, StringComparison.OrdinalIgnoreCase);

    #endregion
}
