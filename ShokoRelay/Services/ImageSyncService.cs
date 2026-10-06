using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image.CrossReferences;
using Shoko.Abstractions.Metadata.Image.Options;

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
public class ImageSyncService(PlexClient plexClient, IMetadataService metadataService, IImageManager imageManager) : IImageSyncService
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <summary>Static configurations for local series artwork types.</summary>
    private static readonly (string[] Names, string Prefix, ImageEntityType Type, string Label)[] s_seriesImageConfigs =
    [
        (["poster", "folder", "show"], "s", ImageEntityType.Primary, "poster"),
        (["art", "backdrop", "background", "fanart"], "b", ImageEntityType.Backdrop, "backdrop"),
        (["clearlogo", "logo"], "l", ImageEntityType.Logo, "logo"),
    ];

    private readonly SemaphoreSlim _syncLock = new(1, 1);

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

            var prefIdCache = new ConcurrentDictionary<int, string?>();
            var errsBag = new ConcurrentBag<string>();
            var uploadedBag = new ConcurrentBag<string>();
            int p = 0,
                u = 0,
                s = 0,
                e = 0;

            void AddStats(bool handled, bool uploaded, bool skipped, bool error)
            {
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
                await SyncEpisodeThumbnailsAsync(targets, allowedSet, prefIdCache, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

            // Sync Collection Posters
            await SyncCollectionPostersAsync(allSeries, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

            // Sync Local Series Images (Posters, Backdrops, Logos)
            await SyncLocalSeriesImagesAsync(allSeries, errsBag, uploadedBag, AddStats, cancellationToken).ConfigureAwait(false);

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
    /// <param name="prefIdCache">Cache dictionary for preferred TMDB ordering IDs.</param>
    /// <param name="errsBag">Bag to collect error messages and missing thumbnail diagnostics.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncEpisodeThumbnailsAsync(
        IReadOnlyList<PlexLibraryTarget> targets,
        HashSet<int>? allowedSet,
        ConcurrentDictionary<int, string?> prefIdCache,
        ConcurrentBag<string> errsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool> addStats,
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
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        return;

                    var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);

                    // Ensure each Shoko Episode (including specials and movies) is only processed once globally per run
                    // Ordering TV targets first ensures Movie libraries skip episodes/specials already synced from TV libraries
                    if (!epId.HasValue || !processedInRun.Add(epId.Value))
                        return;

                    var episode = metadataService.GetShokoEpisodeByID(epId.Value);
                    if (episode == null)
                        return;

                    int primarySeriesId = OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService);
                    if (allowedSet != null && !allowedSet.Contains(primarySeriesId))
                        return;

                    string? prefId = episode.Series != null ? prefIdCache.GetOrAdd(episode.ShokoSeriesID, _ => MapHelper.GetPreferredTmdbOrderingId(episode.Series)) : null;
                    var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                    bool isMovie = target.LibraryType == PlexLibraryType.Movie;
                    string labelType = isMovie ? "Movie" : "Episode";
                    string coordsStr = $"S{coords.Season:D2}E{coords.Episode:D2}";
                    var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - {(isMovie ? $"Movie [{episode.LocalID}]" : coordsStr)}";

                    // File-Anchor Verification: An episode cannot receive a Plex video thumbnail if it possesses no active physical video files
                    var hasPhysicalFiles = (episode.Videos ?? []).Any(v => v.Files?.Any(f => !string.IsNullOrWhiteSpace(f.Path) && File.Exists(f.Path)) == true);
                    if (!hasPhysicalFiles)
                    {
                        if (
                            episode
                                .GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop })
                                .Any(x => x.Source == ServiceRegistration.RelayPlexSource || x.Source == ServiceRegistration.RelaySource)
                        )
                        {
                            await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == ServiceRegistration.RelayPlexSource || x.Source == ServiceRegistration.RelaySource)
                                .ConfigureAwait(false);
                            addStats(false, false, false, false);
                        }
                        return;
                    }

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
                            addStats(true, false, false, true);
                            errsBag.Add($"[Coordinate Mismatch] {epLogName} (Plex: S{item.ParentIndex.Value:D2}E{item.Index.Value:D2}, Shoko: {coordsStr})");
                            return;
                        }
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

                    var (fileExists, u, s, e) = await ProcessLocalAssetAsync(
                            localThumb,
                            episode,
                            ImageEntityType.Backdrop,
                            "local thumbnail",
                            epLogName,
                            false,
                            $"[Local {labelType} Thumb] {epLogName}",
                            errsBag
                        )
                        .ConfigureAwait(false);
                    addStats(fileExists, u, s, e);

                    bool purgePlexThumb = fileExists || Settings.TmdbThumbnails;
                    if (purgePlexThumb)
                    {
                        if (episode.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop }).Any(x => x.Source == ServiceRegistration.RelayPlexSource))
                        {
                            s_logger.Info("ImageSyncService: {0} ... Purging Plex thumbnail for -> {1}", fileExists ? "Local thumbnail found" : "TMDB Thumbnails enabled", epLogName);
                            await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(item.Thumb))
                        {
                            addStats(true, false, false, true);
                            errsBag.Add($"[Missing Plex Thumbnail] {epLogName} (No thumbnail generated or available in Plex)");
                            if (episode.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop }).Any(x => x.Source == ServiceRegistration.RelayPlexSource))
                                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                        }
                        else
                        {
                            var (ph, pu, ps, pe) = await ProcessPlexThumbnailAsync(item.Thumb, episode, epLogName, target, errsBag, uploadedBag, ct).ConfigureAwait(false);
                            addStats(ph, pu, ps, pe);
                        }
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
                addStats(true, false, false, true);
                errsBag.Add($"Failed to scan Plex section {target.SectionId}: {ex.Message}");
                s_logger.Warn(ex, "ImageSyncService: Failed to scan library section {0}", target.SectionId);
            }
        }

        // Safely purge orphaned Plex thumbnails for episodes completely removed from Plex
        if (allowedSet == null && errsBag.IsEmpty)
        {
            var allEpisodeXrefs = imageManager
                .GetAllImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop })
                .Where(x => x.Source == ServiceRegistration.RelayPlexSource && x.EntityID.EntityType == MetadataEntityType.Episode)
                .ToList();

            var orphanedEpIds = allEpisodeXrefs
                .Select(x => x.EntityID.ID)
                .Where(idStr => !Guid.TryParse(idStr, out _) && int.TryParse(idStr, out _))
                .Select(int.Parse)
                .Where(epId => !processedInRun.Contains(epId))
                .Distinct()
                .ToList();

            foreach (var epId in orphanedEpIds)
            {
                var episode = metadataService.GetShokoEpisodeByID(epId);
                if (episode == null)
                    continue;

                string? prefId = episode.Series != null ? prefIdCache.GetOrAdd(episode.ShokoSeriesID, _ => MapHelper.GetPreferredTmdbOrderingId(episode.Series)) : null;
                var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                string coordsStr = $"S{coords.Season:D2}E{coords.Episode:D2}";
                var epLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - {coordsStr}";

                s_logger.Info("ImageSyncService: Episode thumbnail for -> {0} is no longer present in Plex ... Purging from Shoko", epLogName);
                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, x => x.Source == ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                addStats(false, false, false, false);
            }
        }
    }

    /// <summary>Scans local collection posters to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="errsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncCollectionPostersAsync(List<IShokoSeries> allSeries, ConcurrentBag<string> errsBag, ConcurrentBag<string> uploadedBag, Action<bool, bool, bool, bool> addStats, CancellationToken ct)
    {
        var groups = allSeries.Where(s => s.TopLevelGroupID > 0).Select(s => s.TopLevelGroup).OfType<IShokoGroup>().DistinctBy(g => g.LocalID).ToList();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var seriesInGroup = allSeries.FirstOrDefault(s => s.TopLevelGroupID == group.LocalID);
            if (seriesInGroup == null)
                continue;

            string? groupPosterFile = PlexHelper.FindCollectionImagePathByGroup(seriesInGroup, group.LocalID, "", metadataService);
            var (fileExists, u, s, e) = await ProcessLocalAssetAsync(
                    groupPosterFile,
                    group,
                    ImageEntityType.Primary,
                    "collection poster",
                    $"group {group.PreferredTitle?.Value} [{group.LocalID}]",
                    true,
                    $"[Collection Poster] {group.PreferredTitle?.Value}",
                    errsBag
                )
                .ConfigureAwait(false);

            if (fileExists && u)
                uploadedBag.Add($"[Collection Poster] {group.PreferredTitle?.Value}");
            addStats(fileExists, u, s, e);
        }
    }

    /// <summary>Scans local series artwork (posters, backdrops, logos) to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="errsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SyncLocalSeriesImagesAsync(List<IShokoSeries> allSeries, ConcurrentBag<string> errsBag, ConcurrentBag<string> uploadedBag, Action<bool, bool, bool, bool> addStats, CancellationToken ct)
    {
        await Parallel
            .ForEachAsync(
                allSeries,
                DefaultParallelOptions(ct),
                async (series, token) =>
                {
                    foreach (var config in s_seriesImageConfigs)
                    {
                        if (OverrideHelper.GetPrimary(series.LocalID, metadataService) != series.LocalID)
                        {
                            if (series.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = config.Type }).Any(x => x.Source == ServiceRegistration.RelaySource))
                            {
                                await PurgeEntityImagesAsync(series, config.Type, x => x.Source == ServiceRegistration.RelaySource).ConfigureAwait(false);
                                addStats(false, false, false, false);
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

                        var (fileExists, u, s, e) = await ProcessLocalAssetAsync(
                                foundFile,
                                series,
                                config.Type,
                                config.Label,
                                $"series {series.GetDisplayTitle()} [{series.LocalID}]",
                                true,
                                $"[Local {config.Label}] {series.GetDisplayTitle()}",
                                errsBag
                            )
                            .ConfigureAwait(false);

                        if (fileExists && u)
                            uploadedBag.Add($"[Local {config.Label}] {series.GetDisplayTitle()}");
                        addStats(fileExists, u, s, e);
                    }
                }
            )
            .ConfigureAwait(false);
    }

    #endregion

    #region Core Processing Logic

    /// <summary>Universal method for caching, purging, and uploading local image assets.</summary>
    private async Task<(bool FileExists, bool Uploaded, bool Skipped, bool Error)> ProcessLocalAssetAsync(
        string? foundFile,
        IWithImages entity,
        ImageEntityType imageType,
        string label,
        string entityName,
        bool userSubmitted,
        string? uploadDetail,
        ConcurrentBag<string> errorsBag
    )
    {
        // Resolve a file's physical target (bypassing symlinks)
        bool exists = false;
        if (!string.IsNullOrEmpty(foundFile))
        {
            try
            {
                var fi = new FileInfo(foundFile);
                fi = fi.LinkTarget != null ? (fi.ResolveLinkTarget(true) as FileInfo ?? fi) : fi;
                if (exists = fi.Exists)
                    foundFile = fi.FullName;
            }
            catch { }
        }

        var existingXrefs = entity.GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = imageType }).Where(x => x.Source == ServiceRegistration.RelaySource).ToList();

        if (!exists)
        {
            if (existingXrefs.Count > 0)
            {
                s_logger.Info("ImageSyncService: Local {0} for -> {1} no longer present on disk ... Purging from Shoko", label, entityName);
                await PurgeEntityImagesAsync(entity, imageType, x => x.Source == ServiceRegistration.RelaySource).ConfigureAwait(false);
            }
            return (false, false, false, false);
        }

        string md5;
        using (var fs = new FileStream(foundFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            md5 = Convert.ToHexString(MD5.HashData(fs));

        var matchingXref = existingXrefs.FirstOrDefault(x => string.Equals(x.GetImage()?.ResourceID, md5, StringComparison.OrdinalIgnoreCase));
        if (matchingXref != null)
            return (true, false, true, false); // Skipped: We already own this exact image for this entity. Shoko handles the IsPreferred exclusivity natively.

        s_logger.Debug("ImageSyncService: Local {0} changed or new for -> {1} ... Uploading", label, entityName);
        await PurgeEntityImagesAsync(entity, imageType, x => x.Source == ServiceRegistration.RelaySource).ConfigureAwait(false);

        try
        {
            // Upload a local file from disk to Shoko and establish explicit ownership using the RelaySource cross-reference
            using var stream = new FileStream(foundFile!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = ImageHelper.GetMimeType(Path.GetExtension(foundFile!)) ?? "image/jpeg";
            var uploadedImage = imageManager.UploadImage(stream, contentType, userSubmitted: userSubmitted);

            imageManager.AddImageCrossReference(
                entity,
                uploadedImage,
                new ImageCrossReferenceData
                {
                    ImageType = imageType,
                    Source = ServiceRegistration.RelaySource,
                    IsPreferred = true,
                    IsDesired = true,
                }
            );

            if (uploadDetail != null)
                s_logger.Info("ImageSyncService: Successfully uploaded and preferred {0} for -> {1}", label, entityName);
            return (true, true, false, false);
        }
        catch (Exception ex)
        {
            errorsBag.Add($"Failed to process {label} for -> {entityName}: {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to upload {0} for -> {1}", label, entityName);
            return (true, false, false, true);
        }
    }

    /// <summary>Downloads and processes Plex-generated thumbnails.</summary>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error)> ProcessPlexThumbnailAsync(
        string thumbUrl,
        IShokoEpisode episode,
        string epLogName,
        PlexLibraryTarget target,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        CancellationToken ct
    )
    {
        var existingXrefs = episode
            .GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop })
            .Where(x => x.Source == ServiceRegistration.RelayPlexSource)
            .ToList();

        if (existingXrefs.Count > 0)
            return (true, false, true, false); // Skipped: We already own a downloaded Plex thumbnail for this episode.

        s_logger.Trace("ImageSyncService: Fetching Plex thumbnail for episode -> {0}", epLogName);
        try
        {
            using var req = plexClient.CreateRequest(HttpMethod.Get, thumbUrl, target.ServerUrl);
            using var resp = await plexClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                errorsBag.Add($"[Failed Plex Download] {epLogName} (HTTP {resp.StatusCode})");
                return (true, false, false, true);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // Stream through imageManager.UploadImage to guarantee cross-reference creation for both new and existing images
            using var stream = new MemoryStream(bytes);
            var uploadedImage = imageManager.UploadImage(stream, "image/jpeg", userSubmitted: false);

            imageManager.AddImageCrossReference(
                episode,
                uploadedImage,
                new ImageCrossReferenceData
                {
                    ImageType = ImageEntityType.Backdrop,
                    Source = ServiceRegistration.RelayPlexSource,
                    IsPreferred = true,
                    IsDesired = true,
                }
            );

            uploadedBag.Add($"[Plex Thumb] {epLogName}");
            s_logger.Info("ImageSyncService: Successfully uploaded and preferred thumbnail for episode -> {0}", epLogName);
            return (true, true, false, false);
        }
        catch (Exception ex)
        {
            errorsBag.Add($"[Plex Thumbnail Exception] {epLogName}: {ex.Message}");
            s_logger.Warn(ex, "ImageSyncService: Failed to process Plex thumbnail for {0}", epLogName);
            return (true, false, false, true);
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
            var filterOpts = new ImageCrossReferenceFilteringOptions { ImageType = imageType };
            foreach (var xref in entity.GetImageCrossReferences(filterOpts).Where(predicate))
            {
                imageManager.RemoveImageCrossReference(xref);
                // Only purge the underlying image if no other entities are actively referencing it
                if (imageManager.GetImageByID(xref.ImageID) is { } oldImg && !imageManager.GetAllImageCrossReferences(filterOpts).Any(x => x.ImageID == oldImg.ID && x.ID != xref.ID))
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
