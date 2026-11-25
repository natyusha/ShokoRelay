using System.Diagnostics;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.FileSystem.Services;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Services;

#region Data Models

/// <summary>Result returned by <see cref="CollectionService.BuildCollectionsAsync"/>.</summary>
/// <param name="Processed">Number of series processed.</param>
/// <param name="Created">Number of collections successfully assigned.</param>
/// <param name="Uploaded">Number of posters uploaded.</param>
/// <param name="AlreadyUploaded">Number of collection images already set and skipped from re-uploading.</param>
/// <param name="Skipped">Number of items skipped.</param>
/// <param name="Errors">Count of errors encountered.</param>
/// <param name="DeletedEmptyCollections">Number of empty collections removed.</param>
/// <param name="CreatedCollections">List of metadata objects for created collections.</param>
/// <param name="UploadedDetails">List of specific uploaded poster details.</param>
/// <param name="DeletedCollections">List of deleted empty collection details.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
/// <param name="TotalElapsed">The total time elapsed during the task.</param>
public sealed record BuildCollectionsResult(
    int Processed,
    int Created,
    int Uploaded,
    int AlreadyUploaded,
    int Skipped,
    int Errors,
    int DeletedEmptyCollections,
    List<CollectionAssignmentDetail> CreatedCollections,
    List<CollectionUploadDetail> UploadedDetails,
    List<CollectionDeletionDetail> DeletedCollections,
    List<string> ErrorsList,
    TimeSpan TotalElapsed
);

#endregion

