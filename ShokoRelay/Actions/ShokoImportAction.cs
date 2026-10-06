using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Services;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to trigger Shoko to scan managed folders for new or unrecognized files.</summary>
public class ShokoImportAction(IShokoImportService shokoImportService, ILogger<ShokoImportAction> logger) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Shoko Relay: Auto Import";

    /// <inheritdoc/>
    public string Description => "Triggers Shoko to scan managed folders for new or unrecognized files.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        progress.Report(10);
        await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        progress.Report(100);
        logger.LogInformation("Scheduled auto import completed.");
    }
}
