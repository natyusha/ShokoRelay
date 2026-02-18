using System.Text.Json;
using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Services
{
    /// <summary>
    /// Calls the Shoko Server v3 HTTP API to trigger import scans.
    /// Used by both the dashboard "Run Import" action and the automation scheduler.
    /// </summary>
    public class ShokoImportService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ConfigProvider _configProvider;

        // Default to localhost:8111 (Shoko's default). This can be changed in future if needed.
        private const string DefaultBase = "http://127.0.0.1:8111";

        public ShokoImportService(ConfigProvider configProvider)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        }

        /// <summary>
        /// Trigger import scans for every ImportFolder with DropFolderType == "Source" using the configured Shoko API key.
        /// Returns a list of folder names that were scanned.
        /// </summary>
        public async Task<IReadOnlyList<string>> TriggerImportAsync(CancellationToken ct = default)
        {
            var cfg = _configProvider.GetSettings();
            var apiKey = (cfg?.ShokoApiKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Shoko API key is not configured (ShokoApiKey).");

            var baseUrl = DefaultBase; // intentionally conservative default

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var scanned = new List<string>();

            try
            {
                var importUrl = $"{baseUrl}/api/v3/ManagedFolder?apikey={Uri.EscapeDataString(apiKey)}";
                Logger.Info("ShokoImportService: fetching import folders from {Url}", importUrl);

                using var res = await http.GetAsync(importUrl, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var txt = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new HttpRequestException($"Failed to list import folders: {res.StatusCode} - {txt}");
                }

                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var folders = JsonSerializer.Deserialize<List<JsonElement>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<JsonElement>();

                foreach (var f in folders)
                {
                    try
                    {
                        var dropType = f.GetProperty("DropFolderType").GetString();
                        if (!string.Equals(dropType, "Source", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var id = f.GetProperty("ID").GetInt32();
                        var name = f.GetProperty("Name").GetString() ?? id.ToString();

                        var scanUrl = $"{baseUrl}/api/v3/ImportFolder/{id}/Scan?apikey={Uri.EscapeDataString(apiKey)}";
                        Logger.Info("ShokoImportService: scanning import folder {Name} (id={Id}) via {Url}", name, id, scanUrl);

                        using var scanRes = await http.GetAsync(scanUrl, ct).ConfigureAwait(false);
                        if (scanRes.IsSuccessStatusCode)
                        {
                            scanned.Add(name);
                        }
                        else
                        {
                            var sTxt = await scanRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            Logger.Warn("ShokoImportService: scan request failed for folder {Name} (id={Id}): {Status} {Body}", name, id, scanRes.StatusCode, sTxt);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ShokoImportService: error while scanning a folder");
                    }
                }

                return scanned;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ShokoImportService: TriggerImportAsync failed");
                throw;
            }
        }

        /// <summary>
        /// Call Shoko v3 Action/RemoveMissingFiles and return the server response body (string).
        /// </summary>
        public async Task<string?> RemoveMissingFilesAsync(bool removeFromMyList = true, CancellationToken ct = default)
        {
            var cfg = _configProvider.GetSettings();
            var apiKey = (cfg?.ShokoApiKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Shoko API key is not configured (ShokoApiKey).");

            var baseUrl = DefaultBase;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var url = $"{baseUrl}/api/v3/Action/RemoveMissingFiles/{(removeFromMyList ? "true" : "false")}?apikey={Uri.EscapeDataString(apiKey)}";
            Logger.Info("ShokoImportService: calling RemoveMissingFiles via {Url}", url);

            using var res = await http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Logger.Warn("ShokoImportService: RemoveMissingFiles failed: {Status} {Body}", res.StatusCode, body);
                throw new HttpRequestException($"RemoveMissingFiles failed: {res.StatusCode} - {body}");
            }

            return body;
        }
    }
}
