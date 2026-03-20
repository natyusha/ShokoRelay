using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.</summary>
public class AnimeThemesMapping(IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IVideoService _videoService = videoService;
    private readonly AnimeThemesApi _apiClient = new();
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
            using var client = new HttpClient();
            AnimeThemesHelper.EnsureUserAgent(client);
            var content = await client.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (0, "Downloaded content empty");

            await File.WriteAllTextAsync(Path.Combine(_configDirectory, ShokoRelayConstants.FileAtMapping), content, ct).ConfigureAwait(false);
            int count = AnimeThemesHelper.ParseMappingContent(content).Count;
            return (count, $"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nUrl: {rawUrl}\nEntries: {count}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to import mapping from URL");
            return (0, "Import failed: " + ex.Message);
        }
    }

    /// <summary>Scan configured import roots for AnimeThemes files and write a mapping CSV.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A build result with statistics.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken ct = default)
    {
        const string taskName = ShokoRelayConstants.TaskAtMapBuild;
        TaskHelper.StartTask(taskName);
        Logger.Info("AnimeThemes Mapping: Starting scan...");

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
                    foreach (var e in AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct)))
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
            await Parallel.ForEachAsync(
                toProcess,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism), CancellationToken = ct },
                async (item, token) =>
                {
                    try
                    {
                        var (lookup, idMissing) = await FetchMetadataAsync(Path.GetFileName(item.File), token);
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
            );

            var finalEntries = entries.GroupBy(e => e.FilePath).Select(g => g.First()).ToList();
            await File.WriteAllTextAsync(mapPath, AnimeThemesHelper.SerializeMapping(finalEntries), ct);
            Logger.Info("AnimeThemes Mapping: Task finished. {0} entries written.", finalEntries.Count);
            return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, entries.Count - toProcess.Count, errors, messages);
        }
        finally
        {
            TaskHelper.FinishTask(taskName);
        }
    }

    /// <summary>Test the mapping process for a single webm filename without adding it to the CSV.</summary>
    /// <param name="webmFileName">The webm filename to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the entry, error, and generated filename.</returns>
    public async Task<(AnimeThemesMappingEntry? entry, string? error, string filename)> TestMappingEntryAsync(string webmFileName, CancellationToken ct = default)
    {
        var (lookup, idMissing) = await FetchMetadataAsync(webmFileName, ct);
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

    /// <summary>
    /// Read a previously built mapping file and create/update VFS links for matching theme files.
    /// Prioritizes BD sources: if a filename collision occurs and a BD source is available, non-BD sources are skipped.
    /// If multiple BD sources collide (or no BD sources exist), they are de-duplicated with (2), (3), etc.
    /// </summary>
    /// <param name="seriesFilter">Optional set of Shoko series IDs to limit processing to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts and results.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        const string taskName = ShokoRelayConstants.TaskAtVfsBuild;
        TaskHelper.StartTask(taskName);
        Logger.Info("AnimeThemes VFS: Starting task...");

        try
        {
            string mapPath = Path.Combine(_configDirectory, ShokoRelayConstants.FileAtMapping);
            if (!File.Exists(mapPath))
                throw new FileNotFoundException("Mapping file not found");

            var entries = AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct));
            var sw = Stopwatch.StartNew();
            var state = new MappingState();
            string themeRoot = VfsShared.ResolveAnimeThemesFolderName();
            string vfsRoot = VfsShared.ResolveRootFolderName();

            // Resolve which series we are processing
            var seriesList = (seriesFilter?.Any() == true ? seriesFilter.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)) : _metadataService.GetAllShokoSeries())
                .Where(s => s?.AnidbAnimeID > 0)
                .ToList();

            // Group by AniDB ID (the source of the themes)
            var groups = seriesList.GroupBy(s => s!.AnidbAnimeID).ToList();

            Parallel.ForEach(
                groups,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism), CancellationToken = ct },
                group =>
                {
                    ct.ThrowIfCancellationRequested();

                    var matchedThemes = entries.Where(e => e.AniDbId == group.Key && IsAllowed(e, ShokoRelay.Settings.Advanced.AnimeThemesOverlapLevel)).ToList();

                    if (!matchedThemes.Any())
                        return;

                    // Further group by the target Primary ID folder (VFS destination)
                    var targetFolders = group.GroupBy(s => OverrideHelper.GetPrimary(s!.ID, _metadataService));

                    foreach (var folderGroup in targetFolders)
                    {
                        int primaryId = folderGroup.Key;
                        var overrideOrder = OverrideHelper.GetGroup(primaryId, _metadataService).ToList();
                        var representativeSeries = folderGroup.First();

                        var roots = representativeSeries!
                            .Episodes.SelectMany(ep => ep.VideoList)
                            .SelectMany(v => v.Files)
                            .Select(VfsShared.ResolveImportRootPath)
                            .Where(r => !string.IsNullOrEmpty(r))
                            .Distinct()
                            .ToList();

                        if (!roots.Any())
                        {
                            Interlocked.Increment(ref state.Skipped);
                            continue;
                        }
                        Interlocked.Increment(ref state.Matched);
                        string shortsDir = Path.Combine(roots[0]!, vfsRoot, primaryId.ToString(), "Shorts");
                        var plannedFilenames = new HashSet<string>(VfsShared.PathComparer);

                        // Map potential theme entries to metadata-based names
                        var potentialLinks = folderGroup
                            .SelectMany(series =>
                            {
                                int seriesIdx = overrideOrder.IndexOf(series!.ID);
                                return matchedThemes.Select(entry =>
                                {
                                    string relPath = entry.FilePath.TrimStart('/', '\\');
                                    string? src = AnimeThemesHelper.ResolveThemeSourcePath(relPath, roots[0]!, themeRoot);
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

                                    // Content Signature: Used to group files that represent the same musical entry
                                    var signatureLookup = lookup with
                                    {
                                        NC = false,
                                    };
                                    string sigName = Path.GetFileNameWithoutExtension(AnimeThemesHelper.BuildNewFileName(signatureLookup, ""));

                                    return new
                                    {
                                        Entry = entry,
                                        Lookup = lookup,
                                        Signature = sigName,
                                        SourcePath = src,
                                        Extension = Path.GetExtension(src),
                                        RelativePath = relPath,
                                        SeriesIndex = seriesIdx,
                                    };
                                });
                            })
                            .Where(x => x != null)
                            .GroupBy(x => x!.Signature);

                        // Resolve collisions and perform linking
                        foreach (var sigGroup in potentialLinks)
                        {
                            // Logic: If any BD source exists, discard non-BD sources. Otherwise, keep all (TV/WEB/etc).
                            bool hasBD = sigGroup.Any(x => x!.Entry.Source == "BD");
                            var sourceSurvivors = hasBD ? [.. sigGroup.Where(x => x!.Entry.Source == "BD")] : sigGroup.ToList();

                            // NC Filtering: If enabled, prefer versions without credits over those with credits
                            var finalSurvivors = sourceSurvivors;
                            if (ShokoRelay.Settings.Advanced.AnimeThemesPreferNc && sourceSurvivors.Any(x => x!.Entry.NC))
                            {
                                finalSurvivors = [.. sourceSurvivors.Where(x => x!.Entry.NC)];
                            }

                            // De-duplicate survivors via numbering
                            int counter = 1;
                            foreach (var item in finalSurvivors)
                            {
                                string suffix = counter > 1 ? $" ({counter})" : "";
                                string finalName = Path.GetFileNameWithoutExtension(AnimeThemesHelper.BuildNewFileName(item!.Lookup, "", item.SeriesIndex)) + suffix + item.Extension;
                                string destPath = VfsShared.NormalizeSeparators(Path.Combine(shortsDir, finalName));

                                Directory.CreateDirectory(shortsDir);
                                lock (plannedFilenames)
                                    plannedFilenames.Add(finalName);

                                string targetOverride = AnimeThemesHelper.BuildThemeRelativeTarget(item.RelativePath, themeRoot);

                                if (VfsShared.TryCreateLink(item.SourcePath, destPath, Logger, targetOverride: targetOverride))
                                {
                                    Interlocked.Increment(ref state.Created);
                                    lock (state.CacheEntries)
                                        state.CacheEntries.Add(new WebmCacheEntry(destPath, item.Entry.VideoId, AnimeThemesHelper.CalculateBitmask(item.Entry)));
                                    Logger.Info("AnimeThemes VFS: Created link '{0}' (VideoID: {1})", Path.GetFileName(destPath), item.Entry.VideoId);
                                }
                                else
                                {
                                    lock (state.Errors)
                                        state.Errors.Add($"Link failed: {destPath}");
                                }

                                counter++;
                            }
                        }

                        // Per-Series Cleanup: Remove orphans in the Shorts folder that were not part of this generation run.
                        if (Directory.Exists(shortsDir))
                        {
                            foreach (var file in Directory.EnumerateFiles(shortsDir))
                            {
                                string fileName = Path.GetFileName(file);
                                if (AnimeThemesHelper.CreditsFileRegex.IsMatch(fileName) || plannedFilenames.Contains(fileName)) // Protection: Skip regular VFS credits (e.g., C1 ❯ Title.ext) and current planned themes
                                    continue;

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

            Logger.Info("AnimeThemes VFS: Task finished. {0} links created in {1}ms.", state.Created, sw.ElapsedMilliseconds);
            return new AnimeThemesMappingApplyResult(state.Created, state.Skipped, state.Matched, state.Errors, state.CacheEntries, sw.Elapsed);
        }
        finally
        {
            TaskHelper.FinishTask(taskName);
        }
    }

    #endregion

    #region Internal Mapping Chk

    /// <summary>Checks if a mapping entry is allowed based on user overlap preferences.</summary>
    private static bool IsAllowed(AnimeThemesMappingEntry e, OverlapLevel level) => level == OverlapLevel.All || e.Overlap == "None" || (level == OverlapLevel.TransitionOnly && e.Overlap == "Transition");

    #endregion

    #region Metadata Fetching

    /// <summary>Fetches theme metadata from the AnimeThemes API.</summary>
    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string fileName, CancellationToken ct)
    {
        var v = await _apiClient.FetchVideoWithArtistsAsync(fileName, ct);
        if (v?.Video == null)
            return (null, false);
        var first = v.Video.Animethemeentries?.FirstOrDefault();
        if (first?.Animetheme == null)
            return (null, false);

        var anime = await _apiClient.FetchAnimeResourcesAsync(v.Video.Id, ct);
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
