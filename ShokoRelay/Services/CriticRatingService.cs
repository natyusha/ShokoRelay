using System.Globalization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Services
{
    /// <summary>
    /// Service for applying critic ratings from Shoko metadata to Plex libraries.
    /// </summary>
    public interface ICriticRatingService
    {
        /// <summary>
        /// Compute and push ratings for shows and episodes, optionally restricted to a subset of series IDs.
        /// </summary>
        /// <param name="allowedSeriesIds">If provided, only these series will be processed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An <see cref="ApplyRatingsResult"/> with counters and error details.</returns>
        Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Aggregated status returned by <see cref="ICriticRatingService.ApplyRatingsAsync"/>, containing counters and any error messages.
    /// </summary>
    public sealed record ApplyRatingsResult(int ProcessedShows, int UpdatedShows, int ProcessedEpisodes, int UpdatedEpisodes, int Errors, List<string> ErrorsList);

    /// <summary>
    /// Default implementation of <see cref="ICriticRatingService"/>, which queries Plex for shows/episodes and applies ratings based on Shoko metadata.
    /// </summary>
    public class CriticRatingService : ICriticRatingService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;
        private readonly PlexClient _plexClient;
        private readonly IMetadataService _metadataService;

        public CriticRatingService(HttpClient httpClient, ConfigProvider configProvider, PlexClient plexClient, IMetadataService metadataService)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _plexClient = plexClient ?? throw new ArgumentNullException(nameof(plexClient));
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        }

        public async Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
        {
            int processedShows = 0,
                updatedShows = 0;
            int processedEpisodes = 0,
                updatedEpisodes = 0;
            int errors = 0;
            var errorsList = new List<string>();

            if (!_plexClient.IsEnabled)
            {
                Logger.Info("CriticRatingService: Plex not enabled, nothing to do");
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList);
            }

            var targets = _plexClient.GetConfiguredTargets();
            if (targets == null || targets.Count == 0)
            {
                Logger.Info("CriticRatingService: no configured Plex targets");
                return new ApplyRatingsResult(0, 0, 0, 0, 0, errorsList);
            }

            foreach (var target in targets)
            {
                // process shows
                List<PlexMetadataItem> shows;
                try
                {
                    shows = await _plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? new List<PlexMetadataItem>();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "CriticRatingService: failed to list shows for {Server}/{Section}", target.ServerUrl, target.SectionId);
                    errors++;
                    errorsList.Add($"Failed to list shows in section {target.SectionId}: {ex.Message}");
                    continue;
                }

                foreach (var item in shows)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    var shokoId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!shokoId.HasValue)
                        continue;

                    if (allowedSeriesIds != null && allowedSeriesIds.Any() && !allowedSeriesIds.Contains(shokoId.Value))
                        continue;

                    processedShows++;
                    var series = _metadataService.GetShokoSeriesByID(shokoId.Value);
                    if (series == null)
                    {
                        errors++;
                        errorsList.Add($"Series {shokoId.Value} not found for Plex item {item.RatingKey}");
                        continue;
                    }

                    var rating = ComputeSeriesRating(series);
                    if (!NeedsRatingUpdate(item.Rating, rating))
                    {
                        Logger.Trace("CriticRatingService: skipping show {RatingKey} because rating {Rating} already matches current Plex rating", item.RatingKey, item.Rating);
                        continue;
                    }

                    try
                    {
                        string requestPath = BuildRatingRequestPath(item.RatingKey!, rating);
                        var logRating = ShokoRelay.Settings.CriticRatingMode == CriticRatingMode.None || rating == null ? "0 (cleared)" : rating.Value.ToString(CultureInfo.InvariantCulture);
                        Logger.Debug("CriticRatingService: updating show {RatingKey} -> {Rating} on {Server}", item.RatingKey, logRating, target.ServerUrl);
                        using var req = _plexClient.CreateRequest(HttpMethod.Put, requestPath, target.ServerUrl);
                        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                            updatedShows++;
                        else
                        {
                            errors++;
                            errorsList.Add($"Failed to update show {shokoId.Value} rating (status {resp.StatusCode})");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add(ex.Message);
                        Logger.Warn(ex, "CriticRatingService: error applying show rating for {Series}", shokoId.Value);
                    }
                }

                // process episodes
                List<PlexMetadataItem> episodes;
                try
                {
                    episodes = await _plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? new List<PlexMetadataItem>();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "CriticRatingService: failed to list episodes for {Server}/{Section}", target.ServerUrl, target.SectionId);
                    errors++;
                    errorsList.Add($"Failed to list episodes in section {target.SectionId}: {ex.Message}");
                    continue;
                }

                foreach (var item in episodes)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    var epId = Sync.SyncHelper.TryParseShokoEpisodeIdFromGuid(item.Guid);
                    if (!epId.HasValue)
                        continue;

                    var episode = _metadataService.GetShokoEpisodeByID(epId.Value);
                    if (episode == null)
                        continue;

                    if (allowedSeriesIds != null && allowedSeriesIds.Any() && !allowedSeriesIds.Contains(episode.SeriesID))
                        continue;

                    processedEpisodes++;
                    var rating = ComputeEpisodeRating(episode);
                    if (!NeedsRatingUpdate(item.Rating, rating))
                    {
                        Logger.Trace("CriticRatingService: skipping episode {RatingKey} because rating {Rating} already matches current Plex rating", item.RatingKey, item.Rating);
                        continue;
                    }

                    try
                    {
                        string requestPath = BuildRatingRequestPath(item.RatingKey!, rating);
                        var logRating = ShokoRelay.Settings.CriticRatingMode == CriticRatingMode.None || rating == null ? "0 (cleared)" : rating.Value.ToString(CultureInfo.InvariantCulture);
                        Logger.Debug("CriticRatingService: updating episode {RatingKey} -> {Rating} on {Server}", item.RatingKey, logRating, target.ServerUrl);
                        using var req = _plexClient.CreateRequest(HttpMethod.Put, requestPath, target.ServerUrl);
                        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                            updatedEpisodes++;
                        else
                        {
                            errors++;
                            errorsList.Add($"Failed to update episode {epId} rating (status {resp.StatusCode})");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add(ex.Message);
                        Logger.Warn(ex, "CriticRatingService: error applying episode rating for ep {Ep}", epId);
                    }
                }
            }

            return new ApplyRatingsResult(processedShows, updatedShows, processedEpisodes, updatedEpisodes, errors, errorsList);
        }

        /// <summary>
        /// Compare the current Plex rating against the computed value and return true when an update PUT is needed.
        /// </summary>
        private static bool NeedsRatingUpdate(double? plexRating, double? computedRating)
        {
            if (!computedRating.HasValue)
                return plexRating.HasValue && plexRating.Value > 0.05;
            if (!plexRating.HasValue)
                return true;
            return Math.Abs(plexRating.Value - computedRating.Value) > 0.05;
        }

        private double? ComputeSeriesRating(IShokoSeries series)
        {
            var tmdbShow = series.TmdbShows?.FirstOrDefault();
            return ShokoRelay.Settings.CriticRatingMode switch
            {
                CriticRatingMode.TMDB => tmdbShow?.Rating > 0 ? tmdbShow.Rating : null,
                CriticRatingMode.AniDB => series.Rating > 0 ? series.Rating : null,
                _ => null,
            };
        }

        private double? ComputeEpisodeRating(IShokoEpisode ep)
        {
            double? tmdbVal = null;
            if (ep.TmdbEpisodes?.FirstOrDefault()?.Rating > 0)
                tmdbVal = ep.TmdbEpisodes.First().Rating;

            return ShokoRelay.Settings.CriticRatingMode switch
            {
                CriticRatingMode.TMDB => tmdbVal,
                CriticRatingMode.AniDB => ep.Rating > 0 ? ep.Rating : null,
                _ => null,
            };
        }

        private string BuildRatingRequestPath(string ratingKey, double? rating)
        {
            // if we don't have a value (for any mode), clear the rating
            if (rating == null || ShokoRelay.Settings.CriticRatingMode == CriticRatingMode.None)
            {
                return $"/library/metadata/{ratingKey}?rating=0&rating.locked=0";
            }

            string ratingStr = rating.Value.ToString(CultureInfo.InvariantCulture);
            return $"/library/metadata/{ratingKey}?rating={ratingStr}&rating.locked=1";
        }
    }
}
