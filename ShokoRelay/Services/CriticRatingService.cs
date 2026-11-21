using System.Diagnostics;
using System.Globalization;

namespace ShokoRelay.Services;

#region Data Models

/// <summary>Represents a specific rating update for the report log.</summary>
/// <param name="Title">Display title of the item.</param>
/// <param name="Type">Type of item (Show or Episode).</param>
/// <param name="RatingKey">Plex rating key.</param>
/// <param name="OldRating">Previous rating in Plex.</param>
/// <param name="NewRating">New rating applied from Shoko.</param>
public sealed record RatingChange(string Title, string Type, string RatingKey, double? OldRating, double? NewRating);

/// <summary>Aggregated status returned by <see cref="CriticRatingService.ApplyRatingsAsync"/>.</summary>
/// <param name="ProcessedShows">Total shows processed.</param>
/// <param name="UpdatedShows">Count of shows where ratings changed.</param>
/// <param name="ProcessedEpisodes">Total episodes processed.</param>
/// <param name="UpdatedEpisodes">Count of episodes where ratings changed.</param>
/// <param name="Errors">Count of encountered errors.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
/// <param name="AppliedChanges">List of detailed rating changes.</param>
/// <param name="TotalElapsed">The total time elapsed during the task.</param>
public sealed record ApplyRatingsResult(
    int ProcessedShows,
    int UpdatedShows,
    int ProcessedEpisodes,
    int UpdatedEpisodes,
    int Errors,
    List<string> ErrorsList,
    List<RatingChange> AppliedChanges,
    TimeSpan TotalElapsed
);

#endregion

/// <summary>Service for applying critic ratings from Shoko metadata to Plex libraries.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="logger">Logger instance.</param>
public class CriticRatingService(PlexClient plexClient, IMetadataService metadataService, ILogger<CriticRatingService> logger)
{
    #region Public API

    /// <summary>Compute and push ratings for shows and episodes, optionally restricted to a subset of series IDs.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="ApplyRatingsResult"/> with counters and error details.</returns>
    public async Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexRatingsApply;
        bool startedTask = TaskHelper.StartTask(TaskName);
        logger.LogInformation("CriticRatingService: Starting task...");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (processedShows, updatedShows, processedEpisodes, updatedEpisodes, errors) = (0, 0, 0, 0, 0);
            var errorsList = new List<string>();
            var appliedChanges = new List<RatingChange>();
            var prefIdCache = new Dictionary<int, string?>();

