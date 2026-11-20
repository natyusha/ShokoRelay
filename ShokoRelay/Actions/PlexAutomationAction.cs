using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to automate Plex Collections, Critic Ratings, Image Sync, and Trash emptying.</summary>
public class PlexAutomationAction(PlexAutomationService plexAutomationService, ConfigProvider configProvider, ILogger<PlexAutomationAction> logger) : IScheduledAction
{
    #region Action Metadata

    /// <inheritdoc/>
    public string Name => $"{ShokoRelayConstants.Name}: Plex Automation";

    /// <inheritdoc/>
    public string Description => "Generates Plex Collections, applies Critic Ratings, syncs Plex Images, and safely empties Plex trash.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(30);

    #endregion

    #region Execution

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(0);
        await SyncHelper.SyncLock.WaitAsync(token).ConfigureAwait(false);
        bool startedTask = TaskHelper.StartTask(ShokoRelayConstants.TaskPlexAutomationRun);
        try
        {
            var result = await plexAutomationService.RunAutomationAsync(progress, token).ConfigureAwait(false);
            string apiBase = $"{configProvider.ServerBaseUrl}{ShokoRelayConstants.BasePath}";
            ReportHelper.WriteReport($"{ShokoRelayConstants.TaskPlexAutomationRun}.report", result, (builder, report) => ReportHelper.BuildPlexAutomationReport(builder, report, apiBase), logger);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(ShokoRelayConstants.TaskPlexAutomationRun);
            SyncHelper.SyncLock.Release();
        }
    }

    #endregion
}
