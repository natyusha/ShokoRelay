using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;

namespace ShokoRelay.Vfs;

/// <summary>Watches for Shoko video-file events and triggers incremental VFS rebuilds plus debounced Plex refreshes.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="releaseService">Shoko video release service.</param>
/// <param name="builder">VFS builder.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="plexLibrary">Plex client.</param>
/// <param name="collectionService">Plex collection service.</param>
/// <param name="atMapping">AnimeThemes mapping service.</param>
/// <param name="criticRatingService">Plex critic rating service.</param>
/// <param name="imageSyncService">Shoko image sync service.</param>
/// <param name="logger">Logger instance.</param>
public class VfsWatcher(
    IVideoService videoService,
    IVideoReleaseService releaseService,
    VfsBuilder builder,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    ICollectionService collectionService,
    AnimeThemesMapping atMapping,
    ICriticRatingService criticRatingService,
    IImageSyncService imageSyncService,
    ILogger<VfsWatcher> logger
)
{
    #region Setup

    /// <summary>Tracks series IDs pending VFS rebuild.</summary>
    private readonly ConcurrentDictionary<int, byte> _pending = new();

    /// <summary>Tracks cancellation tokens for pending debounced metadata fixups.</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingMetadataFixups = new();

    /// <summary>Tracks cancellation tokens for pending debounced library scans.</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingLibraryScans = new();

    /// <summary>Indicates if the background processing loop is currently active.</summary>
    private bool _processing;

    /// <summary>Lock object for synchronizing the processing loop state.</summary>
    private readonly Lock _gate = new();

    #endregion

    #region Lifecycle Management

    /// <summary>Subscribe to Shoko video-file events and begin watching for changes.</summary>
    public void Start()
    {
        videoService.VideoFileRelocated += OnVideoFileRelocated;
        videoService.VideoFileDeleted += OnVideoFileDeleted;
        releaseService.ReleaseSaved += OnVideoReleaseSaved;

        logger.LogInformation("VFS: VfsWatcher -> Started (listening for relocation, matching and deletion events)");
    }

    /// <summary>Unsubscribe from Shoko video-file events and stop watching.</summary>
    public void Stop()
    {
        try
        {
            videoService.VideoFileRelocated -= OnVideoFileRelocated;
            videoService.VideoFileDeleted -= OnVideoFileDeleted;
            releaseService.ReleaseSaved -= OnVideoReleaseSaved;

            foreach (var cts in _pendingMetadataFixups.Values)
                cts.Cancel();
            foreach (var cts in _pendingLibraryScans.Values)
                cts.Cancel();
        }
        catch { }

        logger.LogInformation("VFS: VfsWatcher -> Stopped");
    }

    #endregion

    #region Event Handlers

    /// <summary>Handles Shoko video file relocation and rename events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing file information.</param>
    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e)
    {
        logger.LogDebug("VFS: File relocated/renamed -> {FileName}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko video file deletion events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing file information.</param>
    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs e)
    {
        logger.LogDebug("VFS: File deleted -> {FileName}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko release matching events, queueing affected series for VFS updates when a video is assigned.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing release associations.</param>
    private void OnVideoReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        if (e.Video?.Series == null || e.Video.Series.Count == 0)
            return;
        logger.LogDebug("VFS: Release saved for video '{FileName}'", Path.GetFileName(e.Video.EarliestKnownName ?? "Unknown File"));

        foreach (var series in e.Video.Series)
        {
            int primaryId = series.GetPrimaryId(metadataService);
            logger.LogDebug("VFS: Adding series -> {Title} [{LocalId}] (Primary: {PrimaryId}) to pending queue due to release save", series.GetDisplayTitle(), series.LocalID, primaryId);
            _pending[primaryId] = 1;
        }

        KickProcessLoop();
    }

    /// <summary>Aggregates multiple video file events into the pending processing queue.</summary>
    /// <param name="e">The video file event arguments.</param>
    private void HandleFileEvent(VideoFileEventArgs? e)
    {
        var seriesList = e?.Series ?? e?.Video?.Series;
        if (seriesList == null || !seriesList.Any())
            return;

        foreach (var series in seriesList)
        {
            int primaryId = series.GetPrimaryId(metadataService);
            _pending[primaryId] = 1;
        }
        KickProcessLoop();
    }

    #endregion

    #region Processing Logic

    /// <summary>Locks and starts the background task loop to process pending series queue updates.</summary>
    private void KickProcessLoop()
    {
        lock (_gate)
        {
            if (_processing)
                return;
            _processing = true;
            Task.Run(ProcessQueueAsync);
        }
    }

    /// <summary>Asynchronously processes queued series, re-generating VFS structures and scheduling Plex notifications.</summary>
    /// <returns>A task representing the queue processing operation.</returns>
    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            var seriesIds = new List<int>();

            // Iteratively extract pending items without clearing the dictionary blindly. This prevents losing events that are added simultaneously by other threads.
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out _))
                    seriesIds.Add(key);
            }

            if (seriesIds.Count == 0)
            {
                lock (_gate)
                {
                    if (_pending.IsEmpty)
                    {
                        _processing = false;
                        return;
                    }
                }
                continue;
            }

            try
            {
                if (Settings.Advanced.DeferVfsCreationUntilFixup)
                {
                    logger.LogInformation("VFS: Deferring VFS creation for {Count} series until fixup ({Delay}m delay)", seriesIds.Count, Settings.Advanced.PlexFixupDelay);
                    foreach (var seriesId in seriesIds)
                        TriggerPlexUpdates(seriesId, deferScan: true);
                }
                else
                {
                    await VfsShared.VfsLock.WaitAsync().ConfigureAwait(false); // Wait for any active dashboard VFS operations to complete before processing the automated queue
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var result = builder.Build(seriesIds, cleanRoot: false);

                        // Restore AnimeThemes links for the affected series if a mapping file exists
                        if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                            await atMapping.ApplyMappingAsync(seriesIds, CancellationToken.None).ConfigureAwait(false);

                        sw.Stop();
                        logger.LogInformation(
                            "VFS: Batch refreshed for {Count} series in {Elapsed}ms -> created={Created} planned={Planned} skipped={Skipped} seriesProcessed={Processed} errors={Errors}",
                            seriesIds.Count,
                            sw.ElapsedMilliseconds,
                            result.CreatedLinks,
                            result.PlannedLinks,
                            result.Skipped,
                            result.SeriesProcessed,
                            result.Errors?.Count ?? 0
                        );

                        foreach (var seriesId in seriesIds)
                            TriggerPlexUpdates(seriesId);
                    }
                    finally
                    {
                        VfsShared.VfsLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VFS: Batch refresh failed");
            }

            await Task.Delay(400).ConfigureAwait(false);
        }
    }

    #endregion

    #region Plex Update Logic

    /// <summary>Orchestrates debounced library scans, metadata refreshes, and collection updates for a recently modified series.</summary>
    /// <param name="seriesId">The Shoko Series ID to update.</param>
    /// <param name="deferScan">Whether to suppress the immediate library scan and only run the deferred fixup.</param>
    public void TriggerPlexUpdates(int seriesId, bool deferScan = false)
    {
        int primaryId = OverrideHelper.GetPrimary(seriesId, metadataService);
        var series = metadataService.GetShokoSeriesByID(primaryId);
        if (series == null)
            return;

        // If the series has no valid VFS paths (e.g., all files reside in excluded folders), bypass Plex updates entirely.
        if (!VfsShared.ResolveSeriesVfsPaths(series, metadataService).Any())
        {
            logger.LogDebug("VFS: Skipping Plex updates for series -> {Title} [{LocalId}] ... No valid VFS paths found (series may be fully excluded or empty)", series.GetDisplayTitle(), series.LocalID);
            return;
        }

        if (!deferScan && plexLibrary.IsEnabled)
            ScheduleLibraryScan(series);

        // Schedules or resets the timer for deferred VFS creation or Plex metadata fixup
        if (plexLibrary.IsEnabled || Settings.Advanced.DeferVfsCreationUntilFixup)
        {
            string actionName = Settings.Advanced.DeferVfsCreationUntilFixup ? "deferred VFS creation" : "metadata fixup";
            logger.LogDebug("VFS: Scheduling {Action} for series -> {Title} [{LocalId}] in {Delay} minute(s)", actionName, series.GetDisplayTitle(), series.LocalID, Settings.Advanced.PlexFixupDelay);
            ScheduleDebouncedAction(series.LocalID, Settings.Advanced.PlexFixupDelay * 60, _pendingMetadataFixups, token => RunMetadataFixupAsync(series, token));
        }
    }

    /// <summary>Generic debouncer wrapper to handle delaying tasks and managing cancellations efficiently.</summary>
    /// <param name="seriesId">The ID of the series being processed.</param>
    /// <param name="delaySeconds">The delay in seconds before executing the action.</param>
    /// <param name="tracker">The dictionary tracking cancellation tokens for pending actions.</param>
    /// <param name="action">The asynchronous action to execute after the delay.</param>
    private void ScheduleDebouncedAction(int seriesId, int delaySeconds, ConcurrentDictionary<int, CancellationTokenSource> tracker, Func<CancellationToken, Task> action)
    {
        if (tracker.TryRemove(seriesId, out var oldCts))
            oldCts.Cancel();

        var cts = new CancellationTokenSource();
        tracker[seriesId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);
                await VfsShared.VfsLock.WaitAsync(cts.Token).ConfigureAwait(false); // Acquire lock to prevent Plex update during VFS build.
                try
                {
                    // If the series is currently sitting in the build queue, skip the individual update to avoid redundant API calls.
                    if (_pending.ContainsKey(seriesId))
                        return;
                    await action(cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    VfsShared.VfsLock.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                var title = metadataService.GetShokoSeriesByID(seriesId)?.GetDisplayTitle() ?? "Series";
                logger.LogError(ex, "VFS: Scheduled action failed for series -> {Title} [{SeriesId}]", title, seriesId);
            }
            finally
            {
                tracker.TryRemove(new KeyValuePair<int, CancellationTokenSource>(seriesId, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>Schedules or resets the timer for a partial Plex library scan for the given series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    private void ScheduleLibraryScan(IShokoSeries series)
    {
        if (!plexLibrary.ScanOnVfsRefresh)
            return;

        ScheduleDebouncedAction(
            series.LocalID,
            Settings.Advanced.PlexScanDelay,
            _pendingLibraryScans,
            async token =>
            {
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
                {
                    if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
                    else
                        logger.LogDebug("VFS: Library scan for series -> {Title} [{LocalId}] skipped; path '{Path}' not ready or empty", series.GetDisplayTitle(), series.LocalID, path);
                }
            }
        );
    }

    /// <summary>Worker task that performs the actual metadata fixup logic, critic rating application, and optional image synchronization after the debounce delay has settled.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the fixup operation.</returns>
    private async Task RunMetadataFixupAsync(IShokoSeries series, CancellationToken token)
    {
        try
        {
            bool isDeferred = Settings.Advanced.DeferVfsCreationUntilFixup;

            // Regenerate the VFS to account for cases where the episode/season numbering was updated in Shoko after the initial file event was processed
            var vfsResult = builder.Build(series.LocalID, cleanRoot: false);
            if (vfsResult.CreatedLinks > 0)
                logger.LogInformation("VFS: {Action} links -> {Title} [{LocalId}] during fixup phase", isDeferred ? "Created" : "Re-generated", series.GetDisplayTitle(), series.LocalID);

            // Restore AnimeThemes links for this specific series if a mapping file exists to prevent the pruned folder from losing them
            if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                await atMapping.ApplyMappingAsync([series.LocalID], token).ConfigureAwait(false);

            // If Plex is not linked exit since VFS creation is complete and the Plex APIs called below cannot be used
            if (!plexLibrary.IsEnabled)
                return;

            // Wait to allow the filesystem to settle, or for Plex's native auto-scanner to index the newly generated VFS symlinks
            await Task.Delay(TimeSpan.FromSeconds(Settings.Advanced.PlexScanDelay), token).ConfigureAwait(false);

            // If partial scans are enabled, trigger them now: In deferred mode this serves as the primary scan / In standard mode it acts as a fallback for the initial scan
            var vfsPaths = VfsShared.ResolveSeriesVfsPaths(series, metadataService).ToList();
            if (plexLibrary.ScanOnVfsRefresh)
            {
                foreach (var path in vfsPaths)
                    await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
            }

            var targets = plexLibrary.GetConfiguredTargets();
            bool foundInAnyTarget = false;

            // Pre-filter targets so we only poll libraries that physically contain the VFS paths for this series, eliminating timeouts on irrelevant libraries
            var matchingTargetIds = vfsPaths.SelectMany(plexLibrary.GetMatchingTargetsForPath).Select(x => x.Target.SectionId).ToHashSet();

            // Wait for Plex to assign rating keys (primarily for users in deferred mode)
            int retryDelaySeconds = Settings.Advanced.PlexScanDelay;
            int maxRetries = isDeferred ? Math.Max(1, 180 / retryDelaySeconds) : 2; // ~3 minutes for deferred, ~2 ticks for standard

            foreach (var target in targets)
            {
                if (!matchingTargetIds.Contains(target.SectionId))
                    continue;

                var ratingKeys = new List<int>();
                for (int i = 0; i < maxRetries; i++)
                {
                    ratingKeys = await plexLibrary.FindRatingKeysForShokoSeriesInSectionAsync(series.LocalID, target, metadataService, token).ConfigureAwait(false);
                    if (ratingKeys.Count > 0)
                        break;

                    if (i < maxRetries - 1)
                    {
                        if (i == 0 && isDeferred)
                            logger.LogDebug(
                                "VFS: Waiting for Plex to index series -> {Title} [{LocalId}] in library '{Library}' on {Server}...",
                                series.GetDisplayTitle(),
                                series.LocalID,
                                target.Title,
                                target.ServerName
                            );
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), token).ConfigureAwait(false);
                    }
                }

                foreach (var ratingKey in ratingKeys)
                {
                    foundInAnyTarget = true;
                    if (!isDeferred || plexLibrary.ScanOnVfsRefresh)
                    {
                        // Shoko may have been missing data (e.g. TMDB IDs) when the VFS was instantly generated necessitating a forced metadata refresh
                        logger.LogInformation(
                            "VFS: Triggering debounced metadata fixup and analysis for series -> {Title} [{LocalId}] (RatingKey: {RatingKey}) in library '{Library}' on {Server}",
                            series.GetDisplayTitle(),
                            series.LocalID,
                            ratingKey,
                            target.Title,
                            target.ServerName
                        );
                        await plexLibrary.RefreshMetadataAsync(ratingKey, target, token).ConfigureAwait(false);
                        await plexLibrary.AnalyzeItemAsync(ratingKey, target, token).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogInformation(
                            "VFS: Series -> {Title} [{LocalId}] (RatingKey: {RatingKey}) successfully indexed by Plex in library '{Library}' on {Server}",
                            series.GetDisplayTitle(),
                            series.LocalID,
                            ratingKey,
                            target.Title,
                            target.ServerName
                        );
                    }
                }
            }

            if (!foundInAnyTarget)
            {
                if (isDeferred)
                    logger.LogWarning("VFS: Automations for series -> {Title} [{LocalId}] skipped; rating key not found in Plex after timeout", series.GetDisplayTitle(), series.LocalID);
                else
                    logger.LogDebug("VFS: Debounced metadata fixup for series -> {Title} [{LocalId}] skipped; rating key not found in Plex yet", series.GetDisplayTitle(), series.LocalID);
            }
            else
            {
                // Execute subsequent API actions sequentially to guarantee metadata framework exists
                logger.LogInformation("VFS: Triggering debounced collection update for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                await collectionService.BuildCollectionsAsync([series], clean: false, cancellationToken: token).ConfigureAwait(false);

                logger.LogInformation("VFS: Triggering debounced critic rating application for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                await criticRatingService.ApplyRatingsAsync([series.LocalID], token).ConfigureAwait(false);

                if (Settings.Advanced.EnableImageSync)
                {
                    // Give Plex's background workers a moment to extract the episode thumbnail before attempting to sync it
                    if (isDeferred && !Settings.TmdbThumbnails)
                    {
                        logger.LogDebug("VFS: Pausing briefly to allow Plex thumbnail extraction for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), token).ConfigureAwait(false);
                    }

                    logger.LogInformation("VFS: Triggering debounced image sync for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                    await imageSyncService.SyncImagesAsync([series.LocalID], token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "VFS: Metadata fixup failed for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
        }
    }

    #endregion
}