/// <summary>Service responsible for building and managing Plex collections based on Shoko series metadata.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="plexCollections">Plex collection service.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="mapper">Plex metadata mapper.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class CollectionService(
    PlexClient plexClient,
    PlexCollections plexCollections,
    IMetadataService metadataService,
    PlexMetadata mapper,
    IVideoService videoService,
    IFileSystem fileSystem,
    ConfigProvider configProvider,
    ILogger<CollectionService> logger
)
{
    #region Setup

    /// <summary>Semaphore serializing collection build operations across manual, scheduled, and debounced fixup triggers.</summary>
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    #endregion

    #region Collection Building

    /// <summary>Create or update Plex collections and their images for the supplied series list, or all series when null.</summary>
    /// <param name="seriesList">Optional collection of series to process, or <c>null</c> to process all libraries in bulk.</param>
    /// <param name="applyAssignment">If true, perform metadata assignment; otherwise, only refresh collection image assets.</param>
    /// <param name="clean">If true, prunes old cached custom posters from Plex's local metadata directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result object containing statistics on the operation.</returns>
    public async Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?>? seriesList, bool applyAssignment = true, bool clean = true, CancellationToken cancellationToken = default)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexCollectionsBuild;
        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool startedTask = TaskHelper.StartTask(TaskName);
        logger.LogInformation("CollectionService: Starting task...");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (created, uploaded, alreadyUploaded, errors, uniqueSeries) = (0, 0, 0, 0, new HashSet<int>());
            var (createdList, uploadedDetails, errorsList) = (new List<CollectionAssignmentDetail>(), new List<CollectionUploadDetail>(), new List<string>());
            HashSet<int>? allowedIds = seriesList != null ? [.. seriesList.WhereNotNull().Select(series => OverrideHelper.GetPrimary(series.LocalID, metadataService))] : null;
            var targets = plexClient.GetConfiguredTargets();

            if (targets.Count == 0 || allowedIds is { Count: 0 })
                return new BuildCollectionsResult(0, 0, 0, 0, 0, 0, 0, createdList, uploadedDetails, [], errorsList, stopwatch.Elapsed);

            var excludedKeywords = (Settings.Advanced.CollectionExclusions ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> globalRoots = VfsShared.GetManagedFolderPaths(videoService);

            string cachePath = Path.Combine(configProvider.CacheDirectory, ShokoRelayConstants.FilePlexCollectionsCache);
            var cache = LoadUploadCache(cachePath);
            bool cacheModified = PruneStaleSectionCacheKeys(cache, targets);

            void RecordUploadOutcome(ImageUploadOutcome uploadOutcome)
            {
                if (uploadOutcome == ImageUploadOutcome.Uploaded)
                {
                    uploaded++;
                    cacheModified = true;
                }
                else if (uploadOutcome == ImageUploadOutcome.Skipped)
                    alreadyUploaded++;
            }

            if (clean && !string.IsNullOrWhiteSpace(Settings.Advanced.PlexMetadataPath) && fileSystem.DirectoryExists(Settings.Advanced.PlexMetadataPath))
                await PrunePlexCollectionImagesAsync(targets, cancellationToken).ConfigureAwait(false);

            foreach (var target in targets)
            {
                bool isMovieTarget = target.LibraryType == PlexLibraryType.Movie;

                var items =
                    allowedIds != null
                        ? await plexClient.GetSectionItemsForSeriesAsync(target, allowedIds, metadataService, cancellationToken: cancellationToken).ConfigureAwait(false)
                        : (
                            isMovieTarget
                                ? await plexClient.GetSectionMoviesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? []
                                : await plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? []
                        );

                var collections = await plexClient.GetSectionCollectionsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
                var collectionRatingKeyMap = collections.Where(collection => !string.IsNullOrEmpty(collection.RatingKey)).ToDictionary(collection => collection.RatingKey!, StringComparer.OrdinalIgnoreCase);
                var collectionIdMap = collections
                    .Where(collection => !string.IsNullOrEmpty(collection.Title) && int.TryParse(collection.RatingKey, out _))
                    .GroupBy(collection => collection.Title!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => int.Parse(group.First().RatingKey!), StringComparer.OrdinalIgnoreCase);

                if (PruneStaleCollectionCacheKeys(cache, target.SectionId, collectionRatingKeyMap))
                    cacheModified = true;

                var posted = new HashSet<(int CollectionId, string Prefix)>();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid) || !int.TryParse(item.RatingKey, out int plexKey))
                        continue;

                    int? seriesId = null;
                    if (isMovieTarget)
                    {
                        var episodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                        if (episodeId.HasValue && metadataService.GetShokoEpisodeByID(episodeId.Value) is { } movieEpisode)
                            seriesId = OverrideHelper.GetPrimary(movieEpisode.ShokoSeriesID, metadataService);
                    }
                    else
                        seriesId = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);

                    if (!seriesId.HasValue || (allowedIds != null && !allowedIds.Contains(seriesId.Value)))
                        continue;

                    uniqueSeries.Add(seriesId.Value);
                    var series = metadataService.GetShokoSeriesByID(seriesId.Value);
                    var collectionName = series != null ? mapper.GetCollectionName(series) : null;

                    logger.LogTrace("CollectionService: Processing series -> {Title} [{SeriesId}] (RatingKey: {RatingKey})", series?.GetDisplayTitle() ?? "Unknown", seriesId.Value, item.RatingKey);

                    // Skip standard metadata assignment if only refreshing poster assets
                    if (applyAssignment)
                    {
                        var currentPlexCollections = item.Collection?.Select(collectionTag => collectionTag.Tag).Where(tag => !string.IsNullOrEmpty(tag)).ToList() ?? [];
                        bool alreadyHasCorrect =
                            !string.IsNullOrEmpty(collectionName)
                            && currentPlexCollections.Any(existingCollectionName => string.Equals(existingCollectionName, collectionName, StringComparison.OrdinalIgnoreCase));

                        foreach (var staleName in currentPlexCollections)
                        {
                            if (PlexHelper.IsCollectionExcluded(staleName, excludedKeywords))
                                continue;

                            // Remove if the series belongs to a different group (rename) or is now a solo group (null)
                            if (
                                (collectionName == null || !string.Equals(staleName, collectionName, StringComparison.OrdinalIgnoreCase))
                                && await plexCollections.RemoveCollectionFromItemAsync(plexKey, staleName!, target, cancellationToken).ConfigureAwait(false)
                            )
                                logger.LogInformation(
                                    "CollectionService: Removed incorrect collection '{Collection}' from -> {Title} [{SeriesId}]",
                                    staleName,
                                    series?.GetDisplayTitle() ?? item.Title,
                                    seriesId.Value
                                );
                        }

                        if (!string.IsNullOrEmpty(collectionName) && !alreadyHasCorrect)
                        {
                            var assignmentOk = await plexCollections.AssignCollectionToItemByMetadataAsync(plexKey, collectionName, target, cancellationToken, skipCheck: true).ConfigureAwait(false);
                            if (assignmentOk)
                            {
                                created++;
                                logger.LogInformation("CollectionService: Assigned '{Collection}' to -> {Title} [{SeriesId}]", collectionName, series?.GetDisplayTitle() ?? item.Title, seriesId.Value);
                                createdList.Add(new CollectionAssignmentDetail(target.Title, target.SectionId, collectionName, seriesId.Value, plexKey, isMovieTarget));
                            }
                            else
                            {
                                errors++;
                                errorsList.Add($"Failed assignment: {seriesId.Value}");
                            }
                        }
                    }

                    // Always handle standard poster and image application
                    if (!string.IsNullOrEmpty(collectionName))
                    {
                        if (!collectionIdMap.TryGetValue(collectionName, out int collectionId))
                        {
                            var newId = await plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);
                            if (newId.HasValue)
                                collectionId = collectionIdMap[collectionName] = newId.Value;
                        }

                        if (collectionId > 0)
                        {
                            if (posted.Add((collectionId, "metadata")))
                            {
                                var group = metadataService.GetShokoGroupByID(series!.TopLevelGroupID);
                                var primarySeries = group?.MainSeries ?? group?.Series?.FirstOrDefault() ?? series!;
                                var summary = TextHelper.GetSanitizedDescription(primarySeries);
                                await plexCollections.UpdateCollectionMetadataAsync(collectionId, collectionName, summary, target, cancellationToken).ConfigureAwait(false);
                            }

                            foreach (var (prefix, suffix, suffixes, label, defaultFallback) in PlexConstants.CollectionImageConfigs)
                            {
                                if (!posted.Add((collectionId, prefix)))
                                    continue;

                                var fallback = defaultFallback && Settings.CollectionImages;
                                var imagePath = PlexHelper.GetCollectionImage(series!, collectionName, collectionId, suffix, suffixes, metadataService, fallback);
                                // If checking poster, verify that the collection in Plex actually has a custom poster set (not empty or composite)
                                bool plexHasPoster =
                                    prefix != "posters"
                                    || (
                                        collectionRatingKeyMap.TryGetValue(collectionId.ToString(), out var plexCollection)
                                        && plexCollection.Thumb != null
                                        && !plexCollection.Thumb.Contains("/composite/", StringComparison.OrdinalIgnoreCase)
                                    );
                                RecordUploadOutcome(
                                    await TryUploadCollectionImageAsync(
                                            target,
                                            collectionId,
                                            collectionName,
                                            imagePath,
                                            prefix,
                                            label,
                                            $"{target.SectionId}|{collectionId}|{prefix}",
                                            plexHasPoster,
                                            isMovieTarget,
                                            "Applied",
                                            cache,
                                            uploadedDetails,
                                            cancellationToken
                                        )
                                        .ConfigureAwait(false)
                                );
                            }
                        }
                    }
                }

                // Apply custom images to smart collections
                foreach (var collection in collections)
                    if (PlexHelper.IsPlexTrue(collection.Smart) && int.TryParse(collection.RatingKey, out int collectionId) && !string.IsNullOrEmpty(collection.Title))
                        foreach (var (prefix, suffix, suffixes, label, _) in PlexConstants.CollectionImageConfigs)
                        {
                            var posterPath = PlexHelper.FindCollectionImagePath(null, collection.Title, collectionId, suffixes, metadataService, globalRoots);
                            bool plexHasPoster = prefix != "posters" || (collection.Thumb != null && !collection.Thumb.Contains("/composite/", StringComparison.OrdinalIgnoreCase));
                            RecordUploadOutcome(
                                await TryUploadCollectionImageAsync(
                                        target,
                                        collectionId,
                                        collection.Title,
                                        posterPath,
                                        prefix,
                                        $"custom {label}",
                                        $"{target.SectionId}|sc{collectionId}|{prefix}",
                                        plexHasPoster,
                                        isMovieTarget,
                                        "Applied smart collection",
                                        cache,
                                        uploadedDetails,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            );
                        }
            }

            var deletedList = applyAssignment ? await plexCollections.DeleteEmptyCollectionsAsync(cancellationToken).ConfigureAwait(false) : [];
            if (deletedList.Count > 0)
                foreach (var deletion in deletedList)
                foreach (var cacheKey in cache.Keys.Where(key => key.Contains($"|{deletion.RatingKey}|") || key.Contains($"|sc{deletion.RatingKey}|")).ToList())
                {
                    cache.Remove(cacheKey);
                    cacheModified = true;
                }

            if (cacheModified)
                try
                {
                    VfsShared.WriteLinesAtomic(cachePath, cache.Select(pair => $"{pair.Key}|{pair.Value}"));
                }
                catch { }

            stopwatch.Stop();
            logger.LogInformation("CollectionService: Task finished -> {Count} collections assigned in {Elapsed}ms", created, stopwatch.ElapsedMilliseconds);
            return new BuildCollectionsResult(
                uniqueSeries.Count,
                created,
                uploaded,
                alreadyUploaded,
                uniqueSeries.Count - created,
                errors,
                deletedList.Count,
                createdList,
                uploadedDetails,
                deletedList,
                errorsList,
                stopwatch.Elapsed
            );
        }
        finally
        {
            _buildLock.Release();
            if (startedTask)
                TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Result state of a single collection image upload evaluation.</summary>
    private enum ImageUploadOutcome
    {
        /// <summary>No local image was available or the upload failed.</summary>
        None,

        /// <summary>An identical cached image was already present on the collection.</summary>
        Skipped,

        /// <summary>The image was uploaded to Plex.</summary>
        Uploaded,
    }

    /// <summary>Loads previously uploaded collection artwork timestamps from the cache file.</summary>
    /// <param name="cachePath">Path to the cache file.</param>
    /// <returns>A dictionary keyed by <c>{sectionId}|{collectionId}|{prefix}</c>.</returns>
    private static Dictionary<string, string> LoadUploadCache(string cachePath)
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(cachePath))
            return cache;

        try
        {
            foreach (var line in VfsShared.ReadLinesShared(cachePath))
            {
                var parts = line.Split('|', 4);
                if (parts.Length == 4)
                    cache[$"{parts[0]}|{parts[1]}|{parts[2]}"] = parts[3];
            }
        }
        catch { }

        return cache;
    }

    /// <summary>Removes cache entries belonging to Plex library sections no longer configured.</summary>
    /// <param name="cache">The upload cache dictionary.</param>
    /// <param name="targets">Active Plex library targets.</param>
    /// <returns><c>true</c> if any entries were removed; otherwise, <c>false</c>.</returns>
    private static bool PruneStaleSectionCacheKeys(Dictionary<string, string> cache, IReadOnlyList<PlexLibraryTarget> targets)
    {
        var validSectionIds = new HashSet<string>(targets.Select(target => target.SectionId.ToString()));
        var staleKeys = cache.Keys.Where(cacheKey => cacheKey.IndexOf('|') is int index && index > 0 && !validSectionIds.Contains(cacheKey[..index])).ToList();
        foreach (var key in staleKeys)
            cache.Remove(key);
        return staleKeys.Count > 0;
    }

    /// <summary>Removes cache entries for collections that no longer exist in the target Plex section.</summary>
    /// <param name="cache">The upload cache dictionary.</param>
    /// <param name="sectionId">The Plex section identifier.</param>
    /// <param name="collectionRatingKeyMap">Active collections in the section keyed by rating key.</param>
    /// <returns><c>true</c> if any entries were removed; otherwise, <c>false</c>.</returns>
    private static bool PruneStaleCollectionCacheKeys(Dictionary<string, string> cache, int sectionId, Dictionary<string, PlexMetadataItem> collectionRatingKeyMap)
    {
        string targetPrefix = $"{sectionId}|";
        var staleKeys = cache
            .Keys.Where(cacheKey =>
                cacheKey.StartsWith(targetPrefix, StringComparison.Ordinal)
                && cacheKey.Split('|') is { Length: 3 } parts
                && !collectionRatingKeyMap.ContainsKey(parts[1].StartsWith("sc", StringComparison.OrdinalIgnoreCase) ? parts[1][2..] : parts[1])
            )
            .ToList();

        foreach (var key in staleKeys)
            cache.Remove(key);
        return staleKeys.Count > 0;
    }

    /// <summary>Prunes stale collection artwork files inside Plex's local metadata directory, keeping only the newest upload per type.</summary>
    /// <param name="targets">Configured Plex library targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the pruning operation.</returns>
    private async Task PrunePlexCollectionImagesAsync(IReadOnlyList<PlexLibraryTarget> targets, CancellationToken cancellationToken)
    {
        logger.LogInformation("CollectionService: Scanning Plex data directory for old collection images to prune...");
        int deletedCount = 0;
        var subFolders = new[] { "posters", "art", "clearLogos", "squareArt" }; // 'Art' / 'squareArt' are NOT plural like the upload endpoints

        foreach (var target in targets)
        {
            var collections = await plexClient.GetSectionCollectionsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var collection in collections)
            {
                var metaDir = collection.GetMetadataDirectory();
                if (string.IsNullOrEmpty(metaDir))
                    continue;

                foreach (var folder in subFolders)
                {
                    string imagesPath = Path.Combine(Settings.Advanced.PlexMetadataPath, metaDir, "Uploads", folder);
                    if (!fileSystem.DirectoryExists(imagesPath))
                        continue;

                    try
                    {
                        var files = fileSystem.GetFileEntries(imagesPath, cancellationToken: cancellationToken).OrderBy(entry => entry.FileStat?.LastWriteTime ?? DateTime.MinValue).ToList();
                        if (files.Count > 1)
                            foreach (var file in files.SkipLast(1))
                                try
                                {
                                    fileSystem.DeleteFile(file.Path);
                                    deletedCount++;
                                }
                                catch { }
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "CollectionService: Failed to prune {Folder} for collection '{Title}'", folder, collection.Title);
                    }
                }
            }
        }
        logger.LogInformation("CollectionService: Finished pruning. Deleted {DeletedCount} stale collection images.", deletedCount);
    }

    /// <summary>Uploads a local collection artwork file to Plex unless an identical cached upload already exists on the collection.</summary>
    /// <param name="target">Target Plex library.</param>
    /// <param name="collectionId">Plex collection rating key.</param>
    /// <param name="collectionName">Collection display title.</param>
    /// <param name="imagePath">Local image path.</param>
    /// <param name="prefix">Plex upload endpoint prefix.</param>
    /// <param name="label">Human-readable artwork label.</param>
    /// <param name="cacheKey">Cache lookup key.</param>
    /// <param name="plexHasPoster">Whether Plex currently reports a non-composite poster.</param>
    /// <param name="isMovieTarget">Whether the target library is a movie library.</param>
    /// <param name="logContext">Log prefix for uploaded details.</param>
    /// <param name="cache">Mutable upload cache dictionary.</param>
    /// <param name="uploadedDetails">Target list for recording uploaded artwork details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="ImageUploadOutcome"/> indicating whether the image was uploaded, skipped, or absent.</returns>
    private async Task<ImageUploadOutcome> TryUploadCollectionImageAsync(
        PlexLibraryTarget target,
        int collectionId,
        string collectionName,
        string? imagePath,
        string prefix,
        string label,
        string cacheKey,
        bool plexHasPoster,
        bool isMovieTarget,
        string logContext,
        Dictionary<string, string> cache,
        List<CollectionUploadDetail> uploadedDetails,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(imagePath) || !fileSystem.FileExists(imagePath))
            return ImageUploadOutcome.None;

        long ticks = 0;
        try
        {
            ticks = fileSystem.GetFileStat(imagePath)?.LastWriteTime.Ticks ?? 0;
        }
        catch { }

        string cacheValue = $"{imagePath}?t={ticks}";
        if (plexHasPoster && cache.TryGetValue(cacheKey, out var lastValue) && string.Equals(lastValue, cacheValue, StringComparison.Ordinal))
            return ImageUploadOutcome.Skipped;

        if (!await plexCollections.UploadCollectionImageAsync(collectionId, imagePath, prefix, target, cancellationToken).ConfigureAwait(false))
            return ImageUploadOutcome.None;

        cache[cacheKey] = cacheValue;
        uploadedDetails.Add(new CollectionUploadDetail(target.Title, isMovieTarget, label, collectionName, collectionId));
        logger.LogInformation("CollectionService: {LogContext} {Label} for collection -> {Collection}", logContext, label, collectionName);
        return ImageUploadOutcome.Uploaded;
    }

    #endregion
}
