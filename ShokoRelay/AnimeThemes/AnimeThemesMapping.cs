using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.</summary>
public class AnimeThemesMapping(HttpClient httpClient, IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
{
    #region Setup & State

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly AnimeThemesApi _apiClient = new(httpClient);

    #endregion

    #region Public API

    /// <summary>Serialize a single AnimeThemesMappingEntry to a CSV line.</summary>
    /// <param name="entry">The entry to serialize.</param>
    /// <returns>A comma-separated string.</returns>
    public static string SerializeMappingEntry(AnimeThemesMappingEntry entry) => AnimeThemesHelper.SerializeEntry(entry);

    /// <summary>Loads and groups the AnimeThemes mapping entries, resolving their expected VFS filenames.</summary>
    /// <param name="configDirectory">The plugin configuration directory path.</param>
    /// <returns>A dictionary mapping AniDB ID to their expected VFS theme items.</returns>
    public static Dictionary<int, List<ThemeMapItem>> LoadThemeMappings(string configDirectory)
    {
        var mappings = new Dictionary<int, List<ThemeMapItem>>();
        string mapPath = Path.Combine(configDirectory, ShokoRelayConstants.FileAtMapping);
        if (!File.Exists(mapPath))
            return mappings;

        try
        {
            var entries = AnimeThemesHelper.ParseMappingContent(File.ReadAllText(mapPath));
            foreach (var entry in entries)
            {
                if (!mappings.TryGetValue(entry.AniDbId, out var list))
                    mappings[entry.AniDbId] = list = [];

                list.Add(new ThemeMapItem(AnimeThemesHelper.BuildNewFileName(new AnimeThemesVideoLookup(entry), ".webm"), entry.FilePath));
            }
        }
        catch { }

        return mappings;
    }

    /// <summary>Download the mapping file from a direct raw URL and save it.</summary>
    /// <param name="rawUrl">Raw URL to download from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of entry count and a log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        try
        {
            var content = await httpClient.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (0, "Downloaded content empty");

            await File.WriteAllTextAsync(Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping), content, ct).ConfigureAwait(false);
            int count = AnimeThemesHelper.ParseMappingContent(content).Count;
            return (count, $"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nUrl: {rawUrl}\nEntries: {count}");
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "AnimeThemes: Failed to import mapping from URL");
            return (0, "Import failed: " + ex.Message);
        }
    }

    /// <summary>Scan configured import roots for AnimeThemes files and write a mapping CSV.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A build result with statistics.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken ct = default)
    {
        TaskHelper.StartTask(ShokoRelayConstants.TaskAtMapBuild);
        s_logger.Info("AnimeThemes: Starting mapping task...");

        try
        {
            string themeFolder = VfsShared.ResolveAnimeThemesFolderName();
            var roots = videoService
                .GetAllVideoFiles()
                .Select(v => v.ManagedFolder?.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .Select(p => Path.Combine(p!, themeFolder))
                .Where(Directory.Exists)
                .ToList();
            if (roots.Count == 0)
                throw new DirectoryNotFoundException("AnimeThemes root folder not found");

            var sw = Stopwatch.StartNew();
            string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
            var entries = new List<AnimeThemesMappingEntry>();
            var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);
            var existingByFilename = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(mapPath))
            {
                try
                {
                    foreach (var e in AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false)))
                    {
                        existing.TryAdd(e.FilePath, e);
                        existingByFilename.TryAdd(Path.GetFileName(e.FilePath), e);
                    }
                }
                catch { }
            }

            var files = roots
                .SelectMany(root =>
                    Directory.EnumerateFiles(root, "*.webm", SearchOption.AllDirectories).Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("misc", StringComparison.OrdinalIgnoreCase)))
                )
                .ToList();
            var toProcess = new List<(string File, string Rel)>();

            foreach (string file in files)
            {
                string? root = roots.FirstOrDefault(r => file.StartsWith(r + Path.DirectorySeparatorChar));
                string rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/') : file;
                if (existing.TryGetValue(rel, out var old))
                    entries.Add(old);
                else if (existingByFilename.TryGetValue(Path.GetFileName(file), out var oldByName))
                    entries.Add(oldByName with { FilePath = rel });
                else
                    toProcess.Add((file, rel));
            }

            s_logger.Info("AnimeThemes: Found {0} total files ({1} cached, {2} pending mapping resolution)...", files.Count, existing.Count, toProcess.Count);

            var errorsList = new ConcurrentBag<string>();
            var newMappingsList = new ConcurrentBag<string>();
            int errors = 0;
            await Parallel
                .ForEachAsync(
                    toProcess,
                    DefaultParallelOptions(ct),
                    async (item, token) =>
                    {
                        try
                        {
                            var (lookup, idMissing) = await FetchMetadataAsync(Path.GetFileName(item.File), token).ConfigureAwait(false);
                            if (lookup == null)
                            {
                                string errMsg = idMissing ? $"AniDB ID missing for {item.Rel}" : $"Missing metadata for {item.Rel}";
                                errorsList.Add(errMsg);
                                s_logger.Warn("AnimeThemes: Failed to map '{0}' -> {1}", item.Rel, idMissing ? "AniDB ID missing" : "Metadata missing");
                                Interlocked.Increment(ref errors);
                                return;
                            }
                            lock (entries)
                                entries.Add(new AnimeThemesMappingEntry(item.Rel, lookup));
                            string mapMsg = $"Mapped: {item.Rel} -> VideoID: {lookup.VideoId}, AniDB ID: {lookup.AniDbId}";
                            newMappingsList.Add(mapMsg);
                            s_logger.Info("AnimeThemes: Mapped '{0}' -> VideoID: {1}, AniDB ID: {2}", item.Rel, lookup.VideoId, lookup.AniDbId);
                        }
                        catch (Exception ex)
                        {
                            string errMsg = $"{item.Rel}: {ex.Message}";
                            errorsList.Add(errMsg);
                            s_logger.Warn(ex, "AnimeThemes: Exception mapping '{0}' -> {1}", item.Rel, ex.Message);
                            Interlocked.Increment(ref errors);
                        }
                    }
                )
                .ConfigureAwait(false);

            var finalEntries = entries.GroupBy(e => e.FilePath).Select(g => g.First()).ToList();
            await File.WriteAllTextAsync(mapPath, AnimeThemesHelper.SerializeMapping(finalEntries), ct).ConfigureAwait(false);
            s_logger.Info("AnimeThemes: Finished mapping task -> {0} entries written.", finalEntries.Count);
            List<string> finalMessages = [.. errorsList.OrderBy(m => m), .. newMappingsList.OrderBy(m => m)];
            return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, entries.Count - toProcess.Count, errors, finalMessages);
        }
        finally
        {
            TaskHelper.FinishTask(ShokoRelayConstants.TaskAtMapBuild);
        }
    }

    /// <summary>Test the mapping process for a single webm filename without adding it to the CSV.</summary>
    /// <param name="webmFileName">The webm filename to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the entry, error, and generated filename.</returns>
    public async Task<(AnimeThemesMappingEntry? entry, string? error, string filename)> TestMappingEntryAsync(string webmFileName, CancellationToken ct = default)
    {
        var (lookup, idMissing) = await FetchMetadataAsync(webmFileName, ct).ConfigureAwait(false);
        if (lookup == null)
            return (null, idMissing ? "AniDB ID missing" : "Missing metadata", webmFileName);
        var entry = new AnimeThemesMappingEntry("/test/" + webmFileName, lookup);
        return (entry, null, AnimeThemesHelper.BuildNewFileName(lookup, Path.GetExtension(webmFileName)));
    }

    /// <summary>Read a previously built mapping file and create/update VFS links for matching theme files.</summary>
    /// <remarks>
    /// Prioritizes BD sources: if a filename collision occurs and a BD source is available, non-BD sources are skipped.
    /// If multiple BD sources collide (or no BD sources exist), they are de-duplicated with (2), (3), etc.
    /// </remarks>
    /// <param name="seriesFilter">Optional collection of series IDs to limit processing to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts and results.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        try
        {
            TaskHelper.StartTask(ShokoRelayConstants.TaskAtVfsBuild);
            s_logger.Info("AnimeThemes VFS: Starting task...");
            string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
            if (!File.Exists(mapPath))
                throw new FileNotFoundException("Mapping file not found");

            var entries = AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false));
            var (sw, state) = (Stopwatch.StartNew(), new MappingState());
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
            string vfsRoot = VfsShared.ResolveRootFolderName();

            var folderGroups = (seriesFilter?.Any() == true ? seriesFilter.Distinct().Select(metadataService.GetShokoSeriesByID) : metadataService.GetAllShokoSeries())
                .Where(s => s?.AnidbAnimeID > 0)
                .GroupBy(s => OverrideHelper.GetPrimary(s!.ID, metadataService))
                .ToList();

            Parallel.ForEach(
                folderGroups,
                DefaultParallelOptions(ct),
                folderGroup =>
                {
                    ct.ThrowIfCancellationRequested();
                    int primaryId = folderGroup.Key;
                    var overrideOrder = OverrideHelper.GetGroup(primaryId, metadataService).ToList();

                    var roots = folderGroup.SelectMany(s => PlexHelper.ResolveImportRoots(s!, metadataService)).Distinct(VfsShared.PathComparer).ToList();
                    if (!roots.Any())
                    {
                        Interlocked.Increment(ref state.Skipped);
                        return;
                    }

                    Interlocked.Increment(ref state.Matched);
                    var isFilteredRun = seriesFilter?.Any() == true;
                    var myPrefixes = folderGroup.Select(s => overrideOrder.IndexOf(s!.ID) is var idx && idx > 0 ? $"P{idx + 1} ❯ " : null).ToHashSet();

                    foreach (var root in roots)
                    {
                        string shortsDir = Path.Combine(root, vfsRoot, primaryId.ToString(), "Shorts");
                        var plannedFilenames = new HashSet<string>(VfsShared.PathComparer);

                        var potentialLinks = overrideOrder
                            .Select(metadataService.GetShokoSeriesByID)
                            .OfType<IShokoSeries>()
                            .SelectMany(series =>
                            {
                                int seriesIdx = overrideOrder.IndexOf(series.ID);
                                return entries
                                    .Where(e => e.AniDbId == series.AnidbAnimeID && IsAllowed(e, Settings.Advanced.AnimeThemesOverlapLevel))
                                    .Select(entry =>
                                    {
                                        string relPath = entry.FilePath.TrimStart('/', '\\');
                                        string? src = AnimeThemesHelper.ResolveThemeSourcePath(relPath, root, themeRootName);
                                        if (src == null)
                                            return null;
                                        var lookup = new AnimeThemesVideoLookup(entry);
                                        return new
                                        {
                                            Entry = entry,
                                            Lookup = lookup,
                                            Signature = Path.GetFileNameWithoutExtension(AnimeThemesHelper.BuildNewFileName(lookup with { NC = false }, "")),
                                            SourcePath = src,
                                            Extension = Path.GetExtension(src),
                                            RelativePath = relPath,
                                            SeriesIndex = seriesIdx,
                                            OriginalId = series.ID,
                                        };
                                    });
                            })
                            .Where(x => x != null)
                            .GroupBy(x => x!.Signature);

                        foreach (var sigGroup in potentialLinks)
                        {
                            // If any BD source exists, discard non-BD sources. Otherwise, keep all (TV/WEB/etc).
                            bool hasBD = sigGroup.Any(x => x!.Entry.Source == "BD");
                            var sourceSurvivors = hasBD ? [.. sigGroup.Where(x => x!.Entry.Source == "BD")] : sigGroup.ToList();

                            // NC Filtering: If enabled, prefer versions without credits over those with credits
                            var finalSurvivors = (Settings.Advanced.AnimeThemesPreferNc && sourceSurvivors.Any(x => x!.Entry.NC)) ? [.. sourceSurvivors.Where(x => x!.Entry.NC)] : sourceSurvivors;

                            int counter = 1;
                            foreach (var item in finalSurvivors)
                            {
                                string finalName = AnimeThemesHelper.BuildNewFileName(item!.Lookup, (counter > 1 ? $" ({counter})" : "") + item.Extension, item.SeriesIndex);
                                string destPath = VfsShared.NormalizeSeparators(Path.Combine(shortsDir, finalName));
                                lock (plannedFilenames)
                                    plannedFilenames.Add(finalName);

                                if (!isFilteredRun || folderGroup.Any(s => s!.ID == item.OriginalId))
                                {
                                    Directory.CreateDirectory(shortsDir);
                                    if (VfsShared.TryCreateLink(item.SourcePath, destPath, s_logger, targetOverride: AnimeThemesHelper.BuildThemeRelativeTarget(item.RelativePath, themeRootName)))
                                    {
                                        Interlocked.Increment(ref state.Created);
                                        lock (state.CacheEntries)
                                            state.CacheEntries.Add(new WebmCacheEntry(destPath, item.Entry.VideoId, AnimeThemesHelper.CalculateBitmask(item.Entry)));
                                    }
                                    else
                                        lock (state.Errors)
                                            state.Errors.Add($"Link failed: {destPath}");
                                }
                                counter++;
                            }
                        }

                        if (Directory.Exists(shortsDir))
                        {
                            foreach (var file in Directory.EnumerateFiles(shortsDir))
                            {
                                string fileName = Path.GetFileName(file);
                                if (AnimeThemesHelper.CreditsFileRegex.IsMatch(fileName) || plannedFilenames.Contains(fileName))
                                    continue;

                                // Protection for Plex Local Extras: Only delete the file if it's a symlink pointing to the AnimeThemes repository.
                                var info = new FileInfo(file);
                                if (!info.Exists || info.LinkTarget == null || !info.LinkTarget.Contains(themeRootName))
                                    continue;

                                if (isFilteredRun)
                                {
                                    bool isMyFile = false;
                                    if (AnimeThemesHelper.OverrideThemeFileRegex.IsMatch(fileName))
                                    {
                                        foreach (var p in myPrefixes)
                                            if (p != null && fileName.StartsWith(p))
                                                isMyFile = true;
                                    }
                                    else if (myPrefixes.Contains(null))
                                        isMyFile = true;

                                    if (!isMyFile)
                                        continue;
                                }
                                try
                                {
                                    File.Delete(file);
                                }
                                catch { }
                            }
                        }
                    }
                }
            );

            s_logger.Info("AnimeThemes VFS: Task finished -> {0} links created in {1}ms.", state.Created, sw.ElapsedMilliseconds);
            return new AnimeThemesMappingApplyResult(state.Created, state.Skipped, state.Matched, state.Errors, state.CacheEntries, sw.Elapsed);
        }
        finally
        {
            TaskHelper.FinishTask(ShokoRelayConstants.TaskAtVfsBuild);
        }
    }

    #endregion

    #region Internal Mapping Chk

    /// <summary>Determines if a theme mapping's overlap level is allowed based on the configured overlap restriction.</summary>
    /// <param name="e">The theme mapping entry to evaluate.</param>
    /// <param name="level">The maximum allowed level of overlap.</param>
    /// <returns>True if the theme's overlap satisfies the filter; otherwise, false.</returns>
    private static bool IsAllowed(AnimeThemesMappingEntry e, OverlapLevel level) =>
        level == OverlapLevel.All || e.Overlap.Equals("None", StringComparison.Ordinal) || (level == OverlapLevel.TransitionOnly && e.Overlap.Equals("Transition", StringComparison.Ordinal));

    #endregion

    #region Metadata Fetching

    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string fileName, CancellationToken ct)
    {
        var v = await _apiClient.FetchVideoWithArtistsAsync(fileName, ct).ConfigureAwait(false);
        if (v?.Video == null)
            return (null, false);
        var first = v.Video.Animethemeentries?.FirstOrDefault();
        if (first?.Animetheme == null)
            return (null, false);

        var anime = await _apiClient.FetchAnimeResourcesAsync(v.Video.Id, ct).ConfigureAwait(false);
        string[] seasonOrder = ["Winter", "Spring", "Summer", "Fall"];
        var best = anime
            ?.Anime?.Select(e => new
            {
                Entry = e,
                Year = e.Year ?? 9999,
                SVal = e.Season != null ? Array.IndexOf(seasonOrder, e.Season) : 9999,
                ids = e.Resources?.Where(r => string.Equals(r.Site, "aniDB", StringComparison.OrdinalIgnoreCase) && r.ExternalId.HasValue).Select(r => r.ExternalId!.Value).OrderBy(i => i).ToList(),
            })
            .Where(x => x.ids?.Count > 0)
            .OrderBy(x => x.Year)
            .ThenBy(x => x.SVal)
            .ThenBy(x => x.ids?.FirstOrDefault() ?? 9999)
            .FirstOrDefault();

        if (best != null)
        {
            var theme = first.Animetheme;
            return (
                new AnimeThemesVideoLookup(
                    v.Video.Id,
                    theme.Id,
                    best.ids![0],
                    v.Video.NC,
                    theme.Slug ?? "",
                    first.Version,
                    string.Join(" / ", theme.Song?.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)) ?? []),
                    theme.Song?.Title ?? "",
                    v.Video.Lyrics,
                    v.Video.Subbed,
                    v.Video.Uncen,
                    first.NSFW,
                    first.Spoiler,
                    v.Video.Source ?? "",
                    v.Video.Resolution ?? 0,
                    first.Episodes ?? "",
                    v.Video.Overlap ?? ""
                ),
                false
            );
        }
        return (null, true);
    }

    #endregion

    #region Internal Classes

    private class MappingState
    {
        public int Created,
            Skipped,
            Matched;
        public List<string> Errors = [];
        public List<WebmCacheEntry> CacheEntries = [];
    }

    #endregion
}
