using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image.CrossReferences;
using Shoko.Abstractions.Metadata.Image.Options;
using ShokoRelay.Vfs;

namespace ShokoRelay.Services;

#region Interface & Models

/// <summary>Service responsible for syncing Plex-generated episode thumbnails and local metadata assets (posters, backdrops, logos) back to Shoko.</summary>
public interface IImageSyncService
{
    /// <summary>Scans all configured Plex libraries and local VFS paths to upload missing or updated screenshots, posters, backdrops, and logos back to Shoko.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default);
}

/// <summary>Represents the final result of an image synchronization task.</summary>
/// <param name="Processed">Total number of images evaluated.</param>
/// <param name="Uploaded">Total number of images successfully uploaded to Shoko.</param>
/// <param name="Skipped">Total number of images already set and skipped from re-uploading.</param>
/// <param name="Errors">Count of errors encountered during connection, upload, or missing thumbnails.</param>
/// <param name="UploadedDetails">List of specific images that were uploaded.</param>
/// <param name="ErrorsList">List of specific error messages and items failing to provide thumbnails.</param>
/// <param name="TotalElapsed">The total time elapsed during the task.</param>
public sealed record ImageSyncResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> UploadedDetails, List<string> ErrorsList, TimeSpan TotalElapsed);

#endregion

