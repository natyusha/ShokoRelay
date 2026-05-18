using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.</summary>
public class AnimeThemesMapping(HttpClient httpClient, IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IVideoService _videoService = videoService;
    private readonly HttpClient _httpClient = httpClient;
    private readonly AnimeThemesApi _apiClient = new(httpClient);
    private readonly string _configDirectory = configProvider.ConfigDirectory;

    #endregion

    #region Public API

    /// <summary>Serialize a single AnimeThemesMappingEntry to a CSV line.</summary>
    /// <param name="entry">The entry to serialize.</param>
    /// <returns>A comma-separated string.</returns>
    public static string SerializeMappingEntry(AnimeThemesMappingEntry entry) => AnimeThemesHelper.SerializeEntry(entry);

    /// <summary>Download the mapping file from a direct raw URL and save it.</summary>
    /// <param name="rawUrl">Raw URL to download from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of entry count and a log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        try
        {
            var content = await _httpClient.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (0, "Downloaded content empty");

            await File.WriteAllTextAsync(Path.Combine(_configDirectory, ShokoRelayConstants.FileAtMapping), content, ct).ConfigureAwait(false);
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
            var roots = _videoService
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
            string mapPath = Path.Combine(_configDirectory, ShokoRelayConstants.FileAtMapping);
            var entries = new List<AnimeThemesMappingEntry>();
            var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(mapPath))
            {
                try
                {
                    foreach (var e in AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false)))
                        existing.TryAdd(e.FilePath, e);
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
                else
                    toProcess.Add((file, rel));
            }

            var messages = new List<string>();
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
                                lock (messages)
                                {
                                    errors++;
                                    messages.Add(idMissing ? $"AniDB ID missing for {item.Rel}" : $"Missing metadata for {item.Rel}");
                                }
                                return;
                            }
                            lock (entries)
                                entries.Add(
                                    new AnimeThemesMappingEntry(
                                        item.Rel,
                                        lookup.VideoId,
                                        lookup.AniDbId,
                                        lookup.NC,
                                        lookup.Slug,
                                        lookup.Version,
                                        lookup.ArtistName,
                                        lookup.SongTitle,
                                        lookup.Lyrics,
                                        lookup.Subbed,
                                        lookup.Uncen,
                                        lookup.NSFW,
                                        lookup.Spoiler,
                                        lookup.Source,
                                        lookup.Resolution,
                                        lookup.Episodes,
                                        lookup.Overlap
                                    )
                                );
                        }
                        catch (Exception ex)
                        {
                            lock (messages)
                            {
                                errors++;
                                messages.Add($"{item.Rel}: {ex.Message}");
                            }
                        }
                    }
                )
                .ConfigureAwait(false);

            var finalEntries = entries.GroupBy(e => e.FilePath).Select(g => g.First()).ToList();
            await File.WriteAllTextAsync(mapPath, AnimeThemesHelper.SerializeMapping(finalEntries), ct).ConfigureAwait(false);
            s_logger.Info("AnimeThemes: Finished mapping task -> {0} entries written.", finalEntries.Count);
            return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, entries.Count - toProcess.Count, errors, messages);
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
        var entry = new AnimeThemesMappingEntry(
            "/test/" + webmFileName,
            lookup.VideoId,
            lookup.AniDbId,
            lookup.NC,
            lookup.Slug,
            lookup.Version,
            lookup.ArtistName,
            lookup.SongTitle,
            lookup.Lyrics,
            lookup.Subbed,
            lookup.Uncen,
            lookup.NSFW,
            lookup.Spoiler,
            lookup.Source,
            lookup.Resolution,
            lookup.Episodes,
            lookup.Overlap
        );
        return (entry, null, AnimeThemesHelper.BuildNewFileName(lookup, Path.GetExtension(webmFileName)));
    }

    /// <summary>Read a previously built mapping file and create/update VFS links for matching theme files.</summary>
    /// <remarks>
    /// Prioritizes BD sources: if a filename collision occurs and a BD source is available, non-BD sources are skipped.
    /// If multiple BD sources collide (or no BD sources exist), they are de-duplicated with (2), (3), etc.
    /// </remarks>
    /// <param name="seriesFilter">Optional set of Shoko series IDs to limit processing to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts and results.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        try
        {
            TaskHelper.StartTask(ShokoRelayConstants.TaskAtVfsBuild);
            s_logger.Info("AnimeThemes VFS: Starting task...");
            string mapPath = Path.Combine(_configDirectory, ShokoRelayConstants.FileAtMapping);
            if (!File.Exists(mapPath))
                throw new FileNotFoundException("Mapping file not found");

            var entries = AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false));
            var (sw, state) = (Stopwatch.StartNew(), new MappingState());
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
            string vfsRoot = VfsShared.ResolveRootFolderName();

            var folderGroups = (seriesFilter?.Any() == true ? seriesFilter.Distinct().Select(_metadataService.GetShokoSeriesByID) : _metadataService.GetAllShokoSeries())
                .Where(s => s?.AnidbAnimeID > 0)
                .GroupBy(s => OverrideHelper.GetPrimary(s!.ID, _metadataService))
                .ToList();

            Parallel.ForEach(
                folderGroups,
                DefaultParallelOptions(ct),
                folderGroup =>
                {
                    ct.ThrowIfCancellationRequested();
                    int primaryId = folderGroup.Key;
                    var overrideOrder = OverrideHelper.GetGroup(primaryId, _metadataService).ToList();

                    var roots = folderGroup.SelectMany(s => PlexHelper.ResolveImportRoots(s!, _metadataService)).Distinct(VfsShared.PathComparer).ToList();
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
                            .Select(_metadataService.GetShokoSeriesByID)
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
                                        var lookup = new AnimeThemesVideoLookup(
                                            entry.VideoId,
                                            0,
                                            entry.AniDbId,
                                            entry.NC,
                                            entry.Slug,
                                            entry.Version,
                                            entry.ArtistName,
                                            entry.SongTitle,
                                            entry.Lyrics,
                                            entry.Subbed,
                                            entry.Uncen,
                                            entry.NSFW,
                                            entry.Spoiler,
                                            entry.Source,
                                            entry.Resolution,
                                            entry.Episodes,
                                            entry.Overlap
                                        );
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

    private static bool IsAllowed(AnimeThemesMappingEntry e, OverlapLevel level) => level == OverlapLevel.All || e.Overlap == "None" || (level == OverlapLevel.TransitionOnly && e.Overlap == "Transition");

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
