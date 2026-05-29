using NLog;

namespace ShokoRelay.Services;

#region Interface and Models

/// <summary>Service responsible for building and managing Plex collections based on Shoko series metadata.</summary>
public interface ICollectionService
{
    /// <summary>Create or update Plex collections and their images for the supplied series list.</summary>
    /// <param name="seriesList">The collection of series to process.</param>
    /// <param name="applyAssignment">If true, perform metadata assignment; otherwise, only refresh collection image assets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result object containing statistics on the operation.</returns>
    Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, bool applyAssignment = true, CancellationToken cancellationToken = default);
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

#endregion

/// <summary>Default implementation of <see cref="ICollectionService"/>.</summary>
public class CollectionService(PlexClient plexClient, PlexCollections plexCollections, IMetadataService metadataService, PlexMetadata mapper) : ICollectionService
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion

    #region Collection Building

    /// <inheritdoc/>
    public async Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, bool applyAssignment = true, CancellationToken cancellationToken = default)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexCollectionsBuild;
        TaskHelper.StartTask(TaskName);
        s_logger.Info("CollectionService: Starting task...");

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
                // Fetch all items at the start to minimize per-item API calls
                var items = await plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
                var collections = await plexClient.GetSectionCollectionsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];

                // Map collection names to their Plex RatingKeys (IDs)
                var collectionIdMap = collections
                    .Where(c => !string.IsNullOrEmpty(c.Title) && !string.IsNullOrEmpty(c.RatingKey))
                    .GroupBy(c => c.Title!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => int.Parse(g.First().RatingKey!), StringComparer.OrdinalIgnoreCase);

                var posted = new HashSet<(int Cid, string Prefix)>();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid) || !int.TryParse(item.RatingKey, out int plexKey))
                        continue;
                    var sid = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!sid.HasValue || (allowedIds.Count > 0 && !allowedIds.Contains(sid.Value)))
                        continue;

                    uniqueSeries.Add(sid.Value);
                    var series = metadataService.GetShokoSeriesByID(sid.Value);
                    var collectionName = series != null ? mapper.GetCollectionName(series) : null;

                    s_logger.Trace("CollectionService: Processing series {0} (Plex Key: {1})", sid.Value, item.RatingKey);

                    // Skip standard metadata assignment if only refreshing poster assets
                    if (applyAssignment)
                    {
                        var currentPlexCollections = item.Collection?.Select(c => c.Tag).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? [];
                        bool alreadyHasCorrect = !string.IsNullOrEmpty(collectionName) && currentPlexCollections.Any(c => string.Equals(c, collectionName, StringComparison.OrdinalIgnoreCase));

                        foreach (var staleName in currentPlexCollections)
                        {
                            // Remove if the series belongs to a different group (rename) or is now a solo group (null)
                            if (collectionName == null || !string.Equals(staleName, collectionName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (await plexCollections.RemoveCollectionFromItemAsync(plexKey, staleName!, target, cancellationToken).ConfigureAwait(false))
                                    s_logger.Info("CollectionService: Removed incorrect collection '{0}' from '{1}'", staleName, item.Title);
                            }
                        }

                        if (!string.IsNullOrEmpty(collectionName) && !alreadyHasCorrect)
                        {
                            var assignmentOk = await plexCollections.AssignCollectionToItemByMetadataAsync(plexKey, collectionName, target, cancellationToken).ConfigureAwait(false);
                            if (assignmentOk)
                            {
                                created++;
                                s_logger.Info("CollectionService: Assigned '{0}' to '{1}'", collectionName, item.Title);
                                createdList.Add(
                                    new
                                    {
                                        seriesId = sid.Value,
                                        ratingKey = plexKey,
                                        collectionName,
                                        sectionId = target.SectionId,
                                    }
                                );
                            }
                            else
                            {
                                errs++;
                                errorsList.Add($"Failed assignment: {sid.Value}");
                            }
                        }
                    }

                    // Always handle standard poster and image application
                    if (!string.IsNullOrEmpty(collectionName))
                    {
                        if (!collectionIdMap.TryGetValue(collectionName, out int cid))
                        {
                            var newId = await plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);
                            if (newId.HasValue)
                                cid = collectionIdMap[collectionName] = newId.Value;
                        }

                        if (cid > 0)
                        {
                            foreach (var config in PlexConstants.CollectionImageConfigs)
                            {
                                if (posted.Add((cid, config.Prefix)))
                                {
                                    var fallback = config.DefaultFallback && Settings.CollectionImages;
                                    var url = PlexHelper.GetCollectionImageUrl(series!, collectionName, cid, config.Suffix, config.Suffixes, metadataService, fallback);
                                    if (!string.IsNullOrEmpty(url) && await plexCollections.UploadCollectionImageByUrlAsync(cid, url, config.Prefix, target, cancellationToken).ConfigureAwait(false))
                                    {
                                        uploaded++;
                                        s_logger.Debug("CollectionService: Applied {0} for '{1}'", config.Label, collectionName);
                                    }
                                }
                            }
                        }
                    }
                }

                // Apply custom images to smart collections
                foreach (var col in collections)
                {
                    if (TextHelper.IsPlexTrue(col.Smart) && int.TryParse(col.RatingKey, out int cid) && !string.IsNullOrEmpty(col.Title))
                    {
                        foreach (var config in PlexConstants.CollectionImageConfigs)
                        {
                            var posterPath = PlexHelper.FindCollectionImagePath(null, col.Title, cid, config.Suffixes, metadataService);
                            if (!string.IsNullOrEmpty(posterPath) && File.Exists(posterPath))
                            {
                                var url =
                                    $"{ServerBaseUrl}{ShokoRelayConstants.BasePath}/collections/user/sc{cid}?name={Uri.EscapeDataString(col.Title)}&suffix={config.Suffix}&t={new FileInfo(posterPath).LastWriteTimeUtc.Ticks}";
                                if (await plexCollections.UploadCollectionImageByUrlAsync(cid, url, config.Prefix, target, cancellationToken).ConfigureAwait(false))
                                {
                                    uploaded++;
                                    s_logger.Info("CollectionService: Applied custom {0} to smart collection '{1}' (ID: {2})", config.Label, col.Title, cid);
                                }
                            }
                        }
                    }
                }
            }
            int deleted = 0;
            if (applyAssignment)
                deleted = await plexCollections.DeleteEmptyCollectionsAsync(cancellationToken).ConfigureAwait(false);

            s_logger.Info("CollectionService: Task finished -> {0} collections assigned", created);
            return new BuildCollectionsResult(uniqueSeries.Count, created, uploaded, 0, uniqueSeries.Count - created, errs, deleted, createdList, errorsList);
        }
        finally
        {
            TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion
}
