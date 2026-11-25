using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Actions;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Actions.Executable;

/// <summary>Executable action to build or rebuild the Shoko Relay Virtual File System across all eligible managed folders.</summary>
/// <param name="vfsBuilder">VFS builder.</param>
/// <param name="atMapping">AnimeThemes mapping service.</param>
/// <param name="logger">Logger instance.</param>
public class BuildVfsAction(VfsBuilder vfsBuilder, AnimeThemesMapping atMapping, ILogger<BuildVfsAction> logger) : IExecutableAction, IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public string Name => "Build VFS";

    /// <inheritdoc/>
    public string? Description => "Builds or rebuilds the Shoko Relay Virtual File System and restores AnimeThemes VFS links.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public ActionPermission Permission => ActionPermission.Admin;

    /// <inheritdoc/>
    public bool RequiresConfirmation => Clean;

    /// <inheritdoc/>
    public string? ConfirmationMessage => "Existing VFS root folders will be deleted and recreated when Clean Roots is enabled.";

    /// <summary>Gets or sets whether to delete existing VFS root directories before building.</summary>
    [Display(Name = "Clean Roots", Description = "Delete existing VFS root directories before rebuilding.")]
    [DefaultValue(true)]
    public bool Clean { get; set; } = true;

    #endregion

    #region Execution

    /// <inheritdoc/>
    public void SetProgress(IProgress<decimal> progress) => _progress = progress;

    /// <inheritdoc/>
    public async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskVfsBuild,
                VfsShared.VfsLock,
                async () =>
                {
                    _progress.Report(10);
                    var result = await Task.Run(() => vfsBuilder.Build((int?)null, Clean, token), token).ConfigureAwait(false);
                    _progress.Report(70);

                    await atMapping.ApplyMappingIfExistsAsync(null, token).ConfigureAwait(false);

                    ReportHelper.WriteReport($"{ShokoRelayConstants.TaskVfsBuild}.report", result, ReportHelper.BuildVfsReport, logger);
                    _progress.Report(100);
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
