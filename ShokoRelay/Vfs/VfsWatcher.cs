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
public class VfsWatcher(IVideoService videoService, VfsBuilder builder, IMetadataService metadataService, PlexMetadata plexMetadata, PlexClient plexLibrary, PlexCollections plexCollections)
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService;
    private readonly VfsBuilder _builder = builder;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly PlexMetadata _plexMetadata = plexMetadata;
    private readonly PlexClient _plexLibrary = plexLibrary;
    private readonly PlexCollections _plexCollections = plexCollections;

    private readonly ConcurrentDictionary<int, byte> _pending = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingMetadataFixups = new();
    private bool _processing;
    private readonly Lock _gate = new();

    #endregion

    #region Lifecycle Management

    /// <summary>Subscribe to Shoko video-file events and begin watching for changes.</summary>
    public void Start()
    {
        _videoService.VideoFileHashed += OnVideoFileHashed;
        _videoService.VideoFileRelocated += OnVideoFileRelocated;
        _videoService.VideoFileDeleted += OnVideoFileDeleted;

        Logger.Info("VFS watcher started (auto-refresh on file changes).");
    }

    /// <summary>Unsubscribe from Shoko video-file events and stop watching.</summary>
    public void Stop()
    {
        try
        {
            _videoService.VideoFileHashed -= OnVideoFileHashed;
            _videoService.VideoFileRelocated -= OnVideoFileRelocated;
            _videoService.VideoFileDeleted -= OnVideoFileDeleted;

            // Clear any pending metadata fixups
            foreach (var kvp in _pendingMetadataFixups)
                kvp.Value.Cancel();
        }
        catch { }

        Logger.Info("VFS watcher stopped.");
    }

    #endregion

    #region Event Handlers

    private void OnFileChanged(object? sender, VideoFileEventArgs e)
    {
        if (e?.Series == null || e.Series.Count == 0)
            return;

        foreach (var series in e.Series)
        {
            _pending[series.ID] = 1;
        }

        KickProcessLoop();
    }

    private void OnVideoFileHashed(object? sender, VideoFileHashedEventArgs e) => OnFileChanged(sender, e);

    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e) => OnFileChanged(sender, e);

    private void OnVideoFileDeleted(object? sender, VideoFileEventArgs e) => OnFileChanged(sender, e);

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
                int seriesId;
                lock (_gate)
                {
                    using var enumerator = _pending.GetEnumerator();
                    if (!enumerator.MoveNext())
                    {
                        _processing = false;
                        break;
                    }
                    seriesId = enumerator.Current.Key;
                    _pending.TryRemove(seriesId, out _);
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = _builder.Build(seriesId, cleanRoot: false, pruneSeries: true);
                    sw.Stop();
                    Logger.Info(
                        "VFS refreshed for series {SeriesId} in {Elapsed}ms — created={Created} planned={Planned} skipped={Skipped} seriesProcessed={SeriesProcessed} errors={ErrorsCount}",
                        seriesId,
                        sw.ElapsedMilliseconds,
                        result.CreatedLinks,
                        result.PlannedLinks,
                        result.Skipped,
                        result.SeriesProcessed,
                        result.Errors?.Count ?? 0
                    );
                    await TriggerPlexUpdatesAsync(seriesId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "VFS refresh failed for series {SeriesId}", seriesId);
                }

                await Task.Delay(400).ConfigureAwait(false); // light debounce
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
    private async Task TriggerPlexUpdatesAsync(int seriesId)
    {
        if (!_plexLibrary.IsEnabled)
            return;

        var series = _metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return;

        // Trigger immediate partial library scans if enabled
        if (_plexLibrary.ScanOnVfsRefresh)
        {
            foreach (var path in ResolveSeriesVfsPaths(series))
                await _plexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false);
        }

        // Handle debounced Metadata Refresh (Mitigation for Plex CMP "empty first pass" bug)
        ScheduleMetadataFixup(series);

        // Update collection assignments and posters (Lightweight metadata-only calls)
        var collectionName = _plexMetadata.GetCollectionName(series);
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            var targets = _plexLibrary.GetConfiguredTargets();
            foreach (var target in targets)
            {
                var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target).ConfigureAwait(false);
                if (!ratingKey.HasValue)
                    continue;

                bool ok = await _plexCollections.AssignCollectionToItemByMetadataAsync(ratingKey.Value, collectionName, target).ConfigureAwait(false);
                if (!ok)
                    continue;

                var collectionId = await _plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, CancellationToken.None).ConfigureAwait(false);
                if (collectionId == null)
                    continue;

                string? posterUrl = PlexHelper.GetCollectionPosterUrl(series, collectionName, collectionId.Value, _metadataService, ShokoRelay.Settings.CollectionPosters);

                if (!string.IsNullOrWhiteSpace(posterUrl))
                {
                    try
                    {
                        bool posted = await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId.Value, posterUrl, target).ConfigureAwait(false);
                        if (!posted)
                            Logger.Warn("Failed to upload collection poster for {CollectionId} on {Server}:{Section}", collectionId.Value, target.ServerUrl, target.SectionId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "UploadCollectionPosterByUrlAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId.Value, target.ServerUrl, target.SectionId);
                    }
                }
            }
        }
    }

    /// <summary>Schedules or resets the timer for a full Plex metadata refresh for the given series.</summary>
    private void ScheduleMetadataFixup(IShokoSeries series)
    {
        int delayMinutes = ShokoRelay.Settings.Advanced.PlexFixupDelay;
        if (delayMinutes <= 0)
            return;

        // Cancel any existing pending fixup for this series to reset the timer (Debounce)
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

                var targets = _plexLibrary.GetConfiguredTargets();
                if (targets.Count == 0)
                {
                    Logger.Warn("VFS: Fixup for '{0}' aborted; no Plex targets configured.", series.PreferredTitle?.Value);
                    return;
                }

                bool foundInAnyTarget = false;
                foreach (var target in targets)
                {
                    var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target).ConfigureAwait(false);
                    if (ratingKey.HasValue)
                    {
                        foundInAnyTarget = true;
                        Logger.Info("VFS: Triggering debounced metadata fixup for '{0}' (RatingKey: {1}) on {2}", series.PreferredTitle?.Value, ratingKey.Value, target.ServerName);
                        await _plexLibrary.RefreshMetadataAsync(ratingKey.Value, target, cts.Token).ConfigureAwait(false);
                    }
                }

                if (!foundInAnyTarget)
                {
                    Logger.Debug("VFS: Debounced fixup for '{0}' skipped; rating key not found in Plex yet (Initial scan may still be in progress).", series.PreferredTitle?.Value);
                }
            }
            catch (OperationCanceledException)
            {
                // Task was reset by a newer file event for the same series
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "VFS: Metadata fixup failed for series {0}", series.ID);
            }
            finally
            {
                _pendingMetadataFixups.TryRemove(new KeyValuePair<int, CancellationTokenSource>(series.ID, cts));
                cts.Dispose();
            }
        });
    }

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
