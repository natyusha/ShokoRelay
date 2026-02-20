using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex
{
    public class PlexClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;

        private PlexLibraryConfig Config => _configProvider.GetSettings().PlexLibrary;

        public PlexClient(HttpClient httpClient, ConfigProvider configProvider)
        {
            _httpClient = httpClient;
            _configProvider = configProvider;
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(Config.ServerUrl) && !string.IsNullOrWhiteSpace(Config.Token) && GetTargets().Count > 0;
        public bool ScanOnVfsRefresh => Config.ScanOnVfsRefresh;

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
            var baseUrl = !string.IsNullOrWhiteSpace(baseServerUrl) ? baseServerUrl : Config.ServerUrl;
            var baseUri = new Uri(baseUrl.TrimEnd('/'));
            var url = new Uri(baseUri, path);

            var request = new HttpRequestMessage(method, url);

            // Allow per-user token override for requests that need per-account visibility (e.g. watched-state).
            // If no override supplied, fall back to the configured Plex token.
            var tokenToUse = !string.IsNullOrWhiteSpace(plexUserToken) ? plexUserToken : Config.Token;
            if (!string.IsNullOrWhiteSpace(tokenToUse))
                request.Headers.TryAddWithoutValidation("X-Plex-Token", tokenToUse);

            if (!string.IsNullOrWhiteSpace(Config.ClientIdentifier))
                request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", Config.ClientIdentifier);

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

            using var request = CreateRequest(HttpMethod.Get, requestPath);
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
            if (Config.SelectedLibraries != null && Config.SelectedLibraries.Count > 0)
                return Config.SelectedLibraries;

            if (Config.LibrarySectionId <= 0)
                return Array.Empty<PlexLibraryTarget>();

            return new[]
            {
                new PlexLibraryTarget
                {
                    SectionId = Config.LibrarySectionId,
                    Title = Config.SelectedLibraryName,
                    Type = Config.LibraryType.ToString(),
                    Uuid = Config.SectionUuid,
                    LibraryType = Config.LibraryType,
                },
            };
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
                string requestPath = $"/library/sections/{target.SectionId}/search?guid={Uri.EscapeDataString(guid)}";
                using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return false;

                var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                return container?.Metadata?.Count > 0;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SearchSectionForGuidAsync failed for guid {Guid} on {Server}:{Section}", guid, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        /// <summary>
        /// List all items (series) in the given section. Uses paging to avoid very large responses.
        /// </summary>
        public async Task<List<PlexMetadataItem>> GetSectionShowsAsync(PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            var results = new List<PlexMetadataItem>();
            if (!IsEnabled || target == null)
                return results;

            int start = 0;
            const int pageSize = 200;
            while (true)
            {
                string requestPath = $"/library/sections/{target.SectionId}/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={pageSize}";
                using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    break;

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
        /// List all episodes in the given section. Uses paging to avoid very large responses.
        /// </summary>
        public async Task<List<PlexMetadataItem>> GetSectionEpisodesAsync(
            PlexLibraryTarget target,
            string? plexUserToken = null,
            CancellationToken cancellationToken = default,
            bool onlyUnwatched = false
        )
        {
            var results = new List<PlexMetadataItem>();
            if (!IsEnabled || target == null)
                return results;

            int start = 0;
            const int pageSize = 200;
            while (true)
            {
                var unwatchedFlag = onlyUnwatched ? "1" : "0";
                string requestPath =
                    $"/library/sections/{target.SectionId}/all?type={PlexConstants.TypeEpisode}&unwatched={unwatchedFlag}&X-Plex-Container-Start={start}&X-Plex-Container-Size={pageSize}";
                using var request = CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl, plexUserToken);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    break;

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
    }
}
