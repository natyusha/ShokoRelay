using Shoko.Abstractions.Actions;

namespace ShokoRelay.Actions.Executable;

/// <summary>Executable action to audit VFS directories and remove broken symlinks and orphaned folders.</summary>
/// <param name="vfsBuilder">VFS builder.</param>
/// <param name="logger">Logger instance.</param>
public class AuditVfsAction(VfsBuilder vfsBuilder, ILogger<AuditVfsAction> logger) : IExecutableAction, IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public string Name => "Audit VFS";

    /// <inheritdoc/>
    public string? Description => "Scans VFS directories to remove broken symlinks and orphaned series folders.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public ActionPermission Permission => ActionPermission.Admin;

    #endregion

    #region Execution

    /// <inheritdoc/>
    public void SetProgress(IProgress<decimal> progress) => _progress = progress;

    /// <inheritdoc/>
    public Task<ActionValidationResult?> Validate(CancellationToken token = default) =>
        Task.FromResult<ActionValidationResult?>(Settings.Advanced.DisableVfsGeneration ? new("VFS generation is disabled in Shoko Relay settings.") : null);

    /// <inheritdoc/>
    public async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskVfsAudit,
                VfsShared.VfsLock,
                async () =>
                {
                    var result = await Task.Run(() => vfsBuilder.Audit(token, _progress), token).ConfigureAwait(false);
                    ReportHelper.WriteReport($"{ShokoRelayConstants.TaskVfsAudit}.report", result, ReportHelper.BuildVfsAuditReport, logger);
                    _progress.Report(100);
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
