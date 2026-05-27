using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
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
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService;
    private readonly IVideoReleaseService _releaseService = releaseService;
    private readonly VfsBuilder _builder = builder;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly PlexMetadata _plexMetadata = plexMetadata;
    private readonly PlexClient _plexLibrary = plexLibrary;
    private readonly PlexCollections _plexCollections = plexCollections;
    private readonly AnimeThemesMapping _atMapping = atMapping;
    private readonly ICriticRatingService _criticRatingService = criticRatingService;
    private readonly IImageSyncService _imageSyncService = imageSyncService;

    private readonly ConcurrentDictionary<int, byte> _pending = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingMetadataFixups = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingLibraryScans = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingCollectionUpdates = new();
    private bool _processing;
    private readonly Lock _gate = new();

    #endregion

    #region Lifecycle Management

    /// <summary>Subscribe to Shoko video-file events and begin watching for changes.</summary>
    public void Start()
    {
        _videoService.VideoFileRelocated += OnVideoFileRelocated;
        _videoService.VideoFileDeleted += OnVideoFileDeleted;
        _releaseService.ReleaseSaved += OnVideoReleaseSaved;

        s_logger.Info("VFS: VfsWatcher -> Started (listening for relocation, matching and deletion events)");
    }

    /// <summary>Unsubscribe from Shoko video-file events and stop watching.</summary>
    public void Stop()
    {
        try
        {
            _videoService.VideoFileRelocated -= OnVideoFileRelocated;
            _videoService.VideoFileDeleted -= OnVideoFileDeleted;
            _releaseService.ReleaseSaved -= OnVideoReleaseSaved;

            foreach (var kvp in _pendingMetadataFixups)
                kvp.Value.Cancel();
            foreach (var kvp in _pendingLibraryScans)
                kvp.Value.Cancel();
            foreach (var kvp in _pendingCollectionUpdates)
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
        s_logger.Info("VFS: File relocated/renamed: {0}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko video file deletion events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing file information.</param>
    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs e)
    {
        s_logger.Info("VFS: File deleted: {0}", Path.GetFileName(e.RelativePath));
        HandleFileEvent(e);
    }

    /// <summary>Handles Shoko release matching events, queueing affected series for VFS updates when a video is assigned.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Event parameters containing release associations.</param>
    private void OnVideoReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        // Trigger build when a video is successfully linked to a series (manual or automatic)
        if (e.Video?.Series == null || e.Video.Series.Count == 0)
            return;

        string fileName = e.Video.EarliestKnownName ?? "Unknown File";
        s_logger.Info("VFS: Release saved for video '{0}'", Path.GetFileName(fileName));

        foreach (var series in e.Video.Series)
        {
            s_logger.Debug("VFS: Adding series '{0}' (ID: {1}) to pending queue due to release save", series.PreferredTitle?.Value, series.ID);
            _pending[series.ID] = 1;
        }

        KickProcessLoop();
    }

    /// <summary>Aggregates multiple video file events into the pending processing queue.</summary>
    /// <param name="e">The video file event arguments.</param>
    private void HandleFileEvent(VideoFileEventArgs? e)
    {
        var seriesList = e?.Series?.ToList() ?? e?.Video?.Series?.ToList() ?? [];
        if (seriesList.Count == 0)
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
            Task.Run(ProcessQueue);
        }
    }

    /// <summary>Asynchronously processes queued series, re-generating VFS structures and scheduling Plex notifications.</summary>
    /// <returns>A task representing the queue processing operation.</returns>
    private async Task ProcessQueue()
    {
        try
        {
            while (true)
            {
                List<int> seriesIds;
                lock (_gate)
                {
                    if (_pending.IsEmpty)
                    {
                        _processing = false;
                        break;
                    }
                    seriesIds = [.. _pending.Keys];
                    foreach (var id in seriesIds)
                        _pending.TryRemove(id, out _);
                }

                await VfsShared.VfsLock.WaitAsync().ConfigureAwait(false); // Wait for any active dashboard VFS operations to complete before processing the automated queue
                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = _builder.Build(seriesIds, cleanRoot: false, pruneSeries: true);

                    // Restore AnimeThemes links for the affected series if a mapping file exists
                    if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                        await _atMapping.ApplyMappingAsync(seriesIds, CancellationToken.None).ConfigureAwait(false);

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
                catch (Exception ex)
                {
                    s_logger.Warn(ex, "VFS: Batch refresh failed");
                }
                finally
                {
                    VfsShared.VfsLock.Release();
                }

                await Task.Delay(400).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.IsEmpty)
                    _processing = false;
                else
                    _ = Task.Run(ProcessQueue);
            }
        }
    }

    #endregion

    #region Plex Update Logic

    /// <summary>Orchestrates debounced library scans, metadata refreshes, and collection updates for a recently modified series.</summary>
    /// <param name="seriesId">The Shoko Series ID to update.</param>
    private void TriggerPlexUpdates(int seriesId)
    {
        if (!_plexLibrary.IsEnabled)
            return;

        var series = _metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return;

        ScheduleLibraryScan(series);
        ScheduleMetadataFixup(series);
        ScheduleCollectionUpdate(series);
    }

    /// <summary>Generic debouncer wrapper to handle delaying tasks and managing cancellations efficiently.</summary>
    /// <param name="seriesId">The ID of the series being processed.</param>
    /// <param name="delaySeconds">The delay in seconds before executing the action.</param>
    /// <param name="tracker">The dictionary tracking cancellation tokens for pending actions.</param>
    /// <param name="action">The asynchronous action to execute after the delay.</param>
    private void ScheduleDebouncedAction(int seriesId, int delaySeconds, ConcurrentDictionary<int, CancellationTokenSource> tracker, Func<CancellationToken, Task> action)
    {
        if (tracker.TryRemove(seriesId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        tracker[seriesId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);
                await VfsShared.VfsLock.WaitAsync(cts.Token).ConfigureAwait(false); // Acquire the lock to ensure a Plex update doesn't start while a VFS build is actively writing to the directory.
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
                s_logger.Error(ex, "VFS: Scheduled action failed for series {0}", seriesId);
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
        if (!_plexLibrary.ScanOnVfsRefresh)
            return;

        ScheduleDebouncedAction(
            series.ID,
            Settings.Advanced.PlexScanDelay,
            _pendingLibraryScans,
            async token =>
            {
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, _metadataService))
                {
                    if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        await _plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
                    else
                        s_logger.Debug("VFS: Library scan for '{0}' skipped -> path '{1}' not ready or empty", series.PreferredTitle?.Value, path);
                }
            }
        );
    }

    /// <summary>Schedules or resets the timer for a full Plex metadata refresh for the given series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    private void ScheduleMetadataFixup(IShokoSeries series)
    {
        s_logger.Debug("VFS: Scheduling metadata fixup for '{0}' (ID: {1}) in {2} minute(s)", series.PreferredTitle?.Value, series.ID, Settings.Advanced.PlexFixupDelay);
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
            // Regenerate the VFS to account for cases where the episode/season numbering was updated in Shoko after the initial file event was processed (a metadata refresh can't do this on its own)
            var vfsResult = _builder.Build(series.ID, cleanRoot: false, pruneSeries: true);
            if (vfsResult.CreatedLinks > 0)
                s_logger.Info("VFS: Re-generated links for '{0}' during fixup phase", series.PreferredTitle?.Value);

            // Restore AnimeThemes links for this specific series if a mapping file exists to prevent the pruned folder from losing them
            if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                await _atMapping.ApplyMappingAsync([series.ID], token).ConfigureAwait(false);

            int bufferSeconds = Settings.Advanced.PlexScanDelay;
            if (bufferSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(bufferSeconds), token).ConfigureAwait(false);

            // Fallback in case the files were not scanned into Plex by the initial scan
            if (_plexLibrary.ScanOnVfsRefresh)
            {
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, _metadataService))
                    await _plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
            }

            var targets = _plexLibrary.GetConfiguredTargets();
            bool foundInAnyTarget = false;
            foreach (var target in targets)
            {
                var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target, token).ConfigureAwait(false);
                if (ratingKey.HasValue)
                {
                    foundInAnyTarget = true;
                    s_logger.Info("VFS: Triggering debounced metadata fixup for '{0}' (RatingKey: {1}) on {2}", series.PreferredTitle?.Value, ratingKey.Value, target.ServerName);
                    await _plexLibrary.RefreshMetadataAsync(ratingKey.Value, target, token).ConfigureAwait(false);
                }
            }

            if (!foundInAnyTarget)
                s_logger.Debug("VFS: Debounced fixup for '{0}' skipped; rating key not found in Plex yet", series.PreferredTitle?.Value);

            s_logger.Info("VFS: Triggering debounced critic rating application for '{0}' (ID: {1})", series.PreferredTitle?.Value, series.ID);
            await _criticRatingService.ApplyRatingsAsync([series.ID], token).ConfigureAwait(false);

            if (Settings.Advanced.EnableImageSync)
            {
                s_logger.Info("VFS: Triggering debounced image sync for '{0}' (ID: {1})", series.PreferredTitle?.Value, series.ID);
                await _imageSyncService.SyncImagesAsync([series.ID], token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            s_logger.Error(ex, "VFS: Metadata fixup failed for series {0}", series.ID);
        }
    }

    /// <summary>Schedules or resets the timer for updating collections and posters in Plex for the given series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    private void ScheduleCollectionUpdate(IShokoSeries series) =>
        ScheduleDebouncedAction(series.ID, Settings.Advanced.PlexFixupDelay * 60, _pendingCollectionUpdates, token => RunCollectionUpdateAsync(series, token));

    /// <summary>Worker task that performs the collection assignment and poster upload logic.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the update operation.</returns>
    private async Task RunCollectionUpdateAsync(IShokoSeries series, CancellationToken token)
    {
        try
        {
            var collectionName = _plexMetadata.GetCollectionName(series);
            if (string.IsNullOrWhiteSpace(collectionName))
                return;

            var targets = _plexLibrary.GetConfiguredTargets();
            foreach (var target in targets)
            {
                var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target, token).ConfigureAwait(false);
                if (!ratingKey.HasValue)
                    continue;

                if (await _plexCollections.AssignCollectionToItemByMetadataAsync(ratingKey.Value, collectionName, target, token).ConfigureAwait(false))
                {
                    var collectionId = await _plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, token).ConfigureAwait(false);
                    if (!collectionId.HasValue)
                        continue;

                    string? posterUrl = PlexHelper.GetCollectionPosterUrl(series, collectionName, collectionId.Value, _metadataService, Settings.CollectionPosters);
                    if (!string.IsNullOrWhiteSpace(posterUrl))
                        await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId.Value, posterUrl, target, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "VFS: Collection update failed for series {0}", series.ID);
        }
    }

    #endregion
}
