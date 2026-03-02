using System.Diagnostics;
using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex
{
    /// <summary>
    /// Provides utilities for working with Plex collections such as creating, updating, and deleting collections as well as assigning items to them.
    /// </summary>
    public class PlexCollections
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;
        private readonly PlexClient _plexClient; // for section/item helpers

        /// <summary>
        /// Initializes a new instance of <see cref="PlexCollections"/>.
        /// </summary>
        /// <param name="httpClient">HTTP client used to communicate with Plex servers.</param>
        /// <param name="configProvider">Provider for configuration settings.</param>
        /// <param name="plexClient">Helper for interacting with Plex-specific APIs.</param>
        public PlexCollections(HttpClient httpClient, ConfigProvider configProvider, PlexClient plexClient)
        {
            _httpClient = httpClient;
            _configProvider = configProvider;
            _plexClient = plexClient;
        }

        /// <summary>
        /// Gets a value indicating whether Plex integration is enabled based on the underlying <see cref="PlexClient"/>.
        /// </summary>
        public bool IsEnabled => _plexClient.IsEnabled;

        /// <summary>
        /// Represents a specific collection target within a Plex library section.
        /// </summary>
        /// <param name="Target">The library target containing server and section information.</param>
        /// <param name="CollectionId">The numeric ID of the collection.</param>
        public sealed record PlexLibraryCollectionTarget(PlexLibraryTarget Target, int CollectionId);

        /// <summary>
        /// Looks up a collection by name using the first configured Plex library target. If the collection does not already exist, an attempt will be made to create it.
        /// </summary>
        /// <param name="collectionName">The name of the collection to find or create.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The numeric ID of the collection on success, or <c>null</c> if the
        /// service is disabled, the name is empty, or the request fails.</returns>
        public async Task<int?> GetOrCreateCollectionIdAsync(string collectionName, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return null;
            if (string.IsNullOrWhiteSpace(collectionName))
                return null;

            var primary = _plexClient.GetConfiguredTargets().FirstOrDefault();
            if (primary == null)
                return null;

            var collectionId = await FindCollectionIdAsync(collectionName, primary, cancellationToken).ConfigureAwait(false);
            if (collectionId == null)
                collectionId = await CreateCollectionAsync(collectionName, primary, cancellationToken).ConfigureAwait(false);

            return collectionId;
        }

        /// <summary>
        /// Looks up or creates a collection with the given name in a specific library target. Also ensures the collection's sort title matches the provided name.
        /// </summary>
        /// <param name="collectionName">The name of the collection.</param>
        /// <param name="target">The library target where the collection resides.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The ID of the collection if successful, otherwise <c>null</c>.</returns>
        public async Task<int?> GetOrCreateCollectionIdAsync(string collectionName, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return null;
            if (string.IsNullOrWhiteSpace(collectionName))
                return null;
            if (target == null)
                return null;

            var collectionId = await FindCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);
            if (collectionId == null)
                collectionId = await CreateCollectionAsync(collectionName, target, cancellationToken).ConfigureAwait(false);

            // Ensure the collection's sort title matches the provided title
            if (collectionId.HasValue)
            {
                bool ok = await UpdateCollectionTitleSortAsync(collectionId.Value, collectionName, target, cancellationToken).ConfigureAwait(false);
                if (!ok)
                    Logger.Debug("Failed to update titleSort for collection {CollectionId} in section {SectionId}", collectionId.Value, target.SectionId);
            }

            return collectionId;
        }

        /// <summary>
        /// Retrieves or creates a collection with the specified name across all configured Plex library targets.
        /// </summary>
        /// <param name="collectionName">The collection name to look up/create.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of <see cref="PlexLibraryCollectionTarget"/> containing
        /// each target and its corresponding collection ID.</returns>
        public async Task<List<PlexLibraryCollectionTarget>> GetOrCreateCollectionIdsAsync(string collectionName, CancellationToken cancellationToken = default)
        {
            var results = new List<PlexLibraryCollectionTarget>();
            if (!IsEnabled)
                return results;
            if (string.IsNullOrWhiteSpace(collectionName))
                return results;

            foreach (var target in _plexClient.GetConfiguredTargets())
            {
                var collectionId = await GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);
                if (collectionId.HasValue)
                    results.Add(new PlexLibraryCollectionTarget(target, collectionId.Value));
            }

            return results;
        }

        /// <summary>
        /// Uploads a poster to a collection by providing an external URL. The URL is forwarded to Plex which fetches and attaches the image.
        /// </summary>
        /// <param name="collectionId">Numeric ID of the collection.</param>
        /// <param name="posterUrl">URL of the poster image.</param>
        /// <param name="target">Library target where the collection resides.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> if the upload succeeded; <c>false</c> otherwise or if
        /// prerequisites are not met.</returns>
        public async Task<bool> UploadCollectionPosterByUrlAsync(int collectionId, string posterUrl, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;
            if (collectionId <= 0)
                return false;
            if (string.IsNullOrWhiteSpace(posterUrl))
                return false;
            if (target == null)
                return false;

            try
            {
                string requestPath = $"/library/metadata/{collectionId}/posters?url={Uri.EscapeDataString(posterUrl)}";
                Logger.Debug("Uploading collection poster via URL: {Server}{Path}", target.ServerUrl, requestPath);
                using var request = _plexClient.CreateRequest(HttpMethod.Post, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Logger.Warn(
                        "Plex collection poster URL upload failed with status {Status} for {CollectionId} on {Server}/{Section}. Response: {Response}",
                        response.StatusCode,
                        collectionId,
                        target.ServerUrl,
                        target.SectionId,
                        body?.Length > 1024 ? body.Substring(0, 1024) + "..." : body
                    );
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "UploadCollectionPosterByUrlAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        private async Task<int?> FindCollectionIdAsync(string title, PlexLibraryTarget target, CancellationToken cancellationToken)
        {
            string requestPath = $"/library/sections/{target.SectionId}/collections?title={Uri.EscapeDataString(title)}&X-Plex-Container-Size=10";

            using var request = _plexClient.CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
            var collection = container?.Metadata?.FirstOrDefault();
            if (collection == null || string.IsNullOrWhiteSpace(collection.RatingKey))
                return null;

            return int.TryParse(collection.RatingKey, out int ratingKey) ? ratingKey : null;
        }

        private async Task<int?> CreateCollectionAsync(string title, PlexLibraryTarget target, CancellationToken cancellationToken)
        {
            // Include titleSort explicitly so Plex uses the provided string for sorting (avoid ignoring prefixes like "The").
            string requestPath = $"/library/collections?title={Uri.EscapeDataString(title)}&titleSort={Uri.EscapeDataString(title)}&sectionId={target.SectionId}&type={(int)target.LibraryType}";

            using var request = _plexClient.CreateRequest(HttpMethod.Post, requestPath, target.ServerUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Logger.Warn(
                    "Plex create collection failed with status {Status} for title {Title} on {Server}/{Section}. Response: {Response}",
                    response.StatusCode,
                    title,
                    target.ServerUrl,
                    target.SectionId,
                    body?.Length > 1024 ? body.Substring(0, 1024) + "..." : body
                );
                return null;
            }

            var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
            var collection = container?.Metadata?.FirstOrDefault();
            if (collection == null || string.IsNullOrWhiteSpace(collection.RatingKey))
                return null;

            return int.TryParse(collection.RatingKey, out int ratingKey) ? ratingKey : null;
        }

        /// <summary>
        /// Assigns an item to a collection by updating the item's metadata on the Plex server. Uses Plex's metadata PUT interface to add a collection tag.
        /// </summary>
        /// <param name="ratingKey">Plex rating key of the item to modify.</param>
        /// <param name="collectionName">Name of the collection to assign.</param>
        /// <param name="target">Target server/section for the operation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> when the assignment succeeds; otherwise <c>false</c>.</returns>
        public async Task<bool> AssignCollectionToItemByMetadataAsync(int ratingKey, string collectionName, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;
            if (ratingKey <= 0)
                return false;
            if (string.IsNullOrWhiteSpace(collectionName))
                return false;
            if (target == null)
                return false;

            try
            {
                // Check if the item already has this collection assigned
                string metadataPath = $"/library/metadata/{ratingKey}?X-Plex-Container-Size=1";
                using var getRequest = _plexClient.CreateRequest(HttpMethod.Get, metadataPath, target.ServerUrl);
                using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
                if (getResponse.IsSuccessStatusCode)
                {
                    var container = await PlexApi.ReadContainerAsync(getResponse, cancellationToken).ConfigureAwait(false);
                    var existing = container?.Metadata?.FirstOrDefault();
                    if (existing?.Collection?.Any(c => string.Equals(c.Tag, collectionName, StringComparison.OrdinalIgnoreCase)) == true)
                    {
                        Logger.Debug("Collection '{CollectionName}' already assigned to ratingKey {RatingKey}, skipping PUT", collectionName, ratingKey);
                        return true;
                    }
                }

                // Use Plex's 'collection[0].tag.tag' parameter which sets the collection tag properly.
                string requestPath = $"/library/metadata/{ratingKey}?collection%5B0%5D.tag.tag={Uri.EscapeDataString(collectionName)}";
                Logger.Debug("Assigning collection via metadata PUT: {Server}{Path}", target.ServerUrl, requestPath);
                using var request = _plexClient.CreateRequest(HttpMethod.Put, requestPath, target.ServerUrl);
                var sw = Stopwatch.StartNew();
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                Logger.Debug(
                    "AssignCollection PUT completed in {Elapsed}ms for ratingKey {RatingKey} status {Status} on {Server}:{Section}",
                    sw.ElapsedMilliseconds,
                    ratingKey,
                    response.StatusCode,
                    target.ServerUrl,
                    target.SectionId
                );

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Logger.Warn(
                        "Plex metadata collection assignment failed with status {Status} for ratingKey {RatingKey} on {Server}:{Section}. Response: {Response}",
                        response.StatusCode,
                        ratingKey,
                        target.ServerUrl,
                        target.SectionId,
                        body?.Length > 1024 ? body.Substring(0, 1024) + "..." : body
                    );
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "AssignCollectionToItemByMetadataAsync failed for {RatingKey} on {Server}:{Section}", ratingKey, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        /// <summary>
        /// Scans all configured Plex libraries and deletes any collections that contain zero items.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The number of collections that were deleted.</returns>
        public async Task<int> DeleteEmptyCollectionsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return 0;

            int deleted = 0;
            foreach (var target in _plexClient.GetConfiguredTargets())
            {
                try
                {
                    string requestPath = $"/library/sections/{target.SectionId}/collections?X-Plex-Container-Size=500";
                    using var request = _plexClient.CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                    if (container?.Metadata == null)
                        continue;

                    foreach (var m in container.Metadata)
                    {
                        if (string.IsNullOrWhiteSpace(m.RatingKey))
                            continue;
                        if (!int.TryParse(m.RatingKey, out int collId))
                            continue;

                        if (m.ChildCount.HasValue)
                        {
                            if (m.ChildCount.Value == 0)
                            {
                                bool ok = await DeleteCollectionAsync(collId, target, cancellationToken).ConfigureAwait(false);
                                if (ok)
                                {
                                    Logger.Info("Deleted empty collection {CollectionId} in section {SectionId}", collId, target.SectionId);
                                    deleted++;
                                }
                            }

                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "DeleteEmptyCollectionsAsync failed for {ServerUrl}/{SectionId}", target.ServerUrl, target.SectionId);
                }
            }

            return deleted;
        }

        /// <summary>
        /// Deletes the specified collection if it contains no child items.
        /// </summary>
        /// <param name="collectionId">The ID of the collection to check/delete.</param>
        /// <param name="target">Library target hosting the collection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> if the collection was deleted; otherwise <c>false</c>.</returns>
        public async Task<bool> DeleteCollectionIfEmptyAsync(int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;

            try
            {
                string childPath = $"/library/collections/{collectionId}/children?X-Plex-Container-Size=1";
                using var childReq = _plexClient.CreateRequest(HttpMethod.Get, childPath, target.ServerUrl);
                using var childResp = await _httpClient.SendAsync(childReq, cancellationToken).ConfigureAwait(false);
                if (childResp.IsSuccessStatusCode)
                {
                    var childCont = await PlexApi.ReadContainerAsync(childResp, cancellationToken).ConfigureAwait(false);
                    if (childCont?.Metadata == null || childCont.Metadata.Count == 0)
                    {
                        return await DeleteCollectionAsync(collectionId, target, cancellationToken).ConfigureAwait(false);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "DeleteCollectionIfEmptyAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        private async Task<bool> DeleteCollectionAsync(int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = _plexClient.CreateRequest(HttpMethod.Delete, $"/library/collections/{collectionId}", target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn("Plex delete collection failed with status {Status} for {CollectionId}", response.StatusCode, collectionId);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "DeleteCollectionAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        /// <summary>
        /// Updates the sort title of a collection so Plex orders it based on the provided <paramref name="title"/> rather than default sorting rules.
        /// </summary>
        /// <param name="collectionId">ID of the collection to update.</param>
        /// <param name="title">New title to use for sorting.</param>
        /// <param name="target">Library target containing the collection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> if the update succeeded, <c>false</c> otherwise.</returns>
        public async Task<bool> UpdateCollectionTitleSortAsync(int collectionId, string title, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;
            if (collectionId <= 0)
                return false;
            if (string.IsNullOrWhiteSpace(title))
                return false;
            if (target == null)
                return false;

            try
            {
                string requestPath = $"/library/metadata/{collectionId}?titleSort={Uri.EscapeDataString(title)}";
                using var request = _plexClient.CreateRequest(HttpMethod.Put, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn("Plex update collection titleSort failed with status {Status} for {CollectionId}", response.StatusCode, collectionId);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "UpdateCollectionTitleSortAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }
    }
}
