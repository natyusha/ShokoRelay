using Shoko.Abstractions.Video;
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
    public record FileMapping(
        IVideo Video,
        IReadOnlyList<IEpisode> Episodes,
        IEpisode PrimaryEpisode,
        PlexCoords Coords,
        string FileName,
        int? PartIndex,
        int PartCount,
        IEpisode? TmdbEpisode,
        bool IsVariation
    );

    /// <summary>Aggregates file mapping information and available seasons for a series.</summary>
    /// <param name="Mappings">List of individual file mappings.</param>
    /// <param name="Seasons">List of unique season numbers.</param>
    public record SeriesFileData(List<FileMapping> Mappings, List<int> Seasons)
    {
        /// <summary>Retrieves mappings specific to a given season.</summary>
        /// <param name="season">The season number.</param>
        /// <returns>A filtered list of mappings.</returns>
        public List<FileMapping> GetForSeason(int season) => [.. Mappings.Where(mapping => mapping.Coords.Season == season).OrderBy(mapping => mapping.Coords.Episode)];
    }

    #endregion

    #region Public API

    /// <summary>Determines if standard and/or Movie VFS directories should be generated for a series based on the current configuration.</summary>
    /// <param name="isMovie">Whether the series is categorized as a movie.</param>
    /// <param name="mode">The configured movie generation mode.</param>
    /// <returns>A tuple indicating whether standard and Movie directories should be generated.</returns>
    public static (bool DoStandard, bool DoMovie) GetGenerationModes(bool isMovie, MovieGenerationMode mode) =>
        (mode == MovieGenerationMode.Disabled || !isMovie || mode == MovieGenerationMode.EnabledMaintain, isMovie && mode != MovieGenerationMode.Disabled);

    /// <summary>Generate SeriesFileData for the given series by building file mappings and seasons.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">Metadata service used for override resolution.</param>
    /// <returns>A SeriesFileData object.</returns>
    public static SeriesFileData GetSeriesFileData(ISeries series, IMetadataService metadataService)
    {
        var mappings = BuildFileMappings(series, metadataService);
        return new SeriesFileData(mappings, [.. mappings.Select(mapping => mapping.Coords.Season).Distinct().OrderBy(season => season)]);
    }

    /// <summary>Generate consolidated SeriesFileData for the given series, automatically handling TMDB enforcement and VFS override groupings.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">Metadata service used for override resolution.</param>
    /// <returns>A SeriesFileData object.</returns>
    public static SeriesFileData GetConsolidatedSeriesFileData(ISeries series, IMetadataService metadataService) =>
        series.GetOverrideGroup(metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList() is var group && group.Count > 1
            ? GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<ISeries>(), metadataService)
            : GetSeriesFileData(group.FirstOrDefault() ?? series, metadataService);

    /// <summary>Returns the TMDB ordering ID that should be used for episode numbering.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>Ordering ID or null.</returns>
    public static string? GetPreferredTmdbOrderingId(ISeries series) =>
        !EnforceTmdbNumbering ? null
        : (series as IShokoSeries ?? series.Episodes.OfType<IShokoEpisode>().FirstOrDefault()?.Series)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()
            is { PreferredOrdering: { IsDefault: false } preferred }
        && !string.IsNullOrWhiteSpace(preferred.ID.ID)
            ? preferred.ID.ID
        : null;

    /// <summary>Return merged file data for a primary series and any additional series in the group.</summary>
    /// <param name="primary">The primary series.</param>
    /// <param name="extras">Secondary series to merge.</param>
    /// <param name="metadataService">Metadata service used for override resolution.</param>
    /// <returns>A combined SeriesFileData object.</returns>
    public static SeriesFileData GetSeriesFileDataMerged(ISeries primary, IEnumerable<ISeries> extras, IMetadataService metadataService)
    {
        var all = BuildFileMappings(primary, metadataService);
        foreach (var extraSeries in extras ?? [])
            if (extraSeries != null)
                all.AddRange(BuildFileMappings(extraSeries, metadataService));

        // A single physical crossover file linked to multiple Shoko series in an override group will produce duplicate mappings. This ensures the VFS only builds the file once.
        var deduped = all.DistinctBy(mapping => (mapping.Video.LocalID, mapping.Coords, mapping.PartIndex, mapping.IsVariation)).ToList();
        return new SeriesFileData(deduped, [.. deduped.Select(mapping => mapping.Coords.Season).Distinct().OrderBy(season => season)]);
    }

    /// <summary>Indicates whether an episode should be treated as hidden.</summary>
    /// <param name="episode">The episode to check.</param>
    /// <returns>True if hidden.</returns>
    public static bool IsHidden(IEpisode episode) => episode.IsHidden;

    /// <summary>Indicates whether a series is considered a movie.</summary>
    /// <param name="series">The Shoko series to check.</param>
    /// <returns>True if the series is categorized as a movie.</returns>
    public static bool IsMovie(IShokoSeries series) => EnforceTmdbNumbering ? series.GetLinkedMovies(MetadataSource.TMDB) is { Count: > 0 } : series.Type == AnimeType.Movie;

    /// <summary>Resolves all unique active physical video files associated with a series, accounting for consolidated override groups and filtering out hidden entries.</summary>
    /// <param name="series">The primary series to resolve videos for.</param>
    /// <param name="metadataService">The metadata service used for override group and primary ID resolution.</param>
    /// <returns>An enumerable collection of active <see cref="IVideo"/> files.</returns>
    public static IEnumerable<IVideo> GetActiveVideos(ISeries series, IMetadataService metadataService)
    {
        var seriesList = series.GetOverrideGroup(metadataService).Select(metadataService.GetShokoSeriesByID).OfType<ISeries>();
        var seenVideoIds = new HashSet<int>();
        foreach (var seriesItem in seriesList)
        {
            foreach (var episode in seriesItem.Episodes)
            {
                if (IsHidden(episode) || episode.Videos == null)
                    continue;

                foreach (var video in episode.Videos)
                    if (!video.IsIgnored && seenVideoIds.Add(video.LocalID))
                        yield return video;
            }
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Builds a comprehensive list of file-to-episode mappings for a series, resolving Plex coordinates and handling file versioning.</summary>
    /// <param name="series">The Shoko series metadata containing episodes and video files.</param>
    /// <param name="metadataService">The metadata service used for override and primary series resolution.</param>
    /// <returns>A list of resolved and mapped <see cref="FileMapping"/> records.</returns>
    private static List<FileMapping> BuildFileMappings(ISeries series, IMetadataService metadataService)
    {
        var result = new List<FileMapping>();
        var seriesEpisodes = series
            .Episodes.Where(episode => episode.Videos.Count > 0 && !IsHidden(episode))
            .Select(episode => (Episode: episode, Videos: episode.Videos.Where(video => !video.IsIgnored).ToList()))
            .Where(tuple => tuple.Videos.Count > 0)
            .ToList();
        if (seriesEpisodes.Count == 0)
            return result;

        string? prefId = GetPreferredTmdbOrderingId(series);
        Dictionary<MetadataGuid, (int Season, int Episode)>? tmdbOrderingLookup = null;

        // If an alternate TMDB ordering is active, pre-index its placed episodes without touching episode.Orderings
        if (EnforceTmdbNumbering)
        {
            var tmdbSeries = (series as IShokoSeries)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault();
            if (tmdbSeries?.PreferredOrdering is { IsDefault: false } ordering && ordering.Series is { } orderedView)
                tmdbOrderingLookup = orderedView
                    .Episodes.DistinctBy(orderedEpisode => orderedEpisode.ID)
                    .ToDictionary(orderedEpisode => orderedEpisode.ID, orderedEpisode => (orderedEpisode.SeasonNumber ?? 1, orderedEpisode.EpisodeNumber));
        }

        var videoToEpisodes = new Dictionary<int, List<(IEpisode Episode, PlexCoords Coords)>>();
        var seriesToPrimary = new Dictionary<int, int>();

        foreach (var (episode, videos) in seriesEpisodes)
        {
            var coords = GetPlexCoordinates(episode, prefId, tmdbOrderingLookup, metadataService: metadataService);
            foreach (var video in videos)
            {
                if (!videoToEpisodes.TryGetValue(video.LocalID, out var list))
                    videoToEpisodes[video.LocalID] = list = [];
                list.Add((episode, coords));
            }
        }

        var distinctVideos = seriesEpisodes.SelectMany(tuple => tuple.Videos).DistinctBy(video => video.LocalID).ToList();
        var videoFileNameCache = distinctVideos.ToDictionary(video => video.LocalID, video => Path.GetFileName(video.Files.FirstOrDefault()?.Path ?? string.Empty));
        var episodeFileLists = seriesEpisodes.ToDictionary(tuple => tuple.Episode.ID, tuple => tuple.Videos.OrderBy(video => videoFileNameCache.GetValueOrDefault(video.LocalID, string.Empty)).ToList());
        var allVideos = distinctVideos.OrderBy(video => videoFileNameCache[video.LocalID]).ToList();

        // Pass 1: Resolve all coordinates and crossover statuses once
        var videoCoords = new Dictionary<int, (PlexCoords Coords, List<(IEpisode Episode, PlexCoords Coords)> Deduped)>();
        var seasonsSet = new HashSet<int>();

        foreach (var video in allVideos)
        {
            if (!videoToEpisodes.TryGetValue(video.LocalID, out var epList))
                continue;

            // Fast path: If the video has only 1 episode link, skip all crossover and deduplication checks
            if (epList.Count == 1)
            {
                videoCoords[video.LocalID] = (epList[0].Coords, epList);
                seasonsSet.Add(epList[0].Coords.Season);
                continue;
            }

            // Handle multi-type tiebreaks using cross-reference ordering
            var sortedEpisodes = epList.OrderBy(item => item.Coords.Season).ThenBy(item => item.Coords.Episode).ToList();

            if (sortedEpisodes.Select(item => item.Episode.Type).Distinct().Count() > 1)
            {
                var distinctPrimarySeriesCount = (video.CrossReferences ?? [])
                    .Where(cr => cr.ShokoEpisode != null)
                    .Select(cr =>
                    {
                        if (!seriesToPrimary.TryGetValue(cr.ShokoEpisode!.ShokoSeriesID, out var primaryId))
                            seriesToPrimary[cr.ShokoEpisode.ShokoSeriesID] = primaryId = OverrideHelper.GetPrimary(cr.ShokoEpisode.ShokoSeriesID, metadataService);
                        return primaryId;
                    })
                    .Distinct()
                    .Count();

                if (distinctPrimarySeriesCount > 1)
                {
                    var firstXrefId = (video.CrossReferences ?? []).FirstOrDefault(cr => cr.ShokoEpisode != null && sortedEpisodes.Any(item => item.Episode.ID == cr.ShokoEpisode.ID))?.ShokoEpisode?.ID;
                    if (firstXrefId is not null)
                    {
                        var primaryType = sortedEpisodes.First(item => item.Episode.ID == firstXrefId).Episode.Type;
                        sortedEpisodes = [.. sortedEpisodes.Where(item => item.Episode.Type == primaryType)];
                    }
                }
            }

            var deduped = DeduplicateByCoords(sortedEpisodes, video);
            if (deduped.Count == 0)
                continue;

            var coords =
                (deduped.Count > 1 && (episodeFileLists.GetValueOrDefault(deduped[0].Episode.ID)?.Count > 1 || deduped.Select(item => item.Episode.Type).Distinct().Count() == 1))
                    ? GetPlexCoordinatesForFile(deduped.Select(item => item.Episode), metadataService: metadataService)
                    : deduped[0].Coords;

            videoCoords[video.LocalID] = (coords, deduped);
            seasonsSet.Add(coords.Season);
        }

        // Apply fallback rules for "Other" (Featurette) season coordinates, remapping them to Standard or Specials folders if those are empty
        var (s1, s0) = (seasonsSet.Contains(PlexConstants.SeasonStandard), seasonsSet.Contains(PlexConstants.SeasonSpecials));

        foreach (var key in videoCoords.Keys.ToList())
        {
            var (coords, deduped) = videoCoords[key];
            if (coords.Season == PlexConstants.SeasonOther)
            {
                coords.Season =
                    !s1 ? PlexConstants.SeasonStandard
                    : !s0 ? PlexConstants.SeasonSpecials
                    : coords.Season;
                videoCoords[key] = (coords, deduped);
            }
        }

        // Fallback for extra-only series: if no standard or special season exists, promote Credits, Trailers, or Parodies (in priority order) to Season 1 so Plex can index the series
        if (!videoCoords.Values.Any(vid => vid.Coords.Season >= 0))
        {
            int? promoSeason =
                videoCoords.Values.Any(vid => vid.Coords.Season == PlexConstants.SeasonCredits) ? PlexConstants.SeasonCredits
                : videoCoords.Values.Any(vid => vid.Coords.Season == PlexConstants.SeasonTrailers) ? PlexConstants.SeasonTrailers
                : videoCoords.Values.Any(vid => vid.Coords.Season == PlexConstants.SeasonParody) ? PlexConstants.SeasonParody
                : null;

            if (promoSeason.HasValue)
                foreach (var key in videoCoords.Keys.ToList())
                {
                    var (coords, deduped) = videoCoords[key];
                    if (coords.Season == promoSeason.Value)
                    {
                        coords.Season = PlexConstants.SeasonStandard;
                        videoCoords[key] = (coords, deduped);
                    }
                }
        }

        // Pass 2: Build final mappings
        var tmdbEpsCache = new Dictionary<int, List<IEpisode>>();
        foreach (var video in allVideos)
        {
            if (!videoCoords.TryGetValue(video.LocalID, out var info))
                continue;

            var (coords, deduped) = info;
            var firstEpisode = deduped[0].Episode;
            var fileList = episodeFileLists.GetValueOrDefault(firstEpisode.ID);
            var (fIdx, fCount) = (fileList?.FindIndex(item => item.LocalID == video.LocalID) ?? 0, fileList?.Count ?? 1);
            string fileName = Path.GetFileName(video.Files?.FirstOrDefault()?.Path ?? "");
            bool allowPt = fCount > 1 && VfsHelper.HasPlexSplitTag(fileName) && deduped.Select(entry => entry.Episode.Type).Distinct().Count() <= 1;

            // TMDB Episode metadata override for multi-part files
            IEpisode? tmdbEpisode = null;
            if (allowPt && EnforceTmdbNumbering && firstEpisode is IShokoEpisode se && !OverrideHelper.HasCustomCoords(se, metadataService: metadataService))
            {
                if (!tmdbEpsCache.TryGetValue(se.LocalID, out var tmdbEps))
                {
                    var rawEps = se.GetLinkedEpisodes(MetadataSource.TMDB);
                    tmdbEpsCache[se.LocalID] = tmdbEps = rawEps is { Count: > 0 } ? SelectPreferredTmdbOrdering(rawEps, prefId) : [];
                }
                tmdbEpisode = tmdbEps.ElementAtOrDefault(fIdx);
            }
            result.Add(new FileMapping(video, [.. deduped.Select(item => item.Episode)], firstEpisode, coords, fileName, allowPt ? fIdx + 1 : null, allowPt ? fCount : 1, tmdbEpisode, video.IsVariation));
        }
        // Deduplicate mappings by Video ID and Coordinates. This prevents duplicate VFS entries (v1/v2) for crossover series that have been consolidated into a single folder via VFS Overrides.
        return [.. result.DistinctBy(mapping => (mapping.Video.LocalID, mapping.Coords, mapping.PartIndex, mapping.IsVariation))];
    }

    /// <summary>Deduplicates a collection of episode-coordinate pairs sharing a single video file, prioritizing primary cross-referenced entries.</summary>
    /// <param name="eps">The raw list of episode-coordinate pairs associated with the video file.</param>
    /// <param name="video">The video file metadata used to evaluate cross-reference priorities.</param>
    /// <returns>A prioritized list of unique episode-coordinate pairs.</returns>
    private static List<(IEpisode Episode, PlexCoords Coords)> DeduplicateByCoords(List<(IEpisode Episode, PlexCoords Coords)> eps, IVideo video)
    {
        if (eps.Count <= 1)
            return eps;

        var deduped = new List<(IEpisode Episode, PlexCoords Coords)>();
        var index = new Dictionary<(int, int), int>();
        var xrefPos = video.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select((xref, index) => (id: xref.ShokoEpisode!.ID, i: index)).ToDictionary(pair => pair.id, pair => pair.i);
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
            int exPos = xrefPos.GetValueOrDefault(deduped[existingIdx].Episode.ID, -1);
            int enPos = xrefPos.GetValueOrDefault(entry.Episode.ID, -1);
            if (enPos >= 0 && (exPos == -1 || enPos < exPos))
                deduped[existingIdx] = entry;
        }
        // Reorder list so the first cross-referenced episode is the Primary
        if (xrefPos != null && deduped.Count > 1)
        {
            var best = deduped.Select((dedupedItem, index) => (dedupedItem, index, pos: xrefPos.GetValueOrDefault(dedupedItem.Episode.ID, int.MaxValue))).OrderBy(entry => entry.pos).First();
            if (best.index > 0)
            {
                deduped.RemoveAt(best.index);
                deduped.Insert(0, best.dedupedItem);
            }
        }
        return deduped;
    }

    #endregion
}
