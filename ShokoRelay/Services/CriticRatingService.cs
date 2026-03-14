using System.Globalization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Services;

/// <summary>Service for applying critic ratings from Shoko metadata to Plex libraries.</summary>
public interface ICriticRatingService
{
    /// <summary>Compute and push ratings for shows and episodes, optionally restricted to a subset of series IDs.</summary>
    /// <param name="allowedSeriesIds">If provided, only these series will be processed.</param>
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
public sealed record ApplyRatingsResult(int ProcessedShows, int UpdatedShows, int ProcessedEpisodes, int UpdatedEpisodes, int Errors, List<string> ErrorsList, List<RatingChange> AppliedChanges);

/// <summary>Default implementation of <see cref="ICriticRatingService"/>.</summary>
public class CriticRatingService(HttpClient httpClient, PlexClient plexClient, IMetadataService metadataService) : ICriticRatingService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc/>
    public async Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        var (pS, uS, pE, uE, errs) = (0, 0, 0, 0, 0);
        var errorsList = new List<string>();
        var appliedChanges = new List<RatingChange>();

        if (!plexClient.IsEnabled)
            return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList, appliedChanges);

        foreach (var target in plexClient.GetConfiguredTargets())
        {
            // Process Shows
            var shows = await plexClient.GetSectionShowsAsync(target, cancellationToken) ?? [];
            foreach (var item in shows)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    continue;
                var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                if (!shokoId.HasValue || (allowedSeriesIds != null && !allowedSeriesIds.Contains(shokoId.Value)))
                    continue;

                pS++;
                var series = metadataService.GetShokoSeriesByID(shokoId.Value);
                if (series == null)
                {
                    errs++;
                    errorsList.Add($"Series {shokoId.Value} not found for Plex key {item.RatingKey}");
                    continue;
                }

                var rating = ComputeSeriesRating(series);
                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    Logger.Trace("CriticRatingService: skipping show {0} ({1}) because rating {2} matches Plex", item.RatingKey, series.PreferredTitle?.Value, item.Rating);
                    continue;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    uS++;
                    appliedChanges.Add(new RatingChange(series.PreferredTitle?.Value ?? "Unknown", "Show", item.RatingKey!, item.Rating, rating));
                }
                else
                {
                    errs++;
                    errorsList.Add($"Failed update for show {shokoId.Value}");
                }
            }

            // Process Episodes
            var episodes = await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken) ?? [];
            foreach (var item in episodes)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    continue;
                var epId = Sync.SyncHelper.TryParseShokoEpisodeIdFromGuid(item.Guid);
                if (!epId.HasValue)
                    continue;

                var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                if (episode == null || (allowedSeriesIds != null && !allowedSeriesIds.Contains(episode.SeriesID)))
                    continue;

                pE++;
                var rating = ComputeEpisodeRating(episode);
                if (!NeedsRatingUpdate(item.Rating, rating))
                {
                    Logger.Trace("CriticRatingService: skipping episode {0} because rating {1} matches Plex", item.RatingKey, item.Rating);
                    continue;
                }

                if (await ApplyRatingAsync(item.RatingKey!, rating, target, cancellationToken))
                {
                    uE++;
                    appliedChanges.Add(new RatingChange($"{episode.Series?.PreferredTitle?.Value} - S{episode.SeasonNumber}E{episode.EpisodeNumber}", "Episode", item.RatingKey!, item.Rating, rating));
                }
                else
                {
                    errs++;
                    errorsList.Add($"Failed update for episode {epId}");
                }
            }
        }
        return new ApplyRatingsResult(pS, uS, pE, uE, errs, errorsList, appliedChanges);
    }

    private async Task<bool> ApplyRatingAsync(string key, double? val, PlexLibraryTarget target, CancellationToken ct)
    {
        string path =
            (val == null || ShokoRelay.Settings.CriticRatingMode == CriticRatingMode.None)
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

    private static bool NeedsRatingUpdate(double? plex, double? shoko) => shoko.HasValue ? (!plex.HasValue || Math.Abs(plex.Value - shoko.Value) > 0.05) : (plex.HasValue && plex.Value > 0.05);

    private static double? ComputeSeriesRating(IShokoSeries s) =>
        ShokoRelay.Settings.CriticRatingMode switch
        {
            CriticRatingMode.TMDB => s.TmdbShows?.FirstOrDefault()?.Rating > 0 ? s.TmdbShows.First().Rating : null,
            CriticRatingMode.AniDB => s.Rating > 0 ? s.Rating : null,
            _ => null,
        };

    private static double? ComputeEpisodeRating(IShokoEpisode e) =>
        ShokoRelay.Settings.CriticRatingMode switch
        {
            CriticRatingMode.TMDB => e.TmdbEpisodes?.FirstOrDefault()?.Rating > 0 ? e.TmdbEpisodes.First().Rating : null,
            CriticRatingMode.AniDB => e.Rating > 0 ? e.Rating : null,
            _ => null,
        };
}
