using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Events;

namespace ShokoRelay.Meta
{
    public class VfsWatcher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IShokoEventHandler _events;
        private readonly VfsBuilder _builder;

        private readonly ConcurrentDictionary<int, byte> _pending = new();
        private bool _processing;
        private readonly object _gate = new();

        public VfsWatcher(IShokoEventHandler events, VfsBuilder builder)
        {
            _events = events;
            _builder = builder;
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
                if (_processing) return;
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
                    if (!_pending.Keys.Any()) break;
                    seriesId = _pending.Keys.First();
                    _pending.TryRemove(seriesId, out _);

                    try
                    {
                        _builder.Build(seriesId, cleanRoot: false, dryRun: false, pruneSeries: true);
                        Logger.Info("VFS refreshed for series {SeriesId}", seriesId);
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
    }
}
