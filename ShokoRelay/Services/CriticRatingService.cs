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
    #region Setup

    /// <summary>Semaphore serializing critic rating application operations across manual, scheduled, and debounced fixup triggers.</summary>
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    #endregion

    #region Public API

    /// <summary>Compute and push ratings for shows and episodes, optionally restricted to a subset of series IDs.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>An <see cref="ApplyRatingsResult"/> with counters and error details.</returns>
    public async Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default, IProgress<decimal>? progress = null)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexRatingsApply;
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool startedTask = TaskHelper.StartTask(TaskName);
        logger.LogInformation("CriticRatingService: Starting task...");
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(0);
        try
        {
            var errorsList = new List<string>();
            var appliedChanges = new List<RatingChange>();
            var preferredOrderingIdCache = new Dictionary<int, string?>();
            var counters = new RatingCounters();

            if (!plexClient.IsEnabled)
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges, stopwatch.Elapsed);

            var allowedSet = allowedSeriesIds != null ? new HashSet<int>(allowedSeriesIds.Select(id => OverrideHelper.GetPrimary(id, metadataService))) : null;
            if (allowedSet is { Count: 0 })
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges, stopwatch.Elapsed);

            var targets = plexClient.GetConfiguredTargets();
            var targetStages = progress.InStages([.. Enumerable.Repeat(1m, Math.Max(1, targets.Count))]);
            targetStages.Report(0);

            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                var target = targets[targetIndex];
                if (allowedSet != null)
                {
                    // Targeted fast-path: resolve rating keys once per series before fetching show and leaf items
                    var ratingKeys = new List<int>();
                    foreach (int seriesId in allowedSet)
                        ratingKeys.AddRange(await plexClient.FindRatingKeysForShokoSeriesInSectionAsync(seriesId, target, metadataService, cancellationToken).ConfigureAwait(false));

                    var itemProgress = targetStages.Throttle().ForItems(Math.Max(1, ratingKeys.Count));
                    itemProgress.Report(0);

                    if (target.LibraryType == PlexLibraryType.Movie)
                    {
                        foreach (int ratingKey in ratingKeys)
                        {
                            try
                            {
                                foreach (
                                    var movieItem in await plexClient
                                        .GetMetadataItemsAsync(target, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                    await ProcessEpisodeOrMovieRatingAsync(movieItem, target, isMovie: true, allowedSet, preferredOrderingIdCache, counters, appliedChanges, errorsList, cancellationToken)
                                        .ConfigureAwait(false);
                            }
                            finally
                            {
                                itemProgress.Increment();
                            }
                        }
                    }
                    else
                    {
                        foreach (int ratingKey in ratingKeys)
                        {
                            try
                            {
                                foreach (
                                    var showItem in await plexClient
                                        .GetMetadataItemsAsync(target, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                    await ProcessShowRatingAsync(showItem, target, allowedSet, counters, appliedChanges, errorsList, cancellationToken).ConfigureAwait(false);

                                foreach (
                                    var episodeItem in await plexClient
                                        .GetMetadataItemsAsync(target, $"/library/metadata/{ratingKey}/allLeaves?X-Plex-Container-Start=0&X-Plex-Container-Size=5000", cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                    await ProcessEpisodeOrMovieRatingAsync(episodeItem, target, isMovie: false, allowedSet, preferredOrderingIdCache, counters, appliedChanges, errorsList, cancellationToken)
                                        .ConfigureAwait(false);
                            }
                            finally
                            {
                                itemProgress.Increment();
                            }
                        }
                    }
                }
                else if (target.LibraryType != PlexLibraryType.Movie)
                {
                    var showItems = await plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
                    var episodeItems = await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [];
                    var itemProgress = targetStages.Throttle().ForItems(Math.Max(1, showItems.Count + episodeItems.Count));
                    itemProgress.Report(0);

                    foreach (var showItem in showItems)
                    {
                        try
                        {
                            await ProcessShowRatingAsync(showItem, target, allowedSet, counters, appliedChanges, errorsList, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            itemProgress.Increment();
                        }
                    }

                    foreach (var episodeItem in episodeItems)
                    {
                        try
                        {
                            await ProcessEpisodeOrMovieRatingAsync(episodeItem, target, isMovie: false, allowedSet, preferredOrderingIdCache, counters, appliedChanges, errorsList, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            itemProgress.Increment();
                        }
                    }
                }
                else
                {
                    var movieItems = await plexClient.GetSectionMoviesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [];
                    var itemProgress = targetStages.Throttle().ForItems(Math.Max(1, movieItems.Count));
                    itemProgress.Report(0);

                    foreach (var movieItem in movieItems)
                    {
                        try
                        {
                            await ProcessEpisodeOrMovieRatingAsync(movieItem, target, isMovie: true, allowedSet, preferredOrderingIdCache, counters, appliedChanges, errorsList, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            itemProgress.Increment();
                        }
                    }
                }

                targetStages.NextStage();
            }

            stopwatch.Stop();
            targetStages.Complete();
            logger.LogInformation(
                "CriticRatingService: Task finished -> Updated {SeriesCount} series and {EpisodeCount} episodes in {Elapsed}ms",
                counters.UpdatedShows,
                counters.UpdatedEpisodes,
                stopwatch.ElapsedMilliseconds
            );
            return new ApplyRatingsResult(counters.ProcessedShows, counters.UpdatedShows, counters.ProcessedEpisodes, counters.UpdatedEpisodes, counters.Errors, errorsList, appliedChanges, stopwatch.Elapsed);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(TaskName);
            _applyLock.Release();
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Mutable counters aggregated during a single <see cref="ApplyRatingsAsync"/> execution.</summary>
    private sealed class RatingCounters
    {
        /// <summary>Total shows evaluated.</summary>
        public int ProcessedShows;

        /// <summary>Total shows whose rating was updated in Plex.</summary>
        public int UpdatedShows;

        /// <summary>Total episodes or movies evaluated.</summary>
        public int ProcessedEpisodes;

        /// <summary>Total episodes or movies whose rating was updated in Plex.</summary>
        public int UpdatedEpisodes;

        /// <summary>Total rating lookup or push errors encountered.</summary>
        public int Errors;
    }

    /// <summary>Evaluates and pushes a critic rating update for a single Plex show item.</summary>
    /// <param name="item">The Plex show metadata item.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="allowedSet">Optional set of allowed primary Shoko series IDs.</param>
    /// <param name="counters">Shared execution counters.</param>
    /// <param name="appliedChanges">Collection recording applied rating changes.</param>
    /// <param name="errorsList">Collection recording error messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task ProcessShowRatingAsync(
        PlexMetadataItem item,
        PlexLibraryTarget target,
        HashSet<int>? allowedSet,
        RatingCounters counters,
        List<RatingChange> appliedChanges,
        List<string> errorsList,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(item.Guid))
            return;

        var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
        if (!shokoId.HasValue || (allowedSet != null && !allowedSet.Contains(shokoId.Value)))
            return;

        counters.ProcessedShows++;
        var series = metadataService.GetShokoSeriesByID(shokoId.Value);
        if (series == null)
        {
            counters.Errors++;
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

        if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken).ConfigureAwait(false))
        {
            counters.UpdatedShows++;
            appliedChanges.Add(new RatingChange($"{series.GetDisplayTitle() ?? "Unknown"} [{series.LocalID}]", "Series", item.RatingKey!, item.Rating, rating));
            logger.LogInformation("CriticRatingService: Updated series -> {Title} [{LocalId}] to {Rating}", series.GetDisplayTitle(), series.LocalID, rating?.ToString("F2") ?? "n/a");
        }
        else
        {
            counters.Errors++;
            errorsList.Add($"CriticRatingService: Failed update for series -> {series.GetDisplayTitle()} [{shokoId.Value}]");
        }
    }

    /// <summary>Evaluates and pushes a critic rating update for a single Plex episode or standalone movie item.</summary>
    /// <param name="item">The Plex episode or movie metadata item.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="isMovie">Whether the target library is a standalone movie library.</param>
    /// <param name="allowedSet">Optional set of allowed primary Shoko series IDs.</param>
    /// <param name="preferredOrderingIdCache">Cache mapping Shoko series IDs to their preferred TMDB ordering IDs.</param>
    /// <param name="counters">Shared execution counters.</param>
    /// <param name="appliedChanges">Collection recording applied rating changes.</param>
    /// <param name="errorsList">Collection recording error messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task ProcessEpisodeOrMovieRatingAsync(
        PlexMetadataItem item,
        PlexLibraryTarget target,
        bool isMovie,
        HashSet<int>? allowedSet,
        Dictionary<int, string?> preferredOrderingIdCache,
        RatingCounters counters,
        List<RatingChange> appliedChanges,
        List<string> errorsList,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(item.Guid))
            return;

        var episodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
        if (!episodeId.HasValue)
            return;

        var episode = metadataService.GetShokoEpisodeByID(episodeId.Value);
        if (episode == null || (allowedSet != null && !allowedSet.Contains(OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService))))
            return;

        counters.ProcessedEpisodes++;
        double? rating = isMovie
            ? Settings.CriticRatingMode switch
            {
                CriticRatingMode.TMDB => (episode.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? episode.Series?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault()) is { Rating: > 0 } tmdbMovie
                    ? tmdbMovie.Rating
                    : (
                        episode.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbEpisode
                            ? tmdbEpisode.Rating
                            : (episode.Series?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbSeries ? tmdbSeries.Rating : null)
                    ),
                CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating : (episode.Series?.Rating > 0 ? episode.Series.Rating : null),
                _ => null,
            }
            : Settings.CriticRatingMode switch
            {
                CriticRatingMode.TMDB => episode.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault() is { Rating: > 0 } tmdbEpisode ? tmdbEpisode.Rating : null,
                CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating : null,
                _ => null,
            };

        string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;
        var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
        string kindLabel = isMovie ? "movie" : "episode";
        var episodeLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2} (RatingKey: {item.RatingKey})";

        if (!NeedsRatingUpdate(item.Rating, rating))
        {
            logger.LogTrace("CriticRatingService: Skipped {Kind} -> {LogName} because rating {Rating} matches Plex", kindLabel, episodeLogName, item.Rating?.ToString("F2") ?? "n/a");
            return;
        }

        if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken).ConfigureAwait(false))
        {
            counters.UpdatedEpisodes++;
            appliedChanges.Add(
                new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", isMovie ? "Movie" : "Episode", item.RatingKey!, item.Rating, rating)
            );
            logger.LogTrace("CriticRatingService: Updated {Kind} -> {LogName} to {Rating}", kindLabel, episodeLogName, rating?.ToString("F2") ?? "n/a");
        }
        else
        {
            counters.Errors++;
            errorsList.Add($"CriticRatingService: Failed update for {kindLabel} -> {episodeLogName}");
        }
    }

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
