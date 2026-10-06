using System.Diagnostics;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Controllers;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to automate Plex Collections, Critic Ratings, Image Sync, and Trash emptying.</summary>
public class PlexAutomationAction(
    ICollectionService collectionService,
    ICriticRatingService criticRatingService,
    IImageSyncService imageSyncService,
    PlexClient plexClient,
    ConfigProvider configProvider,
    IMetadataService metadataService,
    ILogger<PlexAutomationAction> logger
) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Shoko Relay: Plex Automation";

    /// <inheritdoc/>
    public string Description => "Generates Plex Collections, applies Critic Ratings, syncs Plex Images, and safely empties Plex trash.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(30);

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        var settings = configProvider.GetSettings();
        if (settings.Automation.PlexAutomationFrequencyHours == 0)
            return;

        await SyncHelper.SyncLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var allSeries = metadataService.GetAllShokoSeries()?.Cast<IShokoSeries?>().ToList() ?? [];
            progress.Report(10);

            var swCollections = Stopwatch.StartNew();
            var collectionRes = await collectionService.BuildCollectionsAsync(allSeries, cancellationToken: token).ConfigureAwait(false);
            swCollections.Stop();
            progress.Report(40);

            var swRatings = Stopwatch.StartNew();
            var ratingRes = await criticRatingService.ApplyRatingsAsync(null, token).ConfigureAwait(false);
            swRatings.Stop();
            progress.Report(70);

            TimeSpan? imageSyncElapsed = null;
            ImageSyncResult? imageSyncRes = null;
            if (settings.Advanced.EnableImageSync)
            {
                var swImages = Stopwatch.StartNew();
                imageSyncRes = await imageSyncService.SyncImagesAsync(cancellationToken: token).ConfigureAwait(false);
                swImages.Stop();
                imageSyncElapsed = swImages.Elapsed;
            }
            progress.Report(90);

            TimeSpan? trashElapsed = null;
            var trashMessages = new List<string>();
            int threshold = settings.Advanced.EmptyPlexTrashThreshold;
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
            progress.Report(100);

            var result = new PlexAutomationRunResult(sw.Elapsed, swCollections.Elapsed, collectionRes, swRatings.Elapsed, ratingRes, imageSyncElapsed, imageSyncRes, trashElapsed, trashMessages);

            string apiBase = $"{configProvider.ServerBaseUrl}{ShokoRelayConstants.BasePath}";
            LogHelper.WriteReport(configProvider.PluginDirectory, $"{ShokoRelayConstants.TaskPlexAutomationRun}-report.log", result, (sb, r) => LogHelper.BuildPlexAutomationReport(sb, r, apiBase), logger);
        }
        finally
        {
            SyncHelper.SyncLock.Release();
        }
    }
}
