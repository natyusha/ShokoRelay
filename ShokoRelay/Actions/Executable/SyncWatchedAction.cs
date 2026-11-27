using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Actions;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions.Executable;

/// <summary>Executable action to synchronize watched states, ratings, and playback progress between Plex and Shoko with optional parameters.</summary>
/// <param name="syncToShoko">Watched state sync service (Plex to Shoko).</param>
/// <param name="syncToPlex">Watched state sync service (Shoko to Plex).</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class SyncWatchedAction(SyncToShoko syncToShoko, SyncToPlex syncToPlex, PlexClient plexClient, ConfigProvider configProvider, ILogger<SyncWatchedAction> logger)
    : IExecutableAction,
        IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public string Name => "Sync Watched States";

    /// <inheritdoc/>
    public string? Description => "Synchronizes watched states, ratings, and playback progress between Plex and Shoko.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public ActionPermission Permission => ActionPermission.Admin;

    /// <summary>Gets or sets whether to push watched states from Shoko to Plex instead of importing from Plex to Shoko.</summary>
    [Display(Name = "Sync Shoko to Plex", Description = "Push Shoko watched states to Plex instead of importing from Plex to Shoko.")]
    public bool ImportToPlex { get; set; }

    /// <summary>Gets or sets whether to preview changes without modifying Shoko or Plex.</summary>
    [Display(Name = "Dry Run", Description = "Preview changes without modifying Shoko or Plex.")]
    public bool DryRun { get; set; }

    /// <summary>Gets or sets an optional lookback window in hours to limit processed items.</summary>
    [Display(Name = "Lookback Hours", Description = "Optional lookback window in hours to limit processed items.")]
    public int? SinceHours { get; set; }

    /// <summary>Gets or sets an optional override for including user ratings.</summary>
    [Display(Name = "Include Ratings", Description = "Override the Include Ratings setting for this run.")]
    public bool? IncludeRatings { get; set; }

    /// <summary>Gets or sets an optional override for including playback progress.</summary>
    [Display(Name = "Include Progress", Description = "Override the Include Progress setting for this run.")]
    public bool? IncludeProgress { get; set; }

    #endregion

    #region Execution

    /// <inheritdoc/>
    public void SetProgress(IProgress<decimal> progress) => _progress = progress;

    /// <inheritdoc/>
    public Task<ActionValidationResult?> Validate(CancellationToken token = default) => Task.FromResult(ActionScheduleHelper.ValidateWatchedSync(plexClient, configProvider));

    /// <inheritdoc/>
    public async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        var settings = configProvider.GetSettings();
        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskShokoSyncWatched,
                SyncHelper.SyncLock,
                async () =>
                {
                    _progress.Report(10);
                    var result = ImportToPlex
                        ? await syncToPlex.SyncWatchedAsync(DryRun, SinceHours, IncludeRatings, IncludeProgress, cancellationToken: token).ConfigureAwait(false)
                        : await syncToShoko.SyncWatchedAsync(DryRun, SinceHours, IncludeRatings, IncludeProgress, cancellationToken: token).ConfigureAwait(false);

                    result = result with { Direction = ImportToPlex ? "Plex<-Shoko" : "Plex->Shoko" };
                    ReportHelper.WriteReport(
                        $"{ShokoRelayConstants.TaskShokoSyncWatched}.report",
                        result,
                        (builder, report) => ReportHelper.BuildSyncWatchedReport(builder, report, report.Direction, report.DryRun, IncludeRatings ?? settings.Automation.ShokoSyncWatchedIncludeRatings),
                        logger
                    );
                    _progress.Report(100);
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
