using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

/// <summary>
/// Provides utilities for working with Plex collections such as creating, updating, and deleting collections as well as assigning items to them.
/// </summary>
/// <param name="httpClient">HTTP client used to communicate with Plex servers.</param>
/// <param name="plexClient">Helper for interacting with Plex-specific APIs.</param>
public class PlexCollections(HttpClient httpClient, PlexClient plexClient)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _httpClient = httpClient;
    private readonly PlexClient _plexClient = plexClient;

    /// <summary>
    /// Gets a value indicating whether Plex integration is enabled.
    /// </summary>
    public bool IsEnabled => _plexClient.IsEnabled;

    /// <summary>
    /// Represents a specific collection target within a Plex library section.
    /// </summary>
    /// <param name="Target">The library target containing server and section information.</param>
    /// <param name="CollectionId">The numeric ID of the collection.</param>
    public sealed record PlexLibraryCollectionTarget(PlexLibraryTarget Target, int CollectionId);

    /// <summary>
    /// Looks up or creates a collection with the given name in a specific library target.
    /// </summary>
    /// <param name="collectionName">The name of the collection.</param>
    /// <param name="target">The library target where the collection resides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ID of the collection if successful, otherwise <c>null</c>.</returns>
    public async Task<int?> GetOrCreateCollectionIdAsync(string collectionName, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(collectionName) || target == null)
            return null;

        var collectionId =
            await FindCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false) ?? await CreateCollectionAsync(collectionName, target, cancellationToken).ConfigureAwait(false);

        if (collectionId.HasValue)
            await UpdateCollectionTitleSortAsync(collectionId.Value, collectionName, target, cancellationToken).ConfigureAwait(false);

        return collectionId;
    }

    /// <summary>
    /// Retrieves or creates a collection with the specified name across all configured Plex library targets.
    /// </summary>
    /// <param name="collectionName">The collection name to look up/create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of each target and its corresponding collection ID.</returns>
    public async Task<List<PlexLibraryCollectionTarget>> GetOrCreateCollectionIdsAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        var results = new List<PlexLibraryCollectionTarget>();
        if (!IsEnabled || string.IsNullOrWhiteSpace(collectionName))
            return results;

        foreach (var target in _plexClient.GetConfiguredTargets())
            if (await GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false) is { } id)
                results.Add(new PlexLibraryCollectionTarget(target, id));

        return results;
    }

    /// <summary>
    /// Uploads a poster to a collection by providing an external URL.
    /// </summary>
    /// <param name="collectionId">Numeric ID of the collection.</param>
    /// <param name="posterUrl">URL of the poster image.</param>
    /// <param name="target">Library target where the collection resides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the upload succeeded; otherwise <c>false</c>.</returns>
    public async Task<bool> UploadCollectionPosterByUrlAsync(int collectionId, string posterUrl, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        await ExecuteActionAsync(HttpMethod.Post, $"/library/metadata/{collectionId}/posters?url={Uri.EscapeDataString(posterUrl)}", target, $"Upload poster for {collectionId}", cancellationToken);

    /// <summary>
    /// Assigns an item to a collection by updating the item's metadata on the Plex server.
    /// </summary>
    /// <param name="ratingKey">Plex rating key of the item to modify.</param>
    /// <param name="collectionName">Name of the collection to assign.</param>
    /// <param name="target">Target server/section for the operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the assignment succeeds; otherwise <c>false</c>.</returns>
    public async Task<bool> AssignCollectionToItemByMetadataAsync(int ratingKey, string collectionName, PlexLibraryTarget target, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || ratingKey <= 0 || string.IsNullOrWhiteSpace(collectionName) || target == null)
            return false;

        using var checkReq = _plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Size=1", target.ServerUrl);
        using var checkResp = await _httpClient.SendAsync(checkReq, cancellationToken).ConfigureAwait(false);
        return (
                checkResp.IsSuccessStatusCode
                && (await PlexApi.ReadContainerAsync(checkResp, cancellationToken).ConfigureAwait(false))
                    ?.Metadata?.FirstOrDefault()
                    ?.Collection?.Any(c => string.Equals(c.Tag, collectionName, StringComparison.OrdinalIgnoreCase)) == true
            )
            || await ExecuteActionAsync(
                HttpMethod.Put,
                $"/library/metadata/{ratingKey}?collection%5B0%5D.tag.tag={Uri.EscapeDataString(collectionName)}",
                target,
                $"Assign '{collectionName}' to {ratingKey}",
                cancellationToken
            );
    }

    /// <summary>
    /// Scans all configured Plex libraries and deletes any collections that contain zero items.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The number of collections that were deleted.</returns>
    public async Task<int> DeleteEmptyCollectionsAsync(CancellationToken cancellationToken = default)
    {
        int deleted = 0;
        if (!IsEnabled)
            return 0;

        foreach (var target in _plexClient.GetConfiguredTargets())
        {
            using var request = _plexClient.CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/collections?X-Plex-Container-Size=500", target.ServerUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                continue;

            foreach (var m in (await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false))?.Metadata ?? [])
            {
                if (int.TryParse(m.RatingKey, out int id) && m.ChildCount == 0 && await DeleteCollectionAsync(id, target, cancellationToken).ConfigureAwait(false))
                {
                    Logger.Info("Deleted empty collection {CollectionId} in section {SectionId}", id, target.SectionId);
                    deleted++;
                }
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
        using var req = _plexClient.CreateRequest(HttpMethod.Get, $"/library/collections/{collectionId}/children?X-Plex-Container-Size=1", target.ServerUrl);
        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        return resp.IsSuccessStatusCode
            && !((await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.Count > 0)
            && await DeleteCollectionAsync(collectionId, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the sort title of a collection so Plex orders it based on the provided title.
    /// </summary>
    /// <param name="collectionId">ID of the collection to update.</param>
    /// <param name="title">New title to use for sorting.</param>
    /// <param name="target">Library target containing the collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the update succeeded, <c>false</c> otherwise.</returns>
    public async Task<bool> UpdateCollectionTitleSortAsync(int collectionId, string title, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        await ExecuteActionAsync(
            HttpMethod.Put,
            $"/library/metadata/{collectionId}?titleSort={Uri.EscapeDataString(title)}&titleSort.locked=1",
            target,
            $"Update sort title for {collectionId}",
            cancellationToken
        );

    private async Task<int?> FindCollectionIdAsync(string title, PlexLibraryTarget target, CancellationToken ct)
    {
        using var req = _plexClient.CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/collections?title={Uri.EscapeDataString(title)}&X-Plex-Container-Size=10", target.ServerUrl);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var meta = (await PlexApi.ReadContainerAsync(resp, ct).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
        return int.TryParse(meta?.RatingKey, out int id) ? id : null;
    }

    private async Task<int?> CreateCollectionAsync(string title, PlexLibraryTarget target, CancellationToken ct)
    {
        string path = $"/library/collections?title={Uri.EscapeDataString(title)}&titleSort={Uri.EscapeDataString(title)}&sectionId={target.SectionId}&type={(int)target.LibraryType}";
        using var req = _plexClient.CreateRequest(HttpMethod.Post, path, target.ServerUrl);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.Warn(
                "Plex create collection failed with status {0} for title {1} on {2}/{3}. Response: {4}",
                resp.StatusCode,
                title,
                target.ServerUrl,
                target.SectionId,
                body?.Length > 1024 ? body[..1024] + "..." : body
            );
            return null;
        }
        var meta = (await PlexApi.ReadContainerAsync(resp, ct).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
        return int.TryParse(meta?.RatingKey, out int id) ? id : null;
    }

    private async Task<bool> DeleteCollectionAsync(int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        await ExecuteActionAsync(HttpMethod.Delete, $"/library/collections/{collectionId}", target, $"Delete collection {collectionId}", cancellationToken);

    /// <summary>
    /// Internal helper to reduce redundant HTTP request and error logging boilerplate.
    /// </summary>
    /// <param name="method">HTTP method to use.</param>
    /// <param name="path">API path relative to server URL.</param>
    /// <param name="target">Target Plex server/section.</param>
    /// <param name="actionName">Friendly name of the action for logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the request returned a success status code; otherwise <c>false</c>.</returns>
    private async Task<bool> ExecuteActionAsync(HttpMethod method, string path, PlexLibraryTarget target, string actionName, CancellationToken ct)
    {
        try
        {
            using var request = _plexClient.CreateRequest(method, path, target.ServerUrl);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.Warn("Plex {0} failed with status {1} on {2}/{3}. Response: {4}", actionName, response.StatusCode, target.ServerUrl, target.SectionId, body?.Length > 1024 ? body[..1024] + "..." : body);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "{0} failed for {1}:{2}", actionName, target.ServerUrl, target.SectionId);
        }
        return false;
    }
}
