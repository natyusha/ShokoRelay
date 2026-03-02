using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Represents a single anime theme mapping between a local file path and AniDB/video identifiers.
/// </summary>
public sealed record AnimeThemesMappingEntry(
    [property: JsonPropertyName("filepath")] string FilePath,
    [property: JsonPropertyName("videoId")] int VideoId,
    [property: JsonPropertyName("anidbId")] int AniDbId,
    [property: JsonPropertyName("newFilename")] string NewFileName
);

/// <summary>
/// Result returned by a mapping file build operation, including statistics and messages.
/// </summary>
public sealed record AnimeThemesMappingBuildResult(string MapPath, int EntriesWritten, int Reused, int Errors, IReadOnlyList<string> Messages);

/// <summary>
/// Outcome of applying a mapping file to create VFS links, detailing how many links were created or skipped.
/// </summary>
public sealed record AnimeThemesMappingApplyResult(int LinksCreated, int Skipped, int SeriesMatched, IReadOnlyList<string> Errors, IReadOnlyList<string> Planned, TimeSpan Elapsed);

/// <summary>
/// Internal helper record used when looking up theme metadata by video identifier.
/// </summary>
internal sealed record AnimeThemesVideoLookup(int VideoId, int ThemeId, int AniDbId, string Slug, string SongTitle, string Tags);

/// <summary>
/// Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers. Includes helpers for importing mapping data and querying the AnimeThemes API.
/// </summary>
public class AnimeThemesMapping
{
    // helpers for reading/writing the mapping file in CSV form. The format uses simple comma-separated rows; commas inside values are escaped as "\u002C" by the generators.
    private static List<AnimeThemesMappingEntry> ParseMappingContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new List<AnimeThemesMappingEntry>();

        var result = new List<AnimeThemesMappingEntry>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            var fields = TextHelper.SplitCsvLine(line);
            if (fields.Length < 4)
                continue;

            string filepath = fields[0];
            if (!int.TryParse(fields[1], out int vid))
                continue;
            if (!int.TryParse(fields[2], out int aid))
                continue;
            string newFilename = fields[3];

