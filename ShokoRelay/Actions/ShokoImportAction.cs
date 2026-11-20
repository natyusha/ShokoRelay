using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using ShokoRelay.Services;

namespace ShokoRelay.Actions;

/// <summary>Scheduled action to trigger Shoko to scan managed folders for new or unrecognized files.</summary>
public class ShokoImportAction(ShokoImportService shokoImportService, ILogger<ShokoImportAction> logger) : IScheduledAction
{
    #region Action Metadata

    /// <inheritdoc/>
    public string Name => $"{ShokoRelayConstants.Name}: Auto Import";

    /// <inheritdoc/>
    public string Description => "Triggers Shoko to scan managed folders for new or unrecognized files.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public TimeSpan? MinimumInterval => TimeSpan.FromMinutes(10);

    #endregion

    #region Execution

    /// <inheritdoc/>
    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress.Report(0);
        progress.Report(10);
        await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        progress.Report(100);
        logger.LogInformation("Scheduled auto import completed.");
    }

    #endregion
}
