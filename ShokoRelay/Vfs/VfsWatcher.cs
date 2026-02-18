using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Events;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs
{
    public class VfsWatcher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IVideoService _videoService;
        private readonly VfsBuilder _builder;
        private readonly IMetadataService _metadataService;
        private readonly PlexMetadata _plexMetadata;
        private readonly PlexClient _plexLibrary;
        private readonly PlexCollections _plexCollections;
        private readonly ConfigProvider _configProvider;

        private readonly ConcurrentDictionary<int, byte> _pending = new();
        private bool _processing;
        private readonly object _gate = new();

        public VfsWatcher(
            IVideoService videoService,
            VfsBuilder builder,
            IMetadataService metadataService,
            PlexMetadata plexMetadata,
            PlexClient plexLibrary,
            PlexCollections plexCollections,
            ConfigProvider configProvider
        )
        {
            _videoService = videoService;
            _builder = builder;
            _metadataService = metadataService;
            _plexMetadata = plexMetadata;
            _plexLibrary = plexLibrary;
            _plexCollections = plexCollections;
            _configProvider = configProvider;
        }

        public void Start()
        {
            _videoService.VideoFileHashed += OnVideoFileHashed;
            _videoService.VideoFileRelocated += OnVideoFileRelocated;
            _videoService.VideoFileDeleted += OnVideoFileDeleted;

            Logger.Info("VFS watcher started (auto-refresh on file changes).");
        }

        public void Stop()
        {
            try
            {
                _videoService.VideoFileHashed -= OnVideoFileHashed;
                _videoService.VideoFileRelocated -= OnVideoFileRelocated;
                _videoService.VideoFileDeleted -= OnVideoFileDeleted;
            }
            catch { }

            Logger.Info("VFS watcher stopped.");
        }

        private void OnFileChanged(object? sender, FileEventArgs e)
        {
            if (e?.Series == null || e.Series.Count == 0)
                return;

            foreach (var series in e.Series)
            {
                _pending[series.ID] = 1;
            }

            KickProcessLoop();
        }

        private void OnVideoFileHashed(object? sender, FileHashedEventArgs e) => OnFileChanged(sender, e);

        private void OnVideoFileRelocated(object? sender, FileRelocatedEventArgs e) => OnFileChanged(sender, e);

        private void OnVideoFileDeleted(object? sender, FileEventArgs e) => OnFileChanged(sender, e);

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
                    if (!_pending.Keys.Any())
                        break;
                    seriesId = _pending.Keys.First();
                    _pending.TryRemove(seriesId, out _);

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

                    await Task.Delay(200); // light debounce
                }
            }
            finally
            {
                lock (_gate)
                {
                    _processing = false;
                    if (_pending.Count > 0)
                    {
                        _processing = true;
                        Task.Run(ProcessQueue);
                    }
                }
            }
        }

        private async Task TriggerPlexUpdatesAsync(int seriesId)
        {
            if (!_plexLibrary.IsEnabled)
                return;

            var series = _metadataService.GetShokoSeriesByID(seriesId);
            if (series == null)
                return;

            if (_plexLibrary.ScanOnVfsRefresh)
            {
                foreach (var path in ResolveSeriesVfsPaths(series))
                {
                    await _plexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false);
                }
            }

            var collectionName = _plexMetadata.GetCollectionName(series);
            if (!string.IsNullOrWhiteSpace(collectionName))
            {
                var targets = _plexLibrary.GetConfiguredTargets();
                foreach (var target in targets)
                {
                    // Find the Plex ratingKey for this Shoko series in the given target section
                    var ratingKey = await _plexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(series.ID, target).ConfigureAwait(false);
                    if (!ratingKey.HasValue)
                        continue; // series not present in this library/section

                    bool ok = await _plexCollections.AssignCollectionToItemByMetadataAsync(ratingKey.Value, collectionName, target).ConfigureAwait(false);
                    if (!ok)
                        continue;

                    // For poster upload we still need a collection id; get or create it for this target
                    var collectionId = await _plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, CancellationToken.None).ConfigureAwait(false);
                    if (collectionId == null)
                        continue;

                    string? posterUrl = PlexHelpers.GetCollectionPosterUrl(series, collectionName, collectionId.Value, _metadataService, _configProvider.GetSettings().CollectionPosters);

                    if (!string.IsNullOrWhiteSpace(posterUrl))
                    {
                        try
                        {
                            bool posted = await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId.Value, posterUrl, target).ConfigureAwait(false);
                            if (!posted)
                                Logger.Warn("Failed to upload collection poster by URL for collection {CollectionId} on {Server}:{Section}", collectionId.Value, target.ServerUrl, target.SectionId);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "UploadCollectionPosterByUrlAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId.Value, target.ServerUrl, target.SectionId);
                        }
                    }
                }
            }
        }

        private IEnumerable<string> ResolveSeriesVfsPaths(Shoko.Abstractions.Metadata.Shoko.IShokoSeries series)
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
    }
}
