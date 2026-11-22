using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions.Scheduled;

/// <summary>Scheduled action to synchronize watched states and progress from Plex into Shoko.</summary>
public class PlexWatchedSyncAction(SyncToShoko watchedSyncService, PlexClient plexClient, ConfigProvider configProvider, ILogger<PlexWatchedSyncAction> logger) : IScheduledAction
{
    #region Action Metadata

    /// <inheritdoc/>
    public string Name => $"{ShokoRelayConstants.Name}: Watch Sync (Plex to Shoko)";

    /// <inheritdoc/>
    public string Description => "Synchronizes watched states, ratings, and playback progress from Plex into Shoko.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(10);

    #endregion

    #region Execution

    /// <inheritdoc/>
    public Task<ActionValidationResult?> Validate(CancellationToken token) => Task.FromResult(ActionScheduleHelper.ValidateWatchedSync(plexClient, configProvider));

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(0);

        var settings = configProvider.GetSettings();
        var frequency = settings.Automation.ShokoSyncWatchedFrequencyHours;
        int lookback = frequency > 0 ? frequency + 1 : 25;

        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskShokoSyncWatched,
                SyncHelper.SyncLock,
                async () =>
                {
                    progress.Report(10);
                    var result = await watchedSyncService.SyncWatchedAsync(false, lookback, cancellationToken: token).ConfigureAwait(false);
                    progress.Report(100);

                    ReportHelper.WriteReport(
                        $"{ShokoRelayConstants.TaskShokoSyncWatched}.report",
                        result,
                        (builder, report) => ReportHelper.BuildSyncWatchedReport(builder, report, report.Direction, report.DryRun, settings.Automation.ShokoSyncWatchedIncludeRatings),
                        logger
                    );
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
