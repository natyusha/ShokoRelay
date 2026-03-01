using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex
{
    public class PlexClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;

        // helper to retrieve current plex token/config from the token file
        private string Token => _configProvider.GetPlexToken();
        private string ClientIdentifier => _configProvider.GetPlexClientIdentifier();
        private IReadOnlyList<PlexAvailableLibrary> DiscoveredLibraries => _configProvider.GetPlexDiscoveredLibraries();

        public PlexClient(HttpClient httpClient, ConfigProvider configProvider)
        {
            _httpClient = httpClient;
            _configProvider = configProvider;
        }

        /// <summary>
        /// True when a Plex token exists and at least one library target has been discovered from the token file.
        /// </summary>
        public bool IsEnabled => !string.IsNullOrWhiteSpace(Token) && GetTargets().Count > 0;

        /// <summary>
        /// Expose the configuration setting controlling whether Plex section refresh requests should trigger a scan on VFS refresh events.
        /// </summary>
        public bool ScanOnVfsRefresh => _configProvider.GetSettings().ScanOnVfsRefresh;

        /// <summary>
        /// Request Plex to refresh the specified library section path across all configured targets. Returns true if any refresh succeeded.
        /// </summary>
        /// <param name="path">Filesystem path to refresh.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public async Task<bool> RefreshSectionPathAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            bool anyOk = false;
            foreach (var target in GetTargets())
            {
                bool ok = await RefreshSectionPathAsync(path, target, cancellationToken).ConfigureAwait(false);
                if (ok)
                    anyOk = true;
            }

            return anyOk;
        }

        /// <summary>
        /// Assign a collection to a metadata item (ratingKey) by using the metadata PUT shortcut:
        /// PUT /library/metadata/{ratingKey}?collection%5B0%5D.tag.tag={collectionName}
        /// </summary>
        public HttpRequestMessage CreateRequest(HttpMethod method, string path, string? baseServerUrl = null, string? plexUserToken = null)
        {
            if (string.IsNullOrWhiteSpace(baseServerUrl))
                throw new ArgumentException("Base server URL must be supplied", nameof(baseServerUrl));
            var baseUri = new Uri(baseServerUrl.TrimEnd('/'));
            var url = new Uri(baseUri, path);

            var request = new HttpRequestMessage(method, url);

            // Allow per-user token override for requests that need per-account visibility (e.g. watched-state).
            // If no override supplied, fall back to the configured Plex token.
            var tokenToUse = !string.IsNullOrWhiteSpace(plexUserToken) ? plexUserToken : Token;
            if (!string.IsNullOrWhiteSpace(tokenToUse))
                request.Headers.TryAddWithoutValidation("X-Plex-Token", tokenToUse);

            if (!string.IsNullOrWhiteSpace(ClientIdentifier))
                request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);

            // default to JSON for most API calls; callers can override if they need a different format
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        private async Task<bool> RefreshSectionPathAsync(string path, PlexLibraryTarget target, CancellationToken cancellationToken)
        {
            // Map the Shoko path to the Plex path if any mappings are configured
            string mappedPath = MapShokoPathToPlexPath(path);
            if (!string.Equals(mappedPath, path, StringComparison.Ordinal))
            {
                Logger.Debug("Path mapping applied for Plex refresh: {Original} -> {Mapped}", path, mappedPath);
            }

            string encodedPath = Uri.EscapeDataString(mappedPath);
            string requestPath = $"/library/sections/{target.SectionId}/refresh?path={encodedPath}";

            using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn("Plex refresh failed with status {Status} for path {Path}", response.StatusCode, mappedPath);
                return false;
            }

            Logger.Debug("Plex refresh triggered for path {Path} on {Server}:{Section} (status {Status})", mappedPath, target.ServerUrl, target.SectionId, response.StatusCode);
            return true;
        }

        private IReadOnlyList<PlexLibraryTarget> GetTargets()
        {
            if (DiscoveredLibraries != null && DiscoveredLibraries.Count > 0)
            {
                // map available libraries to the simpler target type
                return DiscoveredLibraries
                    .Select(lib => new PlexLibraryTarget
                    {
                        SectionId = lib.Id,
                        Title = lib.Title,
                        Type = lib.Type,
                        Uuid = lib.Uuid,
                        ServerId = lib.ServerId,
                        ServerName = lib.ServerName,
                        ServerUrl = lib.ServerUrl,
                        LibraryType = (lib.Type ?? string.Empty).Trim().ToLowerInvariant() switch
                        {
                            "movie" => PlexLibraryType.Movie,
                            "show" => PlexLibraryType.Show,
                            "artist" => PlexLibraryType.Music,
                            "photo" => PlexLibraryType.Photo,
                            _ => PlexLibraryType.Show,
                        },
                    })
                    .ToList();
            }
            return Array.Empty<PlexLibraryTarget>();
        }

        private string MapShokoPathToPlexPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            var settings = _configProvider.GetSettings();
            var mappings = settings.PathMappings;
            if (mappings == null || mappings.Count == 0)
                return path;

            // Normalize input path for comparison
            string NormalizeForMatch(string p)
            {
                if (string.IsNullOrWhiteSpace(p))
                    return string.Empty;
                string normalized = p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                try
                {
                    normalized = Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return normalized;
            }

            var inputNorm = NormalizeForMatch(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // Prefer longest matching key so more specific mappings take precedence
            foreach (var kvp in mappings.OrderByDescending(k => k.Key?.Length ?? 0))
            {
                string key = kvp.Key ?? string.Empty;
                string val = kvp.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
                    continue;

                var keyNorm = NormalizeForMatch(key);
                if (string.IsNullOrWhiteSpace(keyNorm))
                    continue;

                if (inputNorm.Equals(keyNorm, comparison) || inputNorm.StartsWith(keyNorm + Path.DirectorySeparatorChar, comparison))
                {
                    // Compute remainder after key
                    var remainder = inputNorm.Length > keyNorm.Length ? inputNorm.Substring(keyNorm.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : string.Empty;
                    // Build mapped path using the provided value as base. Use forward slashes for Plex.
                    string basePart = val.TrimEnd('/', '\\');
                    string mapped = string.IsNullOrWhiteSpace(remainder) ? basePart : (basePart + "/" + remainder.Replace(Path.DirectorySeparatorChar, '/'));
                    return mapped;
                }
            }

            return path;
        }

        /// <summary>
        /// Reverse-map a Plex-style path back to the configured Shoko base path using PathMappings.
        /// This allows callers (e.g. AnimeThemes endpoints) to accept paths as Plex exposes them and convert
        /// them to server-local Shoko paths before processing.
        /// </summary>
        public string MapPlexPathToShokoPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            var settings = _configProvider.GetSettings();
            var mappings = settings.PathMappings;
            if (mappings == null || mappings.Count == 0)
                return path;

            // Normalize input path for comparison (match logic mirrors MapShokoPathToPlexPath)
            string NormalizeForMatch(string p)
            {
                if (string.IsNullOrWhiteSpace(p))
                    return string.Empty;
                string normalized = p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                try
                {
                    normalized = Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return normalized;
            }

            var inputNorm = NormalizeForMatch(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // Prefer longest matching VALUE so more specific Plex-path mappings take precedence
            foreach (var kvp in mappings.OrderByDescending(k => (k.Value?.Length ?? 0)))
            {
                string key = kvp.Key ?? string.Empty; // Shoko base
                string val = kvp.Value ?? string.Empty; // Plex base
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
                    continue;

                var valNorm = NormalizeForMatch(val);
                if (string.IsNullOrWhiteSpace(valNorm))
                    continue;

                if (inputNorm.Equals(valNorm, comparison) || inputNorm.StartsWith(valNorm + Path.DirectorySeparatorChar, comparison))
                {
                    // Compute remainder after value
                    var remainder = inputNorm.Length > valNorm.Length ? inputNorm.Substring(valNorm.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : string.Empty;
                    // Build mapped path using the key (Shoko base). Use OS-native separators for server paths.
                    string basePart = key.TrimEnd('/', '\\');
                    string mapped = string.IsNullOrWhiteSpace(remainder) ? basePart : (basePart + Path.DirectorySeparatorChar + remainder);
                    return mapped;
                }
            }

            return path;
        }

        /// <summary>
        /// Expose configured targets for external callers (controllers/VFS) to make per-target decisions.
        /// </summary>
        public IReadOnlyList<PlexLibraryTarget> GetConfiguredTargets() => GetTargets();

        /// <summary>
        /// Check whether a given ratingKey exists in the provided target section on the target server.
        /// Returns true only if the metadata for the ratingKey is present and belongs to the target section.
        /// </summary>
        public async Task<bool> ItemExistsInSectionAsync(int ratingKey, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;
            if (ratingKey <= 0)
                return false;
            if (target == null)
                return false;

            try
            {
                using var request = CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}", target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return false;

                var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                var meta = container?.Metadata?.FirstOrDefault();

                if (meta != null)
                {
                    if (meta.LibrarySectionId.HasValue)
                        return meta.LibrarySectionId.Value == target.SectionId;

                    // No explicit librarySectionID; try searching the section by the Shoko show GUID as a fallback.
                    string guid = $"{ShokoRelayInfo.AgentScheme}://show/{ratingKey}";
                    bool found = await SearchSectionForGuidAsync(guid, target, cancellationToken).ConfigureAwait(false);
                    Logger.Debug(
                        "Metadata for ratingKey {RatingKey} had no librarySectionID on \"{Server}\":{Section}; fallback search by guid {Guid} found={Found}.",
                        ratingKey,
                        target.ServerUrl,
                        target.SectionId,
                        guid,
                        found
                    );
                    return found;
                }

                // No metadata returned at all; treat as absent
                Logger.Debug("No metadata returned for ratingKey {RatingKey} on {Server}:{Section}; treating as absent.", ratingKey, target.ServerUrl, target.SectionId);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ItemExistsInSectionAsync failed for {RatingKey} on {Server}:{Section}", ratingKey, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        /// <summary>
        /// Searches the given section for a specific GUID (returns true if any metadata is found).
        /// </summary>
        private async Task<bool> SearchSectionForGuidAsync(string guid, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            try
            {
                // we only care whether *any* metadata matches the GUID; using the generic enumerator with a type=show filter is sufficient and avoids duplicating query string logic.
                var list = await GetSectionItemsAsync(target, null, cancellationToken, onlyUnwatched: null, guidFilter: guid, mediaType: null).ConfigureAwait(false);
                return list != null && list.Count > 0;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SearchSectionForGuidAsync failed for guid {Guid} on {Server}:{Section}", guid, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        /// <summary>
        /// Find the Plex ratingKey for a Shoko series within the given Plex section by searching for the Shoko GUID.
        /// </summary>
        public async Task<int?> FindRatingKeyForShokoSeriesInSectionAsync(int shokoSeriesId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return null;
            if (shokoSeriesId <= 0)
                return null;
            if (target == null)
                return null;

            try
            {
                string guid = $"{ShokoRelayInfo.AgentScheme}://show/{shokoSeriesId}";
                string requestPath = $"/library/sections/{target.SectionId}/search?guid={Uri.EscapeDataString(guid)}";
                using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                var item = container?.Metadata?.FirstOrDefault();
                if (item == null || string.IsNullOrWhiteSpace(item.RatingKey))
                    return null;

                return int.TryParse(item.RatingKey, out int ratingKey) ? ratingKey : null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "FindRatingKeyForShokoSeriesInSectionAsync failed for series {Series} on {Server}:{Section}", shokoSeriesId, target.ServerUrl, target.SectionId);
                return null;
            }
        }

        /// <summary>
        /// List items in the given section. Uses paging and supports optional watch-state, GUID and media-type filters.
        /// </summary>
        public async Task<List<PlexMetadataItem>> GetSectionItemsAsync(
            PlexLibraryTarget target,
            string? plexUserToken = null,
            CancellationToken cancellationToken = default,
            bool? onlyUnwatched = null,
            string? guidFilter = null,
            long? minLastViewed = null,
            int? mediaType = null
        )
        {
            var results = new List<PlexMetadataItem>();
            if (!IsEnabled || target == null)
                return results;

            int start = 0;
            const int pageSize = 200;
            while (true)
            {
                // build query parameters cleanly to avoid stray leading '&' characters
                var queryParts = new List<string>();
                if (mediaType.HasValue)
                    queryParts.Add($"type={mediaType.Value}");

                if (onlyUnwatched == true)
                    queryParts.Add("unwatched=1");
                else if (onlyUnwatched == false)
                    queryParts.Add("unwatched=0");

                if (!string.IsNullOrWhiteSpace(guidFilter))
                    queryParts.Add($"guid={Uri.EscapeDataString(guidFilter)}");

                if (minLastViewed.HasValue)
                    queryParts.Add($"lastViewedAt>={minLastViewed.Value}");

                // paging parameters are always present
                queryParts.Add($"X-Plex-Container-Start={start}");
                queryParts.Add($"X-Plex-Container-Size={pageSize}");

                string query = string.Join("&", queryParts);
                string requestPath = $"/library/sections/{target.SectionId}/all" + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
                using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl, plexUserToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                if (container?.Metadata == null || container.Metadata.Count == 0)
                    break;

                results.AddRange(container.Metadata);

                int fetched = container.Metadata.Count;
                int? total = container.TotalSize ?? container.Size;
                if (fetched == 0)
                    break;
                if (total.HasValue && start + pageSize >= total.Value)
                    break;

                start += pageSize;
            }

            return results;
        }

        /// <summary>
        /// List all series (shows) in the given section by delegating to the generic item enumeration helper with a show-type filter.
        /// </summary>
        public Task<List<PlexMetadataItem>> GetSectionShowsAsync(PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            // reuse the more flexible helper; passing null for onlyUnwatched/guidFilter yields a complete list of items of the requested type.
            return GetSectionItemsAsync(target, null, cancellationToken, onlyUnwatched: null, guidFilter: null, mediaType: PlexConstants.TypeShow);
        }

        /// <summary>
        /// List all episodes in the given section by delegating to the generic item enumeration helper with an episode-type filter.
        /// </summary>
        public Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
            PlexLibraryTarget target,
            string? plexUserToken = null,
            CancellationToken cancellationToken = default,
            bool? onlyUnwatched = null,
            string? guidFilter = null,
            long? minLastViewed = null
        )
        {
            return GetSectionItemsAsync(target, plexUserToken, cancellationToken, onlyUnwatched, guidFilter, minLastViewed, mediaType: PlexConstants.TypeEpisode);
        }
    }
}
