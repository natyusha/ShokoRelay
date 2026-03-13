using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

/// <summary>
/// HTTP client wrapper that communicates with one or more Plex servers. Handles library target discovery, section refreshes, metadata queries, and path mapping.
/// </summary>
/// <param name="httpClient">HTTP client used for communication with local or remote Plex servers.</param>
/// <param name="configProvider">Plugin configuration provider for retrieving paths and tokens.</param>
public class PlexClient(HttpClient httpClient, ConfigProvider configProvider)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _httpClient = httpClient;
    private readonly ConfigProvider _configProvider = configProvider;

    private string Token => _configProvider.GetPlexToken();
    private string ClientIdentifier => _configProvider.GetPlexClientIdentifier();
    private IReadOnlyList<PlexAvailableLibrary> DiscoveredLibraries => _configProvider.GetPlexDiscoveredLibraries();

    /// <summary>
    /// True when a Plex token exists and at least one library target has been discovered.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Token) && GetConfiguredTargets().Count > 0;

    /// <summary>
    /// Expose the configuration setting controlling whether Plex section refresh requests should trigger a scan on VFS refresh events.
    /// </summary>
    public bool ScanOnVfsRefresh => ShokoRelay.Settings.Automation.ScanOnVfsRefresh;

    /// <summary>
    /// Request Plex to refresh the specified library section path across all configured targets.
    /// </summary>
    /// <param name="path">Filesystem path to refresh.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if any refresh request was successful.</returns>
    public async Task<bool> RefreshSectionPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(path))
            return false;
        bool anyOk = false;
        foreach (var target in GetConfiguredTargets())
        {
            string mapped = MapShokoPathToPlexPath(path);
            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/refresh?path={Uri.EscapeDataString(mapped)}", target.ServerUrl);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                anyOk = true;
                Logger.Debug("Plex refresh triggered for {0} on {1}:{2}", mapped, target.ServerUrl, target.SectionId);
            }
            else
                Logger.Warn("Plex refresh failed ({0}) for {1}", resp.StatusCode, mapped);
        }
        return anyOk;
    }

    /// <summary>
    /// Create an HttpRequestMessage pre-configured with Plex authentication headers.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Relative API path.</param>
    /// <param name="baseServerUrl">The base URL of the Plex server.</param>
    /// <param name="plexUserToken">Optional per-user token override.</param>
    /// <returns>A configured HttpRequestMessage.</returns>
    public HttpRequestMessage CreateRequest(HttpMethod method, string path, string? baseServerUrl = null, string? plexUserToken = null)
    {
        if (string.IsNullOrWhiteSpace(baseServerUrl))
            throw new ArgumentException("Base server URL must be supplied", nameof(baseServerUrl));
        var req = new HttpRequestMessage(method, new Uri(new Uri(baseServerUrl.TrimEnd('/')), path));
        req.Headers.TryAddWithoutValidation("X-Plex-Token", !string.IsNullOrWhiteSpace(plexUserToken) ? plexUserToken : Token);
        if (!string.IsNullOrWhiteSpace(ClientIdentifier))
            req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return req;
    }

    /// <summary>
    /// Expose configured targets for external callers to make per-target decisions.
    /// </summary>
    public IReadOnlyList<PlexLibraryTarget> GetConfiguredTargets() =>
        [
            .. DiscoveredLibraries.Select(l => new PlexLibraryTarget
            {
                SectionId = l.Id,
                Title = l.Title,
                Type = l.Type,
                Uuid = l.Uuid,
                ServerId = l.ServerId,
                ServerName = l.ServerName,
                ServerUrl = l.ServerUrl,
                LibraryType = (l.Type ?? "").ToLowerInvariant() switch
                {
                    "movie" => PlexLibraryType.Movie,
                    "artist" => PlexLibraryType.Music,
                    "photo" => PlexLibraryType.Photo,
                    _ => PlexLibraryType.Show,
                },
            }),
        ];

    /// <summary>
    /// Find the Plex ratingKey for a Shoko series within the given Plex section.
    /// </summary>
    public async Task<int?> FindRatingKeyForShokoSeriesInSectionAsync(int shokoSeriesId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || shokoSeriesId <= 0 || target == null)
            return null;
        try
        {
            string guid = $"{ShokoRelayInfo.AgentScheme}://show/{shokoSeriesId}";
            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/search?guid={Uri.EscapeDataString(guid)}", target.ServerUrl);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var item = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
            return int.TryParse(item?.RatingKey, out int key) ? key : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check whether a given ratingKey exists in the provided target section.
    /// </summary>
    public async Task<bool> ItemExistsInSectionAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || ratingKey <= 0 || target == null)
            return false;
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Size=1", target.ServerUrl);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var meta = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
            return meta?.LibrarySectionId == target.SectionId
                || (meta != null && await GetSectionItemsAsync(target, null, cancellationToken, null, $"{ShokoRelayInfo.AgentScheme}://show/{ratingKey}").ContinueWith(t => t.Result.Any()));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// List items in the given section. Uses paging and supports optional filters.
    /// </summary>
    public async Task<List<PlexMetadataItem>> GetSectionItemsAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
        bool? onlyUnwatched = null,
        string? guid = null,
        long? minLast = null,
        int? type = null
    )
    {
        var results = new List<PlexMetadataItem>();
        if (!IsEnabled || target == null)
            return results;
        int start = 0;
        while (true)
        {
            var q = new List<string> { $"X-Plex-Container-Start={start}", "X-Plex-Container-Size=200" };
            if (type.HasValue)
                q.Add($"type={type.Value}");
            if (onlyUnwatched.HasValue)
                q.Add($"unwatched={(onlyUnwatched.Value ? 1 : 0)}");
            if (!string.IsNullOrEmpty(guid))
                q.Add($"guid={Uri.EscapeDataString(guid)}");
            if (minLast.HasValue)
                q.Add($"lastViewedAt>={minLast.Value}");

            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?{string.Join("&", q)}", target.ServerUrl, token);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var cont = resp.IsSuccessStatusCode ? await PlexApi.ReadContainerAsync(resp, ct).ConfigureAwait(false) : null;
            if (cont?.Metadata == null || cont.Metadata.Count == 0)
                break;
            results.AddRange(cont.Metadata);
            if (results.Count >= (cont.TotalSize ?? cont.Size))
                break;
            start += 200;
        }
        return results;
    }

    public Task<List<PlexMetadataItem>> GetSectionShowsAsync(PlexLibraryTarget target, CancellationToken ct = default) => GetSectionItemsAsync(target, null, ct, type: PlexConstants.TypeShow);

    public Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
        bool? onlyUnwatched = null,
        string? guidFilter = null,
        long? minLastViewed = null
    ) => GetSectionItemsAsync(target, token, ct, onlyUnwatched, guidFilter, minLastViewed, PlexConstants.TypeEpisode);

    /// <summary>
    /// Map a Shoko path to the Plex path using configured PathMappings.
    /// </summary>
    public string MapShokoPathToPlexPath(string path) => MapPath(path, ShokoRelay.Settings.Advanced.PathMappings, true);

    /// <summary>
    /// Reverse-map a Plex-style path back to the configured Shoko base path.
    /// </summary>
    public string MapPlexPathToShokoPath(string path) => MapPath(path, ShokoRelay.Settings.Advanced.PathMappings, false);

    private static string MapPath(string path, Dictionary<string, string> mappings, bool shokoToPlex)
    {
        if (string.IsNullOrWhiteSpace(path) || mappings == null || mappings.Count == 0)
            return path;
        string input = Normalize(path);
        var match = mappings
            .Select(m => new { Shoko = Normalize(m.Key), Plex = m.Value.Replace('\\', '/').TrimEnd('/') })
            .OrderByDescending(m => (shokoToPlex ? m.Shoko : m.Plex).Length)
            .FirstOrDefault(m => input.StartsWith(shokoToPlex ? m.Shoko : m.Plex, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        if (match == null)
            return path;
        string baseIn = shokoToPlex ? match.Shoko : match.Plex,
            baseOut = shokoToPlex ? match.Plex : match.Shoko;
        string rem = input.Length > baseIn.Length ? input[baseIn.Length..].TrimStart('/', '\\') : "";
        return string.IsNullOrEmpty(rem)
            ? baseOut
            : $"{baseOut.TrimEnd('/', '\\')}{(shokoToPlex ? "/" : Path.DirectorySeparatorChar.ToString())}{rem.Replace(shokoToPlex ? Path.DirectorySeparatorChar : '/', shokoToPlex ? '/' : Path.DirectorySeparatorChar)}";
    }

    private static string Normalize(string p)
    {
        try
        {
            return Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return p.TrimEnd('/', '\\');
        }
    }
}
