using System.Collections.Concurrent;

namespace ShokoRelay.Helpers;

/// <summary>Provides shared global name caching and parallel cache-warming utilities across series metadata helpers.</summary>
internal static class CacheHelper
{
    #region Global Name Cache

    /// <summary>Tracks display name variants and series counts across multiple series for global cross-series deduplication.</summary>
    internal sealed class GlobalNameEntry
    {
        /// <summary>Synchronization lock for thread-safe variant recording.</summary>
        private readonly Lock _lock = new();

        /// <summary>Lowest numerical priority recorded so far (1 = AniDB, 2 = TMDB, 3 = User).</summary>
        private int _priority = int.MaxValue;

        /// <summary>Current winning display name across all recorded series.</summary>
        private string _bestDisplayName = string.Empty;

        /// <summary>Number of series using the winning display name at the winning priority.</summary>
        private int _bestCount;

        /// <summary>Counts per distinct casing/spelling variant at the current priority level.</summary>
        private readonly Dictionary<string, int> _variantCounts = new(StringComparer.Ordinal);

        /// <summary>Resolves the preferred display name, optionally recording a new series observation or upgrading to a higher-priority source.</summary>
        /// <param name="variant">The formatted display variant string.</param>
        /// <param name="priority">The source priority value (lower numerical values indicate higher priority).</param>
        /// <param name="record">True to record a new observation; false to read the cached winner unless a higher-priority source arrives.</param>
        /// <param name="count">The number of series observations to record when <paramref name="record"/> is true.</param>
        /// <returns>The preferred display name for the normalized key.</returns>
        public string ResolveDisplayName(string variant, int priority, bool record, int count = 1)
        {
            lock (_lock)
                return (record || priority < _priority || string.IsNullOrEmpty(_bestDisplayName)) ? RecordLocked(variant, priority, count) : _bestDisplayName;
        }

        /// <summary>Records an incoming variant under lock and returns the updated winning display name.</summary>
        /// <param name="variant">The formatted display variant string.</param>
        /// <param name="priority">The source priority value (lower numerical values indicate higher priority).</param>
        /// <param name="weight">The number of series observations to add for this variant.</param>
        /// <returns>The preferred display name for the normalized key.</returns>
        private string RecordLocked(string variant, int priority, int weight)
        {
            // Higher-priority source (e.g. AniDB 1 beats TMDB 2)
            if (priority < _priority)
            {
                _priority = priority;
                _variantCounts.Clear();
                _variantCounts[variant] = weight;
                _bestDisplayName = variant;
                _bestCount = weight;
                return _bestDisplayName;
            }

            // Same priority: choose the variant used on the highest number of series, breaking ties deterministically by length then ordinal order
            if (priority == _priority)
            {
                int count = _variantCounts.GetValueOrDefault(variant, 0) + weight;
                _variantCounts[variant] = count;
                if (
                    count > _bestCount
                    || (
                        count == _bestCount
                        && (variant.Length < _bestDisplayName.Length || (variant.Length == _bestDisplayName.Length && string.Compare(variant, _bestDisplayName, StringComparison.Ordinal) < 0))
                    )
                )
                {
                    _bestDisplayName = variant;
                    _bestCount = count;
                }
                return _bestDisplayName;
            }

            // Lower-priority source arriving when a higher-priority display name is already cached
            return _bestDisplayName;
        }
    }

    #endregion

    #region Cache Warming

    /// <summary>Executes a parallel, lock-free thread-local scan over all Shoko series for cache warming, skipping already-recorded series IDs.</summary>
    /// <typeparam name="TState">The thread-local state type created per worker thread.</typeparam>
    /// <param name="metadataService">The metadata service used to enumerate all Shoko series.</param>
    /// <param name="recordedSeriesIds">Tracking set of series IDs already recorded into the cache.</param>
    /// <param name="processSeries">Callback executed per unrecorded series to populate its thread-local state.</param>
    /// <param name="mergeState">Sequential callback invoked after parallel execution to merge each thread-local state into shared caches.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static void WarmSeriesCache<TState>(
        IMetadataService metadataService,
        ConcurrentDictionary<int, byte> recordedSeriesIds,
        Action<IShokoSeries, TState> processSeries,
        Action<TState> mergeState,
        CancellationToken cancellationToken = default
    )
        where TState : new()
    {
        var allSeries = metadataService.GetAllShokoSeries() ?? [];
        var localStates = new ConcurrentBag<TState>();

        Parallel.ForEach(
            allSeries,
            DefaultParallelOptions(cancellationToken),
            () =>
            {
                var ts = new TState();
                localStates.Add(ts);
                return ts;
            },
            (series, _, local) =>
            {
                if (recordedSeriesIds.TryAdd(series.LocalID, 0))
                    processSeries(series, local);
                return local;
            },
            _ => { }
        );

        foreach (var local in localStates)
            mergeState(local);
    }

    #endregion
}
