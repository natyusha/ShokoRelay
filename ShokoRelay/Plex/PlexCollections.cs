namespace ShokoRelay.Plex;

#region Data Models

/// <summary>Details of a collection assignment operation.</summary>
/// <param name="TargetTitle">Title of the Plex target section.</param>
/// <param name="SectionId">Plex section ID.</param>
/// <param name="CollectionName">Name of the assigned collection.</param>
/// <param name="SeriesId">Shoko series ID.</param>
/// <param name="RatingKey">Plex rating key.</param>
/// <param name="IsMovie">Whether the target library is a movie library.</param>
public sealed record CollectionAssignmentDetail(string TargetTitle, int SectionId, string CollectionName, int SeriesId, int RatingKey, bool IsMovie);

/// <summary>Details of a collection artwork upload operation.</summary>
/// <param name="TargetTitle">Title of the Plex target section.</param>
/// <param name="IsMovie">Whether the target library is a movie library.</param>
/// <param name="Label">Artwork label (poster, backdrop, logo, square art).</param>
/// <param name="CollectionName">Name of the collection.</param>
/// <param name="RatingKey">Plex collection rating key.</param>
public sealed record CollectionUploadDetail(string TargetTitle, bool IsMovie, string Label, string CollectionName, int RatingKey);

/// <summary>Details of a collection deletion operation.</summary>
/// <param name="TargetTitle">Title of the Plex target section.</param>
/// <param name="IsMovie">Whether the target library is a movie library.</param>
/// <param name="CollectionName">Name of the collection.</param>
/// <param name="RatingKey">Plex collection rating key.</param>
public sealed record CollectionDeletionDetail(string TargetTitle, bool IsMovie, string CollectionName, int RatingKey);

#endregion

/// <summary>Provides utilities for working with Plex collections.</summary>
public class PlexCollections(HttpClient httpClient, PlexClient plexClient)
{
    #region Setup & State

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <summary>Whether Plex integration is enabled.</summary>
    public bool IsEnabled => plexClient.IsEnabled;

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

