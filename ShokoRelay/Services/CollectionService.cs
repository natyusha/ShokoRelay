using System.Diagnostics;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Services;

#region Interface and Models

/// <summary>Service responsible for building and managing Plex collections based on Shoko series metadata.</summary>
public interface ICollectionService
{
    /// <summary>Create or update Plex collections and their images for the supplied series list.</summary>
    /// <param name="seriesList">The collection of series to process.</param>
    /// <param name="applyAssignment">If true, perform metadata assignment; otherwise, only refresh collection image assets.</param>
    /// <param name="clean">If true, prunes old cached custom posters from Plex's local metadata directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result object containing statistics on the operation.</returns>
    Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, bool applyAssignment = true, bool clean = true, CancellationToken cancellationToken = default);
}

/// <summary>Result returned by <see cref="ICollectionService.BuildCollectionsAsync"/>.</summary>
/// <param name="Processed">Number of series processed.</param>
/// <param name="Created">Number of collections successfully assigned.</param>
/// <param name="Uploaded">Number of posters uploaded.</param>
/// <param name="AlreadyUploaded">Number of collection images already set and skipped from re-uploading.</param>
/// <param name="SeasonPostersUploaded">Number of season-specific posters uploaded.</param>
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
    int SeasonPostersUploaded,
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

