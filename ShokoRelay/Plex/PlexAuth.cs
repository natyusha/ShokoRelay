using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

#region Data Models

/// <summary>Represents a single connection endpoint for a Plex device.</summary>
/// <param name="Uri">Connection URI.</param>
/// <param name="Local">Local network status.</param>
/// <param name="Relay">Plex Relay status.</param>
public sealed record PlexDeviceConnection(string? Uri, bool Local, bool Relay);

/// <summary>Plex device resource returned by the Plex.tv resources API.</summary>
/// <param name="ClientIdentifier">Device UUID.</param>
/// <param name="Name">Device name.</param>
/// <param name="Provides">Capability list.</param>
/// <param name="AccessToken">Device access token.</param>
/// <param name="Connections">Available network connections.</param>
public sealed record PlexDevice(string? ClientIdentifier, string? Name, string? Provides, string? AccessToken, List<PlexDeviceConnection>? Connections);

/// <summary>Summary of a Plex server connection.</summary>
/// <param name="Id">Server identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="PreferredUri">Best connection URL.</param>
public sealed record PlexServerInfo(string Id, string Name, string? PreferredUri);

/// <summary>Summary of a Plex library section including physical locations.</summary>
/// <param name="Id">Section ID.</param>
/// <param name="Title">Library title.</param>
/// <param name="Type">Media type.</param>
/// <param name="Agent">Agent ID.</param>
/// <param name="Uuid">Section UUID.</param>
/// <param name="Locations">Root paths.</param>
public sealed record PlexLibraryInfo(int Id, string Title, string Type, string Agent, string Uuid, List<string> Locations);

/// <summary>Record representing a Plex Home user.</summary>
/// <param name="Id">Numeric ID.</param>
/// <param name="Title">Display title.</param>
/// <param name="Username">Actual username.</param>
/// <param name="Uuid">Unique identifier.</param>
public sealed record PlexHomeUser(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("uuid")] string? Uuid
);

#endregion

/// <summary>Handles authentication with Plex.tv, utilizing modern v2 JSON endpoints and legacy XML for user switching.</summary>
public class PlexAuth(HttpClient httpClient, PlexAuthConfig config)
{
    #region Fields & Constructor

    private const string BaseUrl = "https://plex.tv";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _httpClient = httpClient;
    private readonly PlexAuthConfig _config = config;

    #endregion

    #region PIN Operations

    /// <summary>Request a new Plex authentication PIN from the Plex.tv v2 API.</summary>
    public async Task<PlexPinResponse> CreatePinAsync(bool strong = true, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri($"{BaseUrl}/api/v2/pins{(strong ? "?strong=true" : "")}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    /// <summary>Construct the URL that the user must visit in order to authenticate the PIN.</summary>
    public string BuildAuthUrl(string pinCode, string productName, string? forwardUrl = null)
    {
        if (pinCode.Length <= 4)
            return $"https://plex.tv/link/?pin={Uri.EscapeDataString(pinCode)}";
        var query = $"clientID={Uri.EscapeDataString(_config.ClientIdentifier)}&code={Uri.EscapeDataString(pinCode)}&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(productName)}";
        if (!string.IsNullOrWhiteSpace(forwardUrl))
            query += $"&forwardUrl={Uri.EscapeDataString(forwardUrl)}";
        return $"https://app.plex.tv/auth#?{query}";
    }

    /// <summary>Retrieve the status for a specific Plex PIN from the v2 API.</summary>
    public async Task<PlexPinResponse> GetPinAsync(string pinId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/pins/{Uri.EscapeDataString(pinId)}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    #endregion

    #region Server/Lib Discovery

    /// <summary>Query the Plex.tv v2 resources API for all accessible servers.</summary>
    public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<PlexDevice> Devices)> GetPlexServerListAsync(string token, string cid, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri("https://clients.plex.tv/api/v2/resources"), token, cid);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden, [], []);

        var devices = await ReadJsonAsync<List<PlexDevice>>(response, ct).ConfigureAwait(false) ?? [];
        var servers = devices
            .Where(d => d.Provides?.Contains("server", StringComparison.OrdinalIgnoreCase) == true)
            .Select(d =>
            {
                var pref = d.Connections?.OrderByDescending(c => (c.Local && !c.Relay ? 100 : 0) + (c.Uri?.StartsWith("https") == true ? 10 : 0) + (!c.Relay ? 1 : 0)).FirstOrDefault()?.Uri;
                return new PlexServerInfo(d.ClientIdentifier ?? "", d.Name ?? "", pref);
            })
            .ToList();
        return (true, servers, devices);
    }

    /// <summary>Fetch the list of library sections for a specific Plex server.</summary>
    public async Task<List<PlexLibraryInfo>> GetPlexLibrariesAsync(string token, string cid, string url, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{url.TrimEnd('/')}/library/sections"), token, cid);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var wrapper = await ReadJsonAsync<LibrarySectionsResponse>(resp, ct).ConfigureAwait(false);
        return wrapper
                ?.MediaContainer?.Directory?.Select(d => new PlexLibraryInfo(
                    int.TryParse(d.Key, out int id) ? id : 0,
                    d.Title ?? "",
                    d.Type ?? "",
                    d.Agent ?? "",
                    d.Uuid ?? "",
                    d.Locations?.Select(l => l.Path).ToList() ?? []
                ))
                .Where(l => l.Id > 0)
                .ToList()
            ?? [];
    }

