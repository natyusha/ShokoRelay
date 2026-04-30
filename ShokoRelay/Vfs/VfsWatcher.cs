using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs;

/// <summary>Watches for Shoko video-file events and triggers incremental VFS rebuilds plus debounced Plex refreshes.</summary>
public class VfsWatcher(
    IVideoService videoService,
    IVideoReleaseService releaseService,
    VfsBuilder builder,
    IMetadataService metadataService,
    PlexMetadata plexMetadata,
    PlexClient plexLibrary,
    PlexCollections plexCollections
)
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService;
    private readonly IVideoReleaseService _releaseService = releaseService;
    private readonly VfsBuilder _builder = builder;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly PlexMetadata _plexMetadata = plexMetadata;
    private readonly PlexClient _plexLibrary = plexLibrary;
    private readonly PlexCollections _plexCollections = plexCollections;

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

        Logger.Info("VFS watcher started (listening for relocation, matching and deletion events).");
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

        Logger.Info("VFS watcher stopped.");
    }

    #endregion

    #region Event Handlers

    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e) => HandleFileEvent(e);

    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs e) => HandleFileEvent(e);

    private void OnVideoReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        // Trigger build when a video is successfully linked to a series (manual or automatic)
        if (e.Video?.Series == null || e.Video.Series.Count == 0)
            return;

        foreach (var series in e.Video.Series)
            _pending[series.ID] = 1;

        KickProcessLoop();
    }

    /// <summary>Unified handler for standard video file events.</summary>
    /// <param name="e">Video file event arguments.</param>
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

                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = _builder.Build(seriesIds, cleanRoot: false, pruneSeries: true);
                    sw.Stop();
                    Logger.Info(
                        "VFS batch refreshed for {0} series in {1}ms — created={2} planned={3} skipped={4} seriesProcessed={5} errors={6}",
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
                    Logger.Warn(ex, "VFS batch refresh failed");
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

    /// <summary>Refresh Plex library paths and metadata for a series after VFS links are updated.</summary>
    /// <param name="seriesId">The Shoko Series ID to update in Plex.</param>
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

    /// <summary>Schedules or resets the timer for a partial Plex library scan for the given series.</summary>
    /// <param name="series">The Shoko series being updated.</param>
    private void ScheduleLibraryScan(IShokoSeries series)
    {
        if (!_plexLibrary.ScanOnVfsRefresh)
            return;

        int delaySeconds = ShokoRelay.Settings.Advanced.PlexScanDelay;

        if (_pendingLibraryScans.TryRemove(series.ID, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingLibraryScans[series.ID] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);

                lock (VfsBuilder.GlobalBuildLock)
                {
                    if (_pending.ContainsKey(series.ID))
                        return;
                    foreach (var path in ResolveSeriesVfsPaths(series))
                    {
                        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                            _ = _plexLibrary.RefreshSectionPathAsync(path, cts.Token);
                        else
                            Logger.Debug("VFS: Library scan for '{0}' skipped; path '{1}' not ready or empty.", series.PreferredTitle?.Value, path);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(ex, "VFS: Library scan failed for series {0}", series.ID);
            }
            finally
            {
                _pendingLibraryScans.TryRemove(new KeyValuePair<int, CancellationTokenSource>(series.ID, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>Schedules or resets the timer for a full Plex metadata refresh for the given series.</summary>
    /// <param name="series">The Shoko series being updated.</param>
    private void ScheduleMetadataFixup(IShokoSeries series)
    {
        int delayMinutes = ShokoRelay.Settings.Advanced.PlexFixupDelay;

        if (_pendingMetadataFixups.TryRemove(series.ID, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        Logger.Debug("VFS: Scheduling metadata fixup for '{0}' (ID: {1}) in {2} minute(s)", series.PreferredTitle?.Value, series.ID, delayMinutes);

        var cts = new CancellationTokenSource();
        _pendingMetadataFixups[series.ID] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), cts.Token).ConfigureAwait(false);
                lock (VfsBuilder.GlobalBuildLock)
                {
                    if (_pending.ContainsKey(series.ID))
                        return;

                    _ = RunMetadataFixupAsync(series, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _pendingMetadataFixups.TryRemove(new KeyValuePair<int, CancellationTokenSource>(series.ID, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>Worker task that performs the actual metadata fixup logic after the debounce delay has settled.</summary>
    /// <param name="series">The Shoko series to fix up.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task representing the fixup operation.</returns>
    private async Task RunMetadataFixupAsync(IShokoSeries series, CancellationToken token)
    {
        try
        {
            // Regenerate the VFS to account for cases where the episode/season numbering was updated in Shoko after the initial file event was processed (a metadata refresh can't do this on its own)
            var vfsResult = _builder.Build(series.ID, cleanRoot: false, pruneSeries: true);
            if (vfsResult.CreatedLinks > 0)
                Logger.Info("VFS: Re-generated links for '{0}' during fixup phase", series.PreferredTitle?.Value);

            int bufferSeconds = ShokoRelay.Settings.Advanced.PlexScanDelay;
            if (bufferSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(bufferSeconds), token).ConfigureAwait(false);

            var targets = _plexLibrary.GetConfiguredTargets();
            bool foundInAnyTarget = false;
            foreach (var target in targets)
            {
                var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target, token).ConfigureAwait(false);
                if (ratingKey.HasValue)
                {
                    foundInAnyTarget = true;
                    Logger.Info("VFS: Triggering debounced metadata fixup for '{0}' (RatingKey: {1}) on {2}", series.PreferredTitle?.Value, ratingKey.Value, target.ServerName);
                    await _plexLibrary.RefreshMetadataAsync(ratingKey.Value, target, token).ConfigureAwait(false);
                }
            }

            if (!foundInAnyTarget)
                Logger.Debug("VFS: Debounced fixup for '{0}' skipped; rating key not found in Plex yet.", series.PreferredTitle?.Value);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error(ex, "VFS: Metadata fixup failed for series {0}", series.ID);
        }
    }

    /// <summary>Schedules or resets the timer for updating collections and posters in Plex for the given series.</summary>
    /// <param name="series">The Shoko series being updated.</param>
    private void ScheduleCollectionUpdate(IShokoSeries series)
    {
        int delayMinutes = ShokoRelay.Settings.Advanced.PlexFixupDelay;

        if (_pendingCollectionUpdates.TryRemove(series.ID, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingCollectionUpdates[series.ID] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), cts.Token).ConfigureAwait(false);
                lock (VfsBuilder.GlobalBuildLock)
                {
                    if (_pending.ContainsKey(series.ID))
                        return;

                    _ = RunCollectionUpdateAsync(series, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _pendingCollectionUpdates.TryRemove(new KeyValuePair<int, CancellationTokenSource>(series.ID, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>Worker task that performs the collection assignment and poster upload logic.</summary>
    /// <param name="series">The Shoko series to update.</param>
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

                    string? posterUrl = PlexHelper.GetCollectionPosterUrl(series, collectionName, collectionId.Value, _metadataService, ShokoRelay.Settings.CollectionPosters);
                    if (!string.IsNullOrWhiteSpace(posterUrl))
                        await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId.Value, posterUrl, target, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VFS: Collection update failed for series {0}", series.ID);
        }
    }

    /// <summary>Resolves the list of physical VFS series directories associated with a series across all import roots.</summary>
    /// <param name="series">The Shoko series.</param>
    /// <returns>An enumerable of absolute directory paths.</returns>
    private IEnumerable<string> ResolveSeriesVfsPaths(IShokoSeries series)
    {
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string rootName = VfsShared.ResolveRootFolderName();

        var fileData = MapHelper.GetSeriesFileData(series);
        foreach (var mapping in fileData.Mappings)
        {
            var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
            if (location == null)
                continue;

            string? importRoot = VfsShared.ResolveImportRootPath(location);
            if (string.IsNullOrWhiteSpace(importRoot))
                continue;

            string seriesPath = Path.Combine(importRoot, rootName, series.ID.ToString());
            roots.Add(seriesPath);
        }

        return roots;
    }

    #endregion
}
