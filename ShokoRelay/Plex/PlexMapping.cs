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

        if (EnforceTmdbNumbering && e is IShokoEpisode shokoEpisode && shokoEpisode.GetLinkedEpisodes<ITmdbEpisode>(MetadataSource.TMDB) is { Count: > 0 } tmdbEps)
        {
            var tmdbEpisodes = string.IsNullOrWhiteSpace(showPrefId) ? [.. tmdbEps.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)] : SelectPreferredTmdbOrdering(tmdbEps, showPrefId);
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
                    return new PlexCoords
                    {
                        Season = season.Value,
                        Episode = episode,
                        EndEpisode = endEp,
                    };
                }
            }
        }

        // Resolve season coordinate, falling back to Plex extra season constants for non-standard episodes
        int seasonNum = e.Type switch
        {
            EpisodeType.Other => PlexConstants.SeasonOther,
            EpisodeType.Credits => PlexConstants.SeasonCredits,
            EpisodeType.Trailer => PlexConstants.SeasonTrailers,
            EpisodeType.Parody => PlexConstants.SeasonParody,
            _ => e.SeasonNumber ?? (e.Type == EpisodeType.Special ? PlexConstants.SeasonSpecials : PlexConstants.SeasonStandard),
        };

        return new PlexCoords { Season = seasonNum, Episode = e.EpisodeNumber };
    }

    /// <summary>Determine Plex coordinates for episodes sharing a file.</summary>
    /// <param name="episodes">Episode list.</param>
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
            var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().SelectMany(se => se.GetLinkedEpisodes<ITmdbEpisode>(MetadataSource.TMDB) ?? []).ToList();
            string? showPrefId = eps.OfType<IShokoEpisode>().Select(se => se.Series).FirstOrDefault()?.GetLinkedSeries<ITmdbShow>(MetadataSource.TMDB)?.FirstOrDefault()?.PreferredOrdering?.ID.ID;
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
    /// <param name="entries">The collection of TMDB episodes to filter.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A reordered and filtered list of TMDB episodes.</returns>
    public static List<ITmdbEpisode> SelectPreferredTmdbOrdering(IEnumerable<ITmdbEpisode>? entries, string? showPreferredOrderingId = null)
    {
        if (entries == null)
            return [];
        var list = entries.ToList();
        return list.Count == 0 ? list
            : string.IsNullOrWhiteSpace(showPreferredOrderingId) ? [.. list.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber)]
            :
            [
                .. list.Select(te =>
                        (
                            Episode: te,
                            Priority: string.Equals(te.TmdbOrderingID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase) ? 0
                            : te.TmdbOrderings?.Any(o => string.Equals(o.OrderingID.ID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase)) == true ? 1
                            : 2
                        )
                    )
                    .OrderBy(x => x.Priority)
                    .ThenBy(x => x.Episode.SeasonNumber ?? 0)
                    .ThenBy(x => x.Episode.EpisodeNumber)
                    .Select(x => x.Episode),
            ];
    }

    /// <summary>Convert a TMDB episode into season/episode coordinates.</summary>
    /// <param name="ep">The TMDB episode to inspect.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A tuple containing the resolved season and episode numbers.</returns>
    public static (int? Season, int Episode) GetOrderingCoords(ITmdbEpisode ep, string? showPreferredOrderingId = null) =>
        ep == null ? (null, 0)
        : !string.IsNullOrWhiteSpace(showPreferredOrderingId)
            ? ep.TmdbOrderings?.FirstOrDefault(o => string.Equals(o.OrderingID.ID, showPreferredOrderingId, StringComparison.OrdinalIgnoreCase)) is { } byAll ? (byAll.SeasonNumber, byAll.EpisodeNumber)
                : (ep.SeasonNumber, ep.EpisodeNumber)
        : (ep.SeasonNumber, ep.EpisodeNumber);

    #endregion
}