    /// <summary>Discover Shoko-enabled libraries across all accessible Plex servers.</summary>
    public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries)> DiscoverShokoLibrariesAsync(
        string token,
        string cid,
        CancellationToken ct = default
    )
    {
        var (TokenValid, Servers, _) = await GetPlexServerListAsync(token, cid, ct).ConfigureAwait(false);
        var list = new List<(PlexLibraryInfo, PlexServerInfo)>();
        if (TokenValid)
        {
            foreach (var srv in Servers.Where(s => !string.IsNullOrEmpty(s.PreferredUri)))
            {
                try
                {
                    var libs = await GetPlexLibrariesAsync(token, cid, srv.PreferredUri!, ct).ConfigureAwait(false);
                    foreach (var l in libs.Where(l => string.Equals(l.Agent, ShokoRelayInfo.AgentScheme, StringComparison.OrdinalIgnoreCase)))
                        list.Add((l, srv));
                }
                catch { }
            }
        }
        return (TokenValid, Servers, list);
    }

    #endregion

    #region Home & User Mgmt.

    /// <summary>Retrieve all managed/home users associated with the account via the v2 API.</summary>
    public async Task<List<PlexHomeUser>> GetHomeUsersAsync(string adminToken, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/home/users"), adminToken);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var result = await ReadJsonAsync<PlexHomeResponse>(resp, ct).ConfigureAwait(false);
        return result?.Users ?? [];
    }

    /// <summary>Switch to a Plex Home managed user and return a transient token via XML.</summary>
    /// <remarks><b>IMPORTANT</b>: This old Plex endpoint always returns XML.</remarks>
    public async Task<string?> SwitchHomeUserAsync(int userId, string adminToken, string? pin = null, CancellationToken ct = default)
    {
        try
        {
            var uriText = $"{BaseUrl}/api/home/users/{userId}/switch" + (string.IsNullOrWhiteSpace(pin) ? "" : $"?pin={Uri.EscapeDataString(pin)}");
            using var req = CreateRequest(HttpMethod.Post, new Uri(uriText), adminToken);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);
            var node = doc.SelectSingleNode("//user") ?? doc.DocumentElement;
            return node?.Attributes?["authToken"]?.Value ?? node?.Attributes?["authenticationToken"]?.Value;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "PlexAuth: Failed to parse Switch User XML response.");
            return null;
        }
    }

    /// <summary>Retrieve account information for the provided token via the v2 API.</summary>
    public async Task<PlexAccountInfo?> GetAccountInfoAsync(string token, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/user"), token);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(content);
        return new PlexAccountInfo(doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null, doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null);
    }

    #endregion

    #region Token Management

    /// <summary>Revokes an authentication token at Plex.tv via the v2 API.</summary>
    public async Task RevokePlexTokenAsync(string token, string cid, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Delete, new Uri($"{BaseUrl}/api/v2/user/authentication"), token, cid);
            await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            Logger.Warn("Failed to revoke Plex token.");
        }
    }

    #endregion

    #region Internal Helpers

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri url, string? token = null, string? cid = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", cid ?? _config.ClientIdentifier);
        req.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayInfo.Name);
        req.Headers.TryAddWithoutValidation("X-Plex-Version", ShokoRelayInfo.Version);
        req.Headers.TryAddWithoutValidation("X-Plex-Platform", "Shoko Relay");
        req.Headers.TryAddWithoutValidation("X-Plex-Device", "Shoko Relay");
        req.Headers.TryAddWithoutValidation("X-Plex-Device-Name", ShokoRelayInfo.Name);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return req;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
            return default;
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            Logger.Warn(ex, "PlexAuth: Failed to parse JSON. Body starts with: {0}", content.Length > 512 ? content[..512] : content);
            return default;
        }
    }

    #endregion

    #region Response Wrapper

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

        [JsonPropertyName("Location")]
        public List<LocationEntry>? Locations { get; init; }
    }

    /// <summary>Represents a physical location entry for a Plex library.</summary>
    /// <param name="Id">Location ID.</param>
    /// <param name="Path">Filesystem path.</param>
    private sealed record LocationEntry(int Id, string Path);

    /// <summary>Minimal Plex account information.</summary>
    /// <param name="Title">Account title.</param>
    /// <param name="Username">Account username.</param>
    public sealed record PlexAccountInfo(string? Title, string? Username);

    /// <summary>Wrapper for the Plex Home API response.</summary>
    /// <param name="Users">The list of users in the Plex Home.</param>
    private sealed record PlexHomeResponse([property: JsonPropertyName("users")] List<PlexHomeUser>? Users);

    #endregion
}

#region PIN Response & Conv.

/// <summary>Response from the Plex PIN API containing identifiers and tokens.</summary>
public class PlexPinResponse
{
    /// <summary>The unique identifier for the PIN request.</summary>
    [JsonConverter(typeof(StringOrNumberConverter)), JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The user-facing code to be entered at plex.tv/link.</summary>
    public string? Code { get; set; }

    /// <summary>The transient authentication token.</summary>
    [JsonPropertyName("authToken")]
    public string? AuthTokenCamel { get; set; }

    /// <summary>The transient authentication token (snake_case).</summary>
    [JsonPropertyName("auth_token")]
    public string? AuthTokenSnake { get; set; }

    /// <summary>The resolved authentication token.</summary>
    [JsonIgnore]
    public string? AuthToken => AuthTokenSnake ?? AuthTokenCamel;
}

/// <summary>Custom converter to handle Plex IDs which can be returned as either integers or strings.</summary>
public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    /// <inheritdoc/>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(),
            _ => null,
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

#endregion
