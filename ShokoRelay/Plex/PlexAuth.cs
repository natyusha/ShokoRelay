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
/// <param name="HttpsRequired">True if the server requires secure connections.</param>
/// <param name="Connections">Available network connections.</param>
public sealed record PlexDevice(string? ClientIdentifier, string? Name, string? Provides, string? AccessToken, bool HttpsRequired, List<PlexDeviceConnection>? Connections);

/// <summary>Summary of a Plex server connection.</summary>
/// <param name="Id">Server identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="PreferredUri">Best connection URL.</param>
/// <param name="HttpsRequired">True if HTTPS is mandatory.</param>
public sealed record PlexServerInfo(string Id, string Name, string? PreferredUri, bool HttpsRequired);

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
    /// <param name="strong">Whether to request a high-entropy PIN.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A pin response containing ID and Code.</returns>
    public async Task<PlexPinResponse> CreatePinAsync(bool strong = true, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri($"{BaseUrl}/api/v2/pins{(strong ? "?strong=true" : "")}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    /// <summary>Construct the URL that the user must visit in order to authenticate the PIN.</summary>
    /// <param name="pinCode">The pin code string.</param>
    /// <param name="productName">Product name to show in Plex.</param>
    /// <param name="forwardUrl">Optional redirection URL.</param>
    /// <returns>An authorization URL.</returns>
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
    /// <param name="pinId">The PIN identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A pin response with current status and optional token.</returns>
    public async Task<PlexPinResponse> GetPinAsync(string pinId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/pins/{Uri.EscapeDataString(pinId)}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    #endregion

    #region Server/Lib Discovery

    /// <summary>Query the Plex.tv v2 resources API for all accessible servers.</summary>
    /// <param name="token">Plex authentication token.</param>
    /// <param name="cid">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of token validity and lists of servers/devices.</returns>
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
                // Logic: Sort connections by Locality, Protocol (HTTPS), and Relay status. If HttpsRequired is true filter out non-HTTPS URIs entirely.
                var validConnections = d.HttpsRequired ? d.Connections?.Where(c => c.Uri?.StartsWith("https") == true) : d.Connections;
                var pref = validConnections?.OrderByDescending(c => (c.Local && !c.Relay ? 100 : 0) + (c.Uri?.StartsWith("https") == true ? 10 : 0) + (!c.Relay ? 1 : 0)).FirstOrDefault()?.Uri;
                return new PlexServerInfo(d.ClientIdentifier ?? "", d.Name ?? "", pref, d.HttpsRequired);
            })
            .ToList();
        
        return (true, servers, devices);
    }

    /// <summary>Fetch the list of library sections for a specific Plex server.</summary>
    /// <param name="token">Auth token.</param>
    /// <param name="cid">Client ID.</param>
    /// <param name="url">Base server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of library info objects.</returns>
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
    /// <param name="token">Admin token.</param>
    /// <param name="cid">Client ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Discovery results containing server and library pairs.</returns>
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
                    foreach (var l in libs.Where(l => string.Equals(l.Agent, ShokoRelayConstants.AgentScheme, StringComparison.OrdinalIgnoreCase)))
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
    /// <param name="adminToken">Admin authentication token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of home users.</returns>
    public async Task<List<PlexHomeUser>> GetHomeUsersAsync(string adminToken, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/home/users"), adminToken);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var result = await ReadJsonAsync<PlexHomeResponse>(resp, ct).ConfigureAwait(false);
        return result?.Users ?? [];
    }

    /// <summary>Switch to a Plex Home managed user and return a transient token via XML.</summary>
    /// <remarks><b>IMPORTANT</b>: This old Plex endpoint always returns XML.</remarks>
    /// <param name="userId">Home user ID.</param>
    /// <param name="adminToken">Admin token.</param>
    /// <param name="pin">Optional user PIN.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user token or null.</returns>
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
    /// <param name="token">Token to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account info or null.</returns>
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
    /// <param name="token">The token to revoke.</param>
    /// <param name="cid">The client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
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
        req.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "Shoko Server"); // Title (Line 1)
        req.Headers.TryAddWithoutValidation("X-Plex-Version", ShokoRelayConstants.Version); // Label (Line 2)
        req.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayConstants.Name); // Label (Line 3)
        req.Headers.TryAddWithoutValidation("X-Plex-Device", System.Runtime.InteropServices.RuntimeInformation.OSDescription); // Label (Line 4)
        req.Headers.TryAddWithoutValidation("X-Plex-Platform", "Shoko Plugin"); // Not Shown
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
            string snippet = content.Length > 512 ? content[..512] : content;
            Logger.Warn(ex, "PlexAuth: Failed to parse JSON. Body starts with: {0}", snippet);
            return default;
        }
    }

    #endregion

    #region Response Wrapper

    /// <summary>Wrapper for the Plex library sections response.</summary>
    /// <param name="MediaContainer">The container containing section directories.</param>
    private sealed record LibrarySectionsResponse(LibrarySectionsContainer? MediaContainer);

    /// <summary>Container for library section directories.</summary>
    /// <param name="Directory">The list of section entries.</param>
    private sealed record LibrarySectionsContainer(List<LibrarySectionEntry>? Directory);

    /// <summary>Metadata describing a library section entry from the Plex API.</summary>
    /// <param name="Key">The unique numeric key for the section.</param>
    /// <param name="Title">The display title of the library.</param>
    /// <param name="Type">The media type (e.g., show, movie).</param>
    /// <param name="Agent">The identifier of the metadata agent.</param>
    /// <param name="Uuid">The unique identifier for the section.</param>
    /// <param name="Locations">List of physical filesystem root paths.</param>
    private sealed record LibrarySectionEntry(string? Key, string? Title, string? Type, string? Agent, string? Uuid, [property: JsonPropertyName("Location")] List<LocationEntry>? Locations);

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

    /// <summary>Writes the string value to the JSON output.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The string value to write.</param>
    /// <param name="options">Serializer options.</param>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

#endregion
