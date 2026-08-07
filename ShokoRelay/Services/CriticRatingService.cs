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
public class CriticRatingService(HttpClient httpClient, PlexClient plexClient, IMetadataService metadataService) : ICriticRatingService
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

            var allowedSet = allowedSeriesIds != null ? new HashSet<int>(allowedSeriesIds) : null;
            foreach (var target in plexClient.GetConfiguredTargets())
            {
                // Process Shows
                var shows = await plexClient.GetSectionShowsAsync(target, cancellationToken) ?? [];
                foreach (var item in shows)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;
                    var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!shokoId.HasValue || (allowedSet != null && !allowedSet.Contains(shokoId.Value)))
                        continue;

                    pS++;
                    var series = metadataService.GetShokoSeriesByID(shokoId.Value);
                    if (series == null)
                    {
                        errs++;
                        errorsList.Add($"Series {shokoId.Value} not found for RatingKey {item.RatingKey}");
                        continue;
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
                        continue;
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

                // Process Episodes
                var episodes = await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken) ?? [];
                foreach (var item in episodes)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;
                    var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                    if (!epId.HasValue)
                        continue;

                    var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                    if (episode == null || (allowedSet != null && !allowedSet.Contains(episode.SeriesID)))
                        continue;

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
                        continue;
                    }

                    if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                    {
                        uE++;
                        appliedChanges.Add(
                            new RatingChange($"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}", "Episode", item.RatingKey!, item.Rating, rating)
                        );
                        s_logger.Trace("CriticRatingService: Updated episode -> {0} to {1}", epLogName, rating?.ToString("F2") ?? "n/a");
                    }
                    else
                    {
                        errs++;
                        errorsList.Add($"CriticRatingService: Failed update for episode -> {epLogName}");
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
            using var resp = await httpClient.SendAsync(req, ct);
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