            if (!plexClient.IsEnabled)
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges, stopwatch.Elapsed);

            var allowedSet = allowedSeriesIds != null ? new HashSet<int>(allowedSeriesIds.Select(id => OverrideHelper.GetPrimary(id, metadataService))) : null;
            if (allowedSet is { Count: 0 })
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges, stopwatch.Elapsed);

            async Task ProcessShowRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                if (!shokoId.HasValue || (allowedSet != null && !allowedSet.Contains(shokoId.Value)))
                    return;

                processedShows++;
                var series = metadataService.GetShokoSeriesByID(shokoId.Value);
                if (series == null)
                {
                    errors++;
                    errorsList.Add($"Series {shokoId.Value} not found for RatingKey {item.RatingKey}");
                    return;
                }

                // Resolve the critic rating for a series based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => series.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbSeries ? tmdbSeries.Rating : null,
                    CriticRatingMode.AniDB => series.Rating > 0 ? series.Rating : null,
                    _ => null,
                };

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    logger.LogTrace(
                        "CriticRatingService: Skipped series -> {Title} [{LocalId}] (RatingKey: {RatingKey}) because rating {Rating} matches Plex",
                        series.GetDisplayTitle(),
                        series.LocalID,
                        item.RatingKey,
                        item.Rating?.ToString("F2") ?? "n/a"
                    );
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    updatedShows++;
                    appliedChanges.Add(new RatingChange($"{series.GetDisplayTitle() ?? "Unknown"} [{series.LocalID}]", "Series", item.RatingKey!, item.Rating, rating));
                    logger.LogInformation("CriticRatingService: Updated series -> {Title} [{LocalId}] to {Rating}", series.GetDisplayTitle(), series.LocalID, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errors++;
                    errorsList.Add($"CriticRatingService: Failed update for series -> {series.GetDisplayTitle()} [{shokoId.Value}]");
                }
            }

            async Task ProcessEpisodeRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var episodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                if (!episodeId.HasValue)
                    return;

                var episode = metadataService.GetShokoEpisodeByID(episodeId.Value);
                if (episode == null || (allowedSet != null && !allowedSet.Contains(OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService))))
                    return;

                processedEpisodes++;
                // Resolve the critic rating for an episode based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => episode.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbEpisode ? tmdbEpisode.Rating : null,
                    CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating : null,
                    _ => null,
                };

                string? preferredOrderingId = null;
                if (episode.Series != null && !prefIdCache.TryGetValue(episode.ShokoSeriesID, out preferredOrderingId))
                    prefIdCache[episode.ShokoSeriesID] = preferredOrderingId = MapHelper.GetPreferredTmdbOrderingId(episode.Series);

                var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                var episodeLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2} (RatingKey: {item.RatingKey})";

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    logger.LogTrace("CriticRatingService: Skipped episode -> {LogName} because rating {Rating} matches Plex", episodeLogName, item.Rating?.ToString("F2") ?? "n/a");
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    updatedEpisodes++;
                    appliedChanges.Add(
                        new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", "Episode", item.RatingKey!, item.Rating, rating)
                    );
                    logger.LogTrace("CriticRatingService: Updated episode -> {LogName} to {Rating}", episodeLogName, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errors++;
                    errorsList.Add($"CriticRatingService: Failed update for episode -> {episodeLogName}");
                }
            }

            async Task ProcessMovieRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var episodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                if (!episodeId.HasValue)
                    return;

                var episode = metadataService.GetShokoEpisodeByID(episodeId.Value);
                if (episode == null || (allowedSet != null && !allowedSet.Contains(OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService))))
                    return;

                processedEpisodes++;
                // Resolve the critic rating for a movie based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => (episode.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? episode.Series?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault())
                        is { Rating: > 0 } tmdbMovie
                        ? tmdbMovie.Rating
                        : (episode.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbEpisode ? tmdbEpisode.Rating : null),
                    CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating : (episode.Series?.Rating > 0 ? episode.Series.Rating : null),
                    _ => null,
                };

                string? preferredOrderingId = null;
                if (episode.Series != null && !prefIdCache.TryGetValue(episode.ShokoSeriesID, out preferredOrderingId))
                    prefIdCache[episode.ShokoSeriesID] = preferredOrderingId = MapHelper.GetPreferredTmdbOrderingId(episode.Series);

                var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                var episodeLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2} (RatingKey: {item.RatingKey})";

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    logger.LogTrace("CriticRatingService: Skipped movie -> {LogName} because rating {Rating} matches Plex", episodeLogName, item.Rating?.ToString("F2") ?? "n/a");
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    updatedEpisodes++;
                    appliedChanges.Add(
                        new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", "Movie", item.RatingKey!, item.Rating, rating)
                    );
                    logger.LogTrace("CriticRatingService: Updated movie -> {LogName} to {Rating}", episodeLogName, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errors++;
                    errorsList.Add($"CriticRatingService: Failed update for movie -> {episodeLogName}");
                }
            }

            foreach (var target in plexClient.GetConfiguredTargets())
            {
                if (allowedSet != null)
                {
                    // Targeted fast-path: only query rating keys for the specific allowed series
                    foreach (var seriesId in allowedSet)
                    {
                        var ratingKeys = await plexClient.FindRatingKeysForShokoSeriesInSectionAsync(seriesId, target, metadataService, cancellationToken).ConfigureAwait(false);
                        foreach (var ratingKey in ratingKeys)
                        {
                            if (target.LibraryType == PlexLibraryType.Movie)
                            {
                                using var request = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", target.ServerUrl);
                                using var response = await plexClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                                if ((await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault() is { } movieItem)
                                    await ProcessMovieRating(movieItem, target).ConfigureAwait(false);
                            }
                            else
                            {
                                using var showReq = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", target.ServerUrl);
                                using var showResp = await plexClient.SendAsync(showReq, cancellationToken).ConfigureAwait(false);
                                if ((await PlexApi.ReadContainerAsync(showResp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault() is { } showItem)
                                    await ProcessShowRating(showItem, target).ConfigureAwait(false);

                                using var epReq = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}/allLeaves?X-Plex-Container-Start=0&X-Plex-Container-Size=5000", target.ServerUrl);
                                using var epResp = await plexClient.SendAsync(epReq, cancellationToken).ConfigureAwait(false);
                                foreach (var epItem in (await PlexApi.ReadContainerAsync(epResp, cancellationToken).ConfigureAwait(false))?.Metadata ?? [])
                                    await ProcessEpisodeRating(epItem, target).ConfigureAwait(false);
                            }
                        }
                    }
                }
                else
                {
                    // Bulk path: query all items in library section
                    if (target.LibraryType != PlexLibraryType.Movie)
                    {
                        foreach (var item in await plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? [])
                            await ProcessShowRating(item, target).ConfigureAwait(false);

                        foreach (var item in await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [])
                            await ProcessEpisodeRating(item, target).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var item in await plexClient.GetSectionMoviesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [])
                            await ProcessMovieRating(item, target).ConfigureAwait(false);
                    }
                }
            }
            stopwatch.Stop();
            logger.LogInformation("CriticRatingService: Task finished -> Updated {SeriesCount} series and {EpisodeCount} episodes in {Elapsed}ms", updatedShows, updatedEpisodes, stopwatch.ElapsedMilliseconds);
            return new ApplyRatingsResult(processedShows, updatedShows, processedEpisodes, updatedEpisodes, errors, errorsList, appliedChanges, stopwatch.Elapsed);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Pushes a rating value to a specific Plex metadata item.</summary>
    /// <param name="key">The Plex rating key.</param>
    /// <param name="value">The rating value to apply.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the rating was successfully applied.</returns>
    private async Task<bool> ApplyRatingAsync(string key, double? value, PlexLibraryTarget target, CancellationToken cancellationToken)
    {
        string path =
            (value == null || Settings.CriticRatingMode == CriticRatingMode.None)
                ? $"/library/metadata/{key}?rating=0&rating.locked=0"
                : $"/library/metadata/{key}?rating={value.Value.ToString(CultureInfo.InvariantCulture)}&rating.locked=1";

        try
        {
            using var request = plexClient.CreateRequest(HttpMethod.Put, path, target.ServerUrl);
            using var response = await plexClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Determines if a rating update is needed by comparing the current Plex value to the target Shoko value.</summary>
    /// <param name="plex">The current Plex rating.</param>
    /// <param name="shoko">The target Shoko rating.</param>
    /// <returns>True if the rating differs significantly.</returns>
    private static bool NeedsRatingUpdate(double? plex, double? shoko) => shoko.HasValue ? (!plex.HasValue || Math.Abs(plex.Value - shoko.Value) > 0.05) : (plex.HasValue && plex.Value > 0.05);

    #endregion
}
