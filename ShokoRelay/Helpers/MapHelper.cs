using System.Collections.Concurrent;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using ShokoRelay.Plex;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Helpers
{
    public static class MapHelper
    {
        public record FileMapping(IVideo Video, IReadOnlyList<IEpisode> Episodes, IEpisode PrimaryEpisode, PlexCoords Coords, string FileName, int? PartIndex, int PartCount, object? TmdbEpisode);

        public record SeriesFileData(List<FileMapping> Mappings, List<int> Seasons)
        {
            public List<FileMapping> GetForSeason(int season) => Mappings.Where(m => m.Coords.Season == season).OrderBy(m => m.Coords.Episode).ToList();
        }

        public static SeriesFileData GetSeriesFileData(ISeries series)
        {
            var mappings = BuildFileMappings(series);
            var seasons = mappings.Select(m => m.Coords.Season).Distinct().OrderBy(s => s).ToList();
            return new SeriesFileData(mappings, seasons);
        }

        private static List<FileMapping> BuildFileMappings(ISeries series)
        {
            var result = new List<FileMapping>();

            // Cache VideoList and filter once
            var seriesEpisodes = new List<(IEpisode Episode, List<IVideo> Videos)>();
            foreach (var e in series.Episodes)
            {
                var videos = e.VideoList.ToList();
                if (videos.Count > 0 && !IsHidden(e))
                    seriesEpisodes.Add((e, videos));
            }

            if (seriesEpisodes.Count == 0)
                return result;

            // Cache the series' TMDB preferred ordering id (used repeatedly when applying TMDB episode-numbering).
            // Normalize away the default show-ordering (Shoko's API sometimes exposes the show id as the
            // PreferredOrdering.OrderingID when no alternate ordering is selected). Only keep a non-default
            // ordering id if it's genuinely an alternate ordering.
            string? seriesPrefOrderingId = null;
            if (ShokoRelay.Settings.TMDBEpNumbering)
            {
                var firstShokoEp = seriesEpisodes.Select(x => x.Episode).OfType<IShokoEpisode>().FirstOrDefault();
                var tmdbShow = firstShokoEp?.Series?.TmdbShows?.FirstOrDefault();
                var pref = tmdbShow?.PreferredOrdering?.OrderingID;
                if (!string.IsNullOrWhiteSpace(pref) && tmdbShow != null)
                {
                    // If PreferredOrdering.OrderingID equals the show's own ordering id (i.e. the show id string),
                    // treat it as "no alternate ordering" and ignore.
                    var showDefaultOrderingId = tmdbShow.ID.ToString();
                    if (!string.Equals(pref, showDefaultOrderingId, StringComparison.OrdinalIgnoreCase))
                        seriesPrefOrderingId = pref;
                }
            }

            // Build episode file lists from cached videos
            var episodeFileLists = seriesEpisodes.ToDictionary(x => x.Episode.ID, x => x.Videos.OrderBy(v => Path.GetFileName(v.Files.FirstOrDefault()?.Path ?? "")).ToList());

            // Build episode lookup by video ID (also cache which seasons have files so Featurettes fallback can be O(1))

            // Precompute episode coords in parallel to reduce wall-time when GetPlexCoordinates performs lazy loads
            var coordsByEpisode = new ConcurrentDictionary<int, PlexCoords>();
            Parallel.ForEach(
                seriesEpisodes,
                pair =>
                {
                    var episode = pair.Episode;
                    var coords = GetPlexCoordinates(episode, seriesPrefOrderingId);
                    coordsByEpisode[episode.ID] = coords;
                }
            );

            // Build video->episode mapping in parallel into a concurrent structure, then materialize to a Dictionary
            var videoToEpisodesConcurrent = new ConcurrentDictionary<int, ConcurrentBag<(IEpisode Episode, PlexCoords Coords)>>();
            var seasonsSet = new ConcurrentDictionary<int, byte>();

            Parallel.ForEach(
                seriesEpisodes,
                pair =>
                {
                    var episode = pair.Episode;
                    var videos = pair.Videos;
                    var coords = coordsByEpisode[episode.ID];

                    seasonsSet.TryAdd(coords.Season, 0);

                    foreach (var video in videos)
                    {
                        var bag = videoToEpisodesConcurrent.GetOrAdd(video.ID, _ => new ConcurrentBag<(IEpisode Episode, PlexCoords Coords)>());
                        bag.Add((episode, coords));
                    }
                }
            );

            // Materialize concurrent results into the expected Dictionary<int, List<...>> shape
            var videoToEpisodes = videoToEpisodesConcurrent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());

            var seasonsWithFiles = new HashSet<int>(seasonsSet.Keys);
            bool hasSeasonOther = seasonsWithFiles.Contains(PlexConstants.SeasonOther);

            // video->episodes mapping built

            // Only compute season-occupancy flags when we have SeasonOther episodes (Featurettes fallback only matters then)
            bool cachedSeason1HasFiles = false;
            bool cachedSeason0HasFiles = false;
            if (hasSeasonOther)
            {
                cachedSeason1HasFiles = seasonsWithFiles.Contains(PlexConstants.SeasonStandard);
                cachedSeason0HasFiles = seasonsWithFiles.Contains(PlexConstants.SeasonSpecials);
            }

            // Get unique videos sorted by filename
            var allVideos = seriesEpisodes.SelectMany(x => x.Videos).GroupBy(v => v.ID).Select(g => g.First()).OrderBy(v => Path.GetFileName(v.Files.FirstOrDefault()?.Path ?? "")).ToList();

            foreach (var video in allVideos)
            {
                if (!videoToEpisodes.TryGetValue(video.ID, out var epList) || epList.Count == 0)
                {
                    continue;
                }

                // If a single file maps to multiple episodes with differing episode types,
                // the first type listed in the file's cross-reference ordering (EpisodeOrder)
                // is authoritative — when present we filter to that type and do not apply other tiebreaks.
                if (epList.Count > 1 && epList.Select(x => x.Episode.Type).Distinct().Count() > 1)
                {
                    var epIdSet = epList.Select(x => x.Episode.ID).ToHashSet();

                    // Find the first cross-reference that corresponds to one of the episodes in epList
                    var firstXref = video.CrossReferences?.FirstOrDefault(cr => cr.ShokoEpisode != null && epIdSet.Contains(cr.ShokoEpisode!.ID));
                    if (firstXref?.ShokoEpisode != null)
                    {
                        var preferredEpisodeId = firstXref.ShokoEpisode.ID;
                        var primaryType = epList.First(x => x.Episode.ID == preferredEpisodeId).Episode.Type;
                        epList = epList.Where(x => x.Episode.Type == primaryType).ToList();
                    }
                    // If no matching cross-reference found, do not filter by type (preserve all relations)
                }

                // Sort by pre-computed coords
                var sortedEps = epList.OrderBy(x => x.Coords.Season).ThenBy(x => x.Coords.Episode).ToList();

                // Use shared helper for primary-type selection + dedupe (same logic used by the debug path).
                var (filteredEps, deduped) = FilterAndDedupeByPrimaryType(sortedEps, video);

                if (deduped.Count == 0)
                {
                    continue;
                }

                var (firstEp, _) = deduped[0];
                var epId = firstEp.ID;
                var episodes = deduped.Select(x => x.Episode).ToList();

                int fileIndex = episodeFileLists.TryGetValue(epId, out var files) ? files.FindIndex(x => x.ID == video.ID) : 0;

                int fileCount = files?.Count ?? 1;

                // Access the primary file path once (avoid repeated property access that may trigger lazy loads)
                var firstFile = video.Files?.FirstOrDefault();
                var firstPath = firstFile?.Path;

                string fileName = Path.GetFileName(firstPath ?? "");

                bool allowPartSuffix = ComputeAllowPartSuffix(deduped, fileCount, fileName);

                int? fileIndexParam = allowPartSuffix && fileCount > 1 ? fileIndex : null;

                PlexCoords coords;

                if (deduped.Count == 1)
                {
                    // Reuse the episode's precomputed coords for the common single-ep case,
                    // but if this is a multi-part file and TMDB episode-numbering is enabled
                    // prefer a file-specific TMDB selection (avoids returning episode-level ranges).
                    var ded0 = deduped[0];
                    bool preferFileSpecificTmdb = false;
                    if (fileCount > 1 && ShokoRelay.Settings.TMDBEpNumbering && ded0.Episode is IShokoEpisode shokoEpisodeLocal && shokoEpisodeLocal.TmdbEpisodes?.Any() == true)
                    {
                        // Only use the series-level preferred ordering (do not probe per-episode); MapHelper
                        // computed `seriesPrefOrderingId` once above and normalized default ordering ids away.
                        string? showPrefId = seriesPrefOrderingId;
                        var tmdbEps = string.IsNullOrWhiteSpace(showPrefId)
                            ? shokoEpisodeLocal.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList()
                            : SelectPreferredTmdbOrdering(shokoEpisodeLocal.TmdbEpisodes, showPrefId);

                        if (fileIndex < tmdbEps.Count)
                            preferFileSpecificTmdb = true;
                    }

                    if (preferFileSpecificTmdb)
                        coords = GetPlexCoordinatesForFile(episodes, fileIndexParam);
                    else
                        coords = ded0.Coords;
                }
                else
                {
                    coords = GetPlexCoordinatesForFile(episodes, fileIndexParam);
                }

                // Apply Featurettes fallback rule only when the file would be placed into Featurettes (SeasonOther)
                if (coords.Season == PlexConstants.SeasonOther)
                    coords = ApplyFeaturettesSeasonFallbackCached(coords, cachedSeason1HasFiles, cachedSeason0HasFiles);

                int? partIndex = allowPartSuffix && fileCount > 1 ? fileIndex + 1 : null;
                int partCount = allowPartSuffix ? fileCount : 1;

                object? tmdbEpisode = null;
                if (fileCount > 1 && ShokoRelay.Settings.TMDBEpNumbering && firstEp is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    string? showPrefId = seriesPrefOrderingId ?? shokoEp.Series?.TmdbShows?.FirstOrDefault()?.PreferredOrdering?.OrderingID;
                    var tmdbEps = string.IsNullOrWhiteSpace(showPrefId)
                        ? shokoEp.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList()
                        : SelectPreferredTmdbOrdering(shokoEp.TmdbEpisodes, showPrefId);

                    if (fileIndex < tmdbEps.Count)
                    {
                        var sel = tmdbEps[fileIndex];
                        var ord = Plex.PlexMapping.GetOrderingCoords(sel, showPrefId);
                        tmdbEpisode = new
                        {
                            SeasonNumber = ord.Season,
                            EpisodeNumber = ord.Episode,
                            PreferredTitle = sel.PreferredTitle?.Value,
                        };
                    }
                }

                result.Add(new FileMapping(video, episodes, firstEp, coords, fileName, partIndex, partCount, tmdbEpisode));
            }

            return result;
        }

        public static bool IsHidden(IEpisode e)
        {
            return e is IShokoEpisode shokoEp && shokoEp.IsHidden;
        }

        // Select primary episode type and deduplicate coordinates.
        // Returns (filteredEps, dedupedEps).
        private static (List<(IEpisode Episode, PlexCoords Coords)> Filtered, List<(IEpisode Episode, PlexCoords Coords)> Deduped) FilterAndDedupeByPrimaryType(
            List<(IEpisode Episode, PlexCoords Coords)> sortedEps,
            IVideo? video
        )
        {
            // If this video maps to multiple episodes of differing AniDB/TMDB types,
            // the first type listed in the video's cross-reference ordering (EpisodeOrder)
            // is authoritative — when present we filter to that type and do not apply other tiebreaks.
            if (sortedEps.Count > 1 && sortedEps.Select(x => x.Episode.Type).Distinct().Count() > 1 && video?.CrossReferences?.Count > 0)
            {
                var epIdSet = sortedEps.Select(x => x.Episode.ID).ToHashSet();
                var firstXref = video.CrossReferences.FirstOrDefault(cr => cr.ShokoEpisode != null && epIdSet.Contains(cr.ShokoEpisode!.ID));
                if (firstXref?.ShokoEpisode != null)
                {
                    var preferredEpisodeId = firstXref.ShokoEpisode.ID;
                    var primaryType = sortedEps.First(x => x.Episode.ID == preferredEpisodeId).Episode.Type;
                    sortedEps = sortedEps.Where(x => x.Episode.Type == primaryType).ToList();
                }
            }

            List<(IEpisode Episode, PlexCoords Coords)> filteredEps;

            var distinctTypes = sortedEps.Select(x => x.Episode.Type).Distinct().ToList();
            if (distinctTypes.Count > 1)
            {
                // Ambiguous multi-type relations — no fallback logic applies (file's first cross-ref is authoritative).
                // Treat as ambiguous and skip mapping for this file.
                filteredEps = new List<(IEpisode Episode, PlexCoords Coords)>();
            }
            else
            {
                filteredEps = new List<(IEpisode Episode, PlexCoords Coords)>(sortedEps);
            }

            // Compute file cross-reference position map once and reuse for dedupe + primary selection.
            Dictionary<int, int>? xrefPosMap = null;
            if (video?.CrossReferences?.Count > 0)
            {
                try
                {
                    xrefPosMap = video.CrossReferences.Where(cr => cr.ShokoEpisode != null).Select((cr, idx) => (id: cr.ShokoEpisode!.ID, idx)).ToDictionary(t => t.id, t => t.idx);
                }
                catch
                {
                    xrefPosMap = null;
                }
            }

            var deduped = DeduplicateByCoords(filteredEps, video, xrefPosMap);

            // Prefer the episode that appears earliest in the file's cross-reference list
            // (this matches the behavior consumers see from the server API `/File/{id}/Episode`).
            // We only reorder the `deduped` list so the primary selection (deduped[0]) will
            // reflect the file's cross-reference precedence without changing dedupe semantics.
            if (xrefPosMap != null && deduped.Count > 1)
            {
                try
                {
                    ReorderListByXref(xrefPosMap, deduped);
                }
                catch
                {
                    // ignore any ordering failures and fall back to existing deterministic behavior
                }
            }

            /* FilterAndDedupeByPrimaryType diagnostics removed */
            return (filteredEps, deduped);
        }

        // Determine whether Plex-style part suffix is allowed for this file
        private static bool ComputeAllowPartSuffix(List<(IEpisode Episode, PlexCoords Coords)> deduped, int fileCount, string fileName)
        {
            if (fileCount > 1 && TextHelper.HasPlexSplitTag(fileName))
            {
                var types = deduped.Select(d => d.Episode.Type).Distinct().ToList();
                return types.Count <= 1;
            }
            return false;
        }

        // Extracted helpers to simplify primary-type selection and deduplication logic.
        private static List<(IEpisode Episode, PlexCoords Coords)> DeduplicateByCoords(
            List<(IEpisode Episode, PlexCoords Coords)> filteredEps,
            IVideo? video = null,
            Dictionary<int, int>? xrefPosMap = null
        )
        {
            // Deduplicate by (season, episode). When multiple episodes map to the same coordinates,
            // prefer episodes based on explicit episode relations where possible.
            var deduped = new List<(IEpisode Episode, PlexCoords Coords)>();
            var coordIndex = new Dictionary<(int Season, int Episode), int>();

            for (int i = 0; i < filteredEps.Count; i++)
            {
                var entry = filteredEps[i];
                var key = (entry.Coords.Season, entry.Coords.Episode);

                if (!coordIndex.TryGetValue(key, out var existingIdx))
                {
                    coordIndex[key] = deduped.Count;
                    deduped.Add(entry);
                    continue;
                }

                // Tie: decide whether to replace the existing selected episode for this coord.
                var existing = deduped[existingIdx];

                // Absolute file cross-reference precedence: if the file lists either the existing or the
                // current entry in its CrossReferences, prefer whichever appears earlier in that list.
                // This makes the file's cross-reference ordering authoritative (no fallback overrides).
                Dictionary<int, int>? localPosMap = xrefPosMap;
                if (localPosMap == null && video?.CrossReferences?.Count > 0)
                {
                    try
                    {
                        localPosMap = video.CrossReferences.Where(cr => cr.ShokoEpisode != null).Select((cr, idx) => (id: cr.ShokoEpisode!.ID, idx)).ToDictionary(t => t.id, t => t.idx);
                    }
                    catch
                    {
                        localPosMap = null;
                    }
                }

                if (localPosMap != null)
                {
                    int existingPos = localPosMap.TryGetValue(existing.Episode.ID, out var epPos) ? epPos : -1;
                    int entryPos = localPosMap.TryGetValue(entry.Episode.ID, out var enPos) ? enPos : -1;

                    if (existingPos >= 0 || entryPos >= 0)
                    {
                        if (existingPos >= 0 && (entryPos == -1 || existingPos <= entryPos))
                        {
                            // existing appears earlier (or only existing is referenced) -> keep it
                            continue;
                        }

                        if (entryPos >= 0 && (existingPos == -1 || entryPos < existingPos))
                        {
                            // entry appears earlier -> replace existing with entry
                            deduped[existingIdx] = entry;
                            continue;
                        }
                    }
                }
            }

            return deduped;
        }

        private static void ReorderListByXref(Dictionary<int, int> xrefPosMap, List<(IEpisode Episode, PlexCoords Coords)> list)
        {
            if (xrefPosMap == null || list == null || list.Count <= 1)
                return;
            int bestIdx = -1;
            int bestPos = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var id = list[i].Episode.ID;
                if (xrefPosMap.TryGetValue(id, out var pos) && pos < bestPos)
                {
                    bestPos = pos;
                    bestIdx = i;
                }
            }
            if (bestIdx > 0)
            {
                var preferred = list[bestIdx];
                list.RemoveAt(bestIdx);
                list.Insert(0, preferred);
            }
        }

        // If a file would be placed into Featurettes (SeasonOther), prefer to place it into Season 1 if Season 1 is empty;
        // otherwise, if Season 1 has files, prefer Season 0 (Specials) if empty; otherwise leave in Featurettes.
        private static PlexCoords ApplyFeaturettesSeasonFallback(ISeries? series, PlexCoords coords)
        {
            // Backward-compatible (rare) path kept for debug/endpoints that call this directly.
            // If we don't have series context, leave coords unchanged.
            if (series == null)
                return coords;

            if (coords.Season != PlexConstants.SeasonOther)
                return coords;

            // Determine whether season 1 and season 0 currently have any episodes with files (non-hidden)
            bool season1HasFiles = series.Episodes.Any(e => !IsHidden(e) && (e.VideoList?.Any() ?? false) && GetPlexCoordinates(e).Season == PlexConstants.SeasonStandard);
            if (!season1HasFiles)
            {
                coords.Season = PlexConstants.SeasonStandard; // move Other -> Season 1 (normal episodes)
                return coords;
            }

            bool season0HasFiles = series.Episodes.Any(e => !IsHidden(e) && (e.VideoList?.Any() ?? false) && GetPlexCoordinates(e).Season == PlexConstants.SeasonSpecials);
            if (!season0HasFiles)
            {
                coords.Season = PlexConstants.SeasonSpecials; // move Other -> Specials
                return coords;
            }

            // Both Season 1 and Specials have files — keep in Featurettes (SeasonOther)
            return coords;
        }

        // Fast variant that uses precomputed per-series occupancy flags to avoid O(N) scans when called repeatedly
        private static PlexCoords ApplyFeaturettesSeasonFallbackCached(PlexCoords coords, bool season1HasFiles, bool season0HasFiles)
        {
            if (coords.Season != PlexConstants.SeasonOther)
                return coords;

            if (!season1HasFiles)
            {
                coords.Season = PlexConstants.SeasonStandard;
                return coords;
            }

            if (!season0HasFiles)
            {
                coords.Season = PlexConstants.SeasonSpecials;
                return coords;
            }

            return coords;
        }

        // Debug helpers removed (Debug endpoints have been removed from the API).
        // Removed: EpisodeDebugInfo, FileMappingDebugInfo, FileMappingSummary, TmdbEpisodeSummary and GetFileMappingDebug.
    }
}
