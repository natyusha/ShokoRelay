using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions.Scheduled;

/// <summary>Scheduled action to automate Plex Collections, Critic Ratings, Image Sync, and Trash emptying.</summary>
/// <param name="plexAutomationService">Plex automation service.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class PlexAutomationAction(PlexAutomationService plexAutomationService, PlexClient plexClient, ConfigProvider configProvider, ILogger<PlexAutomationAction> logger) : IScheduledAction
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
    public Task<ActionValidationResult?> Validate(CancellationToken token) => Task.FromResult(ActionScheduleHelper.ValidatePlexConfigured(plexClient));

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(0);
        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskPlexAutomationRun,
                SyncHelper.SyncLock,
                async () =>
                {
                    var result = await plexAutomationService.RunAutomationAsync(progress, token).ConfigureAwait(false);
                    string apiBase = $"{configProvider.ServerBaseUrl}{ShokoRelayConstants.BasePath}";
                    ReportHelper.WriteReport($"{ShokoRelayConstants.TaskPlexAutomationRun}.report", result, (builder, report) => ReportHelper.BuildPlexAutomationReport(builder, report, apiBase), logger);
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
