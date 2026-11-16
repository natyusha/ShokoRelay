using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to synchronize watched states and progress from Plex into Shoko.</summary>
public class PlexWatchedSyncAction(SyncToShoko watchedSyncService, ConfigProvider configProvider, ILogger<PlexWatchedSyncAction> logger) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Shoko Relay: Watch Sync (Plex to Shoko)";

    /// <inheritdoc/>
    public string Description => "Synchronizes watched states, ratings, and playback progress from Plex into Shoko.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(0);
        TaskHelper.StartTask(ShokoRelayConstants.TaskShokoSyncWatched);

        var settings = configProvider.GetSettings();
        var freq = settings.Automation.ShokoSyncWatchedFrequencyHours;
        int lookback = freq > 0 ? freq + 1 : 25;

        await SyncHelper.SyncLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            progress.Report(10);
            var result = await watchedSyncService.SyncWatchedAsync(false, lookback, cancellationToken: token).ConfigureAwait(false);
            progress.Report(100);

            LogHelper.WriteReport(
                $"{ShokoRelayConstants.TaskShokoSyncWatched}-report.log",
                result,
                (sb, r) => LogHelper.BuildSyncWatchedReport(sb, r, r.Direction, r.DryRun, settings.Automation.ShokoSyncWatchedIncludeRatings),
                logger
            );
        }
        finally
        {
            SyncHelper.SyncLock.Release();
            TaskHelper.FinishTask(ShokoRelayConstants.TaskShokoSyncWatched);
        }
    }
}
