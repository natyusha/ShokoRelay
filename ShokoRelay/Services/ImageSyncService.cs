using System.Security.Cryptography;
using NLog;
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
public sealed record ImageSyncResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> UploadedDetails, List<string> ErrorsList);

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

    /// <summary>Scans all configured Plex libraries and local VFS paths to upload missing or updated screenshots and posters back to Shoko.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    public async Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (processed, uploaded, skipped, errors) = (0, 0, 0, 0);
            var errorsList = new List<string>();
            var uploadedDetails = new List<string>();
            var targets = plexClient.GetConfiguredTargets();
            var allSeries = metadataService.GetAllShokoSeries() ?? [];

            HashSet<int>? allowedSet = null;
            if (allowedSeriesIds != null)
            {
                allowedSet = [.. allowedSeriesIds];
                allSeries = [.. allSeries.Where(s => s != null && allowedSet.Contains(s.ID))];
            }

            var syncDetails = Settings.TmdbThumbnails ? "" : " + Plex episode thumbnails";
            s_logger.Info("ImageSyncService: Starting image synchronization (local collection/series artwork{0})...", syncDetails);

            var cache = LoadCache();
            var processedInRun = new HashSet<int>();
            var updatedCache = false;

            // Sync Plex Episode Screenshots
            if (targets.Count > 0)
            {
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var episodes = await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [];

                        foreach (var item in episodes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                if (string.IsNullOrWhiteSpace(item.Guid) || string.IsNullOrWhiteSpace(item.Thumb))
                                    continue;

                                var shokoEpisodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                                if (!shokoEpisodeId.HasValue)
                                    continue;

                                var episode = metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
                                if (episode == null || (allowedSet != null && !allowedSet.Contains(episode.SeriesID)))
                                    continue;

                                var epLogName = $"'{episode.Series?.PreferredTitle?.Value}' S{episode.SeasonNumber}E{episode.EpisodeNumber}";

                                // Check if a local physical episode thumbnail exists on disk alongside the video file
                                var localEpisodeThumb = FindLocalEpisodeThumbnail(episode);
                                if (localEpisodeThumb != null)
                                {
                                    var (exists, length) = GetFileMetadata(localEpisodeThumb);
                                    if (exists)
                                    {
                                        processed++;
                                        var cacheKeyLocal = episode.ID.ToString();
                                        var preferredBackdropLocal = episode.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault(i => i.IsPreferred);
                                        string? localCacheVal = cache.GetValueOrDefault(cacheKeyLocal);

                                        var (skipUpload, newCacheVal) = EvaluateLocalImageCache(localCacheVal, length, localEpisodeThumb, preferredBackdropLocal);

                                        if (skipUpload)
                                        {
                                            if (localCacheVal != newCacheVal)
                                            {
                                                cache[cacheKeyLocal] = newCacheVal;
                                                updatedCache = true;
                                            }
                                            skipped++;
                                            continue;
                                        }

                                        s_logger.Debug("ImageSyncService: Local thumbnail for episode {0} (ID: {1}) changed or missing -> Purging stale and uploading", epLogName, episode.ID);
                                        await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);

                                        try
                                        {
                                            UploadAndPreferLocalImage(localEpisodeThumb, episode, ImageEntityType.Backdrop, userSubmitted: false);

                                            uploaded++;
                                            uploadedDetails.Add($"[Local Episode Thumb] {episode.Series?.PreferredTitle?.Value} S{episode.SeasonNumber}E{episode.EpisodeNumber}");
                                            cache[cacheKeyLocal] = newCacheVal;
                                            updatedCache = true;
                                            s_logger.Info("ImageSyncService: Successfully uploaded and preferred local thumbnail for episode {0} (ID: {1})", epLogName, episode.ID);
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            throw;
                                        }
                                        catch (Exception ex)
                                        {
                                            errors++;
                                            errorsList.Add($"Failed to process local thumbnail for episode {epLogName}: {ex.Message}");
                                            s_logger.Warn(ex, "ImageSyncService: Failed to upload local thumbnail for episode {0} (ID: {1})", epLogName, episode.ID);
                                        }
                                        continue;
                                    }
                                }

                                // Skip Plex-generated thumbnail downloads if TMDB thumbnails are enabled
                                if (Settings.TmdbThumbnails)
                                    continue;

                                // Avoid duplicate uploads/processing for the same Shoko Episode ID during this active run
                                if (!processedInRun.Add(shokoEpisodeId.Value))
                                    continue;

                                processed++;
                                var isStale = false;
                                var alreadyUploaded = false;
                                var cacheKey = episode.ID.ToString();
                                var preferredBackdrop = episode.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault(i => i.IsPreferred);

                                if (cache.TryGetValue(cacheKey, out var cacheVal))
                                {
                                    string savedThumb;
                                    string? savedMd5 = null;
                                    var pipeIdx = cacheVal.IndexOf('|');
                                    if (pipeIdx >= 0)
                                    {
                                        savedThumb = cacheVal[..pipeIdx];
                                        savedMd5 = cacheVal[(pipeIdx + 1)..];
                                    }
                                    else
                                        savedThumb = cacheVal;

                                    if (string.Equals(savedThumb, item.Thumb, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (preferredBackdrop != null && (savedMd5 == null || string.Equals(preferredBackdrop.ResourceID, savedMd5, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            skipped++;
                                            alreadyUploaded = true;
                                        }
                                    }
                                    else
                                        isStale = true; // The thumbnail URL has changed (indicating the file changed or a new thumbnail was generated)
                                }

                                if (preferredBackdrop != null && preferredBackdrop.Source is not DataSource.LocallyGenerated and not DataSource.User)
                                {
                                    skipped++;
                                    continue;
                                }

                                if (alreadyUploaded)
                                    continue;

                                if (isStale)
                                {
                                    s_logger.Debug("ImageSyncService: Plex thumbnail URL changed for episode {0} (ID: {1}) -> Purging stale thumbnail", epLogName, episode.ID);
                                    await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
                                }
                                else if (preferredBackdrop != null && preferredBackdrop.Source is DataSource.LocallyGenerated or DataSource.User)
                                {
                                    s_logger.Debug("ImageSyncService: Preferred thumbnail mismatch for episode {0} (ID: {1}) -> Purging incorrect thumbnail", epLogName, episode.ID);
                                    await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);
                                }

                                s_logger.Trace("ImageSyncService: Fetching Plex thumbnail for episode {0} (ID: {1})", epLogName, episode.ID);

                                using var req = plexClient.CreateRequest(HttpMethod.Get, item.Thumb, target.ServerUrl);
                                using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);

                                if (!resp.IsSuccessStatusCode)
                                {
                                    errors++;
                                    errorsList.Add($"Plex download failed for episode {epLogName} (ID: {episode.ID}) with status {resp.StatusCode}");
                                    continue;
                                }

                                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                var md5Hex = Convert.ToHexString(MD5.HashData(bytes));
                                var imageId = IImageManager.GetIDForImageSourceAndResourceID(DataSource.LocallyGenerated, md5Hex);

                                // Detect duplicate images on disk before upload and link the existing record as preferred
                                var existingImage = imageManager.GetImageByID(imageId);
                                if (existingImage != null)
                                {
                                    imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, existingImage);
                                    uploaded++;
                                    uploadedDetails.Add($"[Linked Existing Plex Thumb] {epLogName}");
                                    cache[cacheKey] = $"{item.Thumb}|{md5Hex}";
                                    updatedCache = true;
                                    s_logger.Info("ImageSyncService: Linked existing duplicate thumbnail for episode {0} (ID: {1})", epLogName, episode.ID);
                                    continue;
                                }

                                using var stream = new MemoryStream(bytes);

                                // Upload the thumbnail to Shoko and mark it as the preferred backdrop image
                                var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);
                                imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, uploadedImage);

                                uploaded++;
                                uploadedDetails.Add($"[Plex Thumb] {episode.Series?.PreferredTitle?.Value} S{episode.SeasonNumber}E{episode.EpisodeNumber}");
                                cache[cacheKey] = $"{item.Thumb}|{md5Hex}";
                                updatedCache = true;
                                s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode {0} (ID: {1})", epLogName, episode.ID);
                            }
                            catch (Exception ex)
                            {
                                errors++;
                                string errorContext = item.Thumb != null ? $"episode matching Plex URL '{item.Thumb}'" : "unresolved episode";
                                errorsList.Add($"Failed to process {errorContext}: {ex.Message}");
                                s_logger.Warn(ex, "ImageSyncService: Failed to process episode loop iteration");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Failed to scan Plex section {target.SectionId}: {ex.Message}");
                        s_logger.Warn(ex, "ImageSyncService: Failed to scan library section {0}", target.SectionId);
                    }
                }
            }

            // Sync Collection Posters (From !CollectionImages)
            var groups = allSeries.Where(s => s != null && s.TopLevelGroupID > 0).Select(s => s.TopLevelGroup).Where(g => g != null).GroupBy(g => g.ID).Select(g => g.First()).ToList();

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var seriesInGroup = allSeries.FirstOrDefault(s => s != null && s.TopLevelGroupID == group.ID);
                    if (seriesInGroup == null)
                        continue;

                    var groupPosterFile = PlexHelper.FindCollectionImagePathByGroup(seriesInGroup, group.ID, "", metadataService);
                    if (string.IsNullOrEmpty(groupPosterFile))
                        continue;

                    var (exists, length) = GetFileMetadata(groupPosterFile);
                    if (!exists)
                        continue;

                    processed++;
                    var cacheKey = "c" + group.ID;
                    var preferredPoster = group.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault(i => i.IsPreferred);
                    string? cacheVal = cache.GetValueOrDefault(cacheKey);

                    var (skipUpload, newCacheVal) = EvaluateLocalImageCache(cacheVal, length, groupPosterFile, preferredPoster);

                    if (skipUpload)
                    {
                        if (cacheVal != newCacheVal)
                        {
                            cache[cacheKey] = newCacheVal;
                            updatedCache = true;
                        }
                        skipped++;
                        continue;
                    }

                    s_logger.Debug("ImageSyncService: File changed for collection poster '{0}' (ID: {1}) -> Purging stale poster", group.PreferredTitle?.Value, group.ID);
                    await PurgeEntityImagesAsync(group, ImageEntityType.Primary, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);

                    s_logger.Trace("ImageSyncService: Uploading local collection poster for group '{0}' (ID: {1})", group.PreferredTitle?.Value, group.ID);

                    UploadAndPreferLocalImage(groupPosterFile, group, ImageEntityType.Primary, userSubmitted: true);

                    uploaded++;
                    uploadedDetails.Add($"[Collection Poster] {group.PreferredTitle?.Value}");
                    cache[cacheKey] = newCacheVal;
                    updatedCache = true;
                    s_logger.Info("ImageSyncService: Successfully uploaded and preferred collection poster for group '{0}' (ID: {1})", group.PreferredTitle?.Value, group.ID);
                }
                catch (Exception ex)
                {
                    errors++;
                    errorsList.Add($"Failed to process collection poster for group {group.ID}: {ex.Message}");
                    s_logger.Warn(ex, "ImageSyncService: Failed to process collection poster");
                }
            }

            // Sync Local Series Posters, Backdrops, and Logos (From VFS Root)
            (string[] Names, string Prefix, ImageEntityType Type, string Label)[] configs =
            [
                (["poster", "folder", "show"], "s", ImageEntityType.Primary, "poster"),
                (["art", "backdrop", "background", "fanart"], "b", ImageEntityType.Backdrop, "backdrop"),
                (["clearlogo", "logo"], "l", ImageEntityType.Logo, "logo"),
            ];

            foreach (var config in configs)
            {
                foreach (var series in allSeries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (series == null)
                            continue;

                        var (handled, uploadedOk, skippedOk, errorOk, cacheUpdated) = await ProcessLocalSeriesImageAsync(series, config.Names, config.Prefix, config.Type, config.Label, cache, errorsList)
                            .ConfigureAwait(false);

                        if (cacheUpdated)
                            updatedCache = true;

                        if (!handled)
                            continue;

                        if (uploadedOk || skippedOk || errorOk)
                            processed++;

                        if (uploadedOk)
                        {
                            uploaded++;
                            uploadedDetails.Add($"[Local {config.Label}] {series.PreferredTitle?.Value}");
                            updatedCache = true;
                        }
                        else if (skippedOk)
                            skipped++;
                        else if (errorOk)
                            errors++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Failed to process local artwork '{config.Label}' for series {series?.ID}: {ex.Message}");
                        s_logger.Warn(ex, "ImageSyncService: Failed to process local series artwork loop iteration");
                    }
                }
            }

            if (updatedCache)
                SaveCache(cache);

            s_logger.Info("ImageSyncService: Finished synchronization -> uploaded {0} new images to Shoko", uploaded);
            return new ImageSyncResult(processed, uploaded, skipped, errors, uploadedDetails, errorsList);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region Image Helpers

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

    /// <summary>Loads the local image synchronization cache from disk into a case-insensitive dictionary.</summary>
    /// <returns>A dictionary containing cached image tracking mappings.</returns>
    private Dictionary<string, string> LoadCache()
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    private void SaveCache(Dictionary<string, string> cache)
    {
        try
        {
            var lines = cache.Select(kvp => $"{kvp.Key}|{kvp.Value}");
            File.WriteAllLines(CacheFilePath, lines);
        }
        catch { }
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

    /// <summary>Processes local series artwork (posters, backdrops, or logos) by validating overrides, length/hash states, and uploading to Shoko.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="allowedNames">The prioritized array of allowed file names.</param>
    /// <param name="cachePrefix">The prefix representing the image type in the cache.</param>
    /// <param name="imageType">The Shoko target image entity type.</param>
    /// <param name="label">The diagnostic label for logging.</param>
    /// <param name="cache">The active session cache dictionary.</param>
    /// <param name="errorsList">The collection of accumulated sync error messages.</param>
    /// <returns>A tuple indicating handling completion status, upload success, skip state, error presence, and whether the cache was modified.</returns>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error, bool CacheUpdated)> ProcessLocalSeriesImageAsync(
        IShokoSeries series,
        string[] allowedNames,
        string cachePrefix,
        ImageEntityType imageType,
        string label,
        Dictionary<string, string> cache,
        List<string> errorsList
    )
    {
        var cacheKey = cachePrefix + series.ID;
        var cacheUpdated = false;

        // Skip secondary series in VFS overrides to prevent duplicate folder scans and upload conflicts
        if (EnforceTmdbNumbering && OverrideHelper.GetPrimary(series.ID, metadataService) != series.ID)
        {
            if (cache.Remove(cacheKey))
            {
                cacheUpdated = true;
                await PurgeEntityImagesAsync(series, imageType, x => x.Source == DataSource.User && x.IsPreferred).ConfigureAwait(false);
            }
            return (false, false, false, false, cacheUpdated);
        }

        string? foundFile = null;
        foreach (var vfsPath in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
        {
            if (!Directory.Exists(vfsPath))
                continue;

            // Enumerate files exactly once per path to avoid severe SMB/Network latency penalties
            var localArtworks = Directory.EnumerateFiles(vfsPath).Where(f => PlexConstants.LocalMediaAssets.Artwork.Contains(Path.GetExtension(f))).ToList();

            foreach (var name in allowedNames)
            {
                foundFile = localArtworks.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (foundFile != null)
                    break;
            }
            if (foundFile != null)
                break;
        }

        if (string.IsNullOrEmpty(foundFile))
            return (true, false, false, false, false);

        var (exists, length) = GetFileMetadata(foundFile);
        if (!exists)
            return (true, false, false, false, false);

        var preferredImage = series.GetAvailableImages(imageType).FirstOrDefault(i => i.IsPreferred);
        string? cacheVal = cache.GetValueOrDefault(cacheKey);

        var (skipUpload, newCacheVal) = EvaluateLocalImageCache(cacheVal, length, foundFile, preferredImage);

        if (skipUpload)
        {
            if (cacheVal == newCacheVal)
                return (true, false, true, false, false);
            cache[cacheKey] = newCacheVal;
            return (true, false, true, false, true);
        }

        s_logger.Debug("ImageSyncService: File changed for series {0} '{1}' (ID: {2}) -> Purging stale image", label, series.PreferredTitle?.Value, series.ID);
        await PurgeEntityImagesAsync(series, imageType, x => x.Source is not DataSource.TMDB and not DataSource.AniDB).ConfigureAwait(false);

        s_logger.Trace("ImageSyncService: Uploading local series {0} for series '{1}' (ID: {2})", label, series.PreferredTitle?.Value, series.ID);

        try
        {
            UploadAndPreferLocalImage(foundFile, series, imageType, userSubmitted: true);
            cache[cacheKey] = newCacheVal;
            return (true, true, false, false, true);
        }
        catch (Exception ex)
        {
            errorsList.Add($"Failed to process series {label} for series {series.ID}: {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to upload series {0} for series '{1}' (ID: {2})", label, series.PreferredTitle?.Value, series.ID);
            return (true, false, false, true, false);
        }
    }

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

    #endregion
}
