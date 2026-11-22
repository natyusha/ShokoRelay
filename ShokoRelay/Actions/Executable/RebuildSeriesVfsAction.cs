using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Actions;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Actions.Executable;

/// <summary>Series-scoped action to rebuild VFS links and restore AnimeThemes links for the selected Shoko series.</summary>
public class RebuildSeriesVfsAction(
    VfsBuilder vfsBuilder,
    AnimeThemesMapping atMapping,
    VfsWatcher vfsWatcher,
    PlexMetadata plexMetadata,
    PlexClient plexClient,
    IMetadataService metadataService,
    ConfigProvider configProvider
) : SeriesAction, IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public override string Name => "Rebuild VFS";

    /// <inheritdoc/>
    public override string? Description => "Rebuilds the Shoko Relay VFS and AnimeThemes links for this series.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <summary>Gets or sets whether to schedule partial Plex scans and debounced metadata updates after rebuilding.</summary>
    [Display(Name = "Trigger Plex Updates", Description = "Schedule partial Plex scans and debounced metadata updates for this series.")]
    [DefaultValue(true)]
    public bool TriggerPlexUpdates { get; set; } = true;

    #endregion

    #region Execution

    /// <inheritdoc/>
    public void SetProgress(IProgress<decimal> progress) => _progress = progress;

    /// <inheritdoc/>
    public override async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        int primaryId = OverrideHelper.GetPrimary(Series.LocalID, metadataService);
        await VfsShared.VfsLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _progress.Report(15);
            await Task.Run(() => vfsBuilder.Build(primaryId, cleanRoot: false, token), token).ConfigureAwait(false);
            _progress.Report(70);

            await ActionScheduleHelper.ApplyMappingIfExistsAsync(atMapping, configProvider, [primaryId], token).ConfigureAwait(false);

            plexMetadata.InvalidateCache(primaryId);
            if (TriggerPlexUpdates && plexClient.IsEnabled)
                vfsWatcher.TriggerPlexUpdates(primaryId);

            _progress.Report(100);
        }
        finally
        {
            VfsShared.VfsLock.Release();
        }
    }

    #endregion
}
