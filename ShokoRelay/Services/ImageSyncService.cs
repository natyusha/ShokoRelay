using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
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
/// <param name="Skipped">Total number of images skipped because they already had primary artwork.</param>
/// <param name="Errors">Count of errors encountered during connection or upload.</param>
/// <param name="UploadedDetails">List of specific images that were uploaded.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
/// <param name="TotalElapsed">The total time elapsed during the task.</param>
public sealed record ImageSyncResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> UploadedDetails, List<string> ErrorsList, TimeSpan TotalElapsed);

#endregion

/// <summary>Default implementation of <see cref="IImageSyncService"/>.</summary>
public class ImageSyncService(PlexClient plexClient, HttpClient httpClient, IMetadataService metadataService, IImageManager imageManager, ConfigProvider configProvider) : IImageSyncService
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
            var allSeries =
                allowedSeriesIds != null
                    ? [.. allowedSeriesIds.Distinct().Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>()]
                    : metadataService.GetAllShokoSeries()?.Cast<IShokoSeries>().ToList() ?? [];
            HashSet<int>? allowedSet = allowedSeriesIds != null ? [.. allowedSeriesIds] : null;

            var syncDetails = Settings.TmdbThumbnails ? "" : " + Plex episode thumbnails";
            s_logger.Info("ImageSyncService: Starting image synchronization (local collection/series artwork{0})...", syncDetails);

            var cache = LoadCache();
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

            if (cacheModified == 1)
                SaveCache(cache);

            sw.Stop();
            s_logger.Info("ImageSyncService: Finished synchronization -> uploaded {0} new images to Shoko in {1}ms", u, sw.ElapsedMilliseconds);
            return new ImageSyncResult(p, u, s, e, [.. uploadedBag], [.. errsBag], sw.Elapsed);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region Modular Sync Loops

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
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var episodes = await plexClient.GetSectionEpisodesAsync(target, null, ct).ConfigureAwait(false) ?? [];
                foreach (var item in episodes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(item.Guid) || string.IsNullOrWhiteSpace(item.Thumb))
                        continue;

                    var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                    if (!epId.HasValue)
                        continue;

                    var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                    if (episode == null || (allowedSet != null && !allowedSet.Contains(episode.SeriesID)))
                        continue;

                    var prefId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                    var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                    var epLogName = $"'{episode.Series?.GetDisplayTitle()}' [{episode.SeriesID}] S{coords.Season:D2}E{coords.Episode:D2}";

                    var localThumb = FindLocalEpisodeThumbnail(episode);
                    var (h, u, s, e, cu) = await ProcessLocalAssetAsync(
                            localThumb,
                            episode,
                            ImageEntityType.Backdrop,
                            episode.ID.ToString(),
                            "local thumbnail",
                            epLogName,
                            false,
                            $"[Local Episode Thumb] {epLogName}",
                            cache,
                            errsBag
                        )
                        .ConfigureAwait(false);
                    addStats(h, u, s, e, cu);

                    if (!h && !Settings.TmdbThumbnails && processedInRun.Add(epId.Value))
                    {
                        var (ph, pu, ps, pe, pcu) = await ProcessPlexThumbnailAsync(item.Thumb, episode, epLogName, target, cache, errsBag, uploadedBag, ct).ConfigureAwait(false);
                        addStats(ph, pu, ps, pe, pcu);
                    }
                }
            }
            catch (Exception ex)
            {
                addStats(true, false, false, true, false);
                errsBag.Add($"Failed to scan Plex section {target.SectionId}: {ex.Message}");
                s_logger.Warn(ex, "ImageSyncService: Failed to scan library section {0}", target.SectionId);
            }
        }
    }

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
                    $"group '{group.PreferredTitle?.Value}' [{group.ID}]",
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
                        if (EnforceTmdbNumbering && OverrideHelper.GetPrimary(series.ID, metadataService) != series.ID)
                        {
                            if (cache.TryRemove(cacheKey, out _))
                            {
                                await PurgeEntityImagesAsync(series, config.Type, x => x.Source == DataSource.User && x.IsPreferred).ConfigureAwait(false);
                                addStats(false, false, false, false, true);
                            }
                            continue;
                        }

                        string? foundFile = FindLocalSeriesArtwork(series, config.Names);
                        var (h, u, s, e, cu) = await ProcessLocalAssetAsync(
                                foundFile,
                                series,
                                config.Type,
                                cacheKey,
                                config.Label,
                                $"series '{series.GetDisplayTitle()}' [{series.ID}]",
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
        var (exists, length) = !string.IsNullOrEmpty(foundFile) ? GetFileMetadata(foundFile) : (false, 0L);
        var preferredImg = entity.GetAvailableImages(imageType).FirstOrDefault(i => i.IsPreferred);

        if (!exists)
        {
            bool hadCache = cache.TryGetValue(cacheKey, out string? cachedVal);
            bool isLocalCache = hadCache && !string.IsNullOrEmpty(cachedVal) && char.IsAsciiDigit(cachedVal[0]);

            if ((hadCache && isLocalCache) || (!hadCache && preferredImg?.Source is DataSource.User or DataSource.LocallyGenerated))
            {
                cache.TryRemove(cacheKey, out _);
                s_logger.Info("ImageSyncService: Local {0} for -> {1} no longer present on disk ... Purging from Shoko", label, entityName);
                await PurgeEntityImagesAsync(entity, imageType, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
                return (true, false, false, false, true);
            }
            return (false, false, false, false, false);
        }

        string? cacheVal = cache.GetValueOrDefault(cacheKey);
        var (skipUpload, newCacheVal) = EvaluateLocalImageCache(cacheVal, length, foundFile!, preferredImg);

        if (skipUpload)
        {
            if (cacheVal == newCacheVal)
                return (true, false, true, false, false);
            cache[cacheKey] = newCacheVal;
            return (true, false, true, false, true);
        }

        if (cacheVal == null)
            s_logger.Debug("ImageSyncService: New local {0} found for -> {1} ... Uploading", label, entityName);
        else
            s_logger.Debug("ImageSyncService: File changed for {0} -> {1} ... Purging stale image and uploading", label, entityName);

        await PurgeEntityImagesAsync(entity, imageType, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
        s_logger.Trace("ImageSyncService: Uploading local {0} for -> {1}", label, entityName);

        try
        {
            UploadAndPreferLocalImage(foundFile!, entity, imageType, userSubmitted);
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

    /// <summary>Downloads and processes Plex-generated thumbnails.</summary>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error, bool CacheUpdated)> ProcessPlexThumbnailAsync(
        string thumbUrl,
        IShokoEpisode episode,
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
        string? cacheVal = cache.GetValueOrDefault(cacheKey);

        bool isStale = false;
        if (cacheVal != null)
        {
            var parts = cacheVal.Split('|', 2);
            string savedThumb = parts[0];
            string? savedMd5 = parts.Length > 1 ? parts[1] : null;

            if (string.Equals(savedThumb, thumbUrl, StringComparison.OrdinalIgnoreCase))
            {
                if (preferredBackdrop != null && (savedMd5 == null || string.Equals(preferredBackdrop.ResourceID, savedMd5, StringComparison.OrdinalIgnoreCase)))
                    return (true, false, true, false, false); // Valid cache, skip
            }
            else
                isStale = true;
        }

        if (preferredBackdrop != null && preferredBackdrop.Source is not DataSource.LocallyGenerated and not DataSource.User)
            return (true, false, true, false, false); // Preferred image is external, skip

        if (isStale || (preferredBackdrop != null && preferredBackdrop.Source is DataSource.LocallyGenerated or DataSource.User))
        {
            s_logger.Debug("ImageSyncService: Plex thumbnail mismatch for -> {0} ... Purging stale thumbnail", epLogName);
            await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
        }

        s_logger.Trace("ImageSyncService: Fetching Plex thumbnail for episode -> {0}", epLogName);
        try
        {
            using var req = plexClient.CreateRequest(HttpMethod.Get, thumbUrl, target.ServerUrl);
            using var resp = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                errorsBag.Add($"Plex download failed for {epLogName} with status {resp.StatusCode}");
                return (true, false, false, true, false);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var md5Hex = Convert.ToHexString(MD5.HashData(bytes));
            var imageId = IImageManager.GetIDForImageSourceAndResourceID(DataSource.LocallyGenerated, md5Hex);

            var existingImage = imageManager.GetImageByID(imageId);
            if (existingImage != null)
            {
                imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, existingImage);
                uploadedBag.Add($"[Linked Existing Plex Thumb] {epLogName}");
                cache[cacheKey] = $"{thumbUrl}|{md5Hex}";
                s_logger.Info("ImageSyncService: Linked existing duplicate thumbnail for episode -> {0}", epLogName);
                return (true, true, false, false, true);
            }

            using var stream = new MemoryStream(bytes);
            var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);
            imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, uploadedImage);

            uploadedBag.Add($"[Plex Thumb] {epLogName}");
            cache[cacheKey] = $"{thumbUrl}|{md5Hex}";
            s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode -> {0}", epLogName);
            return (true, true, false, false, true);
        }
        catch (Exception ex)
        {
            errorsBag.Add($"Failed to process episode matching Plex URL '{thumbUrl}': {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to process Plex thumbnail loop iteration");
            return (true, false, false, true, false);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Evaluates whether a local image matches the active preferred image in Shoko to safely skip re-uploading.</summary>
    /// <param name="cacheVal">The previously cached metadata string.</param>
    /// <param name="length">The physical length of the local file.</param>
    /// <param name="filePath">The path to the local file.</param>
    /// <param name="preferredImage">The currently preferred image in Shoko.</param>
    /// <returns>A tuple indicating whether to skip the upload, and the newly generated cache string.</returns>
    private static (bool SkipUpload, string NewCacheVal) EvaluateLocalImageCache(string? cacheVal, long length, string filePath, IImage? preferredImage)
    {
        string? md5 = null;
        if (cacheVal != null && cacheVal.StartsWith(length.ToString() + "|"))
        {
            var parts = cacheVal.Split('|');
            if (parts.Length == 2)
            {
                md5 = parts[1];
                if (preferredImage != null && string.Equals(preferredImage.ResourceID, md5, StringComparison.OrdinalIgnoreCase))
                    return (true, cacheVal);
            }
        }
        md5 ??= GetFileMD5(filePath);
        string newCacheVal = $"{length}|{md5}";
        bool skip = preferredImage != null && string.Equals(preferredImage.ResourceID, md5, StringComparison.OrdinalIgnoreCase);
        return (skip, newCacheVal);
    }

    /// <summary>Finds a local artwork file for a series based on a prioritized list of allowed filenames.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="allowedNames">Array of valid filenames (without extension).</param>
    /// <returns>The physical file path if found, otherwise null.</returns>
    private string? FindLocalSeriesArtwork(IShokoSeries series, string[] allowedNames)
    {
        foreach (var vfsPath in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
        {
            if (!Directory.Exists(vfsPath))
                continue;
            var localArtworks = Directory.EnumerateFiles(vfsPath).Where(f => PlexConstants.LocalMediaAssets.Artwork.Contains(Path.GetExtension(f))).ToList();
            foreach (var name in allowedNames)
            {
                var found = localArtworks.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    /// <summary>Finds a local episode thumbnail alongside the physical video files.</summary>
    /// <param name="episode">The Shoko episode to inspect.</param>
    /// <returns>The physical file path if found, otherwise null.</returns>
    private string? FindLocalEpisodeThumbnail(IShokoEpisode episode) =>
        (episode.VideoList ?? [])
            .SelectMany(v => v.Files ?? [])
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (Dir: Path.GetDirectoryName(p), Base: Path.GetFileNameWithoutExtension(p)))
            .Where(x => !string.IsNullOrEmpty(x.Dir) && Directory.Exists(x.Dir))
            .SelectMany(x =>
                Directory
                    .EnumerateFiles(x.Dir!, $"{x.Base}.*")
                    .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), x.Base, StringComparison.OrdinalIgnoreCase) && PlexConstants.LocalMediaAssets.Artwork.Contains(Path.GetExtension(f)))
            )
            .FirstOrDefault();

    /// <summary>Purges stale or demoted cross-referenced images for an entity based on source filters.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="predicate">Filter predicate to select cross-references for purging.</param>
    /// <returns>A task representing the asynchronous purge operation.</returns>
    private async Task PurgeEntityImagesAsync(IWithImages entity, ImageEntityType imageType, Func<IImageCrossReference, bool> predicate)
    {
        try
        {
            var existingXrefs = entity.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = imageType });
            foreach (var xref in existingXrefs)
                if (predicate(xref))
                {
                    imageManager.RemoveImageCrossReference(xref);
                    if (imageManager.GetImageByID(xref.ImageID) is { } oldImg)
                        await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
                }
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "ImageSyncService: Failed to purge stale images for entity of type {0}", entity.GetType().Name);
        }
    }

    /// <summary>Uploads a local file from disk to Shoko and marks it as preferred for the specified entity.</summary>
    /// <param name="filePath">The physical file path on disk.</param>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="userSubmitted">Whether the image is user-submitted (manual) or locally generated.</param>
    private void UploadAndPreferLocalImage(string filePath, IWithImages entity, ImageEntityType imageType, bool userSubmitted)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentType = ImageHelper.GetMimeType(Path.GetExtension(filePath)) ?? "image/jpeg";
        var uploadedImage = imageManager.UploadImage(stream, contentType, userSubmitted: userSubmitted);
        imageManager.SetPreferredImageForEntity(entity, imageType, uploadedImage);
    }

    /// <summary>Resolves a file's physical target (bypassing symlinks) and retrieves its physical length.</summary>
    /// <param name="path">The file path to inspect.</param>
    /// <returns>A tuple containing a boolean existence check and the file's physical byte length.</returns>
    private static (bool Exists, long Length) GetFileMetadata(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo targetFi)
                fi = targetFi;
            return (fi.Exists, fi.Length);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>Calculates the MD5 hash of a local file, matching Shoko's internal ResourceID format.</summary>
    /// <param name="path">The file path to hash.</param>
    /// <returns>The upper-case hex string representation of the MD5 hash.</returns>
    private static string GetFileMD5(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(MD5.HashData(fs));
    }

    /// <summary>Loads the local image synchronization cache from disk into a thread-safe concurrent dictionary.</summary>
    /// <returns>A dictionary containing cached image tracking mappings.</returns>
    private ConcurrentDictionary<string, string> LoadCache()
    {
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
        return cache;
    }

    /// <summary>Persists the current image synchronization cache to disk.</summary>
    /// <param name="cache">The dictionary of cache keys to save.</param>
    private void SaveCache(ConcurrentDictionary<string, string> cache)
    {
        try
        {
            File.WriteAllLines(CacheFilePath, cache.Select(kvp => $"{kvp.Key}|{kvp.Value}"));
        }
        catch { }
    }

    #endregion
}
