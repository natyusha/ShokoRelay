using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata.Events;
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
/// <param name="linkingService">Shoko linking service.</param>
/// <param name="orderingService">Shoko metadata ordering service.</param>
/// <param name="plexLibrary">Plex client.</param>
/// <param name="collectionService">Plex collection service.</param>
/// <param name="atMapping">AnimeThemes mapping service.</param>
/// <param name="criticRatingService">Plex critic rating service.</param>
/// <param name="imageSyncService">Shoko image sync service.</param>
/// <param name="plexMetadata">Plex metadata mapper.</param>
/// <param name="logger">Logger instance.</param>
public class VfsWatcher(
    IVideoService videoService,
    IVideoReleaseService releaseService,
    VfsBuilder builder,
    IMetadataService metadataService,
    IMetadataLinkingService linkingService,
    IMetadataOrderingService orderingService,
    PlexClient plexLibrary,
    CollectionService collectionService,
    AnimeThemesMapping atMapping,
    CriticRatingService criticRatingService,
    ImageSyncService imageSyncService,
    PlexMetadata plexMetadata,
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

    /// <summary>Indicates if the watcher is currently subscribed and running (1 = active, 0 = stopped).</summary>
    private int _isRunning;

    /// <summary>Indicates if the background processing loop is currently active.</summary>
    private bool _processing;

    /// <summary>Lock object for synchronizing the processing loop state.</summary>
    private readonly Lock _gate = new();

    /// <summary>Cancellation source signaled when the watcher stops.</summary>
    private CancellationTokenSource? _stopCts;

    #endregion

    #region Lifecycle Management

    /// <summary>Subscribe to Shoko video-file events and begin watching for changes.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _isRunning, 1) != 0)
            return;

        lock (_gate)
        {
            _stopCts?.Dispose();
            _stopCts = new CancellationTokenSource();
        }

        videoService.VideoFileRelocated += OnVideoFileRelocated;
        videoService.VideoFileDeleted += OnVideoFileDeleted;
        videoService.VideoUpdated += OnVideoUpdated;
        releaseService.ReleaseSaved += OnVideoReleaseSaved;
        releaseService.ReleaseDeleted += OnVideoReleaseDeleted;
        metadataService.SeriesRemoved += OnSeriesRemoved;
        linkingService.LinksChanged += OnLinksChanged;
        orderingService.EpisodeHiddenChanged += OnEpisodeHiddenChanged;
        orderingService.PreferredOrderingChanged += OnPreferredOrderingChanged;

        logger.LogInformation("VFS: VfsWatcher -> Started (listening for relocation, matching, hidden, and deletion events)");
    }

    /// <summary>Unsubscribe from Shoko video-file events and stop watching.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _isRunning, 0) == 0)
            return;

        try
        {
            videoService.VideoFileRelocated -= OnVideoFileRelocated;
            videoService.VideoFileDeleted -= OnVideoFileDeleted;
            videoService.VideoUpdated -= OnVideoUpdated;
            releaseService.ReleaseSaved -= OnVideoReleaseSaved;
            releaseService.ReleaseDeleted -= OnVideoReleaseDeleted;
            metadataService.SeriesRemoved -= OnSeriesRemoved;
            linkingService.LinksChanged -= OnLinksChanged;
            orderingService.EpisodeHiddenChanged -= OnEpisodeHiddenChanged;
            orderingService.PreferredOrderingChanged -= OnPreferredOrderingChanged;

            lock (_gate)
            {
                _stopCts?.Cancel();
                _stopCts?.Dispose();
                _stopCts = null;
            }

            foreach (var cts in _pendingMetadataFixups.Values)
                try
                {
                    cts.Cancel();
                }
                catch { }
            foreach (var cts in _pendingLibraryScans.Values)
                try
                {
                    cts.Cancel();
                }
                catch { }

            _pendingMetadataFixups.Clear();
            _pendingLibraryScans.Clear();
        }
        catch { }

        logger.LogInformation("VFS: VfsWatcher -> Stopped");
    }

    #endregion

    #region Event Handlers

    /// <summary>Handles Shoko video file relocation and rename events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing file information.</param>
    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs @event)
    {
        logger.LogDebug("VFS: File relocated/renamed -> {FileName}", Path.GetFileName(@event.RelativePath));
        HandleFileEvent(@event);
    }

    /// <summary>Handles Shoko video file deletion events, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing file information.</param>
    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs @event)
    {
        logger.LogDebug("VFS: File deleted -> {FileName}", Path.GetFileName(@event.RelativePath));
        HandleFileEvent(@event);
    }

    /// <summary>Handles Shoko video variation or ignored status updates, queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing video information.</param>
    private void OnVideoUpdated(object? sender, VideoUpdatedEventArgs @event)
    {
        if (@event.Video?.Series == null || @event.Video.Series.Count == 0)
            return;
        logger.LogDebug("VFS: Video updated for '{FileName}'", Path.GetFileName(@event.Video.EarliestKnownName ?? "Unknown File"));
        EnqueueSeries(@event.Video.Series.Select(series => series.GetPrimaryId(metadataService)));
    }

    /// <summary>Handles Shoko release matching events, queueing affected series for VFS updates when a video is assigned.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing release associations.</param>
    private void OnVideoReleaseSaved(object? sender, VideoReleaseSavedEventArgs @event)
    {
        if (@event.Video?.Series == null || @event.Video.Series.Count == 0)
            return;
        logger.LogDebug("VFS: Release saved for video '{FileName}'", Path.GetFileName(@event.Video.EarliestKnownName ?? "Unknown File"));

        foreach (var series in @event.Video.Series)
        {
            int primaryId = series.GetPrimaryId(metadataService);
            logger.LogDebug("VFS: Adding series -> {Title} [{LocalId}] (Primary: {PrimaryId}) to pending queue due to release save", series.GetDisplayTitle(), series.LocalID, primaryId);
        }

        EnqueueSeries(@event.Video.Series.Select(series => series.GetPrimaryId(metadataService)));
    }

    /// <summary>Handles Shoko release unlinking events, queueing affected series for VFS updates when a release is removed.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing release associations.</param>
    private void OnVideoReleaseDeleted(object? sender, VideoReleaseDeletedEventArgs @event)
    {
        var seriesIds = new HashSet<int>();
        if (@event.Video?.Series is { Count: > 0 } videoSeries)
            foreach (var series in videoSeries)
                seriesIds.Add(series.GetPrimaryId(metadataService));

        if (seriesIds.Count == 0 && @event.ReleaseInfo?.CrossReferences is { Count: > 0 } xrefs)
        {
            foreach (var xref in xrefs)
            {
                if (xref.AnidbAnimeID is { } animeId && animeId > 0 && metadataService.GetShokoSeriesByAnidbID(animeId) is { } series)
                    seriesIds.Add(series.GetPrimaryId(metadataService));
                else if (xref.AnidbEpisodeID > 0 && metadataService.GetShokoEpisodeByAnidbID(xref.AnidbEpisodeID) is { } episode)
                    seriesIds.Add(OverrideHelper.GetPrimary(episode.ShokoSeriesID, metadataService));
            }
        }

        if (seriesIds.Count == 0)
            return;

        logger.LogDebug("VFS: Release deleted for video '{FileName}'", Path.GetFileName(@event.Video?.EarliestKnownName ?? "Unknown File"));
        EnqueueSeries(seriesIds);
    }

    /// <summary>Handles Shoko series deletion events, queueing the series for locked VFS pruning and cache invalidation.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing removed series information.</param>
    private void OnSeriesRemoved(object? sender, SeriesInfoUpdatedEventArgs @event)
    {
        if (@event.SeriesInfo is not IShokoSeries series)
            return;

        int primaryId = series.GetPrimaryId(metadataService);
        logger.LogInformation("VFS: Series removed -> {Title} [{LocalId}] ... Queueing VFS prune", series.GetDisplayTitle(), series.LocalID);
        EnqueueSeries(primaryId == series.LocalID ? [primaryId] : [primaryId, series.LocalID], invalidateMetadata: true);
    }

    /// <summary>Handles Shoko metadata link changes, queueing affected series for VFS updates and cache invalidation when TMDB links change.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing changed metadata links.</param>
    private void OnLinksChanged(object? sender, MetadataLinksChangedEventArgs @event)
    {
        var affectedIds = new HashSet<int>();
        var affectedSeries = new List<IShokoSeries>();
        foreach (var change in @event.Changes)
        {
            if (change.Source != MetadataSource.TMDB || metadataService.GetShokoSeriesByAnidbID(change.AnidbAnimeID) is not { } series)
                continue;

            affectedIds.Add(series.GetPrimaryId(metadataService));
            foreach (int memberId in OverrideHelper.GetGroup(series.LocalID, metadataService))
                affectedIds.Add(memberId);
            affectedSeries.Add(series);
        }

        if (affectedSeries.Count == 0)
            return;

        OverrideHelper.Reload();
        foreach (var series in affectedSeries)
        {
            affectedIds.Add(series.GetPrimaryId(metadataService));
            foreach (int memberId in OverrideHelper.GetGroup(series.LocalID, metadataService))
                affectedIds.Add(memberId);
        }

        EnqueueSeries(affectedIds, invalidateMetadata: true);
    }

    /// <summary>Handles Shoko episode hidden state changes, queueing affected series for VFS updates and cache invalidation.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing the episode identifier and new hidden state.</param>
    private void OnEpisodeHiddenChanged(object? sender, EpisodeHiddenChangedEventArgs @event)
    {
        if (metadataService.GetEpisode(@event.EpisodeID) is not { } episode)
            return;

        IReadOnlyList<int> primaryIds = episode is IShokoEpisode shokoEpisode
            ? [OverrideHelper.GetPrimary(shokoEpisode.ShokoSeriesID, metadataService)]
            : [.. episode.ShokoEpisodes.Select(linkedEpisode => OverrideHelper.GetPrimary(linkedEpisode.ShokoSeriesID, metadataService)).Distinct()];

        if (primaryIds.Count == 0)
            return;

        logger.LogDebug("VFS: Episode hidden state changed ({EpisodeId}, IsHidden={IsHidden}) -> Queueing VFS update", @event.EpisodeID, @event.IsHidden);
        EnqueueSeries(primaryIds, invalidateMetadata: true);
    }

    /// <summary>Handles Shoko preferred ordering changes, clearing cached series metadata contexts and queueing affected series for VFS updates.</summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="event">Event parameters containing the series and ordering identifiers.</param>
    private void OnPreferredOrderingChanged(object? sender, PreferredOrderingChangedEventArgs @event)
    {
        if (!EnforceTmdbNumbering || @event.SeriesID.Source != MetadataSource.TMDB || metadataService.GetSeries(@event.SeriesID) is not { } tmdbSeries)
            return;

        var affectedIds = tmdbSeries
            .ShokoSeries.Concat(tmdbSeries.MetadataEpisodeCrossReferences.Select(xref => xref.ShokoSeries).OfType<IShokoSeries>())
            .Select(series => series.GetPrimaryId(metadataService))
            .Distinct()
            .ToList();

        if (affectedIds.Count == 0)
            return;

        logger.LogDebug("VFS: TMDB preferred ordering changed ({SeriesId} -> {OrderingId}) -> Queueing VFS update", @event.SeriesID, @event.OrderingID?.ID ?? "default");
        EnqueueSeries(affectedIds, invalidateMetadata: true);
    }

    /// <summary>Aggregates multiple video file events into the pending processing queue.</summary>
    /// <param name="args">The video file event arguments.</param>
    private void HandleFileEvent(VideoFileEventArgs? args)
    {
        var seriesList = args?.Series is { Count: > 0 } ? args.Series : args?.Video?.Series;
        if (seriesList == null || !seriesList.Any())
            return;

        EnqueueSeries(seriesList.Select(series => series.GetPrimaryId(metadataService)));
    }

    #endregion

    #region Processing Logic

    /// <summary>Locks and starts the background task loop to process pending series queue updates.</summary>
    private void KickProcessLoop()
    {
        lock (_gate)
        {
            if (_processing || _stopCts is not { IsCancellationRequested: false } stopCts)
                return;
            _processing = true;
            var stopToken = stopCts.Token;
            _ = Task.Run(() => ProcessQueueAsync(stopToken));
        }
    }

    /// <summary>Asynchronously processes queued series, re-generating VFS structures and scheduling Plex notifications.</summary>
    /// <param name="stopToken">Cancellation token signaled when the watcher stops.</param>
    /// <returns>A task representing the queue processing operation.</returns>
    private async Task ProcessQueueAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            var seriesIds = new List<int>();

            // Iteratively extract pending items without clearing the dictionary blindly. This prevents losing events that are added simultaneously by other threads.
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out _))
                    seriesIds.Add(key);

            if (seriesIds.Count == 0)
            {
                lock (_gate)
                    if (_pending.IsEmpty || stopToken.IsCancellationRequested)
                    {
                        _processing = false;
                        return;
                    }
                continue;
            }

            try
            {
                if (Settings.Advanced.DeferVfsCreationUntilFixup)
                {
                    var removedIds = seriesIds.Where(id => metadataService.GetShokoSeriesByID(id) == null).ToList();
                    if (removedIds.Count > 0)
                    {
                        await VfsShared.VfsLock.WaitAsync(stopToken).ConfigureAwait(false);
                        try
                        {
                            builder.Build(removedIds, cleanRoot: false);
                        }
                        finally
                        {
                            VfsShared.VfsLock.Release();
                        }
                    }

                    var activeIds = seriesIds.Where(id => !removedIds.Contains(id)).ToList();
                    if (activeIds.Count > 0)
                    {
                        logger.LogInformation("VFS: Deferring VFS creation for {Count} series until fixup ({Delay}m delay)", activeIds.Count, Settings.Advanced.PlexFixupDelay);
                        foreach (var seriesId in activeIds)
                        {
                            plexMetadata.InvalidateCache(seriesId);
                            TriggerPlexUpdates(seriesId, deferScan: true);
                        }
                    }
                }
                else
                {
                    await VfsShared.VfsLock.WaitAsync(stopToken).ConfigureAwait(false); // Wait for any active dashboard VFS operations to complete before processing the automated queue
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var result = builder.Build(seriesIds, cleanRoot: false);

                        // Restore AnimeThemes links for the affected series if a mapping file exists
                        if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                            await atMapping.ApplyMappingAsync(seriesIds, stopToken).ConfigureAwait(false);

                        stopwatch.Stop();
                        logger.LogInformation(
                            "VFS: Batch refreshed for {Count} series in {Elapsed}ms -> created={Created} planned={Planned} skipped={Skipped} seriesProcessed={Processed} errors={Errors}",
                            seriesIds.Count,
                            stopwatch.ElapsedMilliseconds,
                            result.CreatedLinks,
                            result.PlannedLinks,
                            result.Skipped,
                            result.SeriesProcessed,
                            result.Errors?.Count ?? 0
                        );

                        foreach (var seriesId in seriesIds)
                        {
                            plexMetadata.InvalidateCache(seriesId);
                            TriggerPlexUpdates(seriesId);
                        }
                    }
                    finally
                    {
                        VfsShared.VfsLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "VFS: Batch refresh failed");
            }

            try
            {
                await Task.Delay(400, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
        }

        lock (_gate)
            _processing = false;
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
            ScheduleDebouncedAction(series.LocalID, Settings.Advanced.PlexFixupDelay * 60, _pendingMetadataFixups, token => RunMetadataFixupAsync(series.LocalID, token));
        }
    }

    /// <summary>Generic debouncer wrapper to handle delaying tasks and managing cancellations efficiently.</summary>
    /// <param name="seriesId">The ID of the series being processed.</param>
    /// <param name="delaySeconds">The delay in seconds before executing the action.</param>
    /// <param name="tracker">The dictionary tracking cancellation tokens for pending actions.</param>
    /// <param name="action">The asynchronous action to execute after the delay.</param>
    private void ScheduleDebouncedAction(int seriesId, int delaySeconds, ConcurrentDictionary<int, CancellationTokenSource> tracker, Func<CancellationToken, Task> action)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_stopCts is not { IsCancellationRequested: false } stopCts)
                return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(stopCts.Token);
        }

        tracker.AddOrUpdate(seriesId, cts, (_, existing) =>
        {
            try
            {
                existing.Cancel();
            }
            catch { }
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (delaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);

                if (_pending.ContainsKey(seriesId))
                    return;

                await action(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                var title = metadataService.GetShokoSeriesByID(seriesId)?.GetDisplayTitle() ?? "Series";
                logger.LogError(exception, "VFS: Scheduled action failed for series -> {Title} [{SeriesId}]", title, seriesId);
            }
            finally
            {
                tracker.TryRemove(new KeyValuePair<int, CancellationTokenSource>(seriesId, cts));
                try
                {
                    cts.Dispose();
                }
                catch { }
            }
        });
    }

    /// <summary>Schedules or resets the timer for a partial Plex library scan for the given series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    private void ScheduleLibraryScan(IShokoSeries series)
    {
        if (!Settings.Automation.ScanOnVfsRefresh)
            return;

        ScheduleDebouncedAction(series.LocalID, Settings.Advanced.PlexScanDelay, _pendingLibraryScans, async token =>
        {
            foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
            {
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);
                else
                    logger.LogDebug("VFS: Library scan for series -> {Title} [{LocalId}] skipped; path '{Path}' not ready or empty", series.GetDisplayTitle(), series.LocalID, path);
            }
        });
    }

    /// <summary>Worker task that performs the actual metadata fixup logic, critic rating application, and optional image synchronization after the debounce delay has settled.</summary>
    /// <param name="seriesId">The Shoko series ID.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the fixup operation.</returns>
    private async Task RunMetadataFixupAsync(int seriesId, CancellationToken token)
    {
        try
        {
            bool isDeferred = Settings.Advanced.DeferVfsCreationUntilFixup;

            // Invalidate cached metadata so Plex receives the fresh, updated series context upon refresh
            plexMetadata.InvalidateCache(seriesId);

            await VfsShared.VfsLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Regenerate the VFS to account for cases where the episode/season numbering was updated in Shoko after the initial file event was processed
                var vfsResult = builder.Build(seriesId, cleanRoot: false);
                if (vfsResult.CreatedLinks > 0)
                {
                    string title = metadataService.GetShokoSeriesByID(seriesId)?.GetDisplayTitle() ?? seriesId.ToString();
                    logger.LogInformation("VFS: {Action} links -> {Title} [{LocalId}] during fixup phase", isDeferred ? "Created" : "Re-generated", title, seriesId);
                }

                // Restore AnimeThemes links for this specific series if a mapping file exists to prevent the pruned folder from losing them
                if (File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                    await atMapping.ApplyMappingAsync([seriesId], token).ConfigureAwait(false);
            }
            finally
            {
                VfsShared.VfsLock.Release();
            }

            // Re-resolve the series fresh after the debounce delay and exit if deleted or Plex is unlinked
            if (!plexLibrary.IsEnabled || metadataService.GetShokoSeriesByID(seriesId) is not { } series)
                return;

            // Wait to allow the filesystem to settle, or for Plex's native auto-scanner to index the newly generated VFS symlinks
            await Task.Delay(TimeSpan.FromSeconds(Settings.Advanced.PlexScanDelay), token).ConfigureAwait(false);

            // If partial scans are enabled, trigger them now: In deferred mode this serves as the primary scan / In standard mode it acts as a fallback for the initial scan
            var vfsPaths = VfsShared.ResolveSeriesVfsPaths(series, metadataService).ToList();
            if (Settings.Automation.ScanOnVfsRefresh)
                foreach (var path in vfsPaths)
                    await plexLibrary.RefreshSectionPathAsync(path, token).ConfigureAwait(false);

            var targets = plexLibrary.GetConfiguredTargets();
            bool foundInAnyTarget = false;

            // Pre-filter targets so we only poll libraries that physically contain the VFS paths for this series, eliminating timeouts on irrelevant libraries
            var matchingTargetIds = vfsPaths.SelectMany(plexLibrary.GetMatchingTargetsForPath).Select(match => (match.Target.ServerId, match.Target.SectionId)).ToHashSet();

            // Wait for Plex to assign rating keys (primarily for users in deferred mode)
            int retryDelaySeconds = Settings.Advanced.PlexScanDelay;
            int maxRetries = isDeferred ? Math.Max(1, 180 / retryDelaySeconds) : 2; // ~3 minutes for deferred, ~2 ticks for standard

            foreach (var target in targets)
            {
                if (!matchingTargetIds.Contains((target.ServerId, target.SectionId)))
                    continue;

                var ratingKeys = new List<int>();
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    ratingKeys = await plexLibrary.FindRatingKeysForShokoSeriesInSectionAsync(series.LocalID, target, metadataService, token).ConfigureAwait(false);
                    if (ratingKeys.Count > 0)
                        break;

                    if (retry < maxRetries - 1)
                    {
                        if (retry == 0 && isDeferred)
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
                    if (!isDeferred || Settings.Automation.ScanOnVfsRefresh)
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
        catch (Exception exception)
        {
            logger.LogError(exception, "VFS: Metadata fixup failed for series -> {Title} [{LocalId}]", metadataService.GetShokoSeriesByID(seriesId)?.GetDisplayTitle() ?? seriesId.ToString(), seriesId);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Queues primary series IDs for incremental VFS processing, optionally invalidating cached Plex metadata contexts.</summary>
    /// <param name="primaryIds">The primary Shoko series IDs to enqueue.</param>
    /// <param name="invalidateMetadata">When <c>true</c>, clears cached <see cref="PlexMetadata"/> contexts for each queued series.</param>
    private void EnqueueSeries(IEnumerable<int> primaryIds, bool invalidateMetadata = false)
    {
        bool anyQueued = false;
        foreach (int primaryId in primaryIds)
        {
            if (invalidateMetadata)
                plexMetadata.InvalidateCache(primaryId);
            _pending[primaryId] = 1;
            anyQueued = true;
        }
        if (anyQueued)
            KickProcessLoop();
    }

    #endregion
}