            result.Add(new AnimeThemesMappingEntry(filepath, vid, aid, newFilename));
        }
        return result;
    }

    private static string SerializeMapping(List<AnimeThemesMappingEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Shoko Relay AniDB AnimeThemes Xrefs ##");
        sb.AppendLine();
        sb.AppendLine("# filepath, videoId, anidbId, newFilename");
        foreach (var e in entries)
        {
            sb.Append(TextHelper.EscapeCsvCommas(e.FilePath)).Append(',');
            sb.Append(e.VideoId).Append(',');
            sb.Append(e.AniDbId).Append(',');
            sb.Append(TextHelper.EscapeCsvCommas(e.NewFileName)).AppendLine();
        }
        return sb.ToString();
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new HttpClient();
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(0.7); // ~86 req/min to stay under the 90 that is enforced by AnimeThemes

    // static constructor used to configure shared HttpClient (setting UA etc.)
    static AnimeThemesMapping()
    {
        // some API endpoints now reject requests without a custom User-Agent header
        AnimeThemesConstants.EnsureUserAgent(Http);
    }

    private readonly IMetadataService _metadataService;
    private readonly IVideoService _videoService;

    private readonly string _configDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public AnimeThemesMapping(IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
    {
        _metadataService = metadataService;
        _videoService = videoService;

        _configDirectory = configProvider.ConfigDirectory;
    }

    /// <summary>
    /// Download the mapping file from a direct raw URL (e.g. gist raw link) and save it.
    /// </summary>
    /// <param name="rawUrl">Raw URL to download the mapping content from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (Count, Log) with the number of entries parsed and a human-readable log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        int count = 0;
        string logMsg;

        try
        {
            using var client = new HttpClient();
            AnimeThemesConstants.EnsureUserAgent(client);
            var content = await client.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                logMsg = "Downloaded content empty";
                return (0, logMsg);
            }

            string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
            await File.WriteAllTextAsync(mapPath, content, ct).ConfigureAwait(false);

            try
            {
                count = ParseMappingContent(content).Count;
            }
            catch { }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Url: {rawUrl}");
            sb.AppendLine($"Entries: {count}");
            logMsg = sb.ToString();
            return (count, logMsg);
        }
        catch (Exception ex)
        {
            logMsg = "Import failed: " + ex.Message;
            Logger.Warn(ex, "Failed to import mapping from URL");
            return (0, logMsg);
        }
    }

    /// <summary>
    /// Scan configured import roots for AnimeThemes files and write a mapping CSV.
    /// The resulting file is always written to the standard location in the configuration directory and any existing file will be read to perform incremental updates.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingBuildResult"/> with statistics and the output file path.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken ct = default)
    {
        // build list of candidate root folders containing the AnimeThemes files
        var rootPaths = new List<string>();

        // auto-detect by looking for the configured root folder under every destination import root
        {
            var themeFolder = GetThemeRootFolderName();
            var candidates = _videoService
                .GetAllVideoFiles()
                .Select(v => v.ManagedFolder?.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.Combine(p!, themeFolder))
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .ToList();

            if (candidates.Count > 0)
                rootPaths.AddRange(candidates);
        }

        // discard any paths that no longer exist
        rootPaths = rootPaths.Where(Directory.Exists).ToList();
        if (rootPaths.Count == 0)
            throw new DirectoryNotFoundException("AnimeThemes root folder not found (no valid root paths)");

        Logger.Info("AnimeThemes mapping build started (roots={RootList})", string.Join(';', rootPaths));
        var sw = Stopwatch.StartNew();

        string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
        // map file path (CSV) for output. existing CSV contents will be read when performing incremental updates.
        var messages = new List<string>();
        var entries = new List<AnimeThemesMappingEntry>();
        int errors = 0;
        int reusedCount = 0; // number of files already present in existing map

        // load existing map to avoid re-querying the API for entries we already have
        var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(mapPath))
        {
            try
            {
                string oldContent = await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false);
                foreach (var e in ParseMappingContent(oldContent))
                    existing.TryAdd(e.FilePath, e);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read existing AnimeThemes mapping; regenerating all entries");
            }
        }

        // gather all webm files from every detected root, but ignore any that live inside a "misc" subfolder
        var files = rootPaths
            .Where(Directory.Exists)
            .SelectMany(root =>
                Directory
                    .EnumerateFiles(root, "*.webm", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var rel = Path.GetRelativePath(root, f);
                        // split on both separators to support inconsistent paths
                        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return !parts.Any(p => string.Equals(p, "misc", StringComparison.OrdinalIgnoreCase));
                    })
            )
            .ToList();

        // phase 1: partition files into reused (already in existing map) and toProcess (need API calls)
        var toProcess = new List<(string File, string Rel)>();
        foreach (string file in files)
        {
            string? root = rootPaths.FirstOrDefault(r =>
                file.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || file.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            );
            string rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/').TrimStart('/') : file;

            if (existing.TryGetValue(rel, out var oldEntry))
            {
                entries.Add(oldEntry);
                reusedCount++;
            }
            else
            {
                toProcess.Add((file, rel));
            }
        }

        // phase 2: fetch metadata for new files in parallel; the rate limiter serialises API calls
        int maxDop = Math.Max(1, ShokoRelay.Settings.Parallelism);
        await Parallel
            .ForEachAsync(
                toProcess,
                new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = ct },
                async (item, token) =>
                {
                    try
                    {
                        string baseName = Path.GetFileName(item.File);
                        string nameNoExt = Path.GetFileNameWithoutExtension(baseName) ?? "";
                        string versionSuffix = TextHelper.ExtractThemeVersionSuffix(nameNoExt);

                        var (lookup, idMissing) = await FetchMetadataAsync(baseName, token);
                        if (lookup == null)
                        {
                            lock (messages)
                            {
                                Interlocked.Increment(ref errors);
                                messages.Add(idMissing ? $"AniDB ID missing for {item.Rel}" : $"Missing metadata for {item.Rel}");
                            }
                            return;
                        }

                        string ext = Path.GetExtension(baseName) ?? string.Empty;
                        string cleanName = BuildNewFileName(lookup, ext, versionSuffix);
                        lock (entries)
                        {
                            entries.Add(new AnimeThemesMappingEntry(item.Rel, lookup.VideoId, lookup.AniDbId, cleanName));
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        Logger.Warn(ex, "Failed to build mapping for {File}", item.File);
                        lock (messages)
                        {
                            messages.Add($"{item.Rel}: {ex.Message}");
                        }
                    }
                }
            )
            .ConfigureAwait(false);

        var fileContent = SerializeMapping(entries);
        await File.WriteAllTextAsync(mapPath, fileContent, ct);

        sw.Stop();
        Logger.Info("AnimeThemes mapping build completed in {Elapsed}ms: entries={Count}, reused={Reused}, errors={Errors}", sw.ElapsedMilliseconds, entries.Count, reusedCount, errors);
        Logger.Info("AnimeThemes mapping written to {Path} with {Count} entries", mapPath, entries.Count);
        return new AnimeThemesMappingBuildResult(mapPath, entries.Count, reusedCount, errors, messages);
    }

    /// <summary>
    /// Read a previously built mapping file and create/update VFS links for matching theme files.
    /// Optionally restrict to a set of series via <paramref name="seriesFilter"/>.
    /// </summary>
    /// <param name="seriesFilter">Optional set of Shoko series IDs to limit processing to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts of operations performed and any errors encountered.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
        if (!File.Exists(mapPath))
            throw new FileNotFoundException("Mapping file not found", mapPath);

        string content = await File.ReadAllTextAsync(mapPath, ct);
        var entries = ParseMappingContent(content);

        Logger.Info("AnimeThemes apply mapping started (map={MapPath}, entries={EntryCount}, filter={FilterCount})", mapPath, entries.Count, seriesFilter?.Count ?? 0);
        var sw = Stopwatch.StartNew();
        string themeRootFolder = GetThemeRootFolderName();

        List<IShokoSeries?> seriesList;
        if (seriesFilter != null && seriesFilter.Count > 0)
            seriesList = seriesFilter.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)).ToList();
        else
            seriesList = _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>().ToList();

        seriesList = seriesList.Where(s => s != null && s.AnidbAnimeID > 0).ToList();
        var byAniDb = seriesList.GroupBy(s => s!.AnidbAnimeID).ToDictionary(g => g.Key, g => g.ToList());

        string rootName = VfsShared.ResolveRootFolderName();
        var destSeen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var createdDirs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // cache import roots per series ID to avoid repeated episode/file enumeration
        var importRootsCache = new Dictionary<int, List<string>>();

        int created = 0;
        int skipped = 0;
        int matchedSeries = 0;
        var errors = new List<string>();
        var planned = new List<string>();
        var plannedDests = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (seriesFilter != null && seriesFilter.Count > 0)
        {
            foreach (var id in seriesFilter)
            {
                if (seriesList.All(s => s?.ID != id))
                    errors.Add($"Series {id} not found or missing AniDB id");
            }
        }

        // pre-group entries by AniDB ID so we can process per-series with timing
        var entriesByAniDb = new Dictionary<int, List<AnimeThemesMappingEntry>>();
        foreach (var entry in entries)
        {
            string relativePath = entry.FilePath.TrimStart('/', '\\');
            var segments = relativePath.Split('/', '\\');
            if (string.IsNullOrWhiteSpace(relativePath) || segments.Any(s => string.Equals(s, "misc", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            if (!byAniDb.ContainsKey(entry.AniDbId))
            {
                skipped++;
                continue;
            }

            if (!entriesByAniDb.TryGetValue(entry.AniDbId, out var list))
            {
                list = new List<AnimeThemesMappingEntry>();
                entriesByAniDb[entry.AniDbId] = list;
            }
            list.Add(entry);
        }

        // process per-series (grouped by AniDB ID) to enable per-series logging
        foreach (var (anidbId, matchedEntries) in entriesByAniDb)
        {
            ct.ThrowIfCancellationRequested();
            if (!byAniDb.TryGetValue(anidbId, out var seriesMatches))
                continue;

            foreach (var series in seriesMatches)
            {
                if (series == null)
                {
                    skipped++;
                    continue;
                }

                int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);

                if (!importRootsCache.TryGetValue(series.ID, out var roots))
                {
                    roots = GetImportRoots(series);
                    importRootsCache[series.ID] = roots;
                }
                if (roots.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var seriesSw = Stopwatch.StartNew();
                int seriesCreated = 0,
                    seriesSkipped = 0,
                    seriesErrors = 0,
                    seriesPlanned = 0;

                matchedSeries++;
                foreach (var entry in matchedEntries)
                {
                    string relativePath = entry.FilePath.TrimStart('/', '\\');
                    foreach (string importRoot in roots)
                    {
                        string? source = ResolveThemeSourcePath(relativePath, importRoot, themeRootFolder);
                        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                        {
                            skipped++;
                            seriesSkipped++;
                            errors.Add($"Source missing for {entry.FilePath} under {importRoot}");
                            continue;
                        }

                        string shortsDir = Path.Combine(importRoot, rootName, primaryId.ToString(), "Shorts");
                        string destName = VfsHelper.CleanEpisodeTitleForFilename(entry.NewFileName);
                        destName = TextHelper.AnimeThemesPlexFileNames(destName);
                        destName = TextHelper.ReplaceFirstHyphenWithChevron(destName);
                        destName = EnsureExtension(destName, Path.GetExtension(source));
                        string destPath = Path.Combine(shortsDir, destName);

                        string relativeTarget = BuildThemeRelativeTarget(relativePath, themeRootFolder);

                        if (plannedDests.Add(destPath))
                        {
                            planned.Add($"{destPath} <- {relativeTarget}");
                            seriesPlanned++;
                        }

                        // only issue the syscall the first time we encounter this directory
                        if (createdDirs.Add(shortsDir))
                        {
                            try
                            {
                                Directory.CreateDirectory(shortsDir);
                            }
                            catch (Exception ex)
                            {
                                skipped++;
                                seriesSkipped++;
                                seriesErrors++;
                                Logger.Warn(ex, "Failed to create directory {Dir}", shortsDir);
                                errors.Add($"Failed to create directory {shortsDir}: {ex.Message}");
                                continue;
                            }
                        }

                        // ensure destPath is unique; if already seen, append numeric suffix until free
                        string originalDest = destPath;
                        int dupIdx = 1;
                        while (!destSeen.Add(destPath))
                        {
                            dupIdx++;
                            string baseNoExt = Path.GetFileNameWithoutExtension(originalDest);
                            string ext = Path.GetExtension(originalDest);
                            destPath = Path.Combine(shortsDir, $"{baseNoExt} ({dupIdx}){ext}");
                            for (int pi = 0; pi < planned.Count; pi++)
                            {
                                if (planned[pi].StartsWith(originalDest, StringComparison.OrdinalIgnoreCase))
                                    planned[pi] = planned[pi].Replace(originalDest, destPath);
                            }
                        }

                        if (VfsShared.TryCreateLink(source, destPath, Logger, targetOverride: relativeTarget))
                        {
                            created++;
                            seriesCreated++;
                        }
                        else
                        {
                            skipped++;
                            seriesSkipped++;
                            seriesErrors++;
                            Logger.Warn("Failed to link {Src} -> {Dest}", source, destPath);
                            errors.Add($"Failed to link {source} -> {destPath}");
                        }
                    }
                }

                seriesSw.Stop();
                Logger.Debug(
                    "AnimeThemesMapping --- BuildSeries {SeriesId} completed in {Elapsed}ms: mappings={Mappings} created={Created} planned={Planned} skipped={Skipped} errors={Errors}",
                    series.ID,
                    seriesSw.ElapsedMilliseconds,
                    matchedEntries.Count,
                    seriesCreated,
                    seriesPlanned,
                    seriesSkipped,
                    seriesErrors
                );
            }
        }

        sw.Stop();
        Logger.Info(
            "AnimeThemes apply mapping completed in {Elapsed}ms: created={Created}, skipped={Skipped}, matchedSeries={Matched}, errors={Errors}",
            sw.ElapsedMilliseconds,
            created,
            skipped,
            matchedSeries,
            errors.Count
        );
        return new AnimeThemesMappingApplyResult(created, skipped, matchedSeries, errors, planned, sw.Elapsed);
    }

    private static string EnsureExtension(string fileName, string ext)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "Unknown" + ext;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) && !string.IsNullOrWhiteSpace(ext))
            return fileName + ext;
        return fileName;
    }

    private static List<string> GetImportRoots(IShokoSeries series)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return series.Episodes.SelectMany(ep => ep.VideoList).SelectMany(v => v.Files).Select(VfsShared.ResolveImportRootPath).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(comparer).ToList()!;
    }

    // returns tuple containing lookup and flag if AniDB id was missing
    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string baseName, CancellationToken ct)
    {
        bool idMissing = false;
        string videoUrl = $"{AnimeThemesConstants.AtApiBase}/video/{Uri.EscapeDataString(baseName)}?include=animethemeentries.animetheme";
        var videoResp = await GetJsonAsync<VideoLookupResponse>(videoUrl, ct);
        if (videoResp?.Video == null)
            return (null, idMissing);

        int videoId = videoResp.Video.Id;
        int? themeId = videoResp.Video.Animethemeentries?.FirstOrDefault()?.Animetheme?.Id;
        if (!themeId.HasValue)
            return (null, idMissing);

        string animeUrl = $"{AnimeThemesConstants.AtApiBase}/anime?filter[has]=animethemes.animethemeentries.videos,animethemes&include=resources&filter[resource][site]=AniDB&filter[video][id]={videoId}";
        var animeResp = await GetJsonAsync<AnimeLookupResponse>(animeUrl, ct);
        int anidbId = animeResp?.Anime?.FirstOrDefault()?.Resources?.FirstOrDefault(r => string.Equals(r.Site, "AniDB", StringComparison.OrdinalIgnoreCase) && r.ExternalId.HasValue)?.ExternalId ?? 0;
        if (anidbId == 0)
        {
            // video exists but the AniDB id was absent/nullable
            idMissing = true;
            return (null, idMissing);
        }

        string themeUrl = $"{AnimeThemesConstants.AtApiBase}/animetheme/{themeId.Value}?include=animethemeentries.videos,song.artists";
        var themeResp = await GetJsonAsync<ThemeLookupResponse>(themeUrl, ct);
        string slug = themeResp?.Animetheme?.Slug ?? "";
        string songTitle = themeResp?.Animetheme?.Song?.Title ?? "";

        string tags = "";
        var matchingVideo = themeResp?.Animetheme?.Animethemeentries?.SelectMany(e => e.Videos ?? new List<ThemeVideoEntry>()).FirstOrDefault(v => v.Id == videoId);
        if (matchingVideo != null && !string.IsNullOrWhiteSpace(matchingVideo.Tags))
            tags = matchingVideo.Tags!;

        return (new AnimeThemesVideoLookup(videoId, themeId.Value, anidbId, slug, songTitle, tags), idMissing);
    }

    // versionSuffix may contain something like "v2" or "v3" pulled from the original filename.
    // It is appended to the slug part of the name before the song title so that differences are preserved without relying on the later dedupe pass.
    private static string BuildNewFileName(AnimeThemesVideoLookup lookup, string extension, string versionSuffix = "")
    {
        string slug = string.IsNullOrWhiteSpace(lookup.Slug) ? "Theme" : lookup.Slug;
        if (!string.IsNullOrWhiteSpace(versionSuffix))
            slug += versionSuffix;

        string baseName = slug;
        if (!string.IsNullOrWhiteSpace(lookup.SongTitle))
            baseName = $"{slug} - {lookup.SongTitle}";

        if (!string.IsNullOrWhiteSpace(lookup.Tags))
            baseName += $" [{lookup.Tags}]";

        string full = baseName + extension;
        return VfsHelper.CleanEpisodeTitleForFilename(full);
    }

    private static string GetThemeRootFolderName() => VfsShared.ResolveAnimeThemesFolderName();

    private static string? ResolveThemeSourcePath(string relativeFilePath, string importRoot, string themeRootFolder)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(rel))
            return null;

        string basePath = Path.Combine(importRoot, themeRootFolder);

        string normalizedBase = Path.GetFullPath(basePath);
        string relativePart = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string candidate = Path.Combine(normalizedBase, relativePart);
        string fullCandidate = Path.GetFullPath(candidate);

        return File.Exists(fullCandidate) ? fullCandidate : null;
    }

    private static string BuildThemeRelativeTarget(string relativeFilePath, string themeRootFolder)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        string normalized = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        string basePath = Path.Combine("..", "..", "..", themeRootFolder);
        string target = Path.Combine(basePath, normalized);

        return target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        await RateLimitAsync(ct);
        // ensure UA is configured before every request in case the static ctor wasn't
        AnimeThemesConstants.EnsureUserAgent(Http);
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Logger.Warn("AnimeThemes API returned Forbidden for {Url} (check user-agent/config)", url);
            }
            else
            {
                Logger.Warn("AnimeThemes API returned {Status} for {Url}", response.StatusCode, url);
            }
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, ct);
    }

    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = _lastRequest + RateLimitDelay - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private sealed record VideoLookupResponse(VideoEntry? Video);

    private sealed record VideoEntry(int Id, string Basename, List<ThemeEntryWrapper>? Animethemeentries);

    private sealed record ThemeEntryWrapper(ThemeOnly? Animetheme);

    private sealed record ThemeOnly(int Id);

    private sealed record AnimeLookupResponse(List<AnimeEntry>? Anime);

    private sealed record AnimeEntry(List<AnimeResource>? Resources);

    private sealed record AnimeResource([property: JsonPropertyName("external_id")] int? ExternalId, string Site);

    private sealed record ThemeLookupResponse(ThemeFull? Animetheme);

    private sealed record ThemeFull(string? Slug, ThemeSong? Song, List<ThemeEntry>? Animethemeentries);

    private sealed record ThemeSong(string? Title);

    private sealed record ThemeEntry(List<ThemeVideoEntry>? Videos);

    private sealed record ThemeVideoEntry(int Id, string? Tags);
}
