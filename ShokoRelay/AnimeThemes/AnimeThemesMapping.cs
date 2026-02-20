using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

public sealed record AnimeThemesMappingEntry(
    [property: JsonPropertyName("filepath")] string FilePath,
    [property: JsonPropertyName("videoId")] int VideoId,
    [property: JsonPropertyName("anidbId")] int AniDbId,
    [property: JsonPropertyName("newFilename")] string NewFileName
);

public sealed record AnimeThemesMappingBuildResult(string MapPath, int EntriesWritten, int Errors, IReadOnlyList<string> Messages);

public sealed record AnimeThemesMappingApplyResult(int LinksCreated, int Skipped, int SeriesMatched, IReadOnlyList<string> Errors, IReadOnlyList<string> Planned);

internal sealed record AnimeThemesVideoLookup(int VideoId, int ThemeId, int AniDbId, string Slug, string SongTitle, string Tags);

public class AnimeThemesMapping
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(0.7); // ~86 req/min to stay under the 90 that is enforced by AnimeThemes

    private readonly IMetadataService _metadataService;
    private readonly string _pluginPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public AnimeThemesMapping(IMetadataService metadataService, IApplicationPaths applicationPaths)
    {
        _metadataService = metadataService;
        _pluginPath = Path.Combine(applicationPaths.PluginsPath, ConfigConstants.PluginSubfolder);
        AnimeThemesConstants.EnsureUserAgent(Http);
    }

    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(string torrentRoot, string? outputPath = null, CancellationToken ct = default)
    {
        string basePath = !string.IsNullOrWhiteSpace(torrentRoot)
            ? torrentRoot
            : (!string.IsNullOrWhiteSpace(ShokoRelay.Settings.AnimeThemesPathMapping) ? ShokoRelay.Settings.AnimeThemesPathMapping : AnimeThemesConstants.BasePath);

        string resolvedRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(basePath) ? "." : basePath);
        if (!Directory.Exists(resolvedRoot))
            throw new DirectoryNotFoundException($"AnimeThemes torrent root not found: {resolvedRoot}");

        Logger.Info("AnimeThemes mapping build started (root={Root})", resolvedRoot);
        var sw = Stopwatch.StartNew();

        string mapPath = outputPath ?? Path.Combine(_pluginPath, AnimeThemesConstants.MapFileName);
        var messages = new List<string>();
        var entries = new List<AnimeThemesMappingEntry>();
        int errors = 0;
        int warns = 0;

        var files = Directory.EnumerateFiles(resolvedRoot, "*.webm", SearchOption.AllDirectories).ToList();

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string rel = Path.GetRelativePath(resolvedRoot, file).Replace('\\', '/').TrimStart('/');
            rel = "/" + rel;

            try
            {
                string baseName = Path.GetFileName(file);
                var lookup = await FetchMetadataAsync(baseName, ct);
                if (lookup == null)
                {
                    errors++;
                    messages.Add($"Missing metadata for {rel}");
                    continue;
                }

                string ext = Path.GetExtension(baseName) ?? string.Empty;
                string cleanName = BuildNewFileName(lookup, ext);

                entries.Add(new AnimeThemesMappingEntry(rel, lookup.VideoId, lookup.AniDbId, cleanName));
            }
            catch (Exception ex)
            {
                errors++;
                Logger.Warn(ex, "Failed to build mapping for {File}", file);
                messages.Add($"{rel}: {ex.Message}");
            }
        }

        var json = JsonSerializer.Serialize(entries, _jsonOptions);
        await File.WriteAllTextAsync(mapPath, json, ct);

        sw.Stop();
        Logger.Info("AnimeThemes mapping build completed in {Elapsed}ms: entries={Count}, errors={Errors}, warnings={Warns}", sw.ElapsedMilliseconds, entries.Count, errors, warns);
        Logger.Info("AnimeThemes mapping written to {Path} with {Count} entries", mapPath, entries.Count);
        return new AnimeThemesMappingBuildResult(mapPath, entries.Count, errors, messages);
    }

    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(
        string? mapPath = null,
        string? torrentRoot = null,
        IReadOnlyCollection<int>? seriesFilter = null,
        CancellationToken ct = default
    )
    {
        string resolvedMap = mapPath ?? Path.Combine(_pluginPath, AnimeThemesConstants.MapFileName);
        if (!File.Exists(resolvedMap))
            throw new FileNotFoundException("Mapping file not found", resolvedMap);

        string json = await File.ReadAllTextAsync(resolvedMap, ct);
        var entries = JsonSerializer.Deserialize<List<AnimeThemesMappingEntry>>(json, _jsonOptions) ?? new();

        List<IShokoSeries?> seriesList = [];
        Logger.Info("AnimeThemes apply mapping started (map={MapPath}, root={Root}, filter={FilterCount})", resolvedMap, torrentRoot ?? "", seriesFilter?.Count ?? 0);
        var sw2 = Stopwatch.StartNew();
        int warns2 = 0;
        if (seriesFilter != null && seriesFilter.Count > 0)
        {
            seriesList = seriesFilter.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)).ToList();
        }
        else
        {
            seriesList = _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>().ToList();
        }

        seriesList = seriesList.Where(s => s != null && s.AnidbAnimeID > 0).ToList();
        var byAniDb = seriesList.GroupBy(s => s!.AnidbAnimeID).ToDictionary(g => g.Key, g => g.ToList());

        string rootName = VfsShared.ResolveRootFolderName();
        var destSeen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        int created = 0;
        int skipped = 0;
        int matchedSeries = 0;
        var errors = new List<string>();

        if (seriesFilter != null && seriesFilter.Count > 0)
        {
            foreach (var id in seriesFilter)
            {
                if (seriesList.All(s => s?.ID != id))
                    errors.Add($"Series {id} not found or missing AniDB id");
            }
        }
        var planned = new List<string>();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!byAniDb.TryGetValue(entry.AniDbId, out var matches) || matches.Count == 0)
            {
                skipped++;
                continue;
            }

            string relativePath = entry.FilePath.TrimStart('/', '\\');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                skipped++;
                errors.Add($"Invalid filepath for mapping entry: {entry.FilePath}");
                continue;
            }

            foreach (var series in matches!)
            {
                if (series == null)
                {
                    skipped++;
                    continue;
                }

                var roots = GetImportRoots(series);
                if (roots.Count == 0)
                {
                    skipped++;
                    continue;
                }

                matchedSeries++;
                foreach (string importRoot in roots)
                {
                    string? source = ResolveThemeSourcePath(relativePath, importRoot, torrentRoot);
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    {
                        skipped++;
                        errors.Add($"Source missing for {entry.FilePath} under {importRoot}");
                        continue;
                    }

                    string shortsDir = Path.Combine(importRoot, rootName, series.ID.ToString(), "Shorts");
                    string destName = VfsHelper.CleanEpisodeTitleForFilename(entry.NewFileName);
                    destName = TextHelper.AnimeThemesPlexFileNames(destName);
                    destName = TextHelper.ReplaceFirstHyphenWithArrow(destName);
                    destName = EnsureExtension(destName, Path.GetExtension(source));
                    string destPath = Path.Combine(shortsDir, destName);

                    string relativeTarget = BuildThemeRelativeTarget(relativePath);

                    // record planned action (avoid duplicates in the planned list)
                    if (!planned.Any(p => p.StartsWith(destPath, StringComparison.OrdinalIgnoreCase)))
                        planned.Add($"{destPath} <- {relativeTarget}");

                    try
                    {
                        Directory.CreateDirectory(shortsDir);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        warns2++;
                        Logger.Warn(ex, "Failed to create directory {Dir}", shortsDir);
                        errors.Add($"Failed to create directory {shortsDir}: {ex.Message}");
                        continue;
                    }

                    if (!destSeen.Add(destPath))
                    {
                        skipped++;
                        warns2++;
                        Logger.Warn("Duplicate target path {Dest}", destPath);
                        continue;
                    }

                    if (VfsShared.TryCreateLink(source, destPath, Logger, targetOverride: relativeTarget))
                    {
                        created++;
                    }
                    else
                    {
                        skipped++;
                        warns2++;
                        Logger.Warn("Failed to link {Src} -> {Dest}", source, destPath);
                        errors.Add($"Failed to link {source} -> {destPath}");
                    }
                }
            }
        }

        sw2.Stop();
        Logger.Info(
            "AnimeThemes apply mapping completed in {Elapsed}ms: created={Created}, skipped={Skipped}, matchedSeries={Matched}, errors={Errors}, warnings={Warns}",
            sw2.ElapsedMilliseconds,
            created,
            skipped,
            matchedSeries,
            errors.Count,
            warns2
        );
        return new AnimeThemesMappingApplyResult(created, skipped, matchedSeries, errors, planned);
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
        var set = new HashSet<string>(comparer);

        foreach (var ep in series.Episodes)
        {
            foreach (var video in ep.VideoList)
            {
                foreach (var loc in video.Files)
                {
                    string? root = VfsShared.ResolveImportRootPath(loc);
                    if (!string.IsNullOrWhiteSpace(root))
                        set.Add(root);
                }
            }
        }

        return set.ToList();
    }

    private async Task<AnimeThemesVideoLookup?> FetchMetadataAsync(string baseName, CancellationToken ct)
    {
        string videoUrl = $"{AnimeThemesConstants.ApiBase}/video/{Uri.EscapeDataString(baseName)}?include=animethemeentries.animetheme";
        var videoResp = await GetJsonAsync<VideoLookupResponse>(videoUrl, ct);
        if (videoResp?.Video == null)
            return null;

        int videoId = videoResp.Video.Id;
        int? themeId = videoResp.Video.Animethemeentries?.FirstOrDefault()?.Animetheme?.Id;
        if (!themeId.HasValue)
            return null;

        string animeUrl =
            $"{AnimeThemesConstants.ApiBase}/anime?filter[has]=animethemes.animethemeentries.videos,animethemes&include=resources&filter[resource][site]=AniDB&filter[video][id]={videoId}";
        var animeResp = await GetJsonAsync<AnimeLookupResponse>(animeUrl, ct);
        int anidbId = animeResp?.Anime?.FirstOrDefault()?.Resources?.FirstOrDefault(r => string.Equals(r.Site, "AniDB", StringComparison.OrdinalIgnoreCase))?.ExternalId ?? 0;
        if (anidbId == 0)
            return null;

        string themeUrl = $"{AnimeThemesConstants.ApiBase}/animetheme/{themeId.Value}?include=animethemeentries.videos,song.artists";
        var themeResp = await GetJsonAsync<ThemeLookupResponse>(themeUrl, ct);
        string slug = themeResp?.Animetheme?.Slug ?? "";
        string songTitle = themeResp?.Animetheme?.Song?.Title ?? "";

        string tags = "";
        var matchingVideo = themeResp?.Animetheme?.Animethemeentries?.SelectMany(e => e.Videos ?? new List<ThemeVideoEntry>()).FirstOrDefault(v => v.Id == videoId);
        if (matchingVideo != null && !string.IsNullOrWhiteSpace(matchingVideo.Tags))
            tags = matchingVideo.Tags!;

        return new AnimeThemesVideoLookup(videoId, themeId.Value, anidbId, slug, songTitle, tags);
    }

    private static string BuildNewFileName(AnimeThemesVideoLookup lookup, string extension)
    {
        string baseName = string.IsNullOrWhiteSpace(lookup.Slug) ? "Theme" : lookup.Slug;
        if (!string.IsNullOrWhiteSpace(lookup.SongTitle))
            baseName = $"{lookup.Slug} - {lookup.SongTitle}";

        if (!string.IsNullOrWhiteSpace(lookup.Tags))
            baseName += $" [{lookup.Tags}]";

        string full = baseName + extension;
        return VfsHelper.CleanEpisodeTitleForFilename(full);
    }

    private static string GetThemeRootFolderName()
    {
        string configured = ShokoRelay.Settings.AnimeThemesRootPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = AnimeThemesConstants.DefaultRootFolder;

        return configured.Trim().Trim('/', '\\');
    }

    private static string? ResolveThemeSourcePath(string relativeFilePath, string importRoot, string? overrideRoot)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(rel))
            return null;

        string basePath;
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            basePath = overrideRoot;
            if (!Path.IsPathRooted(basePath))
                basePath = Path.Combine(importRoot, basePath);
        }
        else
        {
            string folderName = GetThemeRootFolderName();
            basePath = Path.Combine(importRoot, folderName);
        }

        string normalizedBase = Path.GetFullPath(basePath);
        string relativePart = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string candidate = Path.Combine(normalizedBase, relativePart);
        string fullCandidate = Path.GetFullPath(candidate);

        return File.Exists(fullCandidate) ? fullCandidate : null;
    }

    private static string BuildThemeRelativeTarget(string relativeFilePath)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        string normalized = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        string rootFolder = GetThemeRootFolderName();
        string basePath = Path.Combine("..", "..", "..", rootFolder);
        string target = Path.Combine(basePath, normalized);

        return target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        await RateLimitAsync(ct);
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn("AnimeThemes API returned {Status} for {Url}", response.StatusCode, url);
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

    private sealed record AnimeResource([property: JsonPropertyName("external_id")] int ExternalId, string Site);

    private sealed record ThemeLookupResponse(ThemeFull? Animetheme);

    private sealed record ThemeFull(string? Slug, ThemeSong? Song, List<ThemeEntry>? Animethemeentries);

    private sealed record ThemeSong(string? Title);

    private sealed record ThemeEntry(List<ThemeVideoEntry>? Videos);

    private sealed record ThemeVideoEntry(int Id, string? Tags);
}
