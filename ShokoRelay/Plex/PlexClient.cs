using NLog;

namespace ShokoRelay.Plex;

/// <summary>HTTP client wrapper that communicates with one or more Plex servers.</summary>
public class PlexClient(HttpClient httpClient, ConfigProvider configProvider)
{
    #region Setup & Properties

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <summary>Internal access to the shared HttpClient instance.</summary>
    internal HttpClient HttpClient => httpClient;

    private string Token => configProvider.GetPlexToken();
    private string ClientIdentifier => configProvider.GetPlexClientIdentifier();
    private IReadOnlyList<PlexAvailableLibrary> DiscoveredLibraries => configProvider.GetPlexDiscoveredLibraries();

    /// <summary>True when a Plex token exists and at least one library target has been discovered.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Token) && GetConfiguredTargets().Count > 0;

    /// <summary>Expose the configuration setting controlling automatic library scans.</summary>
    public bool ScanOnVfsRefresh => Settings.Automation.ScanOnVfsRefresh;

    /// <summary>Cached list of prioritized Shoko-to-Plex path mappings.</summary>
    private readonly List<(string In, string Out)> _shokoToPlexMappings = [];

    /// <summary>Cached list of prioritized Plex-to-Shoko path mappings.</summary>
    private readonly List<(string In, string Out)> _plexToShokoMappings = [];

    /// <summary>Reference to the last cached raw path mappings dictionary used to detect configuration changes.</summary>
    private Dictionary<string, string>? _lastCachedMappings;

    /// <summary>Exclusive lock used to ensure thread-safe path mapping cache compilation.</summary>
    private readonly Lock _mappingLock = new();

    #endregion

    #region Library & Section

