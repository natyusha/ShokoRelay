using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

/// <summary>
/// Represents a single connection endpoint for a Plex device, including local/relay status and the connection URI.
/// </summary>
public sealed record PlexDeviceConnection
{
    public string? Uri { get; init; }
    public bool Local { get; init; }
    public bool Relay { get; init; }
}

/// <summary>
/// Plex device resource returned by the Plex.tv resources API, containing identifier, name, capabilities, access token, and available connections.
/// </summary>
public sealed record PlexDevice
{
    public string? ClientIdentifier { get; init; }
    public string? Name { get; init; }
    public string? Provides { get; init; }
    public string? AccessToken { get; init; }
    public List<PlexDeviceConnection>? Connections { get; init; }
}

/// <summary>
/// Summary of a Plex server including its client identifier, display name, and preferred connection URI.
/// </summary>
public sealed record PlexServerInfo(string Id, string Name, string? PreferredUri);

/// <summary>
/// Summary of a Plex library section including its numeric ID, title, content type, agent identifier, and unique UUID.
/// </summary>
public sealed record PlexLibraryInfo(int Id, string Title, string Type, string Agent, string Uuid);

public class PlexAuth
{
    private const string BaseUrl = "https://plex.tv";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _httpClient;
    private readonly PlexAuthConfig _config;

    public PlexAuth(HttpClient httpClient, PlexAuthConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    /// <summary>
    /// Request a new Plex authentication PIN from the Plex.tv API.
    /// </summary>
    /// <param name="strong">If true, request a PIN requiring a longer code.</param>
    /// <param name="cancellationToken">Cancellation token to abort the request.</param>
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

    /// <summary>
    /// Construct the URL that the user must visit in order to authenticate the given <paramref name="pinCode"/>.
    /// For short pins a simple link page is returned; otherwise the URL includes client and product details.
    /// </summary>
    /// <param name="pinCode">PIN code received from <see cref="CreatePinAsync"/>.</param>
    /// <param name="productName">Product name to include in the request context.</param>
    /// <param name="forwardUrl">Optional URL to redirect the user after auth.</param>
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

    /// <summary>
    /// Retrieve the status for the specified Plex PIN. The returned object may contain a null auth token until the user completes authentication.
    /// </summary>
    /// <param name="pinId">Identifier returned by <see cref="CreatePinAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
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

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri url, string? token = null, string? clientIdentifier = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier ?? _config.ClientIdentifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
        request.Headers.TryAddWithoutValidation("X-Plex-Version", ShokoRelayInfo.Version);
        request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Shoko Relay");
        request.Headers.TryAddWithoutValidation("X-Plex-Device", "Shoko Relay");
        request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", ShokoRelayInfo.Name);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
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

    // Response DTOs for library sections JSON
    private sealed record LibrarySectionsResponse
    {
        public LibrarySectionsContainer? MediaContainer { get; init; }
    }

    private sealed record LibrarySectionsContainer
    {
        public List<LibrarySectionEntry>? Directory { get; init; }
    }

    private sealed record LibrarySectionEntry
    {
        public string? Key { get; init; }
        public string? Title { get; init; }
        public string? Type { get; init; }
        public string? Agent { get; init; }
        public string? Uuid { get; init; }
    }

    /// <summary>
    /// Query the Plex.tv resources API for all accessible servers and return parsed server information, raw devices, and a token-validity flag.
    /// </summary>
    /// <param name="token">Plex authentication token.</param>
    /// <param name="clientIdentifier">Client identifier string sent in Plex headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (TokenValid, Servers, Devices).</returns>
    public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<PlexDevice> Devices)> GetPlexServerListAsync(string token, string clientIdentifier, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri("https://clients.plex.tv/api/v2/resources"), token, clientIdentifier);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        bool tokenValid = response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden;
        if (!response.IsSuccessStatusCode)
            return (tokenValid, [], []);