/// <summary>Default implementation of <see cref="ICollectionService"/>.</summary>
public class CollectionService(PlexClient plexClient, PlexCollections plexCollections, IMetadataService metadataService, PlexMetadata mapper, IVideoService videoService, ConfigProvider configProvider)
    : ICollectionService
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion


    #region Collection Building

    /// <inheritdoc/>
    public async Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, bool applyAssignment = true, bool clean = true, CancellationToken cancellationToken = default)
    {
        const string TaskName = ShokoRelayConstants.TaskPlexCollectionsBuild;
        TaskHelper.StartTask(TaskName);
        s_logger.Info("CollectionService: Starting task...");
        var sw = Stopwatch.StartNew();

        try
        {
            var (created, uploaded, alreadyUploaded, errs, uniqueSeries) = (0, 0, 0, 0, new HashSet<int>());
            var (createdList, uploadedDetails, errorsList) = (new List<CollectionAssignmentDetail>(), new List<CollectionUploadDetail>(), new List<string>());
            var allowedIds = new HashSet<int>(seriesList?.Where(s => s != null).Select(s => OverrideHelper.GetPrimary(s!.ID, metadataService)) ?? []);
            var targets = plexClient.GetConfiguredTargets();

            if (targets.Count == 0)
                return new BuildCollectionsResult(0, 0, 0, 0, 0, 0, 0, 0, createdList, uploadedDetails, [], errorsList, sw.Elapsed);

            List<string> globalRoots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

            string cachePath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FilePlexCollectionsCache);
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(cachePath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(cachePath))
                    {
                        var parts = line.Split('|', 4);
                        if (parts.Length == 4)
                            cache[$"{parts[0]}|{parts[1]}|{parts[2]}"] = parts[3];
                    }
                }
                catch { }
            }
            bool cacheModified = false;

            // Prune cache entries belonging to library sections that are no longer configured or discovered in Plex
            var validSectionIds = new HashSet<string>(targets.Select(t => t.SectionId.ToString()));
            var staleSectionKeys = cache.Keys.Where(k => k.IndexOf('|') is int idx && idx > 0 && !validSectionIds.Contains(k[..idx])).ToList();
            if (staleSectionKeys.Count > 0)
            {
                foreach (var k in staleSectionKeys)
                    cache.Remove(k);
                cacheModified = true;
            }

            // Execute pre-cleanup pruning of old posters, arts, logos, and square images if configured and enabled
            if (clean && !string.IsNullOrWhiteSpace(Settings.Advanced.PlexMetadataPath) && Directory.Exists(Settings.Advanced.PlexMetadataPath))
            {
                s_logger.Info("CollectionService: Scanning Plex data directory for old collection images to prune...");
                int deletedCount = 0;
                var subFolders = new[] { "posters", "art", "clearLogos", "squareArt" }; // 'Art' / 'squareArt' are NOT plural like the upload endpoints

                foreach (var target in targets)
                {
                    var collections = await plexClient.GetSectionCollectionsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
                    foreach (var col in collections)
                    {
                        var metaDir = col.GetMetadataDirectory();
                        if (string.IsNullOrEmpty(metaDir))
                            continue;

                        foreach (var folder in subFolders)
                        {
                            string imagesPath = Path.Combine(Settings.Advanced.PlexMetadataPath, metaDir, "Uploads", folder);
                            if (!Directory.Exists(imagesPath))
                                continue;

                            try
                            {
                                var files = new DirectoryInfo(imagesPath).EnumerateFiles().OrderBy(f => f.CreationTimeUtc).ToList();
                                if (files.Count > 1)
                                    foreach (var file in files.SkipLast(1))
                                        try
                                        {
                                            file.Delete();
                                            deletedCount++;
                                        }
                                        catch { }
                            }
                            catch (Exception ex)
                            {
                                s_logger.Warn(ex, "CollectionService: Failed to prune {0} for collection '{1}'", folder, col.Title);
                            }
                        }
                    }
                }
                s_logger.Info("CollectionService: Finished pruning. Deleted {0} stale collection images.", deletedCount);
            }

            foreach (var target in targets)
            {
                bool isMovieTarget = target.LibraryType == PlexLibraryType.Movie;

                var items = new List<PlexMetadataItem>();
                if (allowedIds.Count > 0)
                {
                    // Targeted fast-path for filtered series
                    foreach (var seriesId in allowedIds)
                    {
                        var ratingKeys = await plexClient.FindRatingKeysForShokoSeriesInSectionAsync(seriesId, target, metadataService, cancellationToken).ConfigureAwait(false);
                        foreach (var ratingKey in ratingKeys)
                        {
                            using var req = plexClient.CreateRequest(HttpMethod.Get, $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1", target.ServerUrl);
                            using var resp = await plexClient.HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                            if ((await PlexApi.ReadContainerAsync(resp, cancellationToken).ConfigureAwait(false))?.Metadata?.FirstOrDefault() is { } item)
                                items.Add(item);
                        }
                    }
                }
                else
                {
                    // Bulk path: query all items in library section
                    items = isMovieTarget
                        ? await plexClient.GetSectionMoviesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? []
                        : await plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];
                }

                var collections = await plexClient.GetSectionCollectionsAsync(target, cancellationToken).ConfigureAwait(false) ?? [];

                // Map collection rating keys to metadata items to inspect existing poster state
                var collectionRatingKeyMap = collections.Where(c => !string.IsNullOrEmpty(c.RatingKey)).ToDictionary(c => c.RatingKey!, StringComparer.OrdinalIgnoreCase);

                // Prune cache keys for collections that no longer exist in this target (e.g. library deleted and rebuilt)
                var targetPrefix = $"{target.SectionId}|";
                var staleCollectionKeys = cache
                    .Keys.Where(k =>
                        k.StartsWith(targetPrefix, StringComparison.Ordinal)
                        && k.Split('|') is var parts
                        && parts.Length == 3
                        && !collectionRatingKeyMap.ContainsKey(parts[1].StartsWith("sc", StringComparison.OrdinalIgnoreCase) ? parts[1][2..] : parts[1])
                    )
                    .ToList();

                if (staleCollectionKeys.Count > 0)
                {
                    foreach (var k in staleCollectionKeys)
                        cache.Remove(k);
                    cacheModified = true;
                }

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

                    int? sid = null;
                    if (isMovieTarget)
                    {
                        var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                        if (epId.HasValue)
                            sid = metadataService.GetShokoEpisodeByID(epId.Value)?.SeriesID;
                    }
                    else
                        sid = PlexHelper.ExtractShokoSeriesIdFromGuid(item.Guid);

                    if (!sid.HasValue || (allowedIds.Count > 0 && !allowedIds.Contains(sid.Value)))
                        continue;

                    uniqueSeries.Add(sid.Value);
                    var series = metadataService.GetShokoSeriesByID(sid.Value);
                    var collectionName = series != null ? mapper.GetCollectionName(series) : null;

                    s_logger.Trace("CollectionService: Processing series -> {0} [{1}] (RatingKey: {2})", series?.GetDisplayTitle() ?? "Unknown", sid.Value, item.RatingKey);

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
                                    s_logger.Info("CollectionService: Removed incorrect collection '{0}' from -> {1} [{2}]", staleName, series?.GetDisplayTitle() ?? item.Title, sid.Value);
                            }
                        }

                        if (!string.IsNullOrEmpty(collectionName) && !alreadyHasCorrect)
                        {
                            var assignmentOk = await plexCollections.AssignCollectionToItemByMetadataAsync(plexKey, collectionName, target, cancellationToken).ConfigureAwait(false);
                            if (assignmentOk)
                            {
                                created++;
                                s_logger.Info("CollectionService: Assigned '{0}' to -> {1} [{2}]", collectionName, series?.GetDisplayTitle() ?? item.Title, sid.Value);
                                createdList.Add(new CollectionAssignmentDetail(target.Title, target.SectionId, collectionName, sid.Value, plexKey, isMovieTarget));
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
                            if (posted.Add((cid, "metadata")))
                            {
                                var desc = TextHelper.GetDescriptionByLanguage(series!, Settings.DescriptionLanguage);
                                var tmdbDesc = series!.TmdbShows?.FirstOrDefault()?.PreferredDescription?.Value;
                                var summary = TextHelper.SanitizeSummaryWithFallback(desc, tmdbDesc, Settings.SummaryMode);
                                await plexCollections.UpdateCollectionMetadataAsync(cid, collectionName, summary, target, cancellationToken).ConfigureAwait(false);
                            }

                            foreach (var (prefix, suffix, suffixes, label, defaultFallback) in PlexConstants.CollectionImageConfigs)
                            {
                                if (posted.Add((cid, prefix)))
                                {
                                    var fallback = defaultFallback && Settings.CollectionImages;
                                    var url = PlexHelper.GetCollectionImageUrl(series!, collectionName, cid, suffix, suffixes, metadataService, fallback);
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        string cacheVal = NormalizeCacheUrl(url);
                                        string cacheKey = $"{target.SectionId}|{cid}|{prefix}";

                                        // If checking poster, verify that the collection in Plex actually has a custom poster set (not empty or composite)
                                        bool plexHasPoster =
                                            prefix != "posters"
                                            || (
                                                collectionRatingKeyMap.TryGetValue(cid.ToString(), out var pCol) && pCol.Thumb != null && !pCol.Thumb.Contains("/composite/", StringComparison.OrdinalIgnoreCase)
                                            );

                                        if (plexHasPoster && cache.TryGetValue(cacheKey, out var lastVal) && string.Equals(lastVal, cacheVal, StringComparison.Ordinal))
                                        {
                                            alreadyUploaded++;
                                            continue;
                                        }

                                        if (await plexCollections.UploadCollectionImageByUrlAsync(cid, url, prefix, target, cancellationToken).ConfigureAwait(false))
                                        {
                                            cache[cacheKey] = cacheVal;
                                            cacheModified = true;
                                            uploaded++;
                                            uploadedDetails.Add(new CollectionUploadDetail(target.Title, isMovieTarget, label, collectionName, cid));
                                            s_logger.Debug("CollectionService: Applied {0} for collection -> {1}", label, collectionName);
                                        }
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
                        foreach (var (prefix, suffix, suffixes, label, _) in PlexConstants.CollectionImageConfigs)
                        {
                            var posterPath = PlexHelper.FindCollectionImagePath(null, col.Title, cid, suffixes, metadataService, globalRoots);
                            if (!string.IsNullOrEmpty(posterPath) && File.Exists(posterPath))
                            {
                                var url =
                                    $"{ServerBaseUrl}{ShokoRelayConstants.BasePath}/collections/user/sc{cid}?name={Uri.EscapeDataString(col.Title)}&suffix={suffix}&t={new FileInfo(posterPath).LastWriteTimeUtc.Ticks}";

                                string cacheVal = NormalizeCacheUrl(url);
                                string cacheKey = $"{target.SectionId}|sc{cid}|{prefix}";

                                bool plexHasPoster = prefix != "posters" || (col.Thumb != null && !col.Thumb.Contains("/composite/", StringComparison.OrdinalIgnoreCase));

                                if (plexHasPoster && cache.TryGetValue(cacheKey, out var lastVal) && string.Equals(lastVal, cacheVal, StringComparison.Ordinal))
                                {
                                    alreadyUploaded++;
                                    continue;
                                }

                                if (await plexCollections.UploadCollectionImageByUrlAsync(cid, url, prefix, target, cancellationToken).ConfigureAwait(false))
                                {
                                    cache[cacheKey] = cacheVal;
                                    cacheModified = true;
                                    uploaded++;
                                    uploadedDetails.Add(new CollectionUploadDetail(target.Title, isMovieTarget, $"custom {label}", col.Title, cid));
                                    s_logger.Info("CollectionService: Applied custom {0} to smart collection -> {1} (RatingKey: {2})", label, col.Title, cid);
                                }
                            }
                        }
                    }
                }
            }

            var deletedList = new List<CollectionDeletionDetail>();
            if (applyAssignment)
                deletedList = await plexCollections.DeleteEmptyCollectionsAsync(cancellationToken).ConfigureAwait(false);

            if (deletedList.Count > 0)
            {
                foreach (var del in deletedList)
                {
                    var targetKeys = cache.Keys.Where(k => k.Contains($"|{del.RatingKey}|") || k.Contains($"|sc{del.RatingKey}|")).ToList();
                    foreach (var k in targetKeys)
                    {
                        cache.Remove(k);
                        cacheModified = true;
                    }
                }
            }

            if (cacheModified)
            {
                try
                {
                    File.WriteAllLines(cachePath, cache.Select(kvp => $"{kvp.Key}|{kvp.Value}"));
                }
                catch { }
            }

            sw.Stop();
            s_logger.Info("CollectionService: Task finished -> {0} collections assigned in {1}ms", created, sw.ElapsedMilliseconds);
            return new BuildCollectionsResult(
                uniqueSeries.Count,
                created,
                uploaded,
                alreadyUploaded,
                0,
                uniqueSeries.Count - created,
                errs,
                deletedList.Count,
                createdList,
                uploadedDetails,
                deletedList,
                errorsList,
                sw.Elapsed
            );
        }
        finally
        {
            TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Normalizes an artwork URL for caching by stripping the host and port if it targets a local API endpoint.</summary>
    /// <param name="url">The full image URL.</param>
    /// <returns>A normalized relative or external URL string.</returns>
    private static string NormalizeCacheUrl(string url) => url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase) is int idx && idx >= 0 ? url[idx..] : url;

    #endregion
}