    /// <summary>Request Plex to refresh a specific filesystem path, optimized to matching sections with an automatic path mapping fallback.</summary>
    /// <param name="path">The Shoko-side filesystem path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one refresh request was successful.</returns>
    public async Task<bool> RefreshSectionPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(path))
            return false;

        string mapped = MapShokoPathToPlexPath(path);
        string normMapped = TextHelper.NormalizePathForPlex(mapped);

        var allTargets = GetConfiguredTargets();
        var matchingTargets = allTargets.Where(target => target.Locations.Any(loc => normMapped.StartsWith(TextHelper.NormalizePathForPlex(loc), StringComparison.OrdinalIgnoreCase)));

        bool anyOk = false;

        if (matchingTargets.Any())
        {
            foreach (var target in matchingTargets)
            {
                using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/refresh?path={Uri.EscapeDataString(mapped)}", target.ServerUrl);
                using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    anyOk = true;
                    s_logger.Debug("PlexClient: refresh triggered for -> '{0}' on {1}:{2}", mapped, target.ServerUrl, target.SectionId);
                }
                else
                    s_logger.Warn("PlexClient: refresh failed ({0}) for folder -> '{1}' in section {2}", resp.StatusCode, mapped, target.SectionId);
            }
            return anyOk;
        }

        // If Path Mappings aren't configured, safely infer the correct Plex path by matching the VFS root name to Plex's configured library locations.
        string vfsRootName = configProvider.GetSettings().Advanced.VfsRootPath;
        if (string.IsNullOrWhiteSpace(vfsRootName))
            vfsRootName = ShokoRelayConstants.FolderVfsDefault;

        string searchStr = $"/{vfsRootName}/";
        int vfsIdx = normMapped.IndexOf(searchStr, StringComparison.OrdinalIgnoreCase);
        int tokenLength = vfsRootName.Length + 2;

        // Fallback for custom relative paths that might start directly with the VFS root name
        if (vfsIdx < 0 && normMapped.StartsWith(vfsRootName + "/", StringComparison.OrdinalIgnoreCase))
        {
            vfsIdx = 0;
            tokenLength = vfsRootName.Length + 1;
        }

        if (vfsIdx >= 0)
        {
            string relativeSuffix = normMapped[(vfsIdx + tokenLength)..];

            // Extract Shoko's parent directory name preceding the VFS root
            string shokoParentName = "";
            int parentEndIdx = vfsIdx;
            if (parentEndIdx > 0)
            {
                int parentStartIdx = normMapped.LastIndexOf('/', parentEndIdx - 1);
                if (parentStartIdx >= 0)
                    shokoParentName = normMapped[(parentStartIdx + 1)..parentEndIdx];
            }

            var potentialFallbacks = new List<(PlexLibraryTarget Target, string GuessedPath, bool IsParentMatch)>();

            foreach (var target in allTargets)
            {
                foreach (var loc in target.Locations)
                {
                    string normLoc = TextHelper.NormalizePathForPlex(loc);
                    string locSuffix = $"/{vfsRootName}";

                    if (normLoc.EndsWith(locSuffix, StringComparison.OrdinalIgnoreCase) || string.Equals(normLoc, vfsRootName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Detect Plex's native directory separator and format the path to match the target OS
                        char plexSep = loc.Contains('\\') ? '\\' : '/';
                        string nativeSuffix = relativeSuffix.Replace('/', plexSep);
                        string guessedPath = $"{loc.TrimEnd('\\', '/')}{plexSep}{nativeSuffix}";

                        // Extract Plex's parent directory name preceding the VFS root
                        string plexParentName = "";
                        int plexEndIdx = normLoc.LastIndexOf(locSuffix, StringComparison.OrdinalIgnoreCase);
                        if (plexEndIdx > 0)
                        {
                            int plexStartIdx = normLoc.LastIndexOf('/', plexEndIdx - 1);
                            if (plexStartIdx >= 0)
                                plexParentName = normLoc[(plexStartIdx + 1)..plexEndIdx];
                        }

                        bool isParentMatch = !string.IsNullOrEmpty(shokoParentName) && string.Equals(shokoParentName, plexParentName, StringComparison.OrdinalIgnoreCase);
                        potentialFallbacks.Add((target, guessedPath, isParentMatch));
                    }
                }
            }

            // Filter to only parent-matching locations if any exist; otherwise fallback to broad scan
            bool hasParentMatch = false;
            foreach (var (target, guessedPath, isParentMatch) in potentialFallbacks)
            {
                if (isParentMatch)
                {
                    hasParentMatch = true;
                    break;
                }
            }

            foreach (var (tgt, guessedPath, isParentMatch) in potentialFallbacks)
            {
                if (hasParentMatch && !isParentMatch)
                    continue;

                using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{tgt.SectionId}/refresh?path={Uri.EscapeDataString(guessedPath)}", tgt.ServerUrl);
                using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    anyOk = true;
                    s_logger.Debug("PlexClient: auto-mapped fallback refresh triggered for -> '{0}' on {1}:{2}", guessedPath, tgt.ServerUrl, tgt.SectionId);
                }
            }
        }

        if (!anyOk)
            s_logger.Warn(
                "PlexClient: Path '{0}' does not match any known Plex library locations! If Plex and Shoko run on different filesystems, please configure Path Mappings in the Shoko Relay dashboard.",
                normMapped
            );

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
        using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);

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
            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?guid={Uri.EscapeDataString(guid)}&X-Plex-Container-Start=0&X-Plex-Container-Size=1", target.ServerUrl);
            using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var item = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
            return int.TryParse(item?.RatingKey, out int key) ? key : null;
        }
        catch (Exception ex)
        {
            s_logger.Trace(ex, "PlexClient: Failed to find rating key for Shoko series {0} in section {1}", shokoSeriesId, target.SectionId);
            return null;
        }
    }

    /// <summary>List items in the given section with optional filters.</summary>
    /// <param name="target">Target server/section.</param>
    /// <param name="token">Optional token override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="onlyUnwatched">Filter for unwatched.</param>
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <param name="type">Plex type identifier.</param>
    /// <returns>A list of metadata items.</returns>
    public async Task<List<PlexMetadataItem>> GetSectionItemsAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
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
            var q = new List<string> { $"X-Plex-Container-Start={start}", "X-Plex-Container-Size=200" };
            if (type.HasValue)
                q.Add($"type={type.Value}");
            if (onlyUnwatched.HasValue)
                q.Add($"unwatched={(onlyUnwatched.Value ? 1 : 0)}");
            if (hasProgress.HasValue)
                q.Add(hasProgress.Value ? "viewOffset>=1" : "viewOffset=0");
            if (!string.IsNullOrEmpty(guidFilter))
                q.Add($"guid={Uri.EscapeDataString(guidFilter)}");
            if (minLastViewed.HasValue)
                q.Add($"lastViewedAt>={minLastViewed.Value}");

            using var req = CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?{string.Join("&", q)}", target.ServerUrl, token);
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
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

    /// <summary>List all collections in the given section.</summary>
    /// <param name="target">Target library.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of metadata items representing collections.</returns>
    public Task<List<PlexMetadataItem>> GetSectionCollectionsAsync(PlexLibraryTarget target, CancellationToken ct = default) => GetSectionItemsAsync(target, null, ct, type: PlexConstants.TypeCollection);

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
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="guidFilter">Filter for specific GUID.</param>
    /// <param name="minLastViewed">Filter for viewed after.</param>
    /// <returns>A list of metadata items.</returns>
    public Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
        PlexLibraryTarget target,
        string? token = null,
        CancellationToken ct = default,
        bool? onlyUnwatched = null,
        bool? hasProgress = null,
        string? guidFilter = null,
        long? minLastViewed = null
    ) => GetSectionItemsAsync(target, token, ct, onlyUnwatched, hasProgress, guidFilter, minLastViewed, PlexConstants.TypeEpisode);

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

        var currentMappings = configProvider.GetSettings().Advanced.PathMappings;
        if (currentMappings == null || currentMappings.Count == 0)
            return path;

        IReadOnlyList<(string In, string Out)> list;
        lock (_mappingLock)
        {
            if (!ReferenceEquals(_lastCachedMappings, currentMappings))
            {
                _shokoToPlexMappings.Clear();
                _plexToShokoMappings.Clear();

                var pairs = currentMappings.Select(m => new { Shoko = TextHelper.NormalizePathForPlex(m.Key), Plex = TextHelper.NormalizePathForPlex(m.Value) }).ToList();

                _shokoToPlexMappings.AddRange(pairs.Select(p => (p.Shoko, p.Plex)).OrderByDescending(p => p.Shoko.Length));

                _plexToShokoMappings.AddRange(pairs.Select(p => (p.Plex, p.Shoko)).OrderByDescending(p => p.Plex.Length));

                _lastCachedMappings = currentMappings;
            }
            list = shokoToPlex ? _shokoToPlexMappings : _plexToShokoMappings;
        }

        if (list.Count == 0)
            return path;

        string input = TextHelper.NormalizePathForPlex(path);
        var (matchIn, matchOut) = list.FirstOrDefault(m => input.StartsWith(m.In, StringComparison.OrdinalIgnoreCase));
        if (matchIn == null)
            return path;

        string remainder = input.Length > matchIn.Length ? input[matchIn.Length..].TrimStart('/') : "";
        string result = string.IsNullOrEmpty(remainder) ? matchOut : $"{matchOut.TrimEnd('/')}/{remainder}";

        return shokoToPlex ? result : result.Replace('/', Path.DirectorySeparatorChar);
    }

    #endregion
}
