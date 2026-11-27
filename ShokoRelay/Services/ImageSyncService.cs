using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Image.CrossReferences;
using Shoko.Abstractions.Metadata.Image.Options;
using Shoko.Abstractions.Utilities;

namespace ShokoRelay.Services;

#region Data Models

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

/// <summary>Service responsible for syncing Plex-generated episode thumbnails and local metadata assets (posters, backdrops, logos) back to Shoko.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="imageManager">Shoko image manager.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="logger">Logger instance.</param>
public class ImageSyncService(PlexClient plexClient, IMetadataService metadataService, IImageManager imageManager, IFileSystem fileSystem, ILogger<ImageSyncService> logger)
{
    #region Setup

    /// <summary>Static configurations for local series artwork types.</summary>
    private static readonly (string[] Names, ImageEntityType Type, string Label)[] s_seriesImageConfigs =
    [
        (["poster", "folder", "show"], ImageEntityType.Primary, "poster"),
        (["art", "backdrop", "background", "fanart"], ImageEntityType.Backdrop, "backdrop"),
        (["clearlogo", "logo"], ImageEntityType.Logo, "logo"),
    ];

    /// <summary>Listing options restricting directory enumeration to supported artwork file extensions.</summary>
    private static readonly FileSystemListingOptions s_artworkListingOptions = new() { Extensions = PlexConstants.LocalMediaAssets.Artwork.Keys };

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>Serializes Shoko image manager write operations across parallel thumbnail and asset tasks.</summary>
    private readonly SemaphoreSlim _imageWriteLock = new(1, 1);

    #endregion

    #region Public API

