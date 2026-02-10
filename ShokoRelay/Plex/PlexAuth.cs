using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex
{
    // --- Plex discovery DTOs ---
    public sealed record PlexDeviceConnection
    {
        public string? Uri { get; init; }
        public bool Local { get; init; }
        public bool Relay { get; init; }
    }

    public sealed record PlexDevice
    {
        public string? ClientIdentifier { get; init; }
        public string? Name { get; init; }
        public string? Provides { get; init; }
        public string? AccessToken { get; init; }
        public List<PlexDeviceConnection>? Connections { get; init; }
    }

    public sealed record PlexServerInfo(string Id, string Name, string? PreferredUri);

    public sealed record PlexLibraryInfo(int Id, string Title, string Type, string Agent, string Uuid);

    public class PlexAuth
    {
        private const string BaseUrl = "https://plex.tv";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly PlexAuthConfig _config;

        public PlexAuth(HttpClient httpClient, PlexAuthConfig config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        // PIN Authentication Flow - Step 1: Generate a PIN
        public async Task<PlexPinResponse> CreatePinAsync(bool strong = true, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            string urlText = strong ? $"{BaseUrl}/api/v2/pins?strong=true" : $"{BaseUrl}/api/v2/pins";
            var url = new Uri(urlText);

            using var request = CreateRequest(HttpMethod.Post, url);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var pin = await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false);
            if (pin == null)
                throw new InvalidOperationException("Plex pin response was empty.");

            return pin;
        }

        // PIN Authentication Flow - Step 2: User Authentication (build auth URL)
        public string BuildAuthUrl(string pinCode, string productName, string? forwardUrl = null)
        {
            EnsureConfigured();

            if (string.IsNullOrWhiteSpace(pinCode))
                throw new ArgumentException("Pin code is required.", nameof(pinCode));

            // For short 4-digit pins, use the link page (user must type the code manually)
            if (pinCode.Length <= 4)
                return $"https://plex.tv/link/?pin={Uri.EscapeDataString(pinCode)}";

            var query = new List<string>
            {
                $"clientID={Uri.EscapeDataString(_config.ClientIdentifier)}",
                $"code={Uri.EscapeDataString(pinCode)}",
                // encoded form of: context[device][product]=<productName>
                $"context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(productName)}",
            };

            if (!string.IsNullOrWhiteSpace(forwardUrl))
                query.Add($"forwardUrl={Uri.EscapeDataString(forwardUrl)}");

            return $"https://app.plex.tv/auth#?{string.Join("&", query)}";
        }

        // PIN Authentication Flow - Step 3: Check PIN for the access token
        // Note: We return the full PIN record here. In polling flows the auth token may be null
        // until the user completes authentication, so callers should check the returned object.
        public async Task<PlexPinResponse> GetPinAsync(string pinId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            if (string.IsNullOrWhiteSpace(pinId))
                throw new ArgumentException("Pin id is required.", nameof(pinId));

            var url = new Uri($"{BaseUrl}/api/v2/pins/{Uri.EscapeDataString(pinId)}");

            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var pin = await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false);
            if (pin == null)
                throw new InvalidOperationException("Plex pin response was empty.");

            return pin;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, Uri url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", _config.ClientIdentifier);
            request.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
            request.Headers.TryAddWithoutValidation("X-Plex-Version", ShokoRelayInfo.Version);
            request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Shoko Relay");
            request.Headers.TryAddWithoutValidation("X-Plex-Device", "Shoko Relay");
            request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", ShokoRelayInfo.Name);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return default;

            try
            {
                return JsonSerializer.Deserialize<T>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                string snippet = content.Length > 512 ? content[..512] + "..." : content;
                throw new InvalidOperationException($"Expected JSON from Plex but got invalid content. Status={(int)response.StatusCode} {response.ReasonPhrase}. Body starts with: {snippet}", ex);
            }
        }

        private void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(_config.ClientIdentifier))
                throw new InvalidOperationException("Plex auth client identifier is not configured.");
        }

        // --- Plex discovery & helpers (XML-based) ---

        public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<PlexDevice> Devices)> GetPlexServerListAsync(
            string token,
            string clientIdentifier,
            CancellationToken cancellationToken = default
        )
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://clients.plex.tv/api/v2/resources");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            request.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);
            request.Headers.TryAddWithoutValidation("Accept", "application/xml, text/xml, */*");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            bool tokenValid = response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden;
            if (!response.IsSuccessStatusCode)
                return (tokenValid, new List<PlexServerInfo>(), new List<PlexDevice>());

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (tokenValid, new List<PlexServerInfo>(), new List<PlexDevice>());

            var devices = new List<PlexDevice>();
            try
            {
                var xml = XDocument.Parse(content);
                var deviceNodes = xml.Descendants("Device").Concat(xml.Descendants("resource"));
                foreach (var dn in deviceNodes)
                {
                    var clientId = dn.Attribute("clientIdentifier")?.Value ?? dn.Attribute("clientidentifier")?.Value;
                    var name = dn.Attribute("name")?.Value ?? string.Empty;
                    var provides = dn.Attribute("provides")?.Value ?? string.Empty;
                    var accessToken = dn.Attribute("accessToken")?.Value ?? dn.Attribute("access_token")?.Value ?? string.Empty;

                    var conns = new List<PlexDeviceConnection>();
                    foreach (var cn in dn.Descendants("Connection").Concat(dn.Descendants("connection")))
                    {
                        var uri = cn.Attribute("uri")?.Value ?? cn.Attribute("URI")?.Value;
                        var localStr = cn.Attribute("local")?.Value ?? string.Empty;
                        var relayStr = cn.Attribute("relay")?.Value ?? string.Empty;
                        bool local = string.Equals(localStr, "1") || string.Equals(localStr, "true", StringComparison.OrdinalIgnoreCase);
                        bool relay = string.Equals(relayStr, "1") || string.Equals(relayStr, "true", StringComparison.OrdinalIgnoreCase);
                        conns.Add(
                            new PlexDeviceConnection
                            {
                                Uri = uri,
                                Local = local,
                                Relay = relay,
                            }
                        );
                    }

                    devices.Add(
                        new PlexDevice
                        {
                            ClientIdentifier = clientId,
                            Name = name,
                            Provides = provides,
                            AccessToken = accessToken,
                            Connections = conns,
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to parse Plex resources XML: {ex.Message}");
            }

            var servers = new List<PlexServerInfo>();
            foreach (var device in devices ?? new List<PlexDevice>())
            {
                if (device == null || string.IsNullOrWhiteSpace(device.ClientIdentifier))
                    continue;
                if (string.IsNullOrWhiteSpace(device.Provides) || !device.Provides.Contains("server", StringComparison.OrdinalIgnoreCase))
                    continue;

                var connections = device.Connections ?? new List<PlexDeviceConnection>();
                string? preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && c.Local && !c.Relay && c.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))?.Uri;
                if (string.IsNullOrWhiteSpace(preferred))
                    preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && !c.Relay && c.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))?.Uri;
                if (string.IsNullOrWhiteSpace(preferred))
                    preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && !c.Relay)?.Uri;
                if (string.IsNullOrWhiteSpace(preferred))
                    preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri))?.Uri;

                servers.Add(new PlexServerInfo(device.ClientIdentifier, device.Name ?? device.ClientIdentifier, preferred));
            }

            return (tokenValid, servers, devices ?? new List<PlexDevice>());
        }

        public async Task<List<PlexLibraryInfo>> GetPlexLibrariesAsync(string token, string clientIdentifier, string serverUrl, CancellationToken cancellationToken = default)
        {
            var requestUrl = serverUrl.TrimEnd('/') + "/library/sections";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            request.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);
            request.Headers.TryAddWithoutValidation("Accept", "application/xml, text/xml, */*");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new List<PlexLibraryInfo>();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var libraries = new List<PlexLibraryInfo>();

            try
            {
                var xml = XDocument.Parse(content);
                var dirNodes = xml.Descendants("Directory");
                foreach (var dn in dirNodes)
                {
                    var key = dn.Attribute("key")?.Value;
                    if (!int.TryParse(key, out int sectionId) || sectionId <= 0)
                        continue;
                    var title = dn.Attribute("title")?.Value ?? $"Library {sectionId}";
                    var type = dn.Attribute("type")?.Value ?? "show";
                    var agent = dn.Attribute("agent")?.Value ?? string.Empty;
                    var uuid = dn.Attribute("uuid")?.Value ?? string.Empty;
                    libraries.Add(new PlexLibraryInfo(sectionId, title, type, agent, uuid));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to parse Plex libraries from {requestUrl}: {ex.Message}");
            }

            return libraries;
        }

        public async Task RevokePlexTokenAsync(string token, string clientIdentifier, CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/v2/user/authentication");
                request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
                request.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
                request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"Failed to revoke Plex token. Status: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Exception while revoking Plex token: {ex.Message}");
            }
        }

        public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries)> DiscoverShokoLibrariesAsync(
            string token,
            string clientIdentifier,
            CancellationToken cancellationToken = default
        )
        {
            var result = await GetPlexServerListAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);
            var shokoList = new List<(PlexLibraryInfo, PlexServerInfo)>();
            if (!result.TokenValid || result.Servers == null || result.Servers.Count == 0)
                return (result.TokenValid, result.Servers ?? new List<PlexServerInfo>(), shokoList);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var srv in result.Servers)
            {
                if (string.IsNullOrWhiteSpace(srv.PreferredUri))
                    continue;

                try
                {
                    var libs = await GetPlexLibrariesAsync(token, clientIdentifier, srv.PreferredUri, cancellationToken).ConfigureAwait(false);
                    var shoko = libs.Where(l => string.Equals(l.Agent, ShokoRelayInfo.AgentScheme, StringComparison.OrdinalIgnoreCase));
                    foreach (var l in shoko)
                    {
                        var key = !string.IsNullOrWhiteSpace(l.Uuid) ? l.Uuid : $"{srv.PreferredUri}::{l.Id}";
                        if (seen.Contains(key))
                            continue;
                        seen.Add(key);
                        shokoList.Add((l, srv));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to fetch libraries from server {srv.PreferredUri}: {ex.Message}");
                }
            }

            return (result.TokenValid, result.Servers ?? new List<PlexServerInfo>(), shokoList);
        }
    }

    // Request/Response models
    public class PlexPinResponse
    {
        [JsonConverter(typeof(StringOrNumberConverter))]
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("authToken")]
        public string? AuthTokenCamel { get; set; }

        [JsonPropertyName("auth_token")]
        public string? AuthTokenSnake { get; set; }

        [JsonIgnore]
        public string? AuthToken => AuthTokenSnake ?? AuthTokenCamel;
    }

    public sealed class StringOrNumberConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out long value) ? value.ToString() : reader.GetDouble().ToString(),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Unexpected token {reader.TokenType} when parsing string."),
            };
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value);
        }
    }
}
