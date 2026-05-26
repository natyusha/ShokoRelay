using System.Security.Cryptography;
using NLog;
using Shoko.Abstractions.Metadata.Enums;
using ShokoRelay.Vfs;

namespace ShokoRelay.Services;

#region Interface & Models

/// <summary>Service responsible for syncing Plex-generated episode thumbnails and local metadata assets/posters back to Shoko.</summary>
public interface IImageSyncService
{
    /// <summary>Scans all configured Plex libraries and local VFS paths to upload missing or updated screenshots and posters back to Shoko.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit synchronization to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default);
}

/// <summary>Represents the final result of an image synchronization task.</summary>
/// <param name="Processed">Total number of images evaluated.</param>
/// <param name="Uploaded">Total number of images successfully uploaded to Shoko.</param>
/// <param name="Skipped">Total number of images skipped because they already had primary artwork.</param>
/// <param name="Errors">Count of errors encountered during connection or upload.</param>
/// <param name="ErrorsList">List of specific error messages.</param>
public sealed record ImageSyncResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> ErrorsList);

#endregion

/// <summary>Default implementation of <see cref="IImageSyncService"/>.</summary>
public class ImageSyncService(PlexClient plexClient, HttpClient httpClient, IMetadataService metadataService, IImageManager imageManager, ConfigProvider configProvider) : IImageSyncService
{
    #region Fields

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private string CacheFilePath => Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FilePlexImagesCache);

    #endregion

    #region Public API

    /// <summary>Scans all configured Plex libraries and local VFS paths to upload missing or updated screenshots and posters back to Shoko.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit synchronization to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    public async Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default)
    {
        var (processed, uploaded, skipped, errors) = (0, 0, 0, 0);
        var errorsList = new List<string>();
        var targets = plexClient.GetConfiguredTargets();
        var allSeries = metadataService.GetAllShokoSeries() ?? [];

        if (allowedSeriesIds != null)
        {
            var allowedSet = new HashSet<int>(allowedSeriesIds);
            allSeries = [.. allSeries.Where(s => s != null && allowedSet.Contains(s.ID))];
        }

        s_logger.Info("ImageSyncService: Starting image synchronization (local collection/series posters + Plex episode thumbnails)...");

        var cache = LoadCache();
        var processedInRun = new HashSet<int>();
        var updatedCache = false;

        // Sync Plex Episode Screenshots
        if (targets.Count > 0 && !Settings.TmdbThumbnails)
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var episodes = await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? [];

                    foreach (var item in episodes)
                    {
                        if (string.IsNullOrWhiteSpace(item.Guid) || string.IsNullOrWhiteSpace(item.Thumb))
                            continue;

                        var shokoEpisodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                        if (!shokoEpisodeId.HasValue)
                            continue;

                        var episode = metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
                        if (episode == null || (allowedSeriesIds != null && !allowedSeriesIds.Contains(episode.SeriesID)))
                            continue;

                        // Avoid duplicate uploads/processing for the same Shoko Episode ID during this active run
                        if (!processedInRun.Add(shokoEpisodeId.Value))
                            continue;

                        processed++;
                        var isStale = false;
                        var alreadyUploaded = false;
                        var cacheKey = episode.ID.ToString();

                        if (cache.TryGetValue(cacheKey, out var savedThumb))
                        {
                            if (string.Equals(savedThumb, item.Thumb, StringComparison.OrdinalIgnoreCase))
                            {
                                skipped++;
                                alreadyUploaded = true;
                            }
                            else
                                isStale = true; // The thumbnail URL has changed (indicating the file changed or a new thumbnail was generated)
                        }

                        if (episode.GetAvailableImages(ImageEntityType.Backdrop).Any(i => i.IsPreferred && i.Source != DataSource.LocallyGenerated))
                        {
                            skipped++;
                            continue;
                        }

                        if (alreadyUploaded)
                            continue;

                        if (isStale)
                        {
                            s_logger.Debug("ImageSyncService: File changed for episode {0} (ID: {1}) -> Purging stale thumbnail", episode.EpisodeNumber, episode.ID);
                            try
                            {
                                // Fetch and remove all user-uploaded backdrop cross-references and purge their physical files
                                var existingXrefs = imageManager.GetImageCrossReferencesForEntity(episode, imageType: ImageEntityType.Backdrop);
                                foreach (var xref in existingXrefs)
                                {
                                    if (xref.Source is not DataSource.TMDB and not DataSource.AniDB)
                                    {
                                        imageManager.RemoveImageCrossReference(xref);
                                        if (imageManager.GetImageByID(xref.ImageID) is { } oldImg)
                                            await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                s_logger.Warn(ex, "ImageSyncService: Failed to purge stale thumbnail for episode {0}", episode.ID);
                            }
                        }

                        s_logger.Trace("ImageSyncService: Fetching Plex thumbnail for episode {0} (ID: {1})", episode.EpisodeNumber, episode.ID);

                        try
                        {
                            using var req = plexClient.CreateRequest(HttpMethod.Get, item.Thumb, target.ServerUrl);
                            using var resp = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);

                            if (!resp.IsSuccessStatusCode)
                            {
                                errors++;
                                errorsList.Add($"Plex download failed for Shoko episode {episode.ID} with status {resp.StatusCode}");
                                continue;
                            }

                            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                            var md5 = Convert.ToHexString(MD5.HashData(bytes));
                            var imageId = IImageManager.GetIDForImageSourceAndResourceID(DataSource.LocallyGenerated, md5);

                            // Detect duplicate images on disk before upload and link the existing record as preferred
                            var existingImage = imageManager.GetImageByID(imageId);
                            if (existingImage != null)
                            {
                                imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, existingImage);
                                uploaded++;
                                cache[cacheKey] = item.Thumb;
                                updatedCache = true;
                                s_logger.Info("ImageSyncService: Linked existing duplicate thumbnail for episode {0} (ID: {1})", episode.EpisodeNumber, episode.ID);
                                continue;
                            }

                            using var stream = new MemoryStream(bytes);

                            // Upload the thumbnail to Shoko and mark it as the preferred backdrop image
                            var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);
                            imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, uploadedImage);

                            uploaded++;
                            cache[cacheKey] = item.Thumb;
                            updatedCache = true;
                            s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode {0} (ID: {1})", episode.EpisodeNumber, episode.ID);
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            errorsList.Add($"Failed to process Shoko episode {episode.ID}: {ex.Message}");
                            s_logger.Warn(ex, "ImageSyncService: Failed to upload thumbnail for episode {0} (ID: {1})", episode.EpisodeNumber, episode.ID);
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

        // Sync Collection Posters (From !CollectionPosters)
        var groups = allSeries.Where(s => s != null && s.TopLevelGroupID > 0).Select(s => s.TopLevelGroup).Where(g => g != null).GroupBy(g => g.ID).Select(g => g.First()).ToList();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seriesInGroup = allSeries.FirstOrDefault(s => s != null && s.TopLevelGroupID == group.ID);
            if (seriesInGroup == null)
                continue;

            var groupPosterFile = PlexHelper.FindCollectionPosterPathByGroup(seriesInGroup, group.ID, metadataService);
            if (string.IsNullOrEmpty(groupPosterFile) || !File.Exists(groupPosterFile))
                continue;

            processed++;
            var cacheKey = "c" + group.ID;
            var writeTime = new FileInfo(groupPosterFile).LastWriteTimeUtc.Ticks.ToString();
            var isStale = false;

            if (cache.TryGetValue(cacheKey, out var savedWriteTime))
            {
                if (string.Equals(savedWriteTime, writeTime, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                isStale = true;
            }

            if (isStale)
            {
                s_logger.Debug("ImageSyncService: File changed for collection poster {0} (ID: {1}) -> Purging stale poster", group.PreferredTitle?.Value, group.ID);
                try
                {
                    var existingXrefs = imageManager.GetImageCrossReferencesForEntity(group, imageType: ImageEntityType.Primary);
                    foreach (var xref in existingXrefs)
                    {
                        if (xref.Source is not DataSource.TMDB and not DataSource.AniDB)
                        {
                            imageManager.RemoveImageCrossReference(xref);
                            if (imageManager.GetImageByID(xref.ImageID) is { } oldImg)
                                await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_logger.Warn(ex, "ImageSyncService: Failed to purge stale poster for Shoko group {0}", group.ID);
                }
            }

            s_logger.Trace("ImageSyncService: Uploading local collection poster for group {0} (ID: {1})", group.PreferredTitle?.Value, group.ID);

            try
            {
                using var stream = new FileStream(groupPosterFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                var contentType = ImageHelper.GetMimeType(Path.GetExtension(groupPosterFile)) ?? "image/jpeg";
                var uploadedImage = imageManager.UploadImage(stream, contentType, userSubmitted: true);
                imageManager.SetPreferredImageForEntity(group, ImageEntityType.Primary, uploadedImage);

                uploaded++;
                cache[cacheKey] = writeTime;
                updatedCache = true;
                s_logger.Info("ImageSyncService: Successfully uploaded and preferred collection poster for group {0} (ID: {1})", group.PreferredTitle?.Value, group.ID);
            }
            catch (Exception ex)
            {
                errors++;
                errorsList.Add($"Failed to process collection poster for Shoko group {group.ID}: {ex.Message}");
                s_logger.Warn(ex, "ImageSyncService: Failed to upload collection poster for group {0} (ID: {1})", group.PreferredTitle?.Value, group.ID);
            }
        }

        // Sync Local Series Posters (From VFS Root)
        var allowedNames = new[] { "poster", "folder", "show" };

        foreach (var series in allSeries)
        {
            if (series == null)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = "s" + series.ID;

            // Skip secondary series in VFS overrides to prevent duplicate folder scans and upload conflicts
            if (EnforceTmdbNumbering && OverrideHelper.GetPrimary(series.ID, metadataService) != series.ID)
            {
                if (cache.Remove(cacheKey))
                {
                    updatedCache = true;
                    try
                    {
                        var existingXrefs = imageManager.GetImageCrossReferencesForEntity(series, imageType: ImageEntityType.Primary);
                        foreach (var xref in existingXrefs)
                        {
                            if (xref.Source == DataSource.User && xref.IsPreferred)
                            {
                                imageManager.RemoveImageCrossReference(xref);
                                if (imageManager.GetImageByID(xref.ImageID) is { } oldImg)
                                    await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        s_logger.Warn(ex, "ImageSyncService: Failed to purge demoted poster for series {0}", series.ID);
                    }
                }
                continue;
            }

            string? foundPosterFile = null;

            foreach (var vfsPath in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
            {
                if (!Directory.Exists(vfsPath))
                    continue;
                foreach (var name in allowedNames)
                {
                    var matchingFile = Directory
                        .EnumerateFiles(vfsPath)
                        .FirstOrDefault(f =>
                            string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) && PlexConstants.LocalMediaAssets.Artwork.Contains(Path.GetExtension(f))
                        );
                    if (matchingFile != null)
                    {
                        foundPosterFile = matchingFile;
                        break;
                    }
                }
                if (foundPosterFile != null)
                    break;
            }

            if (string.IsNullOrEmpty(foundPosterFile) || !File.Exists(foundPosterFile))
                continue;

            processed++;
            var writeTime = new FileInfo(foundPosterFile).LastWriteTimeUtc.Ticks.ToString();
            var isStale = false;

            if (cache.TryGetValue(cacheKey, out var savedWriteTime))
            {
                if (string.Equals(savedWriteTime, writeTime, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                isStale = true;
            }

            if (isStale)
            {
                s_logger.Debug("ImageSyncService: File changed for series poster {0} (ID: {1}) -> Purging stale poster", series.PreferredTitle?.Value, series.ID);
                try
                {
                    var existingXrefs = imageManager.GetImageCrossReferencesForEntity(series, imageType: ImageEntityType.Primary);
                    foreach (var xref in existingXrefs)
                    {
                        if (xref.Source is not DataSource.TMDB and not DataSource.AniDB)
                        {
                            imageManager.RemoveImageCrossReference(xref);
                            if (imageManager.GetImageByID(xref.ImageID) is { } oldImg)
                                await imageManager.PurgeImage(oldImg).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_logger.Warn(ex, "ImageSyncService: Failed to purge stale poster for series {0}", series.ID);
                }
            }

            s_logger.Trace("ImageSyncService: Uploading local series poster for series {0} (ID: {1})", series.PreferredTitle?.Value, series.ID);

            try
            {
                using var stream = new FileStream(foundPosterFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                var contentType = ImageHelper.GetMimeType(Path.GetExtension(foundPosterFile)) ?? "image/jpeg";
                var uploadedImage = imageManager.UploadImage(stream, contentType, userSubmitted: true);
                imageManager.SetPreferredImageForEntity(series, ImageEntityType.Primary, uploadedImage);

                uploaded++;
                cache[cacheKey] = writeTime;
                updatedCache = true;
                s_logger.Info("ImageSyncService: Successfully uploaded and preferred series poster for Shoko series {0} (ID: {1})", series.PreferredTitle?.Value, series.ID);
            }
            catch (Exception ex)
            {
                errors++;
                errorsList.Add($"Failed to process series poster for Shoko series {series.ID}: {ex.Message}");
                s_logger.Warn(ex, "ImageSyncService: Failed to upload series poster for series {0} (ID: {1})", series.PreferredTitle?.Value, series.ID);
            }
        }

        if (updatedCache)
            SaveCache(cache);

        s_logger.Info("ImageSyncService: Finished synchronization -> uploaded {0} new images to Shoko", uploaded);
        return new ImageSyncResult(processed, uploaded, skipped, errors, errorsList);
    }

    #endregion

    #region Cache Helpers

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

    private void SaveCache(Dictionary<string, string> cache)
    {
        try
        {
            var lines = cache.Select(kvp => $"{kvp.Key}|{kvp.Value}");
            File.WriteAllLines(CacheFilePath, lines);
        }
        catch { }
    }

    #endregion
}