    /// <summary>Scans all configured Plex libraries and local VFS paths to upload missing or updated screenshots, posters, backdrops, and logos back to Shoko.</summary>
    /// <param name="allowedSeriesIds">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>A summary result containing statistics on the synchronization run.</returns>
    public async Task<ImageSyncResult> SyncImagesAsync(IEnumerable<int>? allowedSeriesIds = null, CancellationToken cancellationToken = default, IProgress<decimal>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targets = plexClient.GetConfiguredTargets();
            var allowedPrimaryIds = allowedSeriesIds?.Select(id => OverrideHelper.GetPrimary(id, metadataService)).Distinct().ToList();
            var allSeries =
                allowedPrimaryIds != null ? [.. allowedPrimaryIds.Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>()] : metadataService.GetAllShokoSeries()?.Cast<IShokoSeries>().ToList() ?? [];
            HashSet<int>? allowedSet = allowedPrimaryIds != null ? [.. allowedPrimaryIds] : null;
            if (allowedSet != null && allSeries.Count == 0)
                return new ImageSyncResult(0, 0, 0, 0, [], [], stopwatch.Elapsed);

            var stages = progress.InStages(7m, 1m, 2m);
            stages.Report(0);

            var syncDetails = Settings.TmdbThumbnails ? "" : " + Plex episode thumbnails";
            logger.LogInformation("ImageSyncService: Starting image synchronization (local collection/series artwork{Details})...", syncDetails);

            var preferredOrderingIdCache = new ConcurrentDictionary<int, string?>();
            var errorsBag = new ConcurrentBag<string>();
            var uploadedBag = new ConcurrentBag<string>();
            var (processed, uploaded, skipped, errors) = (0, 0, 0, 0);

            void AddStats(bool handled, bool wasUploaded, bool wasSkipped, bool hasError)
            {
                if (handled && (wasUploaded || wasSkipped || hasError))
                    Interlocked.Increment(ref processed);
                if (wasUploaded)
                    Interlocked.Increment(ref uploaded);
                else if (wasSkipped)
                    Interlocked.Increment(ref skipped);
                else if (hasError)
                    Interlocked.Increment(ref errors);
            }

            // Sync Episode Thumbnails (Local & Plex)
            if (targets.Count > 0)
                await SyncEpisodeThumbnailsAsync(targets, allowedSet, preferredOrderingIdCache, errorsBag, uploadedBag, AddStats, cancellationToken, stages).ConfigureAwait(false);

            stages.NextStage();

            // Sync Collection Posters
            await SyncCollectionPostersAsync(allSeries, errorsBag, uploadedBag, AddStats, cancellationToken, stages).ConfigureAwait(false);

            stages.NextStage();

            // Sync Local Series Images (Posters, Backdrops, Logos)
            await SyncLocalSeriesImagesAsync(allSeries, errorsBag, uploadedBag, AddStats, cancellationToken, stages).ConfigureAwait(false);

            stages.Complete();
            stopwatch.Stop();
            logger.LogInformation("ImageSyncService: Finished synchronization -> uploaded {UploadedCount} new images to Shoko in {Elapsed}ms", uploaded, stopwatch.ElapsedMilliseconds);
            return new ImageSyncResult(processed, uploaded, skipped, errors, [.. uploadedBag.OrderBy(item => item)], [.. errorsBag.OrderBy(item => item)], stopwatch.Elapsed);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Removes and purges all custom images and thumbnails owned by Shoko Relay across both Relay sources.</summary>
    /// <returns>The total number of images purged.</returns>
    public async Task<int> PurgeAllRelayImagesAsync()
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _imageWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var xrefs = imageManager
                    .GetAllImageCrossReferences(new ImageCrossReferenceFilteringOptions { XrefSource = ServiceRegistration.RelaySource })
                    .Concat(imageManager.GetAllImageCrossReferences(new ImageCrossReferenceFilteringOptions { XrefSource = ServiceRegistration.RelayPlexSource }))
                    .ToList();
                int purgedCount = 0;

                foreach (var xref in xrefs)
                {
                    imageManager.RemoveImageCrossReference(xref);
                    if (imageManager.GetImageByID(xref.ImageID) is { } oldImage && oldImage.GetCrossReferences().Count == 0 && await imageManager.PurgeImage(oldImage).ConfigureAwait(false))
                        purgedCount++;
                }

                return purgedCount;
            }
            finally
            {
                _imageWriteLock.Release();
            }
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
    /// <param name="preferredOrderingIdCache">Cache dictionary for preferred TMDB ordering IDs.</param>
    /// <param name="errorsBag">Bag to collect error messages and missing thumbnail diagnostics.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    private async Task SyncEpisodeThumbnailsAsync(
        IReadOnlyList<PlexLibraryTarget> targets,
        HashSet<int>? allowedSet,
        ConcurrentDictionary<int, string?> preferredOrderingIdCache,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool> addStats,
        CancellationToken cancellationToken,
        IProgress<decimal>? progress = null
    )
    {
        var processedInRun = new ConcurrentDictionary<int, byte>();
        var fileDataCache = new ConcurrentDictionary<int, MapHelper.SeriesFileData?>();
        var dirArtCache = new ConcurrentDictionary<string, Lazy<string[]>>(VfsShared.PathComparer);
        var orderedTargets = targets.OrderBy(target => target.LibraryType == PlexLibraryType.Movie ? 1 : 0).ToList();
        var targetStages = progress.InStages([.. Enumerable.Repeat(1m, Math.Max(1, orderedTargets.Count))]);
        targetStages.Report(0);
        bool sectionScanFailed = false;

        foreach (var target in orderedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var items =
                    allowedSet != null
                        ? await plexClient.GetSectionItemsForSeriesAsync(target, allowedSet, metadataService, includeLeaves: target.LibraryType != PlexLibraryType.Movie, cancellationToken).ConfigureAwait(false)
                        : (
                            target.LibraryType == PlexLibraryType.Movie
                                ? await plexClient.GetSectionMoviesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? []
                                : await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false) ?? []
                        );

                var itemProgress = targetStages.Throttle().ForItems(Math.Max(1, items.Count));
                itemProgress.Report(0);

                async ValueTask ProcessThumbnailItem(PlexMetadataItem item, CancellationToken itemToken)
                {
                    try
                    {
                        itemToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(item.Guid))
                            return;

                        var episodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);

                        // Ensure each Shoko Episode (including specials and movies) is only processed once globally per run
                        // Ordering TV targets first ensures Movie libraries skip episodes/specials already synced from TV libraries
                        if (!episodeId.HasValue || !processedInRun.TryAdd(episodeId.Value, 0))
                            return;

                        var episode = metadataService.GetShokoEpisodeByID(episodeId.Value);
                        if (episode == null)
                            return;

                        int primarySeriesId = OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService);
                        if (allowedSet != null && !allowedSet.Contains(primarySeriesId))
                            return;

                        var fileData = fileDataCache.GetOrAdd(primarySeriesId, seriesId =>
                            metadataService.GetShokoSeriesByID(seriesId) is { } primarySeries ? MapHelper.GetConsolidatedSeriesFileData(primarySeries, metadataService) : null
                        );
                        var matchingMappings = fileData?.Mappings.Where(mapping => mapping.Episodes.Any(mappedEpisode => mappedEpisode.ID == episode.ID)).ToList() ?? [];
                        var mappedCoords = matchingMappings.FirstOrDefault()?.Coords;

                        PlexMapping.PlexCoords coords;
                        if (mappedCoords.HasValue)
                            coords = mappedCoords.Value;
                        else
                        {
                            string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;
                            coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId);
                        }

                        bool isMovie = target.LibraryType == PlexLibraryType.Movie;
                        string labelType = isMovie ? "Movie" : "Episode";
                        string coordsText = $"S{coords.Season:D2}E{coords.Episode:D2}";
                        var episodeLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - {(isMovie ? $"Movie [{episode.LocalID}]" : coordsText)}";

                        // File-Anchor Verification: An episode cannot receive a Plex video thumbnail if it possesses no active physical video files
                        var hasPhysicalFiles = (episode.Videos ?? []).Any(video => video.Files?.Any(file => !string.IsNullOrWhiteSpace(file.Path) && fileSystem.FileExists(file.Path)) == true);
                        if (!hasPhysicalFiles)
                        {
                            if (
                                episode
                                    .GetImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop, LinkedEntityImages = false })
                                    .Any(xref => xref.Source == ServiceRegistration.RelayPlexSource || xref.Source == ServiceRegistration.RelaySource)
                            )
                            {
                                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, xref => xref.Source == ServiceRegistration.RelayPlexSource || xref.Source == ServiceRegistration.RelaySource)
                                    .ConfigureAwait(false);
                                addStats(false, false, false, false);
                            }
                            return;
                        }

                        // Coordinate Alignment Guard: Detect when Plex's metadata is in a transient or mismatched state
                        if (!isMovie && item.ParentIndex.HasValue && item.Index.HasValue)
                        {
                            bool MatchesCoords(PlexMapping.PlexCoords candidateCoords) =>
                                item.ParentIndex.Value == candidateCoords.Season
                                && (
                                    item.Index.Value == candidateCoords.Episode
                                    || (candidateCoords.EndEpisode.HasValue && item.Index.Value >= candidateCoords.Episode && item.Index.Value <= candidateCoords.EndEpisode.Value)
                                );

                            bool isMismatch = matchingMappings.Count > 0 ? !matchingMappings.Any(mapping => MatchesCoords(mapping.Coords)) : !MatchesCoords(coords);

                            // Compensate for virtual TMDB episode group splits where individual TMDB episodes sit in different seasons or indices
                            if (
                                isMismatch
                                && EnforceTmdbNumbering
                                && episode.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 1 } tmdbEpisodes
                                && episode.Series != null
                                && MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) is var preferredOrderingId
                                && tmdbEpisodes.Any(tmdbEpisode =>
                                    PlexMapping.GetOrderingCoords(tmdbEpisode, preferredOrderingId) is var (tmdbSeason, tmdbEpisodeNumber)
                                    && tmdbSeason == item.ParentIndex.Value
                                    && tmdbEpisodeNumber == item.Index.Value
                                )
                            )
                                isMismatch = false;

                            if (isMismatch)
                            {
                                addStats(true, false, false, true);
                                errorsBag.Add($"[Coordinate Mismatch] {episodeLogName} (Plex: S{item.ParentIndex.Value:D2}E{item.Index.Value:D2}, Shoko: {coordsText})");
                                return;
                            }
                        }

                        // Find a local episode thumbnail alongside the physical video files
                        var localThumb = (episode.Videos ?? [])
                            .SelectMany(video => video.Files ?? [])
                            .Select(file => file.Path)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .Select(path => (Dir: Path.GetDirectoryName(path), Base: Path.GetFileNameWithoutExtension(path)))
                            .Where(entry => !string.IsNullOrEmpty(entry.Dir))
                            .SelectMany(entry =>
                                dirArtCache
                                    .GetOrAdd(entry.Dir!, dir => new Lazy<string[]>(() => fileSystem.GetFilePaths(dir, s_artworkListingOptions, cancellationToken)))
                                    .Value.Where(file => string.Equals(Path.GetFileNameWithoutExtension(file), entry.Base, StringComparison.OrdinalIgnoreCase))
                            )
                            .FirstOrDefault();

                        var (fileExists, wasUploaded, wasSkipped, hasError) = await ProcessLocalAssetAsync(
                                localThumb,
                                episode,
                                ImageEntityType.Backdrop,
                                "local thumbnail",
                                episodeLogName,
                                $"[Local {labelType} Thumb] {episodeLogName}",
                                uploadedBag,
                                errorsBag
                            )
                            .ConfigureAwait(false);
                        addStats(fileExists, wasUploaded, wasSkipped, hasError);

                        bool purgePlexThumb = fileExists || Settings.TmdbThumbnails;
                        var plexBackdropFilter = new ImageCrossReferenceFilteringOptions
                        {
                            ImageType = ImageEntityType.Backdrop,
                            XrefSource = ServiceRegistration.RelayPlexSource,
                            LinkedEntityImages = false,
                        };
                        if (purgePlexThumb)
                        {
                            if (episode.GetImageCrossReferences(plexBackdropFilter).Count > 0)
                            {
                                logger.LogInformation("ImageSyncService: {Reason} ... Purging Plex thumbnail for -> {LogName}", fileExists ? "Local thumbnail found" : "TMDB Thumbnails enabled", episodeLogName);
                                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(item.Thumb))
                            {
                                addStats(true, false, false, true);
                                errorsBag.Add($"[Missing Plex Thumbnail] {episodeLogName} (No thumbnail generated or available in Plex)");
                                if (episode.GetImageCrossReferences(plexBackdropFilter).Count > 0)
                                    await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                            }
                            else
                            {
                                var (plexHandled, plexUploaded, plexSkipped, plexError) = await ProcessPlexThumbnailAsync(item.Thumb, episode, episodeLogName, target, errorsBag, uploadedBag, itemToken)
                                    .ConfigureAwait(false);
                                addStats(plexHandled, plexUploaded, plexSkipped, plexError);
                            }
                        }
                    }
                    finally
                    {
                        itemProgress.Increment();
                    }
                }

                await Parallel.ForEachAsync(items, DefaultParallelOptions(cancellationToken), ProcessThumbnailItem).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sectionScanFailed = true;
                addStats(true, false, false, true);
                errorsBag.Add($"Failed to scan Plex section {target.SectionId}: {exception.Message}");
                logger.LogWarning(exception, "ImageSyncService: Failed to scan library section {SectionId}", target.SectionId);
            }
            targetStages.NextStage();
        }
        targetStages.Complete();

        // Safely purge orphaned Plex thumbnails for episodes completely removed from Plex
        if (allowedSet == null && !sectionScanFailed)
        {
            var allEpisodeXrefs = imageManager
                .GetAllImageCrossReferences(
                    new ImageCrossReferenceFilteringOptions
                    {
                        ImageType = ImageEntityType.Backdrop,
                        XrefSource = ServiceRegistration.RelayPlexSource,
                        EntityType = MetadataEntityType.Episode,
                    }
                )
                .ToList();

            var orphanedEpisodeIds = allEpisodeXrefs.Select(xref => int.TryParse(xref.EntityID.ID, out int id) ? id : 0).Where(id => id > 0 && !processedInRun.ContainsKey(id)).Distinct().ToList();

            foreach (var episodeId in orphanedEpisodeIds)
            {
                var episode = metadataService.GetShokoEpisodeByID(episodeId);
                if (episode == null)
                    continue;

                string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;
                var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                string coordsText = $"S{coords.Season:D2}E{coords.Episode:D2}";
                var episodeLogName = $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}] - {coordsText}";

                logger.LogDebug("ImageSyncService: Episode thumbnail for -> {LogName} is no longer present in Plex ... Purging from Shoko", episodeLogName);
                await PurgeEntityImagesAsync(episode, ImageEntityType.Backdrop, ServiceRegistration.RelayPlexSource).ConfigureAwait(false);
                addStats(false, false, false, false);
            }
        }
    }

    /// <summary>Scans local collection posters to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="errorsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    private async Task SyncCollectionPostersAsync(
        List<IShokoSeries> allSeries,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool> addStats,
        CancellationToken cancellationToken,
        IProgress<decimal>? progress = null
    )
    {
        var groups = allSeries.Where(series => series.TopLevelGroupID > 0).Select(series => series.TopLevelGroup).OfType<IShokoGroup>().DistinctBy(group => group.LocalID).ToList();
        var itemProgress = progress.Throttle().ForItems(groups.Count);
        itemProgress.Report(0);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var groupSeries = group.AllSeries?.ToList() is { Count: > 0 } allGroupSeries ? allGroupSeries : [.. allSeries.Where(series => series.TopLevelGroupID == group.LocalID)];
                if (groupSeries.Count == 0)
                    continue;

                string? groupPosterFile = groupSeries
                    .Select(series => PlexHelper.FindCollectionImagePathByGroup(series, group.LocalID, "", metadataService, fileSystem))
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path));
                var (fileExists, wasUploaded, wasSkipped, hasError) = await ProcessLocalAssetAsync(
                        groupPosterFile,
                        group,
                        ImageEntityType.Primary,
                        "collection poster",
                        $"group {group.PreferredTitle?.Value} [{group.LocalID}]",
                        $"[Collection Poster] {group.PreferredTitle?.Value}",
                        uploadedBag,
                        errorsBag
                    )
                    .ConfigureAwait(false);

                addStats(fileExists, wasUploaded, wasSkipped, hasError);
            }
            finally
            {
                itemProgress.Increment();
            }
        }
    }

    /// <summary>Scans local series artwork (posters, backdrops, logos) to upload and mark them as preferred in Shoko.</summary>
    /// <param name="allSeries">List of all Shoko series metadata.</param>
    /// <param name="errorsBag">Bag to collect error messages.</param>
    /// <param name="uploadedBag">Bag to collect uploaded item names.</param>
    /// <param name="addStats">Action callback to record execution metrics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    private async Task SyncLocalSeriesImagesAsync(
        List<IShokoSeries> allSeries,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        Action<bool, bool, bool, bool> addStats,
        CancellationToken cancellationToken,
        IProgress<decimal>? progress = null
    )
    {
        var itemProgress = progress.Throttle().ForItems(allSeries.Count);
        itemProgress.Report(0);

        await Parallel
            .ForEachAsync(allSeries, DefaultParallelOptions(cancellationToken), async (series, token) =>
            {
                try
                {
                    if (OverrideHelper.GetPrimary(series.LocalID, metadataService) != series.LocalID)
                    {
                        foreach (var config in s_seriesImageConfigs)
                            if (
                                series
                                    .GetImageCrossReferences(
                                        new ImageCrossReferenceFilteringOptions
                                        {
                                            ImageType = config.Type,
                                            XrefSource = ServiceRegistration.RelaySource,
                                            LinkedEntityImages = false,
                                        }
                                    )
                                    .Count > 0
                            )
                            {
                                await PurgeEntityImagesAsync(series, config.Type, ServiceRegistration.RelaySource).ConfigureAwait(false);
                                addStats(false, false, false, false);
                            }
                        return;
                    }

                    var candidateFiles = VfsShared.ResolveSeriesVfsPaths(series, metadataService).SelectMany(path => fileSystem.GetFilePaths(path, s_artworkListingOptions, token)).ToList();

                    foreach (var config in s_seriesImageConfigs)
                    {
                        // Find a local artwork file for a series based on a prioritized list of allowed filenames
                        string? foundFile = candidateFiles
                            .Select(file => new { File = file, Index = Array.FindIndex(config.Names, name => string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase)) })
                            .Where(item => item.Index >= 0)
                            .OrderBy(item => item.Index)
                            .FirstOrDefault()
                            ?.File;

                        var (fileExists, wasUploaded, wasSkipped, hasError) = await ProcessLocalAssetAsync(
                                foundFile,
                                series,
                                config.Type,
                                config.Label,
                                $"series {series.GetDisplayTitle()} [{series.LocalID}]",
                                $"[Local {config.Label}] {series.GetDisplayTitle()}",
                                uploadedBag,
                                errorsBag
                            )
                            .ConfigureAwait(false);

                        addStats(fileExists, wasUploaded, wasSkipped, hasError);
                    }
                }
                finally
                {
                    itemProgress.Increment();
                }
            })
            .ConfigureAwait(false);
    }

    #endregion

    #region Core Processing Logic

    /// <summary>Universal method for caching, purging, and uploading local image assets.</summary>
    /// <param name="foundFile">The candidate local file path, or <c>null</c> if none was found.</param>
    /// <param name="entity">The Shoko metadata entity receiving the image.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="label">Human-readable asset label for log messages.</param>
    /// <param name="entityName">Human-readable entity label for log messages.</param>
    /// <param name="uploadDetail">Optional detail label recorded on successful upload.</param>
    /// <param name="uploadedBag">Thread-safe collection for recording uploaded asset details.</param>
    /// <param name="errorsBag">Thread-safe collection for recording upload errors.</param>
    /// <returns>A tuple indicating whether the file exists, was uploaded, was skipped, or errored.</returns>
    private async Task<(bool FileExists, bool Uploaded, bool Skipped, bool Error)> ProcessLocalAssetAsync(
        string? foundFile,
        IWithImages entity,
        ImageEntityType imageType,
        string label,
        string entityName,
        string? uploadDetail,
        ConcurrentBag<string> uploadedBag,
        ConcurrentBag<string> errorsBag
    )
    {
        // Resolve a file's physical target (bypassing symlinks)
        bool exists = false;
        if (!string.IsNullOrEmpty(foundFile))
            try
            {
                if (fileSystem.GetFileStat(foundFile) is not null)
                {
                    foundFile = fileSystem.ResolveSymbolicLink(foundFile) ?? foundFile;
                    exists = true;
                }
            }
            catch { }

        var existingXrefs = entity
            .GetImageCrossReferences(
                new ImageCrossReferenceFilteringOptions
                {
                    ImageType = imageType,
                    XrefSource = ServiceRegistration.RelaySource,
                    LinkedEntityImages = false,
                }
            )
            .ToList();

        if (!exists)
        {
            if (existingXrefs.Count > 0)
            {
                logger.LogDebug("ImageSyncService: Local {Label} for -> {Entity} no longer present on disk ... Purging from Shoko", label, entityName);
                await PurgeEntityImagesAsync(entity, imageType, ServiceRegistration.RelaySource).ConfigureAwait(false);
            }
            return (false, false, false, false);
        }

        string md5;
        using (var fileStream = fileSystem.OpenFile(foundFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            md5 = Convert.ToHexString(MD5.HashData(fileStream));

        var matchingXref = existingXrefs.FirstOrDefault(xref => string.Equals(xref.GetImage()?.ResourceID, md5, StringComparison.OrdinalIgnoreCase));
        if (matchingXref != null)
            return (true, false, true, false); // Skipped: We already own this exact image for this entity. Shoko handles the IsPreferred exclusivity natively.

        logger.LogDebug("ImageSyncService: Local {Label} changed or new for -> {Entity} ... Uploading", label, entityName);

        try
        {
            await _imageWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await PurgeEntityImagesUnlockedAsync(entity, imageType, xref => xref.Source == ServiceRegistration.RelaySource).ConfigureAwait(false);

                // Upload a local file from disk to Shoko and establish explicit ownership using the RelaySource cross-reference
                using var stream = fileSystem.OpenFile(foundFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                UploadAndPreferImage(stream, entity, imageType, ServiceRegistration.RelaySource);
            }
            finally
            {
                _imageWriteLock.Release();
            }

            if (uploadDetail != null)
            {
                uploadedBag.Add(uploadDetail);
                logger.LogDebug("ImageSyncService: Successfully uploaded and preferred {Label} for -> {Entity}", label, entityName);
            }
            return (true, true, false, false);
        }
        catch (Exception exception)
        {
            errorsBag.Add($"Failed to process {label} for -> {entityName}: {exception.Message}");
            logger.LogWarning(exception, "ImageSyncService: Failed to upload {Label} for -> {Entity}", label, entityName);
            return (true, false, false, true);
        }
    }

    /// <summary>Downloads and processes Plex-generated thumbnails.</summary>
    /// <param name="thumbUrl">The Plex-relative thumbnail path to download.</param>
    /// <param name="episode">The Shoko episode receiving the thumbnail.</param>
    /// <param name="episodeLogName">Human-readable episode label for log messages.</param>
    /// <param name="target">The source Plex library target.</param>
    /// <param name="errorsBag">Thread-safe collection for recording download or upload errors.</param>
    /// <param name="uploadedBag">Thread-safe collection for recording uploaded thumbnail details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating whether the thumbnail was handled, uploaded, skipped, or errored.</returns>
    private async Task<(bool Handled, bool Uploaded, bool Skipped, bool Error)> ProcessPlexThumbnailAsync(
        string thumbUrl,
        IShokoEpisode episode,
        string episodeLogName,
        PlexLibraryTarget target,
        ConcurrentBag<string> errorsBag,
        ConcurrentBag<string> uploadedBag,
        CancellationToken cancellationToken
    )
    {
        var existingXrefs = episode
            .GetImageCrossReferences(
                new ImageCrossReferenceFilteringOptions
                {
                    ImageType = ImageEntityType.Backdrop,
                    XrefSource = ServiceRegistration.RelayPlexSource,
                    LinkedEntityImages = false,
                }
            )
            .ToList();

        if (existingXrefs.Count > 0)
            return (true, false, true, false); // Skipped: We already own a downloaded Plex thumbnail for this episode.

        logger.LogTrace("ImageSyncService: Fetching Plex thumbnail for episode -> {LogName}", episodeLogName);
        try
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = plexClient.CreateRequest(HttpMethod.Get, thumbUrl, target.ServerUrl);
            using var response = await plexClient.SendAsync(request, requestCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                errorsBag.Add($"[Failed Plex Download] {episodeLogName} (HTTP {response.StatusCode})");
                return (true, false, false, true);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(requestCts.Token).ConfigureAwait(false);

            // Stream through imageManager.UploadImage to guarantee cross-reference creation for both new and existing images
            await _imageWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var stream = new MemoryStream(bytes);
                UploadAndPreferImage(stream, episode, ImageEntityType.Backdrop, ServiceRegistration.RelayPlexSource);
            }
            finally
            {
                _imageWriteLock.Release();
            }

            uploadedBag.Add($"[Plex Thumb] {episodeLogName}");
            logger.LogDebug("ImageSyncService: Successfully uploaded and preferred thumbnail for episode -> {LogName}", episodeLogName);
            return (true, true, false, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            errorsBag.Add($"[Plex Thumbnail Timeout] {episodeLogName} (Timed out after 15s)");
            logger.LogWarning("ImageSyncService: Timed out fetching Plex thumbnail for {LogName}", episodeLogName);
            return (true, false, false, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errorsBag.Add($"[Plex Thumbnail Exception] {episodeLogName}: {exception.Message}");
            logger.LogWarning(exception, "ImageSyncService: Failed to process Plex thumbnail for {LogName}", episodeLogName);
            return (true, false, false, true);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Uploads an image stream to Shoko under the specified source, links it to the entity, and marks it as preferred.</summary>
    /// <param name="stream">Readable image content stream.</param>
    /// <param name="entity">Target Shoko entity.</param>
    /// <param name="imageType">Target image entity type.</param>
    /// <param name="source">Owning metadata source.</param>
    private void UploadAndPreferImage(Stream stream, IWithImages entity, ImageEntityType imageType, MetadataSource source)
    {
        var uploadedImage = imageManager.UploadImage(stream, null, source);
        var addedXref = imageManager.AddImageCrossReference(
            entity,
            uploadedImage,
            new ImageCrossReferenceData
            {
                ImageType = imageType,
                Source = source,
                IsPreferred = true,
                IsDesired = true,
            }
        );
        imageManager.SetPreferredImageForEntity(addedXref);
    }

    /// <summary>Purges stale or demoted cross-referenced images for an entity for a specific metadata source.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="xrefSource">The cross-reference metadata source to purge.</param>
    /// <returns>A task representing the asynchronous purge operation.</returns>
    private Task PurgeEntityImagesAsync(IWithImages entity, ImageEntityType imageType, MetadataSource xrefSource) => PurgeEntityImagesAsync(entity, imageType, xref => xref.Source == xrefSource, xrefSource);

    /// <summary>Purges stale or demoted cross-referenced images for an entity based on a matching predicate.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="predicate">Filter predicate to select cross-references for purging.</param>
    /// <param name="xrefSource">Optional cross-reference metadata source to filter on at the database level.</param>
    /// <returns>A task representing the asynchronous purge operation.</returns>
    private async Task PurgeEntityImagesAsync(IWithImages entity, ImageEntityType imageType, Func<IImageCrossReference, bool> predicate, MetadataSource? xrefSource = null)
    {
        await _imageWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await PurgeEntityImagesUnlockedAsync(entity, imageType, predicate, xrefSource).ConfigureAwait(false);
        }
        finally
        {
            _imageWriteLock.Release();
        }
    }

    /// <summary>Purges stale or demoted cross-referenced images for an entity without acquiring <see cref="_imageWriteLock"/>.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="imageType">The target image entity type.</param>
    /// <param name="predicate">Filter predicate to select cross-references for purging.</param>
    /// <param name="xrefSource">Optional cross-reference metadata source to filter on at the database level.</param>
    /// <returns>A task representing the asynchronous purge operation.</returns>
    private async Task PurgeEntityImagesUnlockedAsync(IWithImages entity, ImageEntityType imageType, Func<IImageCrossReference, bool> predicate, MetadataSource? xrefSource = null)
    {
        try
        {
            var filterOptions = new ImageCrossReferenceFilteringOptions
            {
                ImageType = imageType,
                XrefSource = xrefSource,
                LinkedEntityImages = false,
            };
            foreach (var xref in entity.GetImageCrossReferences(filterOptions).Where(predicate))
            {
                imageManager.RemoveImageCrossReference(xref);
                // Only purge the underlying image if no other entities are actively referencing it
                if (imageManager.GetImageByID(xref.ImageID) is { } oldImage && oldImage.GetCrossReferences().Count == 0)
                    await imageManager.PurgeImage(oldImage).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "ImageSyncService: Failed to purge stale images for entity of type {Type}", entity.GetType().Name);
        }
    }

    #endregion
}
