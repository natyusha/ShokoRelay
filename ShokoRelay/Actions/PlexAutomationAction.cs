using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to automate Plex Collections, Critic Ratings, Image Sync, and Trash emptying.</summary>
public class PlexAutomationAction(IPlexAutomationService plexAutomationService, ConfigProvider configProvider, ILogger<PlexAutomationAction> logger) : IScheduledAction
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
            var result = await plexAutomationService.RunAutomationAsync(progress, token).ConfigureAwait(false);
            string apiBase = $"{configProvider.ServerBaseUrl}{ShokoRelayConstants.BasePath}";
            LogHelper.WriteReport(configProvider.PluginDirectory, $"{ShokoRelayConstants.TaskPlexAutomationRun}-report.log", result, (sb, r) => LogHelper.BuildPlexAutomationReport(sb, r, apiBase), logger);
        }
        finally
        {
            SyncHelper.SyncLock.Release();
        }
    }
}
