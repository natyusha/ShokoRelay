using NLog;
using ShokoRelay.Config;

namespace ShokoRelay.Plex;

/// <summary>Provides utilities for working with Plex collections.</summary>
public class PlexCollections(HttpClient httpClient, PlexClient plexClient)
{
    #region Fields and Data Types

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _httpClient = httpClient;
    private readonly PlexClient _plexClient = plexClient;

    /// <summary>Whether Plex integration is enabled.</summary>
    public bool IsEnabled => _plexClient.IsEnabled;

    /// <summary>Represents a specific collection target within a Plex library section.</summary>
    /// <param name="Target">The library target.</param>
    /// <param name="CollectionId">The numeric collection ID.</param>
    public sealed record PlexLibraryCollectionTarget(PlexLibraryTarget Target, int CollectionId);

    #endregion

    #region Collection Discovery

    /// <summary>Looks up or creates a collection in a specific library target.</summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="target">The target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collection ID or null.</returns>
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

    /// <summary>Retrieves or creates a collection with the specified name across all targets.</summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of target/ID pairs.</returns>
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

    #endregion

    #region Poster Operations

    /// <summary>Uploads a poster to a collection via URL.</summary>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="posterUrl">Poster URL.</param>
    /// <param name="target">Target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success.</returns>
    public async Task<bool> UploadCollectionPosterByUrlAsync(int collectionId, string posterUrl, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        await ExecuteActionAsync(HttpMethod.Post, $"/library/metadata/{collectionId}/posters?url={Uri.EscapeDataString(posterUrl)}", target, $"Upload poster for {collectionId}", cancellationToken);

    #endregion

    #region Item Assignment

    /// <summary>Assigns an item to a collection by updating metadata.</summary>
    /// <param name="ratingKey">Plex rating key.</param>
    /// <param name="collectionName">Collection name.</param>
    /// <param name="target">Target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success.</returns>
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

    #endregion

    #region Cleanup Operations

    /// <summary>Scans Plex libraries and deletes empty collections.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of deleted collections.</returns>
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

    #endregion

    #region Metadata Updates

    /// <summary>Updates the sort title of a collection.</summary>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="title">New sort title.</param>
    /// <param name="target">Target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success.</returns>
    public async Task<bool> UpdateCollectionTitleSortAsync(int collectionId, string title, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        await ExecuteActionAsync(
            HttpMethod.Put,
            $"/library/metadata/{collectionId}?titleSort={Uri.EscapeDataString(title)}&titleSort.locked=1",
            target,
            $"Update sort title for {collectionId}",
            cancellationToken
        );

    #endregion

    #region Internal API Helpers

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

    #endregion
}
