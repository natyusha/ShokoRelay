using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

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
/// <param name="Owned">True if the server is owned by the user; false if it is a shared server.</param>
public sealed record PlexDevice(string? ClientIdentifier, string? Name, string? Provides, string? AccessToken, bool HttpsRequired, List<PlexDeviceConnection>? Connections, bool Owned);

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

/// <summary>Record representing a user with shared server access from Plex.tv.</summary>
/// <param name="Id">Shared server entry ID.</param>
/// <param name="UserId">Plex user ID.</param>
/// <param name="Username">Plex username.</param>
/// <param name="Email">Plex user email address.</param>
/// <param name="AccessToken">Server-scoped access token for this user.</param>
public sealed record PlexSharedServerUser(string? Id, string? UserId, string? Username, string? Email, string? AccessToken);

/// <summary>Record representing a Plex friend/user from the Plex.tv users list.</summary>
/// <param name="Id">Plex user ID.</param>
/// <param name="Title">Display title.</param>
/// <param name="Username">Actual username.</param>
/// <param name="Email">Email address.</param>
public sealed record PlexFriendUser(string? Id, string? Title, string? Username, string? Email);

#endregion

/// <summary>Handles authentication with Plex.tv, utilizing modern v2 JSON endpoints and legacy XML for user switching.</summary>
/// <param name="httpClient">HTTP client.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class PlexAuth(HttpClient httpClient, ConfigProvider configProvider, ILogger<PlexAuth> logger)
{
    #region Setup

    private const string BaseUrl = "https://plex.tv";
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    #endregion

    #region PIN Operations

    /// <summary>Request a new Plex authentication PIN from the Plex.tv v2 API.</summary>
    /// <param name="strong">Whether to request a high-entropy PIN.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A pin response containing ID and Code.</returns>
    public async Task<PlexPinResponse> CreatePinAsync(bool strong = true, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri($"{BaseUrl}/api/v2/pins{(strong ? "?strong=true" : "")}"));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    /// <summary>Construct the URL that the user must visit in order to authenticate the PIN.</summary>
    /// <param name="pinCode">The pin code string.</param>
    /// <param name="productName">Product name to show in Plex.</param>
    /// <param name="forwardUrl">Optional redirection URL.</param>
    /// <returns>An authorization URL.</returns>
    public string BuildAuthUrl(string pinCode, string productName, string? forwardUrl = null) =>
        pinCode.Length <= 4
            ? $"https://plex.tv/link/?pin={Uri.EscapeDataString(pinCode)}"
            : $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(configProvider.GetPlexClientIdentifier())}&code={Uri.EscapeDataString(pinCode)}&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(productName)}{(string.IsNullOrWhiteSpace(forwardUrl) ? "" : $"&forwardUrl={Uri.EscapeDataString(forwardUrl)}")}";

    /// <summary>Retrieve the status for a specific Plex PIN from the v2 API.</summary>
    /// <param name="pinId">The PIN identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A pin response with current status and optional token.</returns>
    public async Task<PlexPinResponse> GetPinAsync(string pinId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/pins/{Uri.EscapeDataString(pinId)}"));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<PlexPinResponse>(response, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Plex pin response was empty.");
    }

    #endregion

    #region Server/Lib Discovery

    /// <summary>Query the Plex.tv v2 resources API for all accessible servers.</summary>
    /// <param name="token">Plex authentication token.</param>
    /// <param name="cid">Client identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Tuple of token validity and lists of servers/devices.</returns>
    public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<PlexDevice> Devices)> GetPlexServerListAsync(string token, string cid, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri("https://clients.plex.tv/api/v2/resources"), token, cid);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden, [], []);

        var devices = await ReadJsonAsync<List<PlexDevice>>(response, cancellationToken).ConfigureAwait(false) ?? [];

        var servers = devices
            .Where(device => device.Owned && device.Provides?.Contains("server", StringComparison.OrdinalIgnoreCase) == true)
            .Select(device =>
            {
                var validConnections = device.HttpsRequired ? device.Connections?.Where(conn => conn.Uri?.StartsWith("https", StringComparison.Ordinal) == true) : device.Connections;

                var pref = validConnections
                    ?.OrderByDescending(conn =>
                        (conn.Local && !conn.Relay ? 100 : 0)
                        + (!conn.Relay ? 1 : 0)
                        + (conn.Uri?.StartsWith("https", StringComparison.Ordinal) == true && (conn.Uri.Contains(".plex.direct", StringComparison.OrdinalIgnoreCase) || device.HttpsRequired) ? 10 : 0)
                    )
                    .FirstOrDefault()
                    ?.Uri;

                return new PlexServerInfo(device.ClientIdentifier ?? "", device.Name ?? "", pref, device.HttpsRequired);
            })
            .ToList();

        return (true, servers, devices);
    }

    /// <summary>Fetch the list of library sections for a specific Plex server.</summary>
    /// <param name="token">Auth token.</param>
    /// <param name="cid">Client ID.</param>
    /// <param name="url">Base server URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of library info objects.</returns>
    public async Task<List<PlexLibraryInfo>> GetPlexLibrariesAsync(string token, string cid, string url, CancellationToken cancellationToken = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{url.TrimEnd('/')}/library/sections"), token, cid);
        using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var wrapper = await ReadJsonAsync<LibrarySectionsResponse>(resp, cancellationToken).ConfigureAwait(false);
        return wrapper
                ?.MediaContainer?.Directory?.Select(directory => new PlexLibraryInfo(
                    int.TryParse(directory.Key, out int id) ? id : 0,
                    directory.Title ?? "",
                    directory.Type ?? "",
                    directory.Agent ?? "",
                    directory.Uuid ?? "",
                    directory.Locations?.Select(loc => loc.Path).ToList() ?? []
                ))
                .Where(lib => lib.Id > 0)
                .ToList()
            ?? [];
    }

    /// <summary>Discover Shoko-enabled libraries across all accessible Plex servers.</summary>
    /// <param name="token">Admin token.</param>
    /// <param name="cid">Client ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Discovery results containing server and library pairs.</returns>
    public async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries)> DiscoverShokoLibrariesAsync(
        string token,
        string cid,
        CancellationToken cancellationToken = default
    )
    {
        var (tokenValid, servers, devices) = await GetPlexServerListAsync(token, cid, cancellationToken).ConfigureAwait(false);
        var list = new List<(PlexLibraryInfo, PlexServerInfo)>();
        if (tokenValid)
        {
            async Task<bool> TryConnectAsync(string uri, PlexServerInfo srv, bool isFallback)
            {
                try
                {
                    // Create a linked token source that cancels after 5 seconds to prevent offline servers from hanging discovery
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    var libs = await GetPlexLibrariesAsync(token, cid, uri, cts.Token).ConfigureAwait(false);
                    var matched = libs.Where(lib =>
                            string.Equals(lib.Agent, ShokoRelayConstants.AgentScheme, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(lib.Agent, ShokoRelayConstants.MovieAgentScheme, StringComparison.OrdinalIgnoreCase)
                        )
                        .ToList();
                    foreach (var lib in matched)
                        list.Add((lib, srv with { PreferredUri = uri }));

                    if (matched.Count > 0)
                        logger.LogInformation("Plex Discovery: Connected to server -> {Name} ... {Uri} (Type: {Type}, Libraries: {Count})", srv.Name, uri, isFallback ? "Fallback" : "Preferred", matched.Count);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Plex Discovery: Connection to {Uri} failed for server -> {Name} ... {Error}", uri, srv.Name, ex.GetBaseException().Message);
                    return false;
                }
            }

            for (int srvIdx = 0; srvIdx < servers.Count; srvIdx++)
            {
                var srv = servers[srvIdx];
                string? customUrl = !string.IsNullOrWhiteSpace(Settings.Advanced.PlexServerUrl) ? Settings.Advanced.PlexServerUrl.Trim().TrimEnd('/') : null;
                var ok =
                    (!string.IsNullOrEmpty(customUrl) && await TryConnectAsync(customUrl, srv with { PreferredUri = customUrl }, false).ConfigureAwait(false))
                    || (!string.IsNullOrEmpty(srv.PreferredUri) && await TryConnectAsync(srv.PreferredUri, srv, false).ConfigureAwait(false));

                // Fallback: If preferred URI failed, try other endpoints
                if (!ok && devices.FirstOrDefault(device => string.Equals(device.ClientIdentifier, srv.Id, StringComparison.OrdinalIgnoreCase)) is { } dev && dev.Connections != null)
                {
                    var conns = srv.HttpsRequired ? dev.Connections.Where(conn => conn.Uri?.StartsWith("https", StringComparison.Ordinal) == true) : dev.Connections;
                    foreach (var conn in conns)
                    {
                        if (string.IsNullOrEmpty(conn.Uri) || string.Equals(conn.Uri, srv.PreferredUri, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var workingSrv = srv with { PreferredUri = conn.Uri };
                        if (await TryConnectAsync(conn.Uri, workingSrv, true).ConfigureAwait(false))
                        {
                            servers[srvIdx] = workingSrv;
                            ok = true;
                            break;
                        }
                    }
                }

                if (!ok)
                    logger.LogWarning("Plex Discovery: Failed to establish any connection to server -> {Name} ... check for DNS rebinding protection, firewall rules, or docker network isolation", srv.Name);
            }
        }
        return (tokenValid, servers, list);
    }

    #endregion

    #region Home & User Mgmt.

    /// <summary>Retrieve all managed/home users associated with the account via the v2 API.</summary>
    /// <param name="adminToken">Admin authentication token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of home users.</returns>
    public async Task<List<PlexHomeUser>> GetHomeUsersAsync(string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/home/users"), adminToken);
        using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var result = await ReadJsonAsync<PlexHomeResponse>(resp, cancellationToken).ConfigureAwait(false);
        return result?.Users ?? [];
    }

    /// <summary>Retrieves all shared server entries (regular Plex friends and Home users) for a given server via XML or JSON.</summary>
    /// <param name="serverId">The Plex server machine/client identifier.</param>
    /// <param name="adminToken">The admin Plex authentication token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of <see cref="PlexSharedServerUser"/> entries.</returns>
    public async Task<List<PlexSharedServerUser>> GetSharedServersAsync(string serverId, string adminToken, CancellationToken cancellationToken = default)
    {
        var users = new List<PlexSharedServerUser>();
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(adminToken))
            return users;

        try
        {
            using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/servers/{Uri.EscapeDataString(serverId)}/shared_servers"), adminToken, acceptJson: false);
            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("PlexAuth: Failed to query Shared Servers for server {ServerId} (HTTP {StatusCode})", serverId, resp.StatusCode);
                return users;
            }

            var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return users;

            if (content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('['))
            {
                using var json = JsonDocument.Parse(content);
                var items =
                    json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray()
                    : json.RootElement.TryGetProperty("MediaContainer", out var container) && container.TryGetProperty("SharedServer", out var sharedServers) ? sharedServers.EnumerateArray()
                    : default;
                foreach (var element in items)
                    users.Add(
                        new PlexSharedServerUser(
                            GetJsonString(element, "id"),
                            GetJsonString(element, "userID") ?? GetJsonString(element, "userId"),
                            GetJsonString(element, "username") ?? GetJsonString(element, "name"),
                            GetJsonString(element, "email"),
                            GetJsonString(element, "accessToken")
                        )
                    );
                return users;
            }

            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(content);
            var nodes = doc.SelectNodes("//SharedServer");
            if (nodes == null)
                return users;

            foreach (XmlNode node in nodes)
            {
                if (node.Attributes is not { } attrs)
                    continue;
                users.Add(new PlexSharedServerUser(attrs["id"]?.Value, attrs["userID"]?.Value, attrs["username"]?.Value ?? attrs["name"]?.Value, attrs["email"]?.Value, attrs["accessToken"]?.Value));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PlexAuth: Failed to parse Shared Servers response for server {ServerId}", serverId);
        }

        return users;
    }

    /// <summary>Retrieves the Plex.tv friend/user list to map display titles to usernames and user IDs.</summary>
    /// <param name="adminToken">The admin Plex authentication token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of <see cref="PlexFriendUser"/> entries.</returns>
    public async Task<List<PlexFriendUser>> GetFriendsAsync(string adminToken, CancellationToken cancellationToken = default)
    {
        var friends = new List<PlexFriendUser>();
        if (string.IsNullOrWhiteSpace(adminToken))
            return friends;

        try
        {
            using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/users"), adminToken, acceptJson: false);
            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogDebug("PlexAuth: Failed to query Plex users list (HTTP {StatusCode})", resp.StatusCode);
                return friends;
            }

            var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return friends;

            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(content);
            if (doc.SelectNodes("//User") is not { } nodes)
                return friends;

            foreach (XmlNode node in nodes)
            {
                if (node.Attributes is not { } attrs)
                    continue;
                friends.Add(new PlexFriendUser(attrs["id"]?.Value, attrs["title"]?.Value, attrs["username"]?.Value, attrs["email"]?.Value));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PlexAuth: Failed to parse Plex users list");
        }

        return friends;
    }

    /// <summary>Switch to a Plex Home managed user and return a transient token via v1 XML or v2 JSON endpoints.</summary>
    /// <param name="userId">Home user ID.</param>
    /// <param name="adminToken">Admin token.</param>
    /// <param name="pin">Optional user PIN.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="userUuid">Optional Home user UUID for v2 switch fallback.</param>
    /// <returns>The user token or null.</returns>
    public async Task<string?> SwitchHomeUserAsync(int userId, string adminToken, string? pin = null, CancellationToken cancellationToken = default, string? userUuid = null)
    {
        string pinQuery = string.IsNullOrWhiteSpace(pin) ? "" : $"?pin={Uri.EscapeDataString(pin)}";
        return await TrySwitchAsync(new Uri($"{BaseUrl}/api/home/users/{userId}/switch{pinQuery}"), false).ConfigureAwait(false) is { Length: > 0 } v1Token ? v1Token
            : !string.IsNullOrWhiteSpace(userUuid) ? await TrySwitchAsync(new Uri($"https://clients.plex.tv/api/v2/home/users/{Uri.EscapeDataString(userUuid)}/switch{pinQuery}"), true).ConfigureAwait(false)
            : null;

        async Task<string?> TrySwitchAsync(Uri uri, bool acceptJson)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Post, uri, adminToken, acceptJson: acceptJson);
                using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("PlexAuth: SwitchHomeUser failed for user {UserId} at {Path} (HTTP {StatusCode})", userId, uri.AbsolutePath, resp.StatusCode);
                    return null;
                }

                var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                if (content.TrimStart().StartsWith('{'))
                {
                    using var json = JsonDocument.Parse(content);
                    return GetJsonString(json.RootElement, "authToken") ?? GetJsonString(json.RootElement, "auth_token");
                }

                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(content);
                var node = doc.SelectSingleNode("//user") ?? doc.DocumentElement;
                return node?.Attributes?["authToken"]?.Value ?? node?.Attributes?["authenticationToken"]?.Value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PlexAuth: Failed to parse Switch User response at {Path}", uri.AbsolutePath);
                return null;
            }
        }
    }

    /// <summary>Retrieve account information for the provided token via the v2 API.</summary>
    /// <param name="token">Token to query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Account info or null.</returns>
    public async Task<PlexAccountInfo?> GetAccountInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        using var req = CreateRequest(HttpMethod.Get, new Uri($"{BaseUrl}/api/v2/user"), token);
        using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(content);
        return new PlexAccountInfo(
            doc.RootElement.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null,
            doc.RootElement.TryGetProperty("username", out var userProp) ? userProp.GetString() : null
        );
    }

    #endregion

    #region Token Management

    /// <summary>Revokes an authentication token at Plex.tv via the v2 API.</summary>
    /// <param name="token">The token to revoke.</param>
    /// <param name="cid">The client identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task RevokePlexTokenAsync(string token, string cid, CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Delete, new Uri($"{BaseUrl}/api/v2/user/authentication"), token, cid);
            await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            logger.LogWarning("PlexAuth: Failed to revoke Plex token");
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Creates an HTTP request message pre-configured with the required Plex application headers and optional authentication.</summary>
    /// <param name="method">The HTTP method to use for the request.</param>
    /// <param name="url">The fully qualified destination URI.</param>
    /// <param name="token">An optional Plex authentication token to embed in headers.</param>
    /// <param name="cid">An optional Plex client identifier override.</param>
    /// <param name="acceptJson">Whether to request <c>application/json</c>; false requests <c>application/xml</c>.</param>
    /// <returns>A pre-configured HttpRequestMessage instance.</returns>
    private HttpRequestMessage CreateRequest(HttpMethod method, Uri url, string? token = null, string? cid = null, bool acceptJson = true)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", cid ?? configProvider.GetPlexClientIdentifier());
        req.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "Shoko Server");
        req.Headers.TryAddWithoutValidation("X-Plex-Version", ShokoRelayConstants.Version);
        req.Headers.TryAddWithoutValidation("X-Plex-Product", ShokoRelayConstants.Name);
        req.Headers.TryAddWithoutValidation("X-Plex-Device", RuntimeInformation.OSDescription);
        req.Headers.TryAddWithoutValidation("X-Plex-Platform", "Shoko Plugin");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptJson ? "application/json" : "application/xml"));
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return req;
    }

    /// <summary>Extracts a property from a JSON element as a string regardless of whether it is encoded as a string or number.</summary>
    /// <param name="element">The JSON object element.</param>
    /// <param name="propertyName">The property name to look up.</param>
    /// <returns>The property value as a string, or null if absent.</returns>
    private static string? GetJsonString(JsonElement element, string propertyName) =>
        !element.TryGetProperty(propertyName, out var prop) ? null
        : prop.ValueKind == JsonValueKind.String ? prop.GetString()
        : prop.ValueKind == JsonValueKind.Number ? prop.GetRawText()
        : null;

    /// <summary>Reads and deserializes a JSON HTTP response body into a strongly-typed object with error logging.</summary>
    /// <typeparam name="T">The target type to deserialize the response content into.</typeparam>
    /// <param name="resp">The HTTP response message to read from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the async operation containing the deserialized object, or default on failure.</returns>
    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        if (!resp.IsSuccessStatusCode)
            return default;
        var content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(content, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            string snippet = content.Length > 512 ? content[..512] : content;
            logger.LogWarning(ex, "PlexAuth: Failed to parse JSON -> Body starts with {Snippet}", snippet);
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
            JsonTokenType.Number => reader.TryGetInt64(out long longVal) ? longVal.ToString() : reader.GetDouble().ToString(),
            _ => null,
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

#endregion
