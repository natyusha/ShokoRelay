using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;

namespace ShokoRelay.Vfs;

/// <summary>Watches for Shoko video-file events and triggers incremental VFS rebuilds plus debounced Plex refreshes.</summary>
public class VfsWatcher(
    IVideoService videoService,
    IVideoReleaseService releaseService,
    VfsBuilder builder,
    IMetadataService metadataService,
    PlexMetadata plexMetadata,
    PlexClient plexLibrary,
    PlexCollections plexCollections,
    AnimeThemesMapping atMapping,
    ICriticRatingService criticRatingService,
    IImageSyncService imageSyncService
)
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<int, byte> _pending = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingMetadataFixups = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingLibraryScans = new();
    private bool _processing;
    private readonly Lock _gate = new();

    #endregion

    #region Lifecycle Management

    /// <summary>Subscribe to Shoko video-file events and begin watching for changes.</summary>
    public void Start()
    {
        videoService.VideoFileRelocated += OnVideoFileRelocated;
        videoService.VideoFileDeleted += OnVideoFileDeleted;
        releaseService.ReleaseSaved += OnVideoReleaseSaved;

        s_logger.Info("VFS: VfsWatcher -> Started (listening for relocation, matching and deletion events)");
    }

    /// <summary>Unsubscribe from Shoko video-file events and stop watching.</summary>
    public void Stop()
    {
        try
        {
            videoService.VideoFileRelocated -= OnVideoFileRelocated;
            videoService.VideoFileDeleted -= OnVideoFileDeleted;
            releaseService.ReleaseSaved -= OnVideoReleaseSaved;

            foreach (var kvp in _pendingMetadataFixups)
                kvp.Value.Cancel();

            foreach (var kvp in _pendingLibraryScans)
                kvp.Value.Cancel();
        }
        catch { }

        s_logger.Info("VFS: VfsWatcher -> Stopped");
    }

    #endregion

    #region Event Handlers

    /// <summary>Handles Shoko video file relocation and rename events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing file information.</param>
    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e)
    {
        s_logger.Info("VFS: File relocated/renamed -> {0}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko video file deletion events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing file information.</param>
    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs e)
    {
        s_logger.Info("VFS: File deleted -> {0}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko release matching events, queueing affected series for VFS updates when a video is assigned.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing release associations.</param>
    private void OnVideoReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        if (e.Video?.Series == null || e.Video.Series.Count == 0)
            return;
        s_logger.Info("VFS: Release saved for video '{0}'", Path.GetFileName(e.Video.EarliestKnownName ?? "Unknown File"));

        foreach (var series in e.Video.Series)
        {
            s_logger.Debug("VFS: Adding series -> {0} [{1}] to pending queue due to release save", series.GetDisplayTitle(), series.ID);
            _pending[series.ID] = 1;
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
            _pending[series.ID] = 1;
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
            List<int> seriesIds;
            lock (_gate)
            {
                if (_pending.IsEmpty)
                {
                    _processing = false;
                    return;
                }
                seriesIds = [.. _pending.Keys];
                _pending.Clear();
            }

            try
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
                    s_logger.Info(
                        "VFS: batch refreshed for {0} series in {1}ms -> created={2} planned={3} skipped={4} seriesProcessed={5} errors={6}",
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
            catch (Exception ex)
            {
                s_logger.Warn(ex, "VFS: Batch refresh failed");
            }

            await Task.Delay(400).ConfigureAwait(false);
        }
    }

    #endregion

    #region Plex Update Logic

    /// <summary>Orchestrates debounced library scans, metadata refreshes, and collection updates for a recently modified series.</summary>
    /// <param name="seriesId">The Shoko Series ID to update.</param>
    public void TriggerPlexUpdates(int seriesId)
    {
        if (!plexLibrary.IsEnabled)
            return;
        var series = metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return;

        // If the series has no valid VFS paths (e.g., all files reside in excluded folders), bypass Plex updates entirely.
        if (!VfsShared.ResolveSeriesVfsPaths(series, metadataService).Any())
        {
            s_logger.Debug("VFS: Skipping Plex updates for series -> {0} [{1}] ... No valid VFS paths found (series may be fully excluded or empty)", series.GetDisplayTitle(), series.ID);
            return;
        }

        ScheduleLibraryScan(series);
        ScheduleMetadataFixup(series);
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
                s_logger.Error(ex, "VFS: Scheduled action failed for series [{0}]", seriesId);
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
            series.ID,
            Settings.Advanced.PlexScanDelay,
            _pendingLibraryScans,
            async token =>
            {
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
                {
                    if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
                    else
                        s_logger.Debug("VFS: Library scan -> {0} [{1}] skipped; path '{2}' not ready or empty", series.GetDisplayTitle(), series.ID, path);
                }
            }
        );
    }

    /// <summary>Schedules or resets the timer for a full Plex metadata refresh for the given series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    private void ScheduleMetadataFixup(IShokoSeries series)
    {
        s_logger.Debug("VFS: Scheduling metadata fixup -> {0} [{1}] in {2} minute(s)", series.GetDisplayTitle(), series.ID, Settings.Advanced.PlexFixupDelay);
        ScheduleDebouncedAction(series.ID, Settings.Advanced.PlexFixupDelay * 60, _pendingMetadataFixups, token => RunMetadataFixupAsync(series, token));
    }

    /// <summary>Worker task that performs the actual metadata fixup logic, critic rating application, and optional image synchronization after the debounce delay has settled.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the fixup operation.</returns>
    private async Task RunMetadataFixupAsync(IShokoSeries series, CancellationToken token)
    {
        try
        {
            // Regenerate the VFS to account for cases where the episode/season numbering was updated in Shoko after the initial file event was processed
            var vfsResult = builder.Build(series.ID, cleanRoot: false);
            if (vfsResult.CreatedLinks > 0)
                s_logger.Info("VFS: Re-generated links -> {0} [{1}] during fixup phase", series.GetDisplayTitle(), series.ID);

            // Restore AnimeThemes links for this specific series if a mapping file exists to prevent the pruned folder from losing them
            if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                await atMapping.ApplyMappingAsync([series.ID], token).ConfigureAwait(false);

            // Wait to allow the filesystem or Plex's native auto-scanner to index the newly generated VFS symlinks
            int bufferSeconds = Settings.Advanced.PlexScanDelay;
            if (bufferSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(bufferSeconds), token).ConfigureAwait(false);

            // Fallback in case the files were not scanned into Plex by the initial scan
            if (plexLibrary.ScanOnVfsRefresh)
            {
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
                    await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
            }

            var targets = plexLibrary.GetConfiguredTargets();
            bool foundInAnyTarget = false;
            foreach (var target in targets)
            {
                var ratingKey = await plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target, token).ConfigureAwait(false);
                if (ratingKey.HasValue)
                {
                    foundInAnyTarget = true;
                    s_logger.Info("VFS: Triggering debounced metadata fixup and analysis -> {0} [{1}] (RatingKey: {2}) on {3}", series.GetDisplayTitle(), series.ID, ratingKey.Value, target.ServerName);
                    await plexLibrary.RefreshMetadataAsync(ratingKey.Value, target, token).ConfigureAwait(false);
                    await plexLibrary.AnalyzeItemAsync(ratingKey.Value, target, token).ConfigureAwait(false);
                }
            }

            if (!foundInAnyTarget)
                s_logger.Debug("VFS: Debounced fixup -> {0} [{1}] skipped; rating key not found in Plex yet", series.GetDisplayTitle(), series.ID);
            else
            {
                // Execute subsequent API actions sequentially to guarantee metadata framework exists
                await RunCollectionUpdateAsync(series, token).ConfigureAwait(false);

                s_logger.Info("VFS: Triggering debounced critic rating application -> {0} [{1}]", series.GetDisplayTitle(), series.ID);
                await criticRatingService.ApplyRatingsAsync([series.ID], token).ConfigureAwait(false);

                if (Settings.Advanced.EnableImageSync)
                {
                    s_logger.Info("VFS: Triggering debounced image sync -> {0} [{1}]", series.GetDisplayTitle(), series.ID);
                    await imageSyncService.SyncImagesAsync([series.ID], token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            s_logger.Error(ex, "VFS: Metadata fixup failed -> {0} [{1}]", series.GetDisplayTitle(), series.ID);
        }
    }

    /// <summary>Worker task that performs the collection assignment and poster/logo/backdrop upload logic.</summary>
    /// <param name="series">The Shoko series to update.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the update operation.</returns>
    private async Task RunCollectionUpdateAsync(IShokoSeries series, CancellationToken token)
    {
        try
        {
            var collectionName = plexMetadata.GetCollectionName(series);
            if (string.IsNullOrWhiteSpace(collectionName))
                return;

            var targets = plexLibrary.GetConfiguredTargets();
            foreach (var target in targets)
            {
                var ratingKey = await plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target, token).ConfigureAwait(false);
                if (!ratingKey.HasValue)
                    continue;

                if (await plexCollections.AssignCollectionToItemByMetadataAsync(ratingKey.Value, collectionName, target, token).ConfigureAwait(false))
                {
                    var collectionId = await plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, token).ConfigureAwait(false);
                    if (!collectionId.HasValue)
                        continue;

                    var desc = TextHelper.GetDescriptionByLanguage(series, Settings.DescriptionLanguage);
                    var tmdbDesc = series.TmdbShows?.FirstOrDefault()?.PreferredDescription?.Value;
                    var summary = TextHelper.SanitizeSummaryWithFallback(desc, tmdbDesc, Settings.SummaryMode);
                    await plexCollections.UpdateCollectionMetadataAsync(collectionId.Value, collectionName, summary, target, token).ConfigureAwait(false);

                    foreach (var config in PlexConstants.CollectionImageConfigs)
                    {
                        var fallback = config.DefaultFallback && Settings.CollectionImages;
                        var url = PlexHelper.GetCollectionImageUrl(series, collectionName, collectionId.Value, config.Suffix, config.Suffixes, metadataService, fallback);
                        if (!string.IsNullOrEmpty(url))
                            await plexCollections.UploadCollectionImageByUrlAsync(collectionId.Value, url, config.Prefix, target, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "VFS: Collection update failed -> {0} [{1}]", series.GetDisplayTitle(), series.ID);
        }
    }

    #endregion
}
