using System.Globalization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Services
{
    public interface ICriticRatingService
    {
        Task<ApplyRatingsResult> ApplyRatingsAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default);
    }

    public sealed record ApplyRatingsResult(int ProcessedShows, int UpdatedShows, int ProcessedEpisodes, int UpdatedEpisodes, int Errors, List<string> ErrorsList);

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

                    var shokoId = PlexHelpers.ExtractShokoSeriesIdFromGuid(item.Guid);
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
                    double? plexRating = item.Rating;
                    // only perform a PUT if the rating would change (including clearing when existing is >0)
                    bool needUpdate;
                    if (!rating.HasValue)
                    {
                        needUpdate = plexRating.HasValue && plexRating.Value > 0.05;
                    }
                    else if (!plexRating.HasValue)
                    {
                        needUpdate = true;
                    }
                    else
                    {
                        needUpdate = Math.Abs(plexRating.Value - rating.Value) > 0.05;
                    }

                    if (!needUpdate)
                    {
                        Logger.Trace("CriticRatingService: skipping show {RatingKey} because rating {Rating} already matches current Plex rating", item.RatingKey, plexRating);
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
                    double? plexRating = item.Rating;
                    bool needUpdate;
                    if (!rating.HasValue)
                    {
                        needUpdate = plexRating.HasValue && plexRating.Value > 0.05;
                    }
                    else if (!plexRating.HasValue)
                    {
                        needUpdate = true;
                    }
                    else
                    {
                        needUpdate = Math.Abs(plexRating.Value - rating.Value) > 0.05;
                    }

                    if (!needUpdate)
                    {
                        Logger.Trace("CriticRatingService: skipping episode {RatingKey} because rating {Rating} already matches current Plex rating", item.RatingKey, plexRating);
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

        private double? ComputeSeriesRating(IShokoSeries series)
        {
            return ShokoRelay.Settings.CriticRatingMode switch
            {
                CriticRatingMode.TMDB => series.TmdbShows?.FirstOrDefault()?.Rating > 0 ? series.TmdbShows.First().Rating : null,
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