        // Find a collection ID by title
        using var req = plexClient.CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/collections?title={Uri.EscapeDataString(collectionName)}&X-Plex-Container-Size=10", target.ServerUrl);
        using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var meta = (await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
        if (int.TryParse(meta?.RatingKey, out int id))
            return id;

        // Create a new collection in the specified target library
        string path = $"/library/collections?title={Uri.EscapeDataString(collectionName)}&titleSort={Uri.EscapeDataString(collectionName)}&sectionId={target.SectionId}&type={(int)target.LibraryType}";
        using var req2 = plexClient.CreateRequest(HttpMethod.Post, path, target.ServerUrl);
        using var resp2 = await httpClient.SendAsync(req2, cancellationToken).ConfigureAwait(false);
        if (!resp2.IsSuccessStatusCode)
        {
            var body = await resp2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            s_logger.Warn(
                "PlexCollections: Create collection failed with status {0} for title {1} on {2}/{3}. Response: {4}",
                resp2.StatusCode,
                collectionName,
                target.ServerUrl,
                target.SectionId,
                body?.Length > 1024 ? body[..1024] + "..." : body
            );
            return null;
        }
        var meta2 = (await PlexApi.ReadContainerAsync(resp2, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault();
        return int.TryParse(meta2?.RatingKey, out int id2) ? id2 : null;
    }

    #endregion

    #region Poster Operations

    /// <summary>Uploads a custom collection image (poster, backdrop, logo, or square art) to Plex by URL.</summary>
    /// <param name="collectionId">Plex collection ID.</param>
    /// <param name="imageUrl">The dynamic callback URL to fetch the image bytes.</param>
    /// <param name="subEndpoint">Plex metadata sub-endpoint (e.g. posters, arts, clearLogos, squareArts).</param>
    /// <param name="target">The target Plex library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the upload was successful; otherwise, false.</returns>
    public Task<bool> UploadCollectionImageByUrlAsync(int collectionId, string imageUrl, string subEndpoint, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(HttpMethod.Post, $"/library/metadata/{collectionId}/{subEndpoint}?url={Uri.EscapeDataString(imageUrl)}", target, $"Upload {subEndpoint} for {collectionId}", cancellationToken);

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

        using var checkReq = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Size=1", target.ServerUrl);
        using var checkResp = await httpClient.SendAsync(checkReq, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Removes a collection tag from an item by updating metadata.</summary>
    /// <param name="ratingKey">Plex rating key.</param>
    /// <param name="collectionName">Collection name to remove.</param>
    /// <param name="target">Target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success.</returns>
    public Task<bool> RemoveCollectionFromItemAsync(int ratingKey, string collectionName, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(
            HttpMethod.Put,
            $"/library/metadata/{ratingKey}?collection%5B%5D.tag.tag-={Uri.EscapeDataString(collectionName)}",
            target,
            $"Remove '{collectionName}' from {ratingKey}",
            cancellationToken
        );

    #endregion

    #region Cleanup Operations

    /// <summary>Scans Plex libraries and deletes empty collections.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of deleted collection details.</returns>
    public async Task<List<CollectionDeletionDetail>> DeleteEmptyCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var deleted = new List<CollectionDeletionDetail>();
        if (!IsEnabled)
            return deleted;

        foreach (var target in plexClient.GetConfiguredTargets())
        {
            bool isMovie = target.LibraryType == PlexLibraryType.Movie;
            using var request = plexClient.CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?type={PlexConstants.TypeCollection}&X-Plex-Container-Size=500", target.ServerUrl);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                continue;

            foreach (var m in (await PlexApi.ReadContainerAsync(response, cancellationToken).ConfigureAwait(false))?.Metadata ?? [])
            {
                // Exclude smart collections from automatic empty collection deletion
                if (TextHelper.IsPlexTrue(m.Smart))
                    continue;

                // Delete a custom collection from the specified Plex library target
                if (
                    int.TryParse(m.RatingKey, out int id)
                    && m.ChildCount == 0
                    && await ExecuteActionAsync(HttpMethod.Delete, $"/library/collections/{id}", target, $"Delete collection {id}", cancellationToken).ConfigureAwait(false)
                )
                {
                    s_logger.Info("PlexCollections: Deleted empty collection '{0}' (RatingKey: {1}) in section {2}", m.Title ?? "Unknown", id, target.SectionId);
                    deleted.Add(new CollectionDeletionDetail(target.Title, isMovie, m.Title ?? "Unknown", id));
                }
            }
        }
        return deleted;
    }

    #endregion

    #region Metadata Updates

    /// <summary>Updates the sort title and summary of a collection.</summary>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="title">New sort title.</param>
    /// <param name="summary">New summary.</param>
    /// <param name="target">Target library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success.</returns>
    public Task<bool> UpdateCollectionMetadataAsync(int collectionId, string title, string summary, PlexLibraryTarget target, CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(
            HttpMethod.Put,
            $"/library/metadata/{collectionId}?titleSort={Uri.EscapeDataString(title)}&titleSort.locked=1&summary={Uri.EscapeDataString(summary)}&summary.locked=1",
            target,
            $"Update metadata for {collectionId}",
            cancellationToken
        );

    #endregion

    #region Internal Helpers

    /// <summary>Executes a generic Plex API action and handles response logging.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">API path.</param>
    /// <param name="target">Target server.</param>
    /// <param name="actionName">Display name for logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the request returned a success status code.</returns>
    private async Task<bool> ExecuteActionAsync(HttpMethod method, string path, PlexLibraryTarget target, string actionName, CancellationToken ct)
    {
        try
        {
            using var request = plexClient.CreateRequest(method, path, target.ServerUrl);
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            s_logger.Warn(
                "PlexCollections: {0} failed with status {1} on {2}/{3} -> Response {4}",
                actionName,
                response.StatusCode,
                target.ServerUrl,
                target.SectionId,
                body?.Length > 1024 ? body[..1024] + "..." : body
            );
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "PlexCollections: {0} failed for {1}:{2}", actionName, target.ServerUrl, target.SectionId);
        }
        return false;
    }

    #endregion
}
