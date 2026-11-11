using System.Diagnostics;

namespace ShokoRelay.Services;

#region Interface & Models

/// <summary>Result of a full Plex automation run containing elapsed times and individual sub-task results.</summary>
/// <param name="TotalElapsed">The total elapsed time for the entire automation run.</param>
/// <param name="CollectionsElapsed">The elapsed time for the collection building sub-task.</param>
/// <param name="CollectionsResult">The result stats returned by the collection building service.</param>
/// <param name="RatingsElapsed">The elapsed time for the critic rating application sub-task.</param>
/// <param name="RatingsResult">The result stats returned by the critic rating application service.</param>
/// <param name="ImageSyncElapsed">The optional elapsed time for the Plex image synchronization sub-task.</param>
/// <param name="ImageSyncResult">The optional result stats returned by the Plex image synchronization service.</param>
/// <param name="TrashElapsed">The optional elapsed time for the Plex empty trash sub-task.</param>
/// <param name="TrashMessages">The optional list of Plex empty trash status messages.</param>
public sealed record PlexAutomationRunResult(
    TimeSpan TotalElapsed,
    TimeSpan CollectionsElapsed,
    BuildCollectionsResult CollectionsResult,
    TimeSpan RatingsElapsed,
    ApplyRatingsResult RatingsResult,
    TimeSpan? ImageSyncElapsed,
    ImageSyncResult? ImageSyncResult,
    TimeSpan? TrashElapsed,
    List<string>? TrashMessages
);

/// <summary>Service responsible for orchestrating Plex collections, critic ratings, image sync, and empty trash automations.</summary>
public interface IPlexAutomationService
{
    /// <summary>Executes the collection generation, critic rating application, image synchronization, and Plex trash emptying automations back-to-back.</summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A <see cref="PlexAutomationRunResult"/> containing the aggregated execution metrics.</returns>
    Task<PlexAutomationRunResult> RunAutomationAsync(IProgress<decimal>? progress = null, CancellationToken token = default);
}

#endregion

/// <summary>Default implementation of <see cref="IPlexAutomationService"/>.</summary>
/// <param name="collectionService">Plex collection service.</param>
/// <param name="criticRatingService">Plex critic rating service.</param>
/// <param name="imageSyncService">Shoko image sync service.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
public class PlexAutomationService(ICollectionService collectionService, ICriticRatingService criticRatingService, IImageSyncService imageSyncService, PlexClient plexClient, IMetadataService metadataService)
    : IPlexAutomationService
{
    #region Public API

    /// <inheritdoc/>
    public async Task<PlexAutomationRunResult> RunAutomationAsync(IProgress<decimal>? progress = null, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var allSeries = metadataService.GetAllShokoSeries()?.Cast<IShokoSeries?>().ToList() ?? [];
        progress?.Report(10);

        var swCollections = Stopwatch.StartNew();
        var collectionRes = await collectionService.BuildCollectionsAsync(allSeries, cancellationToken: token).ConfigureAwait(false);
        swCollections.Stop();
        progress?.Report(40);

        var swRatings = Stopwatch.StartNew();
        var ratingRes = await criticRatingService.ApplyRatingsAsync(null, token).ConfigureAwait(false);
        swRatings.Stop();
        progress?.Report(70);

        TimeSpan? imageSyncElapsed = null;
        ImageSyncResult? imageSyncRes = null;
        if (Settings.Advanced.EnableImageSync)
        {
            var swImages = Stopwatch.StartNew();
            imageSyncRes = await imageSyncService.SyncImagesAsync(cancellationToken: token).ConfigureAwait(false);
            swImages.Stop();
            imageSyncElapsed = swImages.Elapsed;
        }
        progress?.Report(90);

        TimeSpan? trashElapsed = null;
        var trashMessages = new List<string>();
        int threshold = Settings.Advanced.EmptyPlexTrashThreshold;
        if (threshold > 0)
        {
            var swTrash = Stopwatch.StartNew();
            foreach (var target in plexClient.GetConfiguredTargets())
            {
                var (_, _, msg) = await plexClient.EmptyTrashWithSafetyAsync(target, threshold, false, token).ConfigureAwait(false);
                trashMessages.Add($"[{target.Title}] {msg}");
            }
            swTrash.Stop();
            trashElapsed = swTrash.Elapsed;
        }

        sw.Stop();
        progress?.Report(100);

        return new PlexAutomationRunResult(sw.Elapsed, swCollections.Elapsed, collectionRes, swRatings.Elapsed, ratingRes, imageSyncElapsed, imageSyncRes, trashElapsed, trashMessages);
    }

    #endregion
}
