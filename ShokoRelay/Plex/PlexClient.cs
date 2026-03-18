using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

/// <summary>HTTP client wrapper that communicates with one or more Plex servers.</summary>
public class PlexClient(HttpClient httpClient, ConfigProvider configProvider)
{
    #region Fields & Properties

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private string Token => configProvider.GetPlexToken();
    private string ClientIdentifier => configProvider.GetPlexClientIdentifier();
    private IReadOnlyList<PlexAvailableLibrary> DiscoveredLibraries => configProvider.GetPlexDiscoveredLibraries();

    /// <summary>True when a Plex token exists and at least one library target has been discovered.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Token) && GetConfiguredTargets().Count > 0;

    /// <summary>Expose the configuration setting controlling automatic library scans.</summary>
    public bool ScanOnVfsRefresh => ShokoRelay.Settings.Automation.ScanOnVfsRefresh;

    #endregion

    #region Library & Section

    /// <summary>Request Plex to refresh a specific filesystem path, optimized to matching sections with a safety fallback.</summary>
    /// <param name="path">The Shoko-side filesystem path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one refresh request was successful.</returns>
    public async Task<bool> RefreshSectionPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(path))
            return false;

        string mapped = MapShokoPathToPlexPath(path);
        string normMapped = mapped.Replace('\\', '/').TrimEnd('/');

        // Use only the folder name (ShokoSeriesID) for cleaner logging
        string logFolderName = Path.GetFileName(normMapped);

        var allTargets = GetConfiguredTargets();
        var matchingTargets = allTargets.Where(target => target.Locations.Any(loc => normMapped.StartsWith(loc.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase))).ToList();

        var targetsToProcess = matchingTargets.Any() ? matchingTargets : allTargets;

        bool anyOk = false;
        foreach (var target in targetsToProcess)
        {
            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/refresh?path={Uri.EscapeDataString(mapped)}", target.ServerUrl);
            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                anyOk = true;
                Logger.Debug("Plex refresh triggered for folder '{0}' on {1}:{2} (Match: {3})", logFolderName, target.ServerUrl, target.SectionId, matchingTargets.Any());
            }
            else
            {
                Logger.Warn("Plex refresh failed ({0}) for folder '{1}' in section {2}", resp.StatusCode, logFolderName, target.SectionId);
            }
        }
        return anyOk;
    }

    /// <summary>Request Plex to re-run the metadata agent for a specific item (e.g., to fix missing initial metadata).</summary>
    /// <param name="ratingKey">Plex unique rating key.</param>
    /// <param name="target">Target server/section.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the refresh request was successful.</returns>
    public async Task<bool> RefreshMetadataAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || ratingKey <= 0 || target == null)
            return false;

        using var req = CreateRequest(HttpMethod.Put, $"/library/metadata/{ratingKey}/refresh", target.ServerUrl);
        using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);

        return resp.IsSuccessStatusCode;
    }

    #endregion

    #region Request Construction

    /// <summary>Create an HttpRequestMessage pre-configured with Plex authentication headers.</summary>
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

    #endregion

    #region Target Discovery

    /// <summary>Returns a list of library targets based on discovered configuration.</summary>
    /// <returns>A read-only list of configured library targets.</returns>
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
                Locations = l.Locations ?? [],
            }),
        ];

    #endregion

    #region Metadata Queries

    /// <summary>Check whether a given ratingKey exists in the provided target section.</summary>
    /// <param name="ratingKey">Plex rating key.</param>
    /// <param name="target">Target server/section.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the item is present in the section.</returns>
    public async Task<bool> ItemExistsInSectionAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || ratingKey <= 0 || target == null)
            return false;
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Size=1", target.ServerUrl);
            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var meta = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
            return meta?.LibrarySectionId == target.SectionId
                || (meta != null && await GetSectionItemsAsync(target, null, cancellationToken, null, $"{ShokoRelayConstants.AgentScheme}://show/{ratingKey}").ContinueWith(t => t.Result.Any()));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Find the Plex ratingKey for a Shoko series within the given Plex section using its metadata GUID.</summary>
    /// <param name="shokoSeriesId">Shoko series ID.</param>
    /// <param name="target">Target server/section.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The numeric rating key if found, otherwise null.</returns>
    public async Task<int?> FindRatingKeyForShokoSeriesInSectionAsync(int shokoSeriesId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || shokoSeriesId <= 0 || target == null)
            return null;
        try
        {
            string guid = $"{ShokoRelayConstants.AgentScheme}://show/{shokoSeriesId}";
            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?guid={Uri.EscapeDataString(guid)}", target.ServerUrl);
            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var item = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
            return int.TryParse(item?.RatingKey, out int key) ? key : null;
        }
        catch (Exception ex)
        {
            Logger.Trace(ex, "Failed to find rating key for Shoko series {0} in section {1}", shokoSeriesId, target.SectionId);
            return null;
        }
    }

    /// <summary>List items in the given section with optional filters.</summary>
    /// <param name="target">Target server/section.</param>
    /// <param name="token">Optional token override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <param name="type">Plex type identifier.</param>
    /// <returns>A list of metadata items.</returns>
    public async Task<List<PlexMetadataItem>> GetSectionItemsAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
        bool? onlyUnwatched = null,
        string? guidFilter = null,
        long? minLastViewed = null,
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
            if (!string.IsNullOrEmpty(guidFilter))
                q.Add($"guid={Uri.EscapeDataString(guidFilter)}");
            if (minLastViewed.HasValue)
                q.Add($"lastViewedAt>={minLastViewed.Value}");

            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?{string.Join("&", q)}", target.ServerUrl, token);
            using var resp = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
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

    /// <summary>List all shows in the given section.</summary>
    /// <param name="target">Target library.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionShowsAsync(PlexLibraryTarget target, CancellationToken ct = default) => GetSectionItemsAsync(target, null, ct, type: PlexConstants.TypeShow);

    /// <summary>List all episodes in the given section with optional filters.</summary>
    /// <param name="target">Target library.</param>
    /// <param name="token">Token override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
        bool? onlyUnwatched = null,
        string? guidFilter = null,
        long? minLastViewed = null
    ) => GetSectionItemsAsync(target, token, ct, onlyUnwatched, guidFilter, minLastViewed, PlexConstants.TypeEpisode);

    #endregion

    #region Path Mapping

    /// <summary>Map a Shoko path to the Plex path using configured mappings.</summary>
    /// <param name="path">Input Shoko path.</param>
    /// <returns>The mapped Plex path.</returns>
    public string MapShokoPathToPlexPath(string path) => MapPath(path, ShokoRelay.Settings.Advanced.PathMappings, true);

    /// <summary>Reverse-map a Plex path back to Shoko.</summary>
    /// <param name="path">Input Plex path.</param>
    /// <returns>The original Shoko path.</returns>
    public string MapPlexPathToShokoPath(string path) => MapPath(path, ShokoRelay.Settings.Advanced.PathMappings, false);

    private static string MapPath(string path, Dictionary<string, string> mappings, bool shokoToPlex)
    {
        if (string.IsNullOrWhiteSpace(path) || mappings == null || mappings.Count == 0)
            return path;

        string input = path.Replace('\\', '/').TrimEnd('/');

        var match = mappings
            .Select(m => new { Shoko = m.Key.Replace('\\', '/').TrimEnd('/'), Plex = m.Value.Replace('\\', '/').TrimEnd('/') })
            .OrderByDescending(m => (shokoToPlex ? m.Shoko : m.Plex).Length)
            .FirstOrDefault(m => input.StartsWith(shokoToPlex ? m.Shoko : m.Plex, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            return path;

        string baseIn = shokoToPlex ? match.Shoko : match.Plex;
        string baseOut = shokoToPlex ? match.Plex : match.Shoko;
        string remainder = input.Length > baseIn.Length ? input[baseIn.Length..].TrimStart('/') : "";

        string result = string.IsNullOrEmpty(remainder) ? baseOut : $"{baseOut.TrimEnd('/')}/{remainder}";

        return shokoToPlex ? result : result.Replace('/', Path.DirectorySeparatorChar);
    }

    #endregion
}
