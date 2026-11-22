using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions;

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
    public Task<ActionValidationResult?> Validate(CancellationToken token) =>
        Task.FromResult<ActionValidationResult?>(
            !plexClient.IsEnabled ? new("Plex server configuration is missing or no library is selected.")
            : configProvider.GetSettings().Automation.ShokoSyncWatchedUserType == SyncUserType.None ? new("Watched sync is disabled because Sync Users is set to None.")
            : null
        );

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(0);

        var settings = configProvider.GetSettings();
        var freq = settings.Automation.ShokoSyncWatchedFrequencyHours;
        int lookback = freq > 0 ? freq + 1 : 25;

        await SyncHelper.SyncLock.WaitAsync(token).ConfigureAwait(false);
        bool startedTask = TaskHelper.StartTask(ShokoRelayConstants.TaskShokoSyncWatched);
        try
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
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(ShokoRelayConstants.TaskShokoSyncWatched);
            SyncHelper.SyncLock.Release();
        }
    }

    #endregion
}
