using NLog;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Services;

#region Interface & Models

/// <summary>Service responsible for syncing Plex-generated episode thumbnails back to Shoko.</summary>
public interface IImageSyncService
{
    /// <summary>Scans all configured Plex libraries and uploads missing or updated episode screenshots back to Shoko.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    Task<ImageSyncResult> SyncImagesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Represents the final result of an image synchronization task.</summary>
/// <param name="Processed">Total number of episodes evaluated.</param>
/// <param name="Uploaded">Total number of thumbnails successfully uploaded to Shoko.</param>
/// <param name="Skipped">Total number of episodes skipped because they already had primary artwork.</param>
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

    /// <summary>Scans all configured Plex libraries and uploads missing or updated episode screenshots back to Shoko.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    public async Task<ImageSyncResult> SyncImagesAsync(CancellationToken cancellationToken = default)
    {
        var (processed, uploaded, skipped, errors) = (0, 0, 0, 0);
        var errorsList = new List<string>();
        var targets = plexClient.GetConfiguredTargets();

        if (targets.Count == 0)
            return new ImageSyncResult(0, 0, 0, 0, errorsList);

        s_logger.Info("ImageSyncService: Starting episode thumbnail synchronization...");

        var cache = LoadCache();
        var processedInRun = new HashSet<int>();
        var updatedCache = false;

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

                    // Avoid duplicate uploads/processing for the same Shoko Episode ID during this active run
                    if (!processedInRun.Add(shokoEpisodeId.Value))
                        continue;

                    processed++;
                    var episode = metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
                    if (episode == null)
                    {
                        skipped++;
                        continue;
                    }

                    var isStale = false;
                    var alreadyUploaded = false;

                    if (cache.TryGetValue(episode.ID, out var savedThumb))
                    {
                        if (string.Equals(savedThumb, item.Thumb, StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            alreadyUploaded = true;
                        }
                        else
                        {
                            isStale = true; // The thumbnail URL has changed (indicating the file changed or a new thumbnail was generated)
                        }
                    }
                    if (episode.GetAvailableImages(ImageEntityType.Backdrop).Any(i => i.IsPreferred && i.Source != DataSource.TMDB && i.Source != DataSource.AniDB))
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
                            errorsList.Add($"Plex download failed for episode {episode.ID} with status {resp.StatusCode}");
                            continue;
                        }

                        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                        // Upload the thumbnail to Shoko and mark it as the preferred backdrop image
                        var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);
                        imageManager.SetPreferredImageForEntity(episode, ImageEntityType.Backdrop, uploadedImage);

                        uploaded++;
                        cache[episode.ID] = item.Thumb;
                        updatedCache = true;
                        s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode {0} (ID: {1})", episode.EpisodeNumber, episode.ID);
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Failed to process episode {episode.ID}: {ex.Message}");
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

        if (updatedCache)
            SaveCache(cache);

        s_logger.Info("ImageSyncService: Finished synchronization -> uploaded {0} new thumbnails to Shoko", uploaded);
        return new ImageSyncResult(processed, uploaded, skipped, errors, errorsList);
    }

    #endregion

    #region Internal Cache Helpers

    private Dictionary<int, string> LoadCache()
    {
        var cache = new Dictionary<int, string>();
        if (File.Exists(CacheFilePath))
        {
            try
            {
                foreach (var line in File.ReadAllLines(CacheFilePath))
                {
                    var parts = line.Split('|', 2);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int id))
                        cache[id] = parts[1];
                }
            }
            catch { }
        }
        return cache;
    }

    private void SaveCache(Dictionary<int, string> cache)
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
