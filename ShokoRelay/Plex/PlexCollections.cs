using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex
{
    public class PlexCollections
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HttpClient _httpClient;
        private readonly ConfigProvider _configProvider;
        private readonly PlexClient _plexClient; // for section/item helpers

        public PlexCollections(HttpClient httpClient, ConfigProvider configProvider, PlexClient plexClient)
        {
            _httpClient = httpClient;
            _configProvider = configProvider;
            _plexClient = plexClient;
        }

        public bool IsEnabled => _plexClient.IsEnabled;

        public sealed record PlexLibraryCollectionTarget(PlexLibraryTarget Target, int CollectionId);

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
            string requestPath = $"/library/sections/{target.SectionId}/collections?title={Uri.EscapeDataString(title)}";

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
                // Use Plex's 'collection[0].tag.tag' parameter which sets the collection tag properly.
                // Include a simple fallback 'collection[0]' param too for compatibility with variants of Plex.
                string requestPath = $"/library/metadata/{ratingKey}?collection%5B0%5D.tag.tag={Uri.EscapeDataString(collectionName)}&collection%5B0%5D={Uri.EscapeDataString(collectionName)}";
                Logger.Debug("Assigning collection via metadata PUT: {Server}{Path}", target.ServerUrl, requestPath);
                using var request = _plexClient.CreateRequest(HttpMethod.Put, requestPath, target.ServerUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

        public async Task<int> DeleteEmptyCollectionsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return 0;

            int deleted = 0;
            foreach (var target in _plexClient.GetConfiguredTargets())
            {
                try
                {
                    string requestPath = $"/library/sections/{target.SectionId}/collections";
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

                        int? count = await GetCollectionItemCountAsync(collId, target, cancellationToken).ConfigureAwait(false);
                        if (count.HasValue && count.Value == 0)
                        {
                            bool ok = await DeleteCollectionAsync(collId, target, cancellationToken).ConfigureAwait(false);
                            if (ok)
                            {
                                Logger.Info("Deleted empty collection {CollectionId} in section {SectionId}", collId, target.SectionId);
                                deleted++;
                            }
                        }
                        else if (count.HasValue && count.Value > 0)
                        {
                            // Ensure the collection's titleSort matches the provided title so Plex sorts correctly
                            if (!string.IsNullOrWhiteSpace(m.Title))
                            {
                                bool ok = await UpdateCollectionTitleSortAsync(collId, m.Title, target, cancellationToken).ConfigureAwait(false);
                                if (!ok)
                                    Logger.Debug("Failed to update titleSort for collection {CollectionId} in section {SectionId}", collId, target.SectionId);
                            }
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

        public async Task<bool> DeleteCollectionIfEmptyAsync(int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return false;

            try
            {
                var count = await GetCollectionItemCountAsync(collectionId, target, cancellationToken).ConfigureAwait(false);
                if (count.HasValue && count.Value == 0)
                {
                    return await DeleteCollectionAsync(collectionId, target, cancellationToken).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "DeleteCollectionIfEmptyAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }

        private async Task<int?> GetCollectionItemCountAsync(int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken = default)
        {
            try
            {
                // We need to count items *in the context of the target section* so that
                // collections that are empty for this library/section can be removed even
                // if they contain items in other libraries.
                int start = 0;
                const int pageSize = 200;

                while (true)
                {
                    string requestPath = $"/library/collections/{collectionId}/items?X-Plex-Container-Start={start}&X-Plex-Container-Size={pageSize}";
                    using var request = _plexClient.CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl);
                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var container = await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false);
                    if (container == null)
                        return null;

                    // If any metadata explicitly belongs to our section, consider the collection non-empty.
                    if (container.Metadata != null)
                    {
                        foreach (var item in container.Metadata)
                        {
                            if (item.LibrarySectionId.HasValue)
                            {
                                if (item.LibrarySectionId.Value == target.SectionId)
                                    return 1; // non-empty in this section
                            }
                            else if (!string.IsNullOrWhiteSpace(item.RatingKey) && int.TryParse(item.RatingKey, out int ratingKey))
                            {
                                // Fallback: query the specific item's metadata to determine its section
                                bool existsInSection = await _plexClient.ItemExistsInSectionAsync(ratingKey, target, cancellationToken).ConfigureAwait(false);
                                if (existsInSection)
                                    return 1;
                            }
                        }
                    }

                    // Determine pagination bounds
                    int fetched = container.Metadata?.Count ?? 0;
                    int total = container.TotalSize ?? container.Size ?? -1;

                    if (fetched == 0)
                        break;

                    if (total >= 0 && start + pageSize >= total)
                        break;

                    start += pageSize;
                }

                // No items found for this section.
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GetCollectionItemCountAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return null;
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
