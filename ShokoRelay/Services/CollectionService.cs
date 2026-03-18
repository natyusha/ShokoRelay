using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Services;

#region Interface and Models

/// <summary>Service responsible for building and managing Plex collections based on Shoko series metadata.</summary>
public interface ICollectionService
{
    /// <summary>Create or update Plex collections for the supplied series list.</summary>
    /// <param name="seriesList">The collection of series to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result object containing statistics on the operation.</returns>
    Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default);

    /// <summary>Apply collection posters for the given series list.</summary>
    /// <param name="seriesList">The collection of series to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result object containing statistics on the operation.</returns>
    Task<ApplyPostersResult> ApplyCollectionPostersAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default);
}

/// <summary>Result returned by <see cref="ICollectionService.BuildCollectionsAsync"/>.</summary>
/// <param name="Processed">Number of series processed.</param>
/// <param name="Created">Number of collections successfully assigned.</param>
/// <param name="Uploaded">Number of posters uploaded.</param>
/// <param name="SeasonPostersUploaded">Number of season-specific posters uploaded.</param>
/// <param name="Skipped">Number of items skipped.</param>
/// <param name="Errors">Count of errors encountered.</param>
/// <param name="DeletedEmptyCollections">Number of empty collections removed.</param>
/// <param name="CreatedCollections">List of metadata objects for created collections.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
public sealed record BuildCollectionsResult(
    int Processed,
    int Created,
    int Uploaded,
    int SeasonPostersUploaded,
    int Skipped,
    int Errors,
    int DeletedEmptyCollections,
    List<object> CreatedCollections,
    List<string> ErrorsList
);

/// <summary>Outcome of applying collection posters via <see cref="ICollectionService.ApplyCollectionPostersAsync"/>.</summary>
/// <param name="Processed">Number of series processed.</param>
/// <param name="Uploaded">Number of posters successfully uploaded.</param>
/// <param name="Skipped">Number of series skipped.</param>
/// <param name="Errors">Count of errors encountered.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
public sealed record ApplyPostersResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> ErrorsList);

#endregion

/// <summary>Default implementation of <see cref="ICollectionService"/>.</summary>
public class CollectionService(PlexClient plexClient, PlexCollections plexCollections, IMetadataService metadataService, PlexMetadata mapper) : ICollectionService
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    #endregion

    #region Collection Building

    /// <inheritdoc/>
    public async Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default)
    {
        const string taskName = ShokoRelayConstants.TaskPlexCollectionsBuild;
        TaskHelper.StartTask(taskName);
        Logger.Info("Plex Collections: Starting task...");

        try
        {
            var (created, uploaded, errs, uniqueSeries) = (0, 0, 0, new HashSet<int>());
            var (createdList, errorsList) = (new List<object>(), new List<string>());
            var allowedIds = new HashSet<int>(seriesList?.Where(s => s != null).Select(s => OverrideHelper.GetPrimary(s!.ID, metadataService)) ?? []);
            var targets = plexClient.GetConfiguredTargets();

            if (targets.Count == 0)
                return new BuildCollectionsResult(0, 0, 0, 0, 0, 0, 0, createdList, errorsList);

            foreach (var target in targets)
            {
                var items = await plexClient.GetSectionShowsAsync(target, cancellationToken) ?? [];
                var posted = new HashSet<int>();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;
                    var sid = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!sid.HasValue || (allowedIds.Count > 0 && !allowedIds.Contains(sid.Value)))
                        continue;

                    uniqueSeries.Add(sid.Value);
                    var series = metadataService.GetShokoSeriesByID(sid.Value);
                    var collectionName = series != null ? mapper.GetCollectionName(series) : null;
                    if (string.IsNullOrEmpty(collectionName) || !int.TryParse(item.RatingKey, out int plexKey))
                        continue;

                    Logger.Trace("CollectionService: Processing series {0} (Plex Key: {1})", sid.Value, item.RatingKey);
                    if (await plexCollections.AssignCollectionToItemByMetadataAsync(plexKey, collectionName, target, cancellationToken))
                    {
                        created++;
                        Logger.Info("Plex Collections: Assigned '{0}' to '{1}'", collectionName, item.Title);
                        createdList.Add(
                            new
                            {
                                seriesId = sid.Value,
                                ratingKey = plexKey,
                                collectionName,
                                sectionId = target.SectionId,
                            }
                        );

                        if (await plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken) is { } cid && posted.Add(cid))
                        {
                            Logger.Trace("CollectionService: Triggering poster upload for '{0}'", collectionName);
                            if (await TryApplyPoster(series!, collectionName, cid, target, cancellationToken))
                            {
                                uploaded++;
                                Logger.Debug("CollectionService: Uploaded poster for '{0}'", collectionName);
                            }
                        }
                    }
                    else
                    {
                        errs++;
                        errorsList.Add($"Failed assignment: {sid.Value}");
                    }
                }
            }
            int deleted = await plexCollections.DeleteEmptyCollectionsAsync(cancellationToken);
            Logger.Info("Plex Collections: Task finished. {0} collections assigned.", created);
            return new BuildCollectionsResult(uniqueSeries.Count, created, uploaded, 0, uniqueSeries.Count - created, errs, deleted, createdList, errorsList);
        }
        finally
        {
            TaskHelper.FinishTask(taskName);
        }
    }

    #endregion

    #region Poster Application

    /// <inheritdoc/>
    public async Task<ApplyPostersResult> ApplyCollectionPostersAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default)
    {
        var (processed, uploaded, skipped, errs, errorsList) = (0, 0, 0, 0, new List<string>());
        var allowedIds = new HashSet<int>(seriesList?.Where(s => s != null).Select(s => OverrideHelper.GetPrimary(s!.ID, metadataService)) ?? []);
        var targets = plexClient.GetConfiguredTargets();

        foreach (var target in targets)
        {
            var items = await plexClient.GetSectionShowsAsync(target, cancellationToken) ?? [];
            var posted = new HashSet<int>();

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Guid))
                    continue;
                var sid = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                if (!sid.HasValue || (allowedIds.Count > 0 && !allowedIds.Contains(sid.Value)))
                    continue;

                processed++;
                var series = metadataService.GetShokoSeriesByID(sid.Value);
                var name = series != null ? mapper.GetCollectionName(series) : null;
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                if (await plexCollections.GetOrCreateCollectionIdAsync(name, target, cancellationToken) is { } cid && posted.Add(cid))
                {
                    if (await TryApplyPoster(series!, name, cid, target, cancellationToken))
                        uploaded++;
                    else
                    {
                        errs++;
                        errorsList.Add($"Poster failed: {name}");
                    }
                }
                else
                    skipped++;
            }
        }
        return new ApplyPostersResult(processed, uploaded, skipped, errs, errorsList);
    }

    #endregion

    #region Internal Helpers

    private async Task<bool> TryApplyPoster(IShokoSeries series, string name, int cid, PlexLibraryTarget target, CancellationToken ct)
    {
        string? url = PlexHelper.GetCollectionPosterUrl(series, name, cid, metadataService, ShokoRelay.Settings.CollectionPosters);
        return !string.IsNullOrEmpty(url) && await plexCollections.UploadCollectionPosterByUrlAsync(cid, url, target, ct);
    }

    #endregion
}
