using System.Diagnostics;
using System.Globalization;

namespace ShokoRelay.Services;

#region Interface and Models

/// <summary>Service for applying critic ratings from Shoko metadata to Plex libraries.</summary>
public interface ICriticRatingService
{
    /// <summary>Compute and push ratings for shows and episodes, optionally restricted to a subset of series IDs.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="ApplyRatingsResult"/> with counters and error details.</returns>
    Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default);
}

/// <summary>Represents a specific rating update for the report log.</summary>
/// <param name="Title">Display title of the item.</param>
/// <param name="Type">Type of item (Show or Episode).</param>
/// <param name="RatingKey">Plex rating key.</param>
/// <param name="OldRating">Previous rating in Plex.</param>
/// <param name="NewRating">New rating applied from Shoko.</param>
public sealed record RatingChange(string Title, string Type, string RatingKey, double? OldRating, double? NewRating);

/// <summary>Aggregated status returned by <see cref="ICriticRatingService.ApplyRatingsAsync"/>.</summary>
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

/// <summary>Default implementation of <see cref="ICriticRatingService"/>.</summary>
public class CriticRatingService(PlexClient plexClient, IMetadataService metadataService) : ICriticRatingService
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion

    #region Public API

    /// <inheritdoc/>
    public async Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexRatingsApply;
        TaskHelper.StartTask(TaskName);
        s_logger.Info("CriticRatingService: Starting task...");
        var sw = Stopwatch.StartNew();

        try
        {
            var (pS, uS, pE, uE, errs) = (0, 0, 0, 0, 0);
            var errorsList = new List<string>();
            var appliedChanges = new List<RatingChange>();

            if (!plexClient.IsEnabled)
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges, sw.Elapsed);

            var allowedSet = allowedSeriesIds != null ? new HashSet<int>(allowedSeriesIds.Select(id => OverrideHelper.GetPrimary(id, metadataService))) : null;

            async Task ProcessShowRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                if (!shokoId.HasValue || (allowedSet != null && !allowedSet.Contains(shokoId.Value)))
                    return;

                pS++;
                var series = metadataService.GetShokoSeriesByID(shokoId.Value);
                if (series == null)
                {
                    errs++;
                    errorsList.Add($"Series {shokoId.Value} not found for RatingKey {item.RatingKey}");
                    return;
                }

                // Resolve the critic rating for a series based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => series.TmdbShows?.FirstOrDefault()?.Rating > 0 ? series.TmdbShows.First().Rating : null,
                    CriticRatingMode.AniDB => series.Rating > 0 ? series.Rating : null,
                    _ => null,
                };

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    s_logger.Trace(
                        "CriticRatingService: Skipped series -> {0} [{1}] (RatingKey: {2}) because rating {3} matches Plex",
                        series.GetDisplayTitle(),
                        series.ID,
                        item.RatingKey,
                        item.Rating?.ToString("F2") ?? "n/a"
                    );
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    uS++;
                    appliedChanges.Add(new RatingChange($"{series.GetDisplayTitle() ?? "Unknown"} [{series.ID}]", "Series", item.RatingKey!, item.Rating, rating));
                    s_logger.Info("CriticRatingService: Updated series -> {0} [{1}] to {2}", series.GetDisplayTitle(), series.ID, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errs++;
                    errorsList.Add($"CriticRatingService: Failed update for series -> {series.GetDisplayTitle()} [{shokoId.Value}]");
                }
            }

            async Task ProcessEpisodeRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                if (!epId.HasValue)
                    return;

                var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                if (episode == null || (allowedSet != null && !allowedSet.Contains(episode.SeriesID)))
                    return;

                pE++;
                // Resolve the critic rating for an episode based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => episode.TmdbEpisodes?.FirstOrDefault()?.Rating > 0 ? episode.TmdbEpisodes.First().Rating : null,
                    CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating : null,
                    _ => null,
                };

                var prefId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2} (RatingKey: {item.RatingKey})";

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    s_logger.Trace("CriticRatingService: Skipped episode -> {0} because rating {1} matches Plex", epLogName, item.Rating?.ToString("F2") ?? "n/a");
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    uE++;
                    appliedChanges.Add(new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", "Episode", item.RatingKey!, item.Rating, rating));
                    s_logger.Trace("CriticRatingService: Updated episode -> {0} to {1}", epLogName, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errs++;
                    errorsList.Add($"CriticRatingService: Failed update for episode -> {epLogName}");
                }
            }

            async Task ProcessMovieRating(PlexMetadataItem item, PlexLibraryTarget target)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    return;
                var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                if (!epId.HasValue)
                    return;

                var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                if (episode == null || (allowedSet != null && !allowedSet.Contains(episode.SeriesID)))
                    return;

                pE++;
                // Resolve the critic rating for a movie based on user configuration
                double? rating = Settings.CriticRatingMode switch
                {
                    CriticRatingMode.TMDB => (episode.TmdbMovies?.FirstOrDefault() ?? episode.Series?.TmdbMovies?.FirstOrDefault())?.Rating > 0
                        ? (episode.TmdbMovies?.FirstOrDefault() ?? episode.Series?.TmdbMovies?.FirstOrDefault())!.Rating
                        : (episode.TmdbEpisodes?.FirstOrDefault()?.Rating > 0 ? episode.TmdbEpisodes.First().Rating : null),
                    CriticRatingMode.AniDB => episode.Rating > 0 ? episode.Rating
                    : episode.Series?.Rating > 0 ? episode.Series.Rating
                    : null,
                    _ => null,
                };

                var prefId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2} (RatingKey: {item.RatingKey})";

                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    s_logger.Trace("CriticRatingService: Skipped movie -> {0} because rating {1} matches Plex", epLogName, item.Rating?.ToString("F2") ?? "n/a");
                    return;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    uE++;
                    appliedChanges.Add(new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", "Movie", item.RatingKey!, item.Rating, rating));
                    s_logger.Trace("CriticRatingService: Updated movie -> {0} to {1}", epLogName, rating?.ToString("F2") ?? "n/a");
                }
                else
                {
                    errs++;
                    errorsList.Add($"CriticRatingService: Failed update for movie -> {epLogName}");
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
                                using var req = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", target.ServerUrl);
                                using var resp = await plexClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                                if ((await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault() is { } movieItem)
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
            sw.Stop();
            s_logger.Info("CriticRatingService: Task finished -> Updated {0} series and {1} episodes in {2}ms", uS, uE, sw.ElapsedMilliseconds);
            return new ApplyRatingsResult(pS, uS, pE, uE, errs, errorsList, appliedChanges, sw.Elapsed);
        }
        finally
        {
            TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Pushes a rating value to a specific Plex metadata item.</summary>
    /// <param name="key">The Plex rating key.</param>
    /// <param name="val">The rating value to apply.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the rating was successfully applied.</returns>
    private async Task<bool> ApplyRatingAsync(string key, double? val, PlexLibraryTarget target, CancellationToken ct)
    {
        string path =
            (val == null || Settings.CriticRatingMode == CriticRatingMode.None)
                ? $"/library/metadata/{key}?rating=0&rating.locked=0"
                : $"/library/metadata/{key}?rating={val.Value.ToString(CultureInfo.InvariantCulture)}&rating.locked=1";

        try
        {
            using var req = plexClient.CreateRequest(HttpMethod.Put, path, target.ServerUrl);
            using var resp = await plexClient.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
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
