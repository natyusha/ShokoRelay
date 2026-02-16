using System.Collections.Concurrent;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Events;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Vfs
{
    public class VfsWatcher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IShokoEventHandler _events;
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
            IShokoEventHandler events,
            VfsBuilder builder,
            IMetadataService metadataService,
            PlexMetadata plexMetadata,
            PlexClient plexLibrary,
            PlexCollections plexCollections,
            ConfigProvider configProvider
        )
        {
            _events = events;
            _builder = builder;
            _metadataService = metadataService;
            _plexMetadata = plexMetadata;
            _plexLibrary = plexLibrary;
            _plexCollections = plexCollections;
            _configProvider = configProvider;
        }

        public void Start()
        {
            _events.FileMatched += OnFileChanged;
            _events.FileMoved += OnFileChanged;
            _events.FileRenamed += OnFileChanged;
            _events.FileDeleted += OnFileChanged;

            Logger.Info("VFS watcher started (auto-refresh on file changes).");
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
                        _builder.Build(seriesId, cleanRoot: false, pruneSeries: true);
                        Logger.Info("VFS refreshed for series {SeriesId}", seriesId);
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

        private IEnumerable<string> ResolveSeriesVfsPaths(Shoko.Plugin.Abstractions.DataModels.Shoko.IShokoSeries series)
        {
            var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            string rootName = VfsShared.ResolveRootFolderName();

            var fileData = MapHelper.GetSeriesFileData(series);
            foreach (var mapping in fileData.Mappings)
            {
                var location = mapping.Video.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Locations.FirstOrDefault();
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
