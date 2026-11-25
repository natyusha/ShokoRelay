using System.Text.Json;

namespace ShokoRelay.Plex;

/// <summary>HTTP client wrapper that communicates with one or more Plex servers.</summary>
/// <param name="httpClient">HTTP client.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class PlexClient(HttpClient httpClient, ConfigProvider configProvider, ILogger<PlexClient> logger)
{
    #region Setup & Properties

    private string Token => configProvider.GetPlexToken();
    private string ClientIdentifier => configProvider.GetPlexClientIdentifier();
    private IReadOnlyList<PlexAvailableLibrary> DiscoveredLibraries => configProvider.GetPlexDiscoveredLibraries();

    /// <summary>True when a Plex token exists and at least one library target has been discovered.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Token) && GetConfiguredTargets().Count > 0;

    /// <summary>Cached array of prioritized Shoko-to-Plex path mappings.</summary>
    private (string In, string Out)[] _shokoToPlexMappings = [];

    /// <summary>Cached array of prioritized Plex-to-Shoko path mappings.</summary>
    private (string In, string Out)[] _plexToShokoMappings = [];

    /// <summary>Reference to the last cached raw path mappings dictionary used to detect configuration changes.</summary>
    private Dictionary<string, string>? _lastCachedMappings;

    /// <summary>Exclusive lock used to ensure thread-safe path mapping cache compilation.</summary>
    private readonly Lock _mappingLock = new();

    /// <summary>Cached list of compiled library targets.</summary>
    private List<PlexLibraryTarget> _cachedTargets = [];

    /// <summary>Reference to the last evaluated discovered libraries list used for cache invalidation.</summary>
    private IReadOnlyList<PlexAvailableLibrary>? _lastDiscoveredLibraries;

    /// <summary>Reference to the last evaluated custom Plex URL used for cache invalidation.</summary>
    private string? _lastConfiguredPlexUrl;

    /// <summary>Exclusive lock used to ensure thread-safe library target cache compilation.</summary>
    private readonly Lock _targetsLock = new();

    #endregion

    #region Library & Section

    /// <summary>Finds Plex library targets that match the given Shoko VFS path, returning the target and the mapped path to refresh.</summary>
    /// <param name="path">The Shoko-side filesystem path.</param>
    /// <returns>A list of matching targets and their localized paths.</returns>
    public List<(PlexLibraryTarget Target, string PathToRefresh)> GetMatchingTargetsForPath(string path)
    {
        var results = new List<(PlexLibraryTarget Target, string PathToRefresh)>();
        if (string.IsNullOrWhiteSpace(path))
            return results;

        string mapped = MapShokoPathToPlexPath(path);
        string normalizedMappedPath = PlexHelper.NormalizePathForPlex(mapped);
        var allTargets = GetConfiguredTargets();

        var exactMatches = allTargets
            .Where(target =>
                target.Locations.Any(location =>
                {
                    string normalizedLocation = PlexHelper.NormalizePathForPlex(location);
                    return normalizedMappedPath.Equals(normalizedLocation, StringComparison.OrdinalIgnoreCase) || normalizedMappedPath.StartsWith(normalizedLocation + "/", StringComparison.OrdinalIgnoreCase);
                })
            )
            .ToList();
        if (exactMatches.Count > 0)
            return [.. exactMatches.Select(target => (target, mapped))];

        // If Path Mappings aren't configured, safely infer the correct Plex path by matching the VFS root name to Plex's configured library locations.
        string vfsRootName = VfsShared.ResolveRootFolderName();
        string movieRootName = VfsShared.ResolveMovieRootFolderName();

        int GetRootIndex(string rootName) =>
            normalizedMappedPath.StartsWith(rootName + "/", StringComparison.OrdinalIgnoreCase) ? 0 : normalizedMappedPath.IndexOf($"/{rootName}/", StringComparison.OrdinalIgnoreCase);

        int vfsIndex = GetRootIndex(movieRootName);
        string? activeRootName = vfsIndex >= 0 ? movieRootName : ((vfsIndex = GetRootIndex(vfsRootName)) >= 0 ? vfsRootName : null);

        if (vfsIndex >= 0 && activeRootName != null)
        {
            int tokenLength = vfsIndex == 0 ? activeRootName.Length + 1 : activeRootName.Length + 2;
            string relativeSuffix = normalizedMappedPath[(vfsIndex + tokenLength)..];

            // Extract Shoko's parent directory name preceding the VFS root
            string shokoParentName =
                vfsIndex > 0 && normalizedMappedPath.LastIndexOf('/', vfsIndex - 1) is int parentStartIndex && parentStartIndex >= 0 ? normalizedMappedPath[(parentStartIndex + 1)..vfsIndex] : "";

            var potentialFallbacks = new List<(PlexLibraryTarget Target, string GuessedPath, bool IsParentMatch)>();

            foreach (var target in allTargets)
            {
                foreach (var location in target.Locations)
                {
                    string normalizedLocation = PlexHelper.NormalizePathForPlex(location);
                    string locationSuffix = $"/{activeRootName}";

                    if (normalizedLocation.EndsWith(locationSuffix, StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedLocation, activeRootName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Detect Plex's native directory separator and format the path to match the target OS
                        char plexSeparator = location.Contains('\\') ? '\\' : '/';
                        string nativeSuffix = relativeSuffix.Replace('/', plexSeparator);
                        string guessedPath = $"{location.TrimEnd('\\', '/')}{plexSeparator}{nativeSuffix}";

                        // Extract Plex's parent directory name preceding the VFS root
                        int plexEndIndex = normalizedLocation.LastIndexOf(locationSuffix, StringComparison.OrdinalIgnoreCase);
                        string plexParentName =
                            plexEndIndex > 0 && normalizedLocation.LastIndexOf('/', plexEndIndex - 1) is int plexStartIndex && plexStartIndex >= 0 ? normalizedLocation[(plexStartIndex + 1)..plexEndIndex] : "";

                        bool isParentMatch = !string.IsNullOrEmpty(shokoParentName) && string.Equals(shokoParentName, plexParentName, StringComparison.OrdinalIgnoreCase);
                        potentialFallbacks.Add((target, guessedPath, isParentMatch));
                    }
                }
            }

            // Filter to only parent-matching locations if any exist; otherwise fallback to broad scan
            bool hasParentMatch = potentialFallbacks.Any(fallback => fallback.IsParentMatch);
            results.AddRange(potentialFallbacks.Where(fallback => !hasParentMatch || fallback.IsParentMatch).Select(fallback => (fallback.Target, fallback.GuessedPath)));
        }

        return results;
    }

    /// <summary>Request Plex to refresh a specific filesystem path, optimized to matching sections with an automatic path mapping fallback.</summary>
    /// <param name="path">The Shoko-side filesystem path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one refresh request was successful.</returns>
    public async Task<bool> RefreshSectionPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(path))
            return false;

        var targets = GetMatchingTargetsForPath(path);
        bool anyOk = false;

        foreach (var (target, guessedPath) in targets)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/refresh?path={Uri.EscapeDataString(guessedPath)}", target.ServerUrl);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    anyOk = true;
                    logger.LogDebug("PlexClient: refresh triggered for -> '{Path}' on {ServerUrl}:{SectionId}", guessedPath, target.ServerUrl, target.SectionId);
                }
                else
                    logger.LogWarning("PlexClient: refresh failed ({StatusCode}) for folder -> '{Path}' in section {SectionId}", response.StatusCode, guessedPath, target.SectionId);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("PlexClient: Network timeout triggering refresh for '{Path}' in section {SectionId}", guessedPath, target.SectionId);
            }
        }

        if (!anyOk && targets.Count == 0)
            logger.LogWarning(
                "PlexClient: Path '{Path}' does not match any known Plex library locations! If Plex and Shoko run on different filesystems, please configure Path Mappings in the Shoko Relay dashboard.",
                PlexHelper.NormalizePathForPlex(MapShokoPathToPlexPath(path))
            );

        return anyOk;
    }

    /// <summary>Request Plex to re-run the metadata agent for a specific item (e.g., to fix missing initial metadata).</summary>
    /// <param name="ratingKey">The Plex rating key.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the refresh request was successful.</returns>
    public Task<bool> RefreshMetadataAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        PutMetadataCommandAsync(ratingKey, "refresh", "Metadata refresh", target, cancellationToken);

    /// <summary>Triggers a Plex metadata refresh across all configured library targets for the specified Shoko series ID.</summary>
    /// <param name="shokoSeriesId">The Shoko series ID.</param>
    /// <param name="metadataService">Shoko metadata service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count of rating keys refreshed.</returns>
    public async Task<int> RefreshSeriesMetadataAsync(int shokoSeriesId, IMetadataService metadataService, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || shokoSeriesId <= 0)
            return 0;

        int refreshed = 0;
        foreach (var target in GetConfiguredTargets())
        {
            var ratingKeys = await FindRatingKeysForShokoSeriesInSectionAsync(shokoSeriesId, target, metadataService, cancellationToken).ConfigureAwait(false);
            foreach (var ratingKey in ratingKeys)
                if (await RefreshMetadataAsync(ratingKey, target, cancellationToken).ConfigureAwait(false))
                    refreshed++;
        }
        return refreshed;
    }

    /// <summary>Request Plex to analyze the media for a specific item (e.g., to regenerate thumbnails or detect stream changes after a file replacement).</summary>
    /// <param name="ratingKey">The Plex rating key.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the analyze request was successful.</returns>
    public Task<bool> AnalyzeItemAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        PutMetadataCommandAsync(ratingKey, "analyze", "Media analysis", target, cancellationToken);

    /// <summary>Sends a PUT command to a Plex metadata item endpoint (such as <c>refresh</c> or <c>analyze</c>).</summary>
    /// <param name="ratingKey">The Plex rating key.</param>
    /// <param name="command">The metadata sub-command segment.</param>
    /// <param name="commandLabel">Human-readable label used in log messages.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the command request succeeded.</returns>
    private async Task<bool> PutMetadataCommandAsync(int ratingKey, string command, string commandLabel, PlexLibraryTarget target, CancellationToken cancellationToken)
    {
        if (!IsEnabled || ratingKey <= 0 || target == null)
            return false;

        try
        {
            using var request = CreateRequest(HttpMethod.Put, $"/library/metadata/{ratingKey}/{command}", target.ServerUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                logger.LogDebug("PlexClient: {Command} triggered for RatingKey {RatingKey} on {ServerName}", commandLabel, ratingKey, target.ServerName);
            else
                logger.LogWarning("PlexClient: {Command} failed ({StatusCode}) for RatingKey {RatingKey} on {ServerName}", commandLabel, response.StatusCode, ratingKey, target.ServerName);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("PlexClient: Network timeout triggering {Command} for RatingKey {RatingKey}", commandLabel, ratingKey);
            return false;
        }
    }

    /// <summary>Evaluates and safely empties the trash for a given Plex library section based on a percentage threshold of items.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="threshold">The maximum allowed percentage of trashed items (1-100).</param>
    /// <param name="dryRun">If true, prevents the actual empty trash command from being sent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing success status, the list of trashed item display names, and a status message.</returns>
    public async Task<(bool Success, List<string> TrashedItems, string Message)> EmptyTrashWithSafetyAsync(PlexLibraryTarget target, int threshold, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || target == null || threshold <= 0)
            return (false, [], "Threshold is 0 or Plex is disabled.");

        try
        {
            int typeId = target.LibraryType == PlexLibraryType.Movie ? PlexConstants.TypeMovie : PlexConstants.TypeEpisode;

            // Get Total Library Size at the episode/movie level
            using var totalRequest = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?type={typeId}&X-Plex-Container-Start=0&X-Plex-Container-Size=0", target.ServerUrl);
            using var totalResponse = await httpClient.SendAsync(totalRequest, cancellationToken).ConfigureAwait(false);
            var totalContainer = await PlexApi.ReadContainerAsync(totalResponse, cancellationToken).ConfigureAwait(false);
            int totalSize = totalContainer?.TotalSize ?? totalContainer?.Size ?? 0;

            if (totalSize == 0)
                return (false, [], "Library is empty or unreachable.");

            // Get Trashed Items
            var trashedItems = new List<string>();
            var (start, trashedSize) = (0, 0);
            while (true)
            {
                using var trashRequest = CreateRequest(
                    HttpMethod.Get,
                    $"/library/sections/{target.SectionId}/all?type={typeId}&trash=1&X-Plex-Container-Start={start}&X-Plex-Container-Size=200",
                    target.ServerUrl
                );
                using var trashResponse = await httpClient.SendAsync(trashRequest, cancellationToken).ConfigureAwait(false);
                var trashContainer = await PlexApi.ReadContainerAsync(trashResponse, cancellationToken).ConfigureAwait(false);

                trashedSize = trashContainer?.TotalSize ?? trashContainer?.Size ?? trashedSize;

                if (trashContainer?.Metadata == null || trashContainer.Metadata.Count == 0)
                    break;

                foreach (var item in trashContainer.Metadata)
                {
                    string displayName = target.LibraryType == PlexLibraryType.Movie ? item.Title ?? "Unknown" : $"[{item.GrandparentTitle}] S{item.ParentIndex ?? 0:D2}E{item.Index ?? 0:D2} - {item.Title}";
                    trashedItems.Add(displayName);
                }

                if (trashedItems.Count >= trashedSize)
                    break;
                start += 200;
            }

            if (trashedSize == 0)
                return (true, [], "Trash is already empty.");

            double percent = (double)trashedSize / totalSize * 100;

            // Critical guard: if a network mount drops entirely, 100% of the library is trashed. Always abort regardless of threshold setting.
            if (percent >= 100)
                return (false, trashedItems, "Catastrophic failure detected: 100% of library is trashed. Aborting to prevent data loss.");

            if (percent > threshold)
                return (false, trashedItems, $"Safety threshold exceeded. {trashedSize}/{totalSize} ({percent:F1}%) items trashed (Limit: {threshold}%).");

            if (!dryRun)
            {
                using var emptyRequest = CreateRequest(HttpMethod.Put, $"/library/sections/{target.SectionId}/emptyTrash", target.ServerUrl);
                using var emptyResponse = await httpClient.SendAsync(emptyRequest, cancellationToken).ConfigureAwait(false);
                if (!emptyResponse.IsSuccessStatusCode)
                    return (false, trashedItems, $"Plex API rejected the empty trash request: {emptyResponse.StatusCode}");
            }

            return (true, trashedItems, $"Successfully processed {trashedSize} trashed items ({percent:F1}%).");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("PlexClient: Network timeout during empty trash for section {SectionId}", target.SectionId);
            return (false, [], "Network timeout during trash processing.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PlexClient: Failed to process empty trash for section {SectionId}", target.SectionId);
            return (false, [], $"Exception during trash processing: {exception.Message}");
        }
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
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseServerUrl.TrimEnd('/')), path));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", !string.IsNullOrWhiteSpace(plexUserToken) ? plexUserToken : Token);
        if (!string.IsNullOrWhiteSpace(ClientIdentifier))
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    /// <summary>Sends an HTTP request to the Plex server.</summary>
    /// <param name="request">The configured request message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP response message.</returns>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) => httpClient.SendAsync(request, cancellationToken);

    #endregion

    #region Target Discovery

    /// <summary>Returns a list of library targets based on discovered configuration, caching the compiled list to prevent redundant allocations.</summary>
    /// <returns>A read-only list of configured library targets.</returns>
    public IReadOnlyList<PlexLibraryTarget> GetConfiguredTargets()
    {
        var libraries = DiscoveredLibraries;
        var customUrl = Settings.Advanced.PlexServerUrl;
        lock (_targetsLock)
        {
            if (!ReferenceEquals(_lastDiscoveredLibraries, libraries) || _lastConfiguredPlexUrl != customUrl)
            {
                _lastDiscoveredLibraries = libraries;
                _lastConfiguredPlexUrl = customUrl;
                string? trimmedUrl = !string.IsNullOrWhiteSpace(customUrl) ? customUrl.Trim().TrimEnd('/') : null;
                _cachedTargets =
                [
                    .. libraries.Select(library => new PlexLibraryTarget
                    {
                        SectionId = library.Id,
                        Title = library.Title,
                        Type = library.Type,
                        Uuid = library.Uuid,
                        ServerId = library.ServerId,
                        ServerName = library.ServerName,
                        ServerUrl = trimmedUrl ?? library.ServerUrl,
                        LibraryType = (library.Type ?? "").ToLowerInvariant() switch
                        {
                            "movie" => PlexLibraryType.Movie,
                            "artist" => PlexLibraryType.Music,
                            "photo" => PlexLibraryType.Photo,
                            _ => PlexLibraryType.Show,
                        },
                        Locations = library.Locations ?? [],
                    }),
                ];
            }
            return _cachedTargets;
        }
    }

    /// <summary>Retrieves a library target by name directly from the Plex server, bypassing the Shoko Relay agent filter.</summary>
    /// <param name="libraryName">The exact name of the Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A configured library target or null if not found.</returns>
    public async Task<PlexLibraryTarget?> GetFallbackLibraryTargetAsync(string libraryName, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(libraryName))
            return null;

        var server = configProvider.GetPlexDiscoveredServers().FirstOrDefault(server => !string.IsNullOrWhiteSpace(server.PreferredUri));
        if (server == null && string.IsNullOrWhiteSpace(Settings.Advanced.PlexServerUrl))
            return null;

        string preferredUri = !string.IsNullOrWhiteSpace(Settings.Advanced.PlexServerUrl) ? Settings.Advanced.PlexServerUrl.Trim().TrimEnd('/') : server!.PreferredUri;

        using var request = CreateRequest(HttpMethod.Get, "/library/sections", preferredUri);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var metadataClient) || !metadataClient.TryGetProperty("Directory", out var directories))
                return null;

            foreach (var directory in directories.EnumerateArray())
            {
                if (directory.TryGetProperty("title", out var titleProp) && string.Equals(titleProp.GetString(), libraryName, StringComparison.OrdinalIgnoreCase))
                {
                    int id = directory.TryGetProperty("key", out var keyProp) && int.TryParse(keyProp.GetString(), out int parsedKey) ? parsedKey : 0;
                    if (id == 0)
                        continue;

                    string type = directory.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    var locations = new List<string>();
                    if (directory.TryGetProperty("Location", out var locationEntries))
                        foreach (var location in locationEntries.EnumerateArray())
                            if (location.TryGetProperty("path", out var pathProp) && pathProp.GetString() is { } pathValue)
                                locations.Add(pathValue);

                    return new PlexLibraryTarget
                    {
                        SectionId = id,
                        Title = titleProp.GetString() ?? libraryName,
                        Type = type,
                        Uuid = directory.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() ?? "" : "",
                        ServerId = server?.Id ?? "",
                        ServerName = server?.Name ?? "Plex Server",
                        ServerUrl = preferredUri,
                        LibraryType = type.Equals("movie", StringComparison.OrdinalIgnoreCase) ? PlexLibraryType.Movie : PlexLibraryType.Show,
                        Locations = locations,
                    };
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PlexClient: Failed to parse sections for fallback library target '{LibraryName}'", libraryName);
        }

        return null;
    }

    #endregion

    #region Metadata Queries

    /// <summary>Fetches metadata items from a Plex server endpoint path.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="path">The relative Plex API path to query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized list of metadata items, or an empty list if unavailable.</returns>
    public async Task<List<PlexMetadataItem>> GetMetadataItemsAsync(PlexLibraryTarget target, string path, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || target == null || string.IsNullOrWhiteSpace(path))
            return [];

        using var request = CreateRequest(HttpMethod.Get, path, target.ServerUrl);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false))?.Metadata ?? [];
    }

    /// <summary>Fetches Plex metadata items for a set of primary Shoko series IDs within the specified library target.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="primarySeriesIds">The primary Shoko series IDs to resolve.</param>
    /// <param name="metadataService">Shoko metadata service.</param>
    /// <param name="includeLeaves">When <c>true</c> on a show library, retrieves all descendant episode leaves instead of top-level show metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of matching <see cref="PlexMetadataItem"/> entries.</returns>
    public async Task<List<PlexMetadataItem>> GetSectionItemsForSeriesAsync(
        PlexLibraryTarget target,
        IEnumerable<int> primarySeriesIds,
        IMetadataService metadataService,
        bool includeLeaves = false,
        CancellationToken cancellationToken = default
    )
    {
        var items = new List<PlexMetadataItem>();
        if (!IsEnabled || target == null)
            return items;

        foreach (var seriesId in primarySeriesIds)
        {
            var ratingKeys = await FindRatingKeysForShokoSeriesInSectionAsync(seriesId, target, metadataService, cancellationToken).ConfigureAwait(false);
            foreach (var ratingKey in ratingKeys)
            {
                string path =
                    includeLeaves && target.LibraryType != PlexLibraryType.Movie
                        ? $"/library/metadata/{ratingKey}/allLeaves?X-Plex-Container-Start=0&X-Plex-Container-Size=5000"
                        : $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1";
                items.AddRange(await GetMetadataItemsAsync(target, path, cancellationToken).ConfigureAwait(false));
            }
        }
        return items;
    }

    /// <summary>Finds the Plex rating keys for a Shoko series within the given Plex section using its metadata GUID.</summary>
    /// <param name="shokoSeriesId">Shoko series ID.</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="metadataService">Shoko metadata service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of numeric rating keys if found, otherwise empty.</returns>
    public async Task<List<int>> FindRatingKeysForShokoSeriesInSectionAsync(int shokoSeriesId, PlexLibraryTarget target, IMetadataService metadataService, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || shokoSeriesId <= 0 || target == null)
            return [];
        try
        {
            int primaryId = OverrideHelper.GetPrimary(shokoSeriesId, metadataService);
            if (target.LibraryType == PlexLibraryType.Movie)
            {
                var keys = new List<int>();
                var episodes = OverrideHelper
                    .GetGroup(primaryId, metadataService)
                    .Select(metadataService.GetShokoSeriesByID)
                    .OfType<IShokoSeries>()
                    .SelectMany(groupSeries => groupSeries.Episodes)
                    .DistinctBy(episode => episode.LocalID)
                    .Where(episode => episode.Type == EpisodeType.Episode && episode.Videos.Count > 0 && !MapHelper.IsHidden(episode));

                foreach (var episode in episodes)
                {
                    string guid = $"{ShokoRelayConstants.MovieAgentScheme}://movie/{PlexConstants.MoviePrefix}{episode.LocalID}";
                    var item = (
                        await GetMetadataItemsAsync(target, $"/library/sections/{target.SectionId}/all?guid={Uri.EscapeDataString(guid)}&X-Plex-Container-Start=0&X-Plex-Container-Size=1", cancellationToken)
                            .ConfigureAwait(false)
                    ).FirstOrDefault();
                    if (int.TryParse(item?.RatingKey, out int key))
                        keys.Add(key);
                }
                return keys;
            }

            string showGuid = $"{ShokoRelayConstants.AgentScheme}://show/{primaryId}";
            var showItem = (
                await GetMetadataItemsAsync(target, $"/library/sections/{target.SectionId}/all?guid={Uri.EscapeDataString(showGuid)}&X-Plex-Container-Start=0&X-Plex-Container-Size=1", cancellationToken)
                    .ConfigureAwait(false)
            ).FirstOrDefault();
            return int.TryParse(showItem?.RatingKey, out int showKey) ? [showKey] : [];
        }
        catch (Exception exception)
        {
            logger.LogTrace(exception, "PlexClient: Failed to find rating key for Shoko series {SeriesId} in section {SectionId}", shokoSeriesId, target.SectionId);
            return [];
        }
    }

    /// <summary>List items in the given section with optional filters.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="token">Optional token override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <param name="type">Plex type identifier.</param>
    /// <returns>A list of metadata items.</returns>
    public async Task<List<PlexMetadataItem>> GetSectionItemsAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken cancellationToken = default,
        bool? onlyUnwatched = null,
        bool? hasProgress = null,
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
            var queryParams = new List<string> { $"X-Plex-Container-Start={start}", "X-Plex-Container-Size=200" };
            if (type.HasValue)
                queryParams.Add($"type={type.Value}");
            if (onlyUnwatched.HasValue)
                queryParams.Add($"unwatched={(onlyUnwatched.Value ? 1 : 0)}");
            if (hasProgress.HasValue)
                queryParams.Add(hasProgress.Value ? "viewOffset>=1" : "viewOffset=0");
            if (!string.IsNullOrEmpty(guidFilter))
                queryParams.Add($"guid={Uri.EscapeDataString(guidFilter)}");
            if (minLastViewed.HasValue)
                queryParams.Add($"lastViewedAt>={minLastViewed.Value}");

            using var request = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?{string.Join("&", queryParams)}", target.ServerUrl, token);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PlexClient: Failed to query section {SectionId} on {ServerName} (HTTP {StatusCode})", target.SectionId, target.ServerName, response.StatusCode);
                break;
            }
            var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
            if (container?.Metadata == null || container.Metadata.Count == 0)
                break;
            results.AddRange(container.Metadata);
            if (results.Count >= (container.TotalSize ?? container.Size))
                break;
            start += 200;
        }
        return results;
    }

    /// <summary>List all collections in the given section.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of metadata items representing collections.</returns>
    public Task<List<PlexMetadataItem>> GetSectionCollectionsAsync(PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        GetSectionItemsAsync(target, null, cancellationToken, type: PlexConstants.TypeCollection);

    /// <summary>List all shows in the given section.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionShowsAsync(PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        GetSectionItemsAsync(target, null, cancellationToken, type: PlexConstants.TypeShow);

    /// <summary>List all episodes in the given section with optional filters.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="token">Token override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken cancellationToken = default,
        bool? onlyUnwatched = null,
        bool? hasProgress = null,
        string? guidFilter = null,
        long? minLastViewed = null
    ) => GetSectionItemsAsync(target, token, cancellationToken, onlyUnwatched, hasProgress, guidFilter, minLastViewed, PlexConstants.TypeEpisode);

    /// <summary>List all movies in the given section with optional filters.</summary>
    /// <param name="target">The target Plex library.</param>
    /// <param name="token">Token override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionMoviesAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken cancellationToken = default,
        bool? onlyUnwatched = null,
        bool? hasProgress = null,
        string? guidFilter = null,
        long? minLastViewed = null
    ) => GetSectionItemsAsync(target, token, cancellationToken, onlyUnwatched, hasProgress, guidFilter, minLastViewed, PlexConstants.TypeMovie);

    #endregion

    #region Path Mapping

    /// <summary>Map a Shoko path to the Plex path using configured mappings.</summary>
    /// <param name="path">Input Shoko path.</param>
    /// <returns>The mapped Plex path.</returns>
    public string MapShokoPathToPlexPath(string path) => MapPath(path, true);

    /// <summary>Reverse-map a Plex path back to Shoko.</summary>
    /// <param name="path">Input Plex path.</param>
    /// <returns>The original Shoko path.</returns>
    public string MapPlexPathToShokoPath(string path) => MapPath(path, false);

    /// <summary>Internal core for mapping paths between Shoko and Plex using cached, prioritized mappings.</summary>
    /// <param name="path">The absolute or relative path string to translate.</param>
    /// <param name="shokoToPlex">True if mapping from Shoko-to-Plex; false for Plex-to-Shoko.</param>
    /// <returns>The mapped/translated path string.</returns>
    private string MapPath(string path, bool shokoToPlex)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var currentMappings = Settings.Advanced.PathMappings;
        if (currentMappings == null || currentMappings.Count == 0)
            return path;

        (string In, string Out)[] list;
        lock (_mappingLock)
        {
            if (!ReferenceEquals(_lastCachedMappings, currentMappings))
            {
                var pairs = currentMappings.Select(mapping => new { Shoko = PlexHelper.NormalizePathForPlex(mapping.Key), Plex = PlexHelper.NormalizePathForPlex(mapping.Value) }).ToList();
                _shokoToPlexMappings = [.. pairs.Select(pair => (pair.Shoko, pair.Plex)).OrderByDescending(pair => pair.Shoko.Length)];
                _plexToShokoMappings = [.. pairs.Select(pair => (pair.Plex, pair.Shoko)).OrderByDescending(pair => pair.Plex.Length)];
                _lastCachedMappings = currentMappings;
            }
            list = shokoToPlex ? _shokoToPlexMappings : _plexToShokoMappings;
        }

        if (list.Length == 0)
            return path;

        string inputPath = PlexHelper.NormalizePathForPlex(path);
        var (matchIn, matchOut) = list.FirstOrDefault(pair => !string.IsNullOrEmpty(pair.In) && inputPath.StartsWith(pair.In, StringComparison.OrdinalIgnoreCase));
        if (matchIn == null)
            return path;

        string remainder = inputPath.Length > matchIn.Length ? inputPath[matchIn.Length..].TrimStart('/') : "";
        string result = string.IsNullOrEmpty(remainder) ? matchOut : $"{matchOut.TrimEnd('/')}/{remainder}";

        return shokoToPlex ? result : result.Replace('/', Path.DirectorySeparatorChar);
    }

    #endregion
}
