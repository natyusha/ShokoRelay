using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using ShokoRelay.Config;
using ShokoRelay.Helpers;

namespace ShokoRelay.Integrations.Shoko
{
    public class ShokoClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;

        public ShokoClient(HttpClient httpClient, ConfigProvider configProvider)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        }

        /// <summary>
        /// Return the first available poster URL for the given series & season, or null if none found.
        /// Performs the v3 Series/{id}/TMDB/Season?include=Images request on demand.
        /// </summary>
        public async Task<string?> GetSeasonPosterByTmdbAsync(int shokoSeriesId, int seasonNumber, CancellationToken cancellationToken = default)
        {
            var map = await GetSeasonPostersByTmdbAsync(shokoSeriesId, cancellationToken).ConfigureAwait(false);
            if (map == null || map.Count == 0)
                return null;

            if (map.TryGetValue(seasonNumber, out var posters) && posters != null && posters.Count > 0)
            {
                return posters[0];
            }

            return null;
        }

        /// <summary>
        /// Uses the v3 Series TMDB/Season?include=Images endpoint.
        /// Returns a mapping of seasonNumber -> list of poster URLs (may be null if none found or on error).
        /// </summary>
        public async Task<Dictionary<int, List<string>>?> GetSeasonPostersByTmdbAsync(int shokoSeriesId, CancellationToken cancellationToken = default)
        {
            var settings = _configProvider.GetSettings();
            string apiKey = settings.ShokoApiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Logger.Debug("Shoko API key is not configured; skipping Shoko season poster query.");
                return null;
            }

            string baseUrl = ImageHelper.GetBaseUrl().TrimEnd('/');
            // Add apikey as a query parameter for servers that require it and keep X-API-Key header for compatibility
            string requestUrl = $"{baseUrl}/api/v3/Series/{shokoSeriesId}/TMDB/Season?include=Images&apikey={Uri.EscapeDataString(apiKey)}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var res = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return null;

                var map = new Dictionary<int, List<string>>();

                foreach (var seasonEl in doc.RootElement.EnumerateArray())
                {
                    if (!seasonEl.TryGetProperty("SeasonNumber", out var snEl) || snEl.ValueKind != JsonValueKind.Number)
                        continue;
                    int seasonNum = snEl.GetInt32();
                    var posters = new List<string>();

                    if (seasonEl.TryGetProperty("Images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Object)
                    {
                        if (imagesEl.TryGetProperty("Posters", out var postersEl) && postersEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in postersEl.EnumerateArray())
                            {
                                if (p.ValueKind != JsonValueKind.Object)
                                    continue;

                                string? url = null;
                                if (p.TryGetProperty("Url", out var u) && u.ValueKind == JsonValueKind.String)
                                    url = u.GetString();
                                else if (p.TryGetProperty("url", out var u2) && u2.ValueKind == JsonValueKind.String)
                                    url = u2.GetString();

                                if (!string.IsNullOrWhiteSpace(url))
                                {
                                    if (url.StartsWith("/"))
                                        url = baseUrl + url;
                                    posters.Add(url);
                                    continue;
                                }

                                int? id = null;
                                string? source = null;
                                string? type = null;

                                if (p.TryGetProperty("ImageID", out var iid) && iid.ValueKind == JsonValueKind.Number)
                                    id = iid.GetInt32();
                                if (p.TryGetProperty("ID", out var idd) && idd.ValueKind == JsonValueKind.Number)
                                    id = id ?? idd.GetInt32();
                                if (p.TryGetProperty("Source", out var sst) && sst.ValueKind == JsonValueKind.String)
                                    source = sst.GetString();
                                if (p.TryGetProperty("Type", out var ttt) && ttt.ValueKind == JsonValueKind.String)
                                    type = ttt.GetString();

                                if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(type) && id.HasValue)
                                {
                                    string imageUrl = $"{baseUrl}/api/v3/Image/{source}/{type}/{id.Value}";
                                    posters.Add(imageUrl);
                                }
                            }
                        }
                    }

                    if (posters.Count > 0)
                    {
                        map[seasonNum] = posters;
                    }
                }

                return map.Count > 0 ? map : null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GetSeasonPostersByTmdbAsync failed for series {Series}", shokoSeriesId);
                return null;
            }
        }
    }
}
