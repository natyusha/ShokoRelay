using Shoko.Abstractions.Actions;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Actions.Executable;

/// <summary>Executable action to apply the AnimeThemes cross-reference mapping CSV to VFS Shorts folders.</summary>
public class ApplyAnimeThemesVfsAction(AnimeThemesMapping atMapping, ConfigProvider configProvider, ILogger<ApplyAnimeThemesVfsAction> logger) : IExecutableAction, IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public string Name => "Apply AnimeThemes VFS";

    /// <inheritdoc/>
    public string? Description => "Applies the AnimeThemes cross-reference mapping CSV to create theme symlinks in the VFS.";

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
        Task.FromResult<ActionValidationResult?>(
            !File.Exists(Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping)) ? new($"Mapping file '{ShokoRelayConstants.FileAtMapping}' was not found.") : null
        );

    /// <inheritdoc/>
    public async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        await ActionScheduleHelper
            .RunWithTaskLockAsync(
                ShokoRelayConstants.TaskAtVfsBuild,
                VfsShared.VfsLock,
                async () =>
                {
                    _progress.Report(15);
                    var result = await atMapping.ApplyMappingAsync(null, token).ConfigureAwait(false);
                    ReportHelper.WriteReport($"{ShokoRelayConstants.TaskAtVfsBuild}.report", result, (builder, report) => ReportHelper.BuildAtVfsBuildReport(builder, report, []), logger);
                    _progress.Report(100);
                },
                token
            )
            .ConfigureAwait(false);
    }

    #endregion
}
