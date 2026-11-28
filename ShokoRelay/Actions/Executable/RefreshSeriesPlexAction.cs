using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Actions;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Actions.Executable;

/// <summary>Series-scoped action to refresh Plex metadata and optionally run collections, ratings, and image sync for the selected series.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="plexMetadata">Plex metadata mapper.</param>
/// <param name="collectionService">Plex collection service.</param>
/// <param name="criticRatingService">Plex critic rating service.</param>
/// <param name="imageSyncService">Shoko image sync service.</param>
/// <param name="metadataService">Shoko metadata service.</param>
public class RefreshSeriesPlexAction(
    PlexClient plexClient,
    PlexMetadata plexMetadata,
    CollectionService collectionService,
    CriticRatingService criticRatingService,
    ImageSyncService imageSyncService,
    IMetadataService metadataService
) : SeriesAction, IProgressReportingAction
{
    #region Setup & Metadata

    private IProgress<decimal> _progress = null!;

    /// <inheritdoc/>
    public override string Name => "Refresh Plex Metadata";

    /// <inheritdoc/>
    public override string? Description => "Refreshes Plex metadata for this series and optionally updates collections, critic ratings, and synced images.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <summary>Gets or sets whether to update Plex collections for this series.</summary>
    [Display(Name = "Update Collections", Description = "Update Plex collection assignments and artwork for this series.")]
    public bool UpdateCollections { get; set; }

    /// <summary>Gets or sets whether to apply critic ratings to this series in Plex.</summary>
    [Display(Name = "Apply Critic Ratings", Description = "Push configured AniDB or TMDB critic ratings for this series.")]
    public bool ApplyRatings { get; set; }

    /// <summary>Gets or sets whether to sync local/Plex images back to Shoko for this series.</summary>
    [Display(Name = "Sync Images", Description = "Synchronize local artwork and Plex thumbnails back to Shoko for this series.")]
    public bool SyncImages { get; set; }

    #endregion

    #region Execution

    /// <inheritdoc/>
    public void SetProgress(IProgress<decimal> progress) => _progress = progress;

    /// <inheritdoc/>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default) => Task.FromResult(ActionScheduleHelper.ValidatePlexConfigured(plexClient));

    /// <inheritdoc/>
    public override async Task Execute(CancellationToken token = default)
    {
        _progress.Report(0);
        int primaryId = OverrideHelper.GetPrimary(Series.LocalID, metadataService);
        var primarySeries = metadataService.GetShokoSeriesByID(primaryId) ?? Series;

        await SyncHelper.SyncLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _progress.Report(10);
            plexMetadata.InvalidateCache(primaryId);
            await plexClient.RefreshSeriesMetadataAsync(primaryId, metadataService, token).ConfigureAwait(false);

            _progress.Report(40);
            if (UpdateCollections)
                await collectionService.BuildCollectionsAsync([primarySeries], clean: false, cancellationToken: token, progress: _progress.Slice(40m, 60m)).ConfigureAwait(false);

            _progress.Report(60);
            if (ApplyRatings)
                await criticRatingService.ApplyRatingsAsync([primaryId], token, _progress.Slice(60m, 80m)).ConfigureAwait(false);

            _progress.Report(80);
            if (SyncImages)
                await imageSyncService.SyncImagesAsync([primaryId], token, _progress.Slice(80m, 100m)).ConfigureAwait(false);

            _progress.Report(100);
        }
        finally
        {
            SyncHelper.SyncLock.Release();
        }
    }

    #endregion
}
