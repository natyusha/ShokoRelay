using System.Text;

namespace ShokoRelay.Helpers;

/// <summary>Provides utilities for building in-memory diagnostic CSV reports.</summary>
public static class ReportHelper
{
    #region Report Generation

    /// <summary>Parallelized, per-thread memoized aggregator for building diagnostic CSV reports grouped by normalized key with merged variants appended after semicolons.</summary>
    /// <param name="title">Report title banner (e.g., "Tag" or "Credit").</param>
    /// <param name="columnName">Name of the item column (e.g., "tag" or "credit").</param>
    /// <param name="normalizeKey">Function generating a normalized lookup key from a raw name string.</param>
    /// <param name="merges">Dictionary mapping alias keys to canonical target names.</param>
    /// <param name="blacklist">Set of normalized blacklisted keys to exclude.</param>
    /// <param name="formatDisplayName">Optional formatting function for display names (e.g., TitleCase).</param>
    /// <param name="cleanRaw">Optional pre-cleaning function applied before normalization (e.g., stripping moderator notes).</param>
    internal sealed class CsvReportBuilder(
        string title,
        string columnName,
        Func<string, string> normalizeKey,
        IReadOnlyDictionary<string, string> merges,
        IReadOnlySet<string> blacklist,
        Func<string, string>? formatDisplayName = null,
        Func<string, string>? cleanRaw = null
    )
    {
        private readonly record struct ResolvedItem(string NormKey, string DisplayName, string? MergeTarget, bool IsBlacklisted);

        private sealed class VariantStats
        {
            public int Count;
        }

        private sealed class GroupStats
        {
            public string? MergeTarget;
            public readonly HashSet<int> SeriesIds = [];
            public readonly Dictionary<(int SourceOrder, string Source, string DisplayName), VariantStats> Variants = [];
        }

        private sealed class ThreadState
        {
            public readonly Dictionary<string, ResolvedItem> ResolveCache = new(StringComparer.Ordinal);
            public readonly Dictionary<string, GroupStats> Groups = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<(string Source, string NormKey, string DisplayName)> SeenInSeries = [];
        }

        private readonly Lock _mergeLock = new();
        private readonly Dictionary<string, GroupStats> _globalGroups = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Scans all supplied Shoko series in parallel and formats the aggregated results into a sorted CSV report string.</summary>
        /// <param name="seriesList">All Shoko series to evaluate.</param>
        /// <param name="collectSeries">Callback invoked per series to record its items via the supplied recorder delegate.</param>
        /// <returns>A CSV-formatted report string.</returns>
        public string Build(IEnumerable<IShokoSeries> seriesList, Action<IShokoSeries, Action<string, int, string?>> collectSeries)
        {
            Parallel.ForEach(
                seriesList,
                DefaultParallelOptions(),
                () => new ThreadState(),
                (series, _, local) =>
                {
                    local.SeenInSeries.Clear();
                    collectSeries(series, (source, sourceOrder, rawName) => RecordLocal(local, series.LocalID, source, sourceOrder, rawName));
                    return local;
                },
                local =>
                {
                    lock (_mergeLock)
                    {
                        foreach (var (normKey, localGroup) in local.Groups)
                        {
                            if (!_globalGroups.TryGetValue(normKey, out var globalGroup))
                                _globalGroups[normKey] = globalGroup = new GroupStats();

                            globalGroup.MergeTarget ??= localGroup.MergeTarget;
                            foreach (var sid in localGroup.SeriesIds)
                                globalGroup.SeriesIds.Add(sid);

                            foreach (var (vKey, vStats) in localGroup.Variants)
                            {
                                if (!globalGroup.Variants.TryGetValue(vKey, out var gv))
                                    globalGroup.Variants[vKey] = gv = new VariantStats();
                                gv.Count += vStats.Count;
                            }
                        }
                    }
                }
            );

            return FormatReport();
        }

        /// <summary>Resolves and records a single item into thread-local storage using per-thread memoization.</summary>
        /// <param name="local">Thread-local report accumulation state.</param>
        /// <param name="seriesId">The Shoko series identifier being evaluated.</param>
        /// <param name="source">Display name of the metadata source (e.g., "AniDB", "TMDB", "User").</param>
        /// <param name="sourceOrder">Numerical sort order for the source.</param>
        /// <param name="rawName">The raw candidate name string.</param>
        private void RecordLocal(ThreadState local, int seriesId, string source, int sourceOrder, string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return;

            if (!local.ResolveCache.TryGetValue(rawName, out var resolved))
            {
                string trimmed = (cleanRaw != null ? cleanRaw(rawName) : rawName).Trim();
                if (trimmed.Length == 0)
                {
                    local.ResolveCache[rawName] = new ResolvedItem(string.Empty, string.Empty, null, true);
                    return;
                }

                string origNorm = normalizeKey(trimmed);
                bool isMerged = merges.TryGetValue(origNorm, out var target);
                string targetStr = isMerged ? target! : trimmed;
                string normKey = isMerged ? normalizeKey(targetStr) : origNorm;
                bool isBlacklisted = normKey.Length == 0 || blacklist.Contains(normKey) || blacklist.Contains(origNorm);

                string displayName = !isBlacklisted && formatDisplayName != null ? formatDisplayName(trimmed) : trimmed;
                string? formattedTarget = !isBlacklisted && isMerged ? (formatDisplayName != null ? formatDisplayName(targetStr) : targetStr) : null;

                resolved = local.ResolveCache[rawName] = new ResolvedItem(normKey, displayName, formattedTarget, isBlacklisted);
            }

            if (resolved.IsBlacklisted || !local.SeenInSeries.Add((source, resolved.NormKey, resolved.DisplayName)))
                return;

            if (!local.Groups.TryGetValue(resolved.NormKey, out var group))
                local.Groups[resolved.NormKey] = group = new GroupStats();

            group.MergeTarget ??= resolved.MergeTarget;
            group.SeriesIds.Add(seriesId);

            var vKey = (sourceOrder, source, resolved.DisplayName);
            if (!group.Variants.TryGetValue(vKey, out var vStats))
                group.Variants[vKey] = vStats = new VariantStats();
            vStats.Count++;
        }

        /// <summary>Orders normalized groups and formats each line with merged variants separated by semicolons.</summary>
        /// <returns>The completed CSV report string.</returns>
        private string FormatReport()
        {
            var rows = new List<(int SourceOrder, string Name, int Count, string Line)>(_globalGroups.Count);

            foreach (var group in _globalGroups.Values)
            {
                if (group.Variants.Count == 0)
                    continue;

                var ordered = group
                    .Variants.Select(kvp => (kvp.Key.SourceOrder, kvp.Key.Source, kvp.Key.DisplayName, kvp.Value.Count))
                    .OrderByDescending(v => group.MergeTarget != null && string.Equals(v.DisplayName, group.MergeTarget, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(v => v.SourceOrder)
                    .ThenByDescending(v => v.Count)
                    .ThenBy(v => v.DisplayName.Length)
                    .ThenBy(v => v.DisplayName, StringComparer.Ordinal)
                    .ToList();

                var sbLine = new StringBuilder();
                int primaryOrder = ordered[0].SourceOrder;
                string primaryName = ordered[0].DisplayName;
                int primaryCount = ordered[0].Count;

                // If an explicit CSV merge target has no direct raw occurrences, prepend it as an Override entry
                if (group.MergeTarget != null && !ordered.Any(v => string.Equals(v.DisplayName, group.MergeTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    primaryName = group.MergeTarget;
                    primaryCount = group.SeriesIds.Count;
                    sbLine.Append($"Override,{TextHelper.EscapeCsvCommas(primaryName)},{primaryCount}");
                    foreach (var (sourceOrder, source, displayName, count) in ordered)
                        sbLine.Append($";{source},{TextHelper.EscapeCsvCommas(displayName)},{count}");
                }
                else
                {
                    sbLine.Append($"{ordered[0].Source},{TextHelper.EscapeCsvCommas(ordered[0].DisplayName)},{ordered[0].Count}");
                    for (int i = 1; i < ordered.Count; i++)
                        sbLine.Append($";{ordered[i].Source},{TextHelper.EscapeCsvCommas(ordered[i].DisplayName)},{ordered[i].Count}");
                }

                rows.Add((primaryOrder, primaryName, primaryCount, sbLine.ToString()));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## {title} Report ##").AppendLine();
            sb.AppendLine($"#source,{columnName},count");

            foreach (var (sourceOrder, name, count, line) in rows.OrderBy(r => r.SourceOrder).ThenByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(line);

            return sb.ToString();
        }
    }

    #endregion
}
