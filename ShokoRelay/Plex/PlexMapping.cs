namespace ShokoRelay.Plex;

/// <summary>Maps Shoko episode/season data to Plex-style coordinates.</summary>
public static class PlexMapping
{
    #region Data Models

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

    /// <summary>Calculate Plex coordinates for an episode, utilizing an optional pre-indexed TMDB ordering lookup table.</summary>
    /// <param name="episode">The episode metadata.</param>
    /// <param name="seriesPreferredOrderingId">Optional TMDB ordering ID.</param>
    /// <param name="tmdbOrderingLookup">Optional pre-indexed lookup mapping TMDB episode IDs to coordinates.</param>
    /// <param name="metadataService">Optional metadata service used to verify manual override primary series presence.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinates(
        IEpisode episode,
        string? seriesPreferredOrderingId = null,
        Dictionary<MetadataGuid, (int Season, int Episode)>? tmdbOrderingLookup = null,
        IMetadataService? metadataService = null
    )
    {
        if (episode == null)
            return new PlexCoords { Season = PlexConstants.SeasonStandard, Episode = 1 };

        // Fast path: Check for explicit manual VFS coordinate overrides (e.g. placing movies as specials)
        if (episode is IShokoEpisode { Series: { AnidbAnimeID: > 0 } shokoSeries })
        {
            var custom = OverrideHelper.GetCustomCoords(shokoSeries.AnidbAnimeID, episode.Type, episode.EpisodeNumber, metadataService);
            if (custom.HasValue)
                return new PlexCoords { Season = custom.Value.Season, Episode = custom.Value.Episode };
        }

        if (EnforceTmdbNumbering && episode is IShokoEpisode shokoEpisode && shokoEpisode.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 0 } tmdbEps)
        {
            // Fast path: resolve coordinates from the pre-indexed alternate ordering table
            if (tmdbOrderingLookup != null)
            {
                var ordered = tmdbEps
                    .Select(tmdbEpisode => tmdbOrderingLookup.TryGetValue(tmdbEpisode.ID, out var coords) ? (Found: true, Coords: coords) : default)
                    .Where(item => item.Found)
                    .OrderBy(item => item.Coords.Season)
                    .ThenBy(item => item.Coords.Episode)
                    .ToList();

                if (ordered.Count > 0)
                {
                    var (firstSeason, firstEpisode) = ordered[0].Coords;
                    var (lastSeason, lastEpisode) = ordered[^1].Coords;
                    int? endEpisode = ordered.Count > 1 && lastSeason == firstSeason ? lastEpisode : null;
                    return new PlexCoords
                    {
                        Season = firstSeason,
                        Episode = firstEpisode,
                        EndEpisode = endEpisode,
                    };
                }
            }

            var tmdbEpisodes = string.IsNullOrWhiteSpace(seriesPreferredOrderingId)
                ? [.. tmdbEps.OrderBy(tmdbEpisode => tmdbEpisode.SeasonNumber ?? 0).ThenBy(tmdbEpisode => tmdbEpisode.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(tmdbEps, seriesPreferredOrderingId);

            if (tmdbEpisodes.Count > 0)
            {
                var (season, episodeNum) = GetOrderingCoords(tmdbEpisodes[0], seriesPreferredOrderingId);
                if (season.HasValue)
                    return new PlexCoords
                    {
                        Season = season.Value,
                        Episode = episodeNum,
                        EndEpisode = tmdbEpisodes.Count > 1 && GetOrderingCoords(tmdbEpisodes[^1], seriesPreferredOrderingId) is var (lastSeason, lastEpisode) && lastSeason == season ? lastEpisode : null,
                    };
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
    /// <param name="metadataService">Optional metadata service used to verify manual override primary series presence.</param>
    /// <returns>Resolved coordinates.</returns>
    public static PlexCoords GetPlexCoordinatesForFile(IEnumerable<IEpisode> episodes, IMetadataService? metadataService = null)
    {
        var eps = (episodes ?? []).ToList();
        if (eps.Count == 0)
            return new PlexCoords
            {
                Season = 1,
                Episode = 1,
                EndEpisode = null,
            };

        string? showPrefId = eps.OfType<IShokoEpisode>().Select(shokoEpisode => shokoEpisode.Series).FirstOrDefault() is { } series ? MapHelper.GetPreferredTmdbOrderingId(series) : null;
        bool hasCustomOverride = eps.Any(episode =>
            episode is IShokoEpisode { Series: { AnidbAnimeID: > 0 } shokoSeries } && OverrideHelper.GetCustomCoords(shokoSeries.AnidbAnimeID, episode.Type, episode.EpisodeNumber, metadataService).HasValue
        );

        if (!hasCustomOverride && EnforceTmdbNumbering && eps.Select(episode => episode.Type).Distinct().Count() == 1)
        {
            var tmdbEntriesRaw = eps.OfType<IShokoEpisode>().SelectMany(shokoEpisode => shokoEpisode.GetLinkedEpisodes(MetadataSource.TMDB) ?? []).ToList();
            var tmdbEntries = string.IsNullOrWhiteSpace(showPrefId)
                ? [.. tmdbEntriesRaw.OrderBy(tmdbEpisode => tmdbEpisode.SeasonNumber ?? 0).ThenBy(tmdbEpisode => tmdbEpisode.EpisodeNumber)]
                : SelectPreferredTmdbOrdering(tmdbEntriesRaw, showPrefId);

            if (tmdbEntries.Count > 0)
            {
                var (season, episode) = GetOrderingCoords(tmdbEntries[0], showPrefId);
                if (season.HasValue)
                {
                    var (lastSeason, lastEpisode) = GetOrderingCoords(tmdbEntries[^1], showPrefId);
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
            return GetPlexCoordinates(eps[0], showPrefId, metadataService: metadataService);
        var start = GetPlexCoordinates(eps[0], showPrefId, metadataService: metadataService);
        var end = GetPlexCoordinates(eps[^1], showPrefId, metadataService: metadataService);
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
        return list.Count <= 1 ? list
            : string.IsNullOrWhiteSpace(showPreferredOrderingId) ? [.. list.OrderBy(tmdbEpisode => tmdbEpisode.SeasonNumber ?? 0).ThenBy(tmdbEpisode => tmdbEpisode.EpisodeNumber)]
            :
            [
                .. list.Select(tmdbEpisode =>
                    {
                        bool isPref = tmdbEpisode.PreferredOrdering is { } pref && IsTmdbOrdering(pref, showPreferredOrderingId);
                        return (
                            Episode: tmdbEpisode,
                            Priority: isPref ? 0 : 1,
                            Season: isPref ? tmdbEpisode.PreferredOrdering!.SeasonNumber ?? 0 : tmdbEpisode.SeasonNumber ?? 0,
                            Number: isPref ? tmdbEpisode.PreferredOrdering!.EpisodeNumber : tmdbEpisode.EpisodeNumber
                        );
                    })
                    .OrderBy(entry => entry.Priority)
                    .ThenBy(entry => entry.Season)
                    .ThenBy(entry => entry.Number)
                    .Select(entry => entry.Episode),
            ];
    }

    /// <summary>Convert a TMDB episode into season/episode coordinates.</summary>
    /// <param name="episode">The TMDB episode to inspect.</param>
    /// <param name="showPreferredOrderingId">The preferred TMDB ordering identifier.</param>
    /// <returns>A tuple containing the resolved season and episode numbers.</returns>
    public static (int? Season, int Episode) GetOrderingCoords(IEpisode episode, string? showPreferredOrderingId = null) =>
        episode == null ? (null, 0)
        : !string.IsNullOrWhiteSpace(showPreferredOrderingId) && episode.PreferredOrdering is { } pref && IsTmdbOrdering(pref, showPreferredOrderingId) ? (pref.SeasonNumber, pref.EpisodeNumber)
        : (episode.SeasonNumber, episode.EpisodeNumber);

    /// <summary>Indicates whether an episode's place is in the given TMDB ordering.</summary>
    /// <param name="place">The episode's place in one of its show's orderings.</param>
    /// <param name="orderingId">The TMDB ordering identifier.</param>
    /// <returns>True if the place belongs to that TMDB ordering.</returns>
    private static bool IsTmdbOrdering(IEpisodeOrderingInformation place, string? orderingId) =>
        place.OrderingID.Source == MetadataSource.TMDB && string.Equals(place.OrderingID.ID, orderingId, StringComparison.OrdinalIgnoreCase);

    #endregion
}