/// <summary>Default implementation of <see cref="IImageSyncService"/>.</summary>
public class ImageSyncService(PlexClient plexClient, IMetadataService metadataService, IImageManager imageManager, ConfigProvider configProvider) : IImageSyncService
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private string CacheFilePath => Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FilePlexImagesCache);

    #endregion

    #region Public API

    /// <inheritdoc/>
    public async Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targets = plexClient.GetConfiguredTargets();
            var allowedPrimaryIds = allowedSeriesIds?.Select(id => OverrideHelper.GetPrimary(id, metadataService)).Distinct().ToList();
            var allSeries =
                allowedPrimaryIds != null ? [.. allowedPrimaryIds.Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>()] : metadataService.GetAllShokoSeries()?.Cast<IShokoSeries>().ToList() ?? [];
            HashSet<int>? allowedSet = allowedPrimaryIds != null ? [.. allowedPrimaryIds] : null;

            var syncDetails = Settings.TmdbThumbnails ? "" : " + Plex episode thumbnails";
            s_logger.Info("ImageSyncService: Starting image synchronization (local collection/series artwork{0})...", syncDetails);

            // Load the local image synchronization cache from disk into a thread-safe concurrent dictionary
            var cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(CacheFilePath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(CacheFilePath))
                    {
                        var parts = line.Split('|', 2);
                        if (parts.Length == 2)
                            cache[parts[0]] = parts[1];
                    }
                }
                catch { }
            }

            var errsBag = new ConcurrentBag<string>();
            var uploadedBag = new ConcurrentBag<string>();
            int p = 0,
                u = 0,
                s = 0,
                e = 0,
                cacheModified = 0;

            void AddStats(bool handled, bool uploaded, bool skipped, bool error, bool cacheUp)
            {
                if (cacheUp)
                    Interlocked.Exchange(ref cacheModified, 1);
                if (handled && (uploaded || skipped || error))
                    Interlocked.Increment(ref p);
                if (uploaded)
                    Interlocked.Increment(ref u);
                else if (skipped)
                    Interlocked.Increment(ref s);
                else if (error)
                    Interlocked.Increment(ref e);
            }

            // Sync Episode Thumbnails (Local & Plex)
            if (targets.Count > 0)
                await SyncEpisodeThumbnailsAsync(targets, allowedSet, cache, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

            // Sync Collection Posters
            await SyncCollectionPostersAsync(allSeries, cache, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

            // Sync Local Series Images (Posters, Backdrops, Logos)
            await SyncLocalSeriesImagesAsync(allSeries, cache, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

            // Persist the current image synchronization cache to disk
            if (cacheModified == 1)
            {
                try
                {
                    File.WriteAllLines(CacheFilePath, cache.Select(kvp => $"{kvp.Key}|{kvp.Value}"));
                }
                catch { }
            }

            sw.Stop();
            s_logger.Info("ImageSyncService: Finished synchronization -> uploaded {0} new images to Shoko in {1}ms", u, sw.ElapsedMilliseconds);
            return new ImageSyncResult(p, u, s, e, [.. uploadedBag.OrderBy(x => x)], [.. errsBag.OrderBy(x => x)], sw.Elapsed);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region Modular Sync Loops

    /// <summary>Scans Plex sections to locate, upload, and prefer episode or movie thumbnails.</summary>
    /// <param name="targets">Configured Plex library targets.</param>
    /// <param name="allowedSet">Optional filtered series IDs.</param>
    /// <param name="cache">Cache dictionary for image synchronization state.</param>
    /// <param name="errsBag">Bag to collect error messages and missing thumbnail diagnostics.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncEpisodeThumbnailsAsync(
        IReadOnlyList<PlexLibraryTarget> targets,
        HashSet<int>? allowedSet,
        ConcurrentDictionary<string, string> cache,
        ConcurrentBag<string> errsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool, bool> addStats,
        CancellationToken ct
    )
    {
        var processedInRun = new HashSet<int>();
        var orderedTargets = targets.OrderBy(t => t.LibraryType == PlexLibraryType.Movie ? 1 : 0).ToList();

        foreach (var target in orderedTargets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                async Task ProcessThumbnailItem(PlexMetadataItem item)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(item.Guid) || string.IsNullOrWhiteSpace(item.Thumb))
                        return;

                    var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);

                    // Ensure each Shoko Episode (including specials and movies) is only processed once globally per run
                    // Ordering TV targets first ensures Movie libraries skip episodes/specials already synced from TV libraries
                    if (!epId.HasValue || !processedInRun.Add(epId.Value))
                        return;

                    var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                    if (episode == null || (allowedSet != null && !allowedSet.Contains(OverrideHelper.GetPrimary(episode.SeriesID, metadataService))))
                        return;

                    var prefId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                    var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                    bool isMovie = target.LibraryType == PlexLibraryType.Movie;
                    string labelType = isMovie ? "Movie" : "Episode";
                    var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - {(isMovie ? $"Movie [{episode.ID}]" : $"S{coords.Season:D2}E{coords.Episode:D2}")}";

                    // Coordinate Alignment Guard: Detect when Plex's metadata is in a transient or mismatched state
                    if (!isMovie && item.ParentIndex.HasValue && item.Index.HasValue)
                    {
                        bool isMismatch = item.ParentIndex.Value != coords.Season || item.Index.Value != coords.Episode;

                        // Compensate for "Other" type episode fallback logic where Season -4 maps to Season 1 or 0
                        if (
                            isMismatch
                            && coords.Season == PlexConstants.SeasonOther
                            && (item.ParentIndex.Value == PlexConstants.SeasonStandard || item.ParentIndex.Value == PlexConstants.SeasonSpecials)
                            && item.Index.Value == coords.Episode
                        )
                            isMismatch = false;

                        if (isMismatch)
                        {
                            addStats(true, false, false, true, false);
                            errsBag.Add($"[Coordinate Mismatch] {epLogName} (Plex: S{item.ParentIndex.Value:D2}E{item.Index.Value:D2}, Shoko: S{coords.Season:D2}E{coords.Episode:D2})");
                            return;
                        }
                    }

                    // Strict Missing Thumbnail Reporting: Track every Plex item that fails to provide a generated thumbnail
                    if (string.IsNullOrWhiteSpace(item.Thumb))
                    {
                        addStats(true, false, false, true, false);
                        errsBag.Add($"[Missing Plex Thumbnail] {epLogName} (No thumbnail generated or available in Plex)");
                        if (cache.TryRemove(episode.ID.ToString(), out _))
                        {
                            await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == DataSource.LocallyGenerated).ConfigureAwait(false);
                            addStats(false, false, false, false, true);
                        }
                        return;
                    }

                    // Find a local episode thumbnail alongside the physical video files
                    var localThumb = (episode.Videos ?? [])
                        .SelectMany(v => v.Files ?? [])
                        .Select(f => f.Path)
                        .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(Path.GetDirectoryName(p)))
                        .Select(p => (Dir: Path.GetDirectoryName(p)!, Base: Path.GetFileNameWithoutExtension(p)))
                        .SelectMany(x =>
                            Directory
                                .EnumerateFiles(x.Dir, $"{x.Base}.*")
                                .Where(f =>
                                    string.Equals(Path.GetFileNameWithoutExtension(f), x.Base, StringComparison.OrdinalIgnoreCase) && PlexConstants.LocalMediaAssets.Artwork.ContainsKey(Path.GetExtension(f))
                                )
                        )
                        .FirstOrDefault();

                    var (h, u, s, e, cu) = await ProcessLocalAssetAsync(
                            localThumb,
                            episode,
                            ImageEntityType.Backdrop,
                            episode.ID.ToString(),
                            "local thumbnail",
                            epLogName,
                            false,
                            $"[Local {labelType} Thumb] {epLogName}",
                            cache,
                            errsBag
                        )
                        .ConfigureAwait(false);
                    addStats(h, u, s, e, cu);

                    if (!h && !Settings.TmdbThumbnails)
                    {
                        var (ph, pu, ps, pe, pcu) = await ProcessPlexThumbnailAsync(item.Thumb, episode, coords, isMovie, epLogName, target, cache, errsBag, uploadedBag, ct).ConfigureAwait(false);
                        addStats(ph, pu, ps, pe, pcu);
                    }
                }

                if (allowedSet != null)
                {
                    // Targeted fast-path for filtered series
                    foreach (var seriesId in allowedSet)
                    {
                        var ratingKeys = await plexClient.FindRatingKeysForShokoSeriesInSectionAsync(seriesId, target, metadataService, ct).ConfigureAwait(false);
                        foreach (var ratingKey in ratingKeys)
                        {
                            string path =
                                target.LibraryType == PlexLibraryType.Movie
                                    ? $"/library/metadata/{ratingKey}?X-Plex-Container-Start=0&X-Plex-Container-Size=1"
                                    : $"/library/metadata/{ratingKey}/allLeaves?X-Plex-Container-Start=0&X-Plex-Container-Size=5000";
                            using var req = plexClient.CreateRequest(HttpMethod.Get, path, target.ServerUrl);
                            using var resp = await plexClient.SendAsync(req, ct).ConfigureAwait(false);
                            foreach (var item in (await PlexApi.ReadContainerAsync(resp, ct).ConfigureAwait(false))?.Metadata ?? [])
                                await ProcessThumbnailItem(item).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    // Bulk path: query all items in library section
                    var items =
                        target.LibraryType == PlexLibraryType.Movie
                            ? await plexClient.GetSectionMoviesAsync(target, null, ct).ConfigureAwait(false) ?? []
                            : await plexClient.GetSectionEpisodesAsync(target, null, ct).ConfigureAwait(false) ?? [];

                    foreach (var item in items)
                        await ProcessThumbnailItem(item).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                addStats(true, false, false, true, false);
                errsBag.Add($"Failed to scan Plex section {target.SectionId}: {ex.Message}");
                s_logger.Warn(ex, "ImageSyncService: Failed to scan library section {0}", target.SectionId);
            }
        }

        // Safely purge orphaned Plex thumbnails for episodes completely removed from Plex
        if (allowedSet == null && errsBag.IsEmpty)
        {
            var epCacheKeys = cache.Keys.Where(k => int.TryParse(k, out _)).ToList();
            foreach (var key in epCacheKeys)
            {
                int epId = int.Parse(key);
                if (processedInRun.Contains(epId))
                    continue;

                // Local assets store string keys starting with their file length. Exclude them to prevent false purges.
                bool isLocalCache = cache.TryGetValue(key, out string? val) && !string.IsNullOrEmpty(val) && char.IsAsciiDigit(val[0]);
                if (isLocalCache)
                    continue;

                var episode = metadataService.GetShokoEpisodeByID(epId);
                if (episode == null)
                {
                    if (cache.TryRemove(key, out _))
                        addStats(false, false, false, false, true);
                    continue;
                }

                if (cache.TryRemove(key, out _))
                {
                    var prefId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                    var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                    var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}] - S{coords.Season:D2}E{coords.Episode:D2}";

                    s_logger.Info("ImageSyncService: Episode thumbnail for -> {0} is no longer present in Plex ... Purging from Shoko", epLogName);
                    await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == DataSource.LocallyGenerated).ConfigureAwait(false);
                    addStats(false, false, false, false, true);
                }
            }
        }
    }

    /// <summary>Scans local collection posters to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="cache">Cache dictionary for image synchronization state.</param>
    /// <param name="errsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncCollectionPostersAsync(
        List<IShokoSeries> allSeries,
        ConcurrentDictionary<string, string> cache,
        ConcurrentBag<string> errsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool, bool> addStats,
        CancellationToken ct
    )
    {
        var groups = allSeries.Where(s => s.TopLevelGroupID > 0).Select(s => s.TopLevelGroup).OfType<IShokoGroup>().DistinctBy(g => g.ID).ToList();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var seriesInGroup = allSeries.FirstOrDefault(s => s.TopLevelGroupID == group.ID);
            if (seriesInGroup == null)
                continue;

            string? groupPosterFile = PlexHelper.FindCollectionImagePathByGroup(seriesInGroup, group.ID, "", metadataService);
            var (h, u, s, e, cu) = await ProcessLocalAssetAsync(
                    groupPosterFile,
                    group,
                    ImageEntityType.Primary,
                    "c" + group.ID,
                    "collection poster",
                    $"group {group.PreferredTitle?.Value} [{group.ID}]",
                    true,
                    $"[Collection Poster] {group.PreferredTitle?.Value}",
                    cache,
                    errsBag
                )
                .ConfigureAwait(false);

            if (h && u)
                uploadedBag.Add($"[Collection Poster] {group.PreferredTitle?.Value}");
            addStats(h, u, s, e, cu);
        }
    }

    /// <summary>Scans local series artwork (posters, backdrops, logos) to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="cache">Cache dictionary for image synchronization state.</param>
    /// <param name="errsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncLocalSeriesImagesAsync(
        List<IShokoSeries> allSeries,
        ConcurrentDictionary<string, string> cache,
        ConcurrentBag<string> errsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool, bool> addStats,
        CancellationToken ct
    )
    {
        (string[] Names, string Prefix, ImageEntityType Type, string Label)[] configs =
        [
            (["poster", "folder", "show"], "s", ImageEntityType.Primary, "poster"),
            (["art", "backdrop", "background", "fanart"], "b", ImageEntityType.Backdrop, "backdrop"),
            (["clearlogo", "logo"], "l", ImageEntityType.Logo, "logo"),
        ];

        await Parallel
            .ForEachAsync(
                allSeries,
                DefaultParallelOptions(ct),
                async (series, token) =>
                {
                    foreach (var config in configs)
                    {
                        var cacheKey = config.Prefix + series.ID;
                        if (OverrideHelper.GetPrimary(series.ID, metadataService) != series.ID)
                        {
                            if (cache.TryRemove(cacheKey, out _))
                            {
                                await PurgeEntityImagesAsync(series, config.Type, x => x.Source == DataSource.User && x.IsPreferred).ConfigureAwait(false);
                                addStats(false, false, false, false, true);
                            }
                            continue;
                        }

                        // Find a local artwork file for a series based on a prioritized list of allowed filenames
                        string? foundFile = VfsShared
                            .ResolveSeriesVfsPaths(series, metadataService)
                            .Where(Directory.Exists)
                            .SelectMany(Directory.EnumerateFiles)
                            .Where(f => PlexConstants.LocalMediaAssets.Artwork.ContainsKey(Path.GetExtension(f)))
                            .Select(f => new { File = f, Index = Array.FindIndex(config.Names, n => string.Equals(Path.GetFileNameWithoutExtension(f), n, StringComparison.OrdinalIgnoreCase)) })
                            .Where(x => x.Index >= 0)
                            .OrderBy(x => x.Index)
                            .FirstOrDefault()
                            ?.File;

                        var (h, u, s, e, cu) = await ProcessLocalAssetAsync(
                                foundFile,
                                series,
                                config.Type,
                                cacheKey,
                                config.Label,
                                $"series {series.GetDisplayTitle()} [{series.ID}]",
                                true,
                                $"[Local {config.Label}] {series.GetDisplayTitle()}",
                                cache,
                                errsBag
                            )
                            .ConfigureAwait(false);

                        if (h && u)
                            uploadedBag.Add($"[Local {config.Label}] {series.GetDisplayTitle()}");
                        addStats(h, u, s, e, cu);
                    }
                }
            )
            .ConfigureAwait(false);
    }

    #endregion

    #region Core Processing Logic

    /// <summary>Universal method for caching, purging, and uploading local image assets.</summary>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error, bool CacheUpdated)> ProcessLocalAssetAsync(
        string? foundFile,
        IWithImages entity,
        ImageEntityType imageType,
        string cacheKey,
        string label,
        string entityName,
        bool userSubmitted,
        string? uploadDetail,
        ConcurrentDictionary<string, string> cache,
        ConcurrentBag<string> errorsBag
    )
    {
        // Resolve a file's physical target (bypassing symlinks) and retrieve its physical length
        bool exists = false;
        long length = 0;
        if (!string.IsNullOrEmpty(foundFile))
        {
            try
            {
                var fi = new FileInfo(foundFile);
                fi = fi.LinkTarget != null ? (fi.ResolveLinkTarget(true) as FileInfo ?? fi) : fi;
                if (exists = fi.Exists)
                    length = fi.Length;
            }
            catch { }
        }

        var preferredImg = entity.GetAvailableImages(imageType).FirstOrDefault(i => i.IsPreferred);

        if (!exists)
        {
            bool hadCache = cache.TryGetValue(cacheKey, out string? cachedVal);
            bool isLocalCache = hadCache && !string.IsNullOrEmpty(cachedVal) && char.IsAsciiDigit(cachedVal[0]);

            if (hadCache && isLocalCache)
            {
                cache.TryRemove(cacheKey, out _);
                s_logger.Info("ImageSyncService: Local {0} for -> {1} no longer present on disk ... Purging from Shoko", label, entityName);
                await PurgeEntityImagesAsync(entity, imageType, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
                return (true, false, false, false, true);
            }
            return (false, false, false, false, false);
        }

        string? cacheVal = cache.GetValueOrDefault(cacheKey);

        // Evaluate whether a local image matches the active preferred image in Shoko to safely skip re-uploading
        string? md5 = null;
        if (cacheVal != null && cacheVal.StartsWith(length.ToString() + "|"))
        {
            var parts = cacheVal.Split('|');
            if (parts.Length == 2)
            {
                md5 = parts[1];
                if (preferredImg != null && string.Equals(preferredImg.ResourceID, md5, StringComparison.OrdinalIgnoreCase))
                    return (true, false, true, false, false); // Skip upload
            }
        }

        if (md5 == null)
        {
            using var fs = new FileStream(foundFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            md5 = Convert.ToHexString(MD5.HashData(fs));
        }

        string newCacheVal = $"{length}|{md5}";
        if (preferredImg != null && string.Equals(preferredImg.ResourceID, md5, StringComparison.OrdinalIgnoreCase))
        {
            if (cacheVal == newCacheVal)
                return (true, false, true, false, false); // Skip

            cache[cacheKey] = newCacheVal;
            return (true, false, true, false, true);
        }

        if (cacheVal == null)
            s_logger.Debug("ImageSyncService: New local {0} found for -> {1} ... Uploading", label, entityName);
        else
            s_logger.Debug("ImageSyncService: Local {0} changed for -> {1} ... Purging stale image and uploading", label, entityName);

        await PurgeEntityImagesAsync(entity, imageType, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
        s_logger.Trace("ImageSyncService: Uploading local {0} for -> {1}", label, entityName);

        try
        {
            // Upload a local file from disk to Shoko and mark it as preferred for the specified entity
            using var stream = new FileStream(foundFile!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = ImageHelper.GetMimeType(Path.GetExtension(foundFile!)) ?? "image/jpeg";
            var uploadedImage = imageManager.UploadImage(stream, contentType, userSubmitted: userSubmitted);
            imageManager.SetPreferredImageForEntity(entity, imageType, uploadedImage);

            cache[cacheKey] = newCacheVal;
            if (uploadDetail != null)
                s_logger.Info("ImageSyncService: Successfully uploaded and preferred {0} for -> {1}", label, entityName);
            return (true, true, false, false, true);
        }
        catch (Exception ex)
        {
            errorsBag.Add($"Failed to process {label} for -> {entityName}: {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to upload {0} for -> {1}", label, entityName);
            return (true, false, false, true, false);
        }
    }

    /// <summary>Downloads and processes Plex-generated thumbnails with self-healing compound caching.</summary>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error, bool CacheUpdated)> ProcessPlexThumbnailAsync(
        string thumbUrl,
        IShokoEpisode episode,
        PlexMapping.PlexCoords coords,
        bool isMovie,
        string epLogName,
        PlexLibraryTarget target,
        ConcurrentDictionary<string, string> cache,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        CancellationToken ct
    )
    {
        var cacheKey = episode.ID.ToString();
        var preferredBackdrop = episode.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault(i => i.IsPreferred);

        // Strict User Preference Protection: Non-locally-generated preferred images (User, TMDB, AniDB) must never be overwritten
        if (preferredBackdrop != null && preferredBackdrop.Source != DataSource.LocallyGenerated)
            return (true, false, true, false, false);

        string? cacheVal = cache.GetValueOrDefault(cacheKey);
        string coordsToken = isMovie ? $"M{episode.ID}" : $"S{coords.Season:D2}E{coords.Episode:D2}";

        bool isStale = false;
        if (cacheVal != null)
        {
            var parts = cacheVal.Split('|', 3);
            // Support legacy 2-part cache (url|md5) and 3-part cache (coord|url|md5)
            string savedCoords = parts.Length == 3 ? parts[0] : "";
            string savedThumb = parts.Length == 3 ? parts[1] : parts[0];
            string? savedMd5 = parts.Length == 3 ? parts[2] : (parts.Length > 1 ? parts[1] : null);

            // Self-Healing Validation: Verify matching coordinates, matching Plex thumb URL, and actual database attachment in Shoko
            bool coordsMatch = string.IsNullOrEmpty(savedCoords) || string.Equals(savedCoords, coordsToken, StringComparison.OrdinalIgnoreCase);
            bool thumbMatch = string.Equals(savedThumb, thumbUrl, StringComparison.OrdinalIgnoreCase);
            bool shokoHasImage = preferredBackdrop != null && (savedMd5 == null || string.Equals(preferredBackdrop.ResourceID, savedMd5, StringComparison.OrdinalIgnoreCase));

            if (coordsMatch && thumbMatch && shokoHasImage)
            {
                if (string.IsNullOrEmpty(savedCoords) && savedMd5 != null)
                {
                    // Cache Migration: Rewrite old (url|md5) entries to new (coord|url|md5) format without re-downloading
                    cache[cacheKey] = $"{coordsToken}|{thumbUrl}|{savedMd5}";
                    return (true, false, true, false, true);
                }
                return (true, false, true, false, false); // State verified, skip
            }

            isStale = true;
        }

        s_logger.Trace("ImageSyncService: Fetching Plex thumbnail for episode -> {0}", epLogName);
        try
        {
            using var req = plexClient.CreateRequest(HttpMethod.Get, thumbUrl, target.ServerUrl);
            using var resp = await plexClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                errorsBag.Add($"[Failed Plex Download] {epLogName} (HTTP {resp.StatusCode})");
                return (true, false, false, true, false);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var md5Hex = Convert.ToHexString(MD5.HashData(bytes));

            // Safely unlink stale local image cross-references before uploading replacement artwork
            if (isStale && preferredBackdrop != null && preferredBackdrop.Source == DataSource.LocallyGenerated)
                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);

            // Stream through imageManager.UploadImage to guarantee cross-reference creation for both new and existing images
            using var stream = new MemoryStream(bytes);
            var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);
            imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, uploadedImage);

            uploadedBag.Add($"[Plex Thumb] {epLogName}");
            cache[cacheKey] = $"{coordsToken}|{thumbUrl}|{md5Hex}";
            s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode -> {0}", epLogName);
            return (true, true, false, false, true);
        }
        catch (Exception ex)
        {
            errorsBag.Add($"[Plex Thumbnail Exception] {epLogName}: {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to process Plex thumbnail for {0}", epLogName);
            return (true, false, false, true, false);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Purges stale or demoted cross-referenced images for an entity based on source filters.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="predicate">Filter predicate to select cross-references for purging.</param>
    /// <returns>A task representing the asynchronous purge operation.</returns>
    private async Task PurgeEntityImagesAsync(IWithImages entity, ImageEntityType imageType, Func<IImageCrossReference, bool> predicate)
    {
        try
        {
            foreach (var xref in entity.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = imageType }).Where(predicate))
            {
                imageManager.RemoveImageCrossReference(xref);
                // Only purge the underlying image if no other entities are actively referencing it
                if (
                    imageManager.GetImageByID(xref.ImageID) is { } oldImg
                    && !imageManager.GetAllImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = imageType }).Any(x => x.ImageID == oldImg.ID && x.ID != xref.ID)
                )
                    await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "ImageSyncService: Failed to purge stale images for entity of type {0}", entity.GetType().Name);
        }
    }

    #endregion
}