        List<PlexDevice> devices;
        try
        {
            devices = await ReadJsonAsync<List<PlexDevice>>(response, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to parse Plex resources response: {ex.Message}");
            return (tokenValid, [], []);
        }

        var servers = new List<PlexServerInfo>();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.ClientIdentifier) || string.IsNullOrWhiteSpace(device.Provides) || !device.Provides.Contains("server", StringComparison.OrdinalIgnoreCase))
                continue;

            var connections = device.Connections ?? [];
            string? preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && c.Local && !c.Relay && c.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))?.Uri;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && c.Local && !c.Relay && c.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))?.Uri;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && !c.Relay && c.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))?.Uri;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && !c.Relay && c.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))?.Uri;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri) && !c.Relay)?.Uri;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = connections.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Uri))?.Uri;

            servers.Add(new PlexServerInfo(device.ClientIdentifier, device.Name ?? device.ClientIdentifier, preferred));
        }

        return (tokenValid, servers, devices);
    }

    /// <summary>
    /// Fetch the list of library sections for a specific Plex server using the given token and client identifier.
    /// </summary>
    /// <param name="token">Plex authentication token.</param>
    /// <param name="clientIdentifier">Client identifier string.</param>
    /// <param name="serverUrl">Base URL of the target Plex server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of <see cref="PlexLibraryInfo"/> for each library section on the server.</returns>
    public async Task<List<PlexLibraryInfo>> GetPlexLibrariesAsync(string token, string clientIdentifier, string serverUrl, CancellationToken cancellationToken = default)
    {
        var url = new Uri(serverUrl.TrimEnd('/') + "/library/sections");
        using var request = CreateRequest(HttpMethod.Get, url, token, clientIdentifier);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return [];

        try
        {
            var wrapper = await ReadJsonAsync<LibrarySectionsResponse>(response, cancellationToken).ConfigureAwait(false);
            var directories = wrapper?.MediaContainer?.Directory;
            if (directories == null)
                return [];

            var libraries = new List<PlexLibraryInfo>();
            foreach (var dir in directories)
            {
                if (!int.TryParse(dir.Key, out int sectionId) || sectionId <= 0)
                    continue;
                libraries.Add(new PlexLibraryInfo(sectionId, dir.Title ?? $"Library {sectionId}", dir.Type ?? "show", dir.Agent ?? string.Empty, dir.Uuid ?? string.Empty));
            }
            return libraries;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to parse Plex libraries from {url}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Revoke (sign out) a Plex authentication token so it can no longer be used.
    /// </summary>
    /// <param name="token">The token to revoke.</param>
    /// <param name="clientIdentifier">Client identifier used during the original authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RevokePlexTokenAsync(string token, string clientIdentifier, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, new Uri($"{BaseUrl}/api/v2/user/authentication"), token, clientIdentifier);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Logger.Warn($"Failed to revoke Plex token. Status: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Exception while revoking Plex token: {ex.Message}");
        }
    }

    /// <summary>
    /// Represents a managed/home user returned by the Plex Home users API. Contains the user's numeric ID, display title, username, and UUID.
    /// </summary>
    public sealed record PlexHomeUser(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("uuid")] string? Uuid
    );

    /// <summary>
    /// Retrieve all managed/home users associated with the Plex Home of the provided admin token.
    /// </summary>
    /// <param name="adminToken">Admin-level Plex authentication token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of <see cref="PlexHomeUser"/> records.</returns>
    public async Task<List<PlexHomeUser>> GetHomeUsersAsync(string adminToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = new Uri($"{BaseUrl}/api/v2/home/users");
            using var request = CreateRequest(HttpMethod.Get, url, adminToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            var wrapper = await ReadJsonAsync<PlexHomeUsersResponse>(response, cancellationToken).ConfigureAwait(false);
            return wrapper?.Users ?? [];
        }
        catch (Exception ex)
        {
            Logger.Warn($"GetHomeUsersAsync exception: {ex.Message}");
            return [];
        }
    }

    private sealed record PlexHomeUsersResponse([property: JsonPropertyName("users")] List<PlexHomeUser>? Users);

    // Retrieve basic account info (display title and username) for a given Plex token.
    public sealed record PlexAccountInfo(string? Title, string? Username);

    /// <summary>
    /// Retrieve the display name and username for the account associated with the given Plex <paramref name="token"/>.
    /// </summary>
    /// <param name="token">Plex authentication token to query account info for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PlexAccountInfo"/> record, or <c>null</c> if the request fails or yields no data.</returns>
    public async Task<PlexAccountInfo?> GetAccountInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var url = new Uri($"{BaseUrl}/users/account");
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // Attempt to read "title" and "username" fields at top-level or under a nested "user" object.
                string? title = null;
                string? username = null;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                        title = t.GetString();

                    if (root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
                        username = u.GetString();

                    if (string.IsNullOrWhiteSpace(title) && root.TryGetProperty("user", out var userObj) && userObj.ValueKind == JsonValueKind.Object)
                    {
                        if (userObj.TryGetProperty("title", out var ut) && ut.ValueKind == JsonValueKind.String)
                            title = ut.GetString();
                        if (userObj.TryGetProperty("username", out var uu) && uu.ValueKind == JsonValueKind.String)
                            username = uu.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(username))
                    return null;

                return new PlexAccountInfo(title?.Trim(), username?.Trim());
            }
            catch (JsonException ex)
            {
                // Plex sometimes returns an HTML error page (e.g. when BaseUrl is incorrect or token is invalid) which starts with '<'.
                // This isn't JSON, so warn but don't spam with the raw exception message since it's expected in some misconfigurations.
                if (!string.IsNullOrWhiteSpace(content) && content.TrimStart().StartsWith("<"))
                {
                    Logger.Warn("GetAccountInfoAsync: received non-JSON response (HTML?) from Plex, please check BaseUrl and token");
                }
                else
                {
                    Logger.Warn($"GetAccountInfoAsync: failed to parse JSON response: {ex.Message}");
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"GetAccountInfoAsync exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Switch to a Plex Home managed user and return a transient authentication token for that user.
    /// </summary>
    /// <param name="userId">Numeric ID of the managed user to switch to.</param>
    /// <param name="adminToken">Admin-level Plex token authorizing the switch.</param>
    /// <param name="pin">Optional PIN required by the managed user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A transient auth token string, or <c>null</c> if the switch failed.</returns>
    public async Task<string?> SwitchHomeUserAsync(int userId, string adminToken, string? pin = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var uriText = $"{BaseUrl}/api/home/users/{userId}/switch";
            if (!string.IsNullOrWhiteSpace(pin))
                uriText += $"?pin={Uri.EscapeDataString(pin)}";
            var url = new Uri(uriText);

            using var request = CreateRequest(HttpMethod.Post, url, adminToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"SwitchHomeUserAsync failed for user {userId}: status {(int)response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            // IMPORTANT: This older Plex endpoint always returns XML; extract authToken from the <user> element attributes.
            try
            {
                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.LoadXml(content);
                var userNode = xmlDoc.SelectSingleNode("//user") ?? xmlDoc.DocumentElement;
                var token = userNode?.Attributes?["authToken"]?.Value ?? userNode?.Attributes?["authenticationToken"]?.Value;
                if (!string.IsNullOrWhiteSpace(token))
                    return token.Trim();
            }
            catch (Exception ex)
            {
                var snippet = content.Length > 512 ? content[..512] + "..." : content;
                Logger.Warn($"SwitchHomeUserAsync: failed to parse XML response for user {userId}: {ex.Message}. Body starts with: {snippet}");
                return null;
            }

            var debugSnippet = content.Length > 512 ? content[..512] + "..." : content;
            Logger.Warn($"SwitchHomeUserAsync: response for user {userId} did not contain an authToken. Body starts with: {debugSnippet}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SwitchHomeUserAsync exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Discover Shoko-enabled libraries across Plex servers accessible with the supplied token, optionally filtering by server.
    /// </summary>
    /// <param name="token">Plex authentication token.</param>
    /// <param name="clientIdentifier">Client identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (TokenValid, Servers, ShokoLibraries) where ShokoLibraries pairs each matching library with its server.</returns>
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
                    if (!seen.Add(key))
                        continue;
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

/// <summary>
/// Response DTO for the Plex PIN request/poll endpoints. Contains the PIN identifier, code, and — once authenticated — an auth token.
/// </summary>
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

/// <summary>
/// JSON converter that accepts both string and numeric JSON tokens and always produces a <see cref="string"/> value. Used for Plex fields that may be serialized as either type.
/// </summary>
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
