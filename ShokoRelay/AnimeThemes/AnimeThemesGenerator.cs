using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.Video;
using ShokoRelay.Config;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

public record AnimeThemesMp3Query
{
    public string? Path { get; init; }
    public string? Slug { get; init; }
    public int Offset { get; init; } = 0;
    public bool Batch { get; init; }
    public bool Force { get; init; }
}

public record ThemeMp3OperationResult(
    string Folder,
    string Status,
    string? Message,
    string? ThemePath = null,
    string? VfsLinkPath = null,
    string? AnimeTitle = null,
    string? AnimeSlug = null,
    int? SeriesId = null,
    string? Slug = null,
    double? DurationSeconds = null
);

public record ThemeMp3BatchResult(string Root, IReadOnlyList<ThemeMp3OperationResult> Items, int Processed, int Skipped, int Errors);

public record ThemePreviewResult(Stream Stream, string FileName, string ContentType, string? Title);

internal sealed record ThemeSelection(string AudioUrl, string SlugRaw, string SlugDisplay, string SongTitle, string Artist, string AnimeTitle, string AnimeSlug);

public class AnimeThemesGenerator
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new();
    private readonly FfmpegService _ffmpegService;

    private readonly IVideoService _videoService;

    public AnimeThemesGenerator(IVideoService videoService, ConfigProvider configProvider)
    {
        _videoService = videoService;
        _ffmpegService = new FfmpegService(configProvider.PluginDirectory);
        AnimeThemesConstants.EnsureUserAgent(Http);
    }

    public async Task<ThemeMp3BatchResult> ProcessBatchAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        string root = ResolvePath(query.Path ?? string.Empty);
        if (!Directory.Exists(root))
        {
            var missing = new[] { new ThemeMp3OperationResult(root, "error", "Batch root not found.") };
            return new ThemeMp3BatchResult(root, missing, 0, 0, 1);
        }

        var results = new List<ThemeMp3OperationResult>();
        int processed = 0;
        int skipped = 0;
        int errors = 0;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            string folderName = Path.GetFileName(folder);
            string vfsRoot = ShokoRelay.Settings.VfsRootPath ?? "!ShokoRelayVFS";
            string collectionRoot = ShokoRelay.Settings.CollectionPostersRootPath ?? "!CollectionPosters";
            string animeThemesRoot = ShokoRelay.Settings.AnimeThemesRootPath ?? "!AnimeThemes";

            // log start of work on this folder
            Logger.Info("AnimeThemes MP3 batch: processing folder {Folder}", folder);

            if (
                string.Equals(folderName, vfsRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(folderName, collectionRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(folderName, animeThemesRoot, StringComparison.OrdinalIgnoreCase)
            )
            {
                skipped++;
                results.Add(new ThemeMp3OperationResult(folder, "skipped", "Excluded system folder."));
                Logger.Info("AnimeThemes MP3 batch: skipped system folder {Folder}", folder);
                continue;
            }

            var singleQuery = query with { Path = folder, Batch = false };
            var result = await ProcessSingleAsync(singleQuery, ct);
            results.Add(result);

            // log outcome
            Logger.Info("AnimeThemes MP3 batch: folder {Folder} => {Status}{Message}", folder, result.Status, string.IsNullOrWhiteSpace(result.Message) ? "" : ": " + result.Message);

            switch (result.Status)
            {
                case "ok":
                    processed++;
                    break;
                case "skipped":
                    skipped++;
                    break;
                default:
                    errors++;
                    break;
            }
        }

        return new ThemeMp3BatchResult(root, results, processed, skipped, errors);
    }

    public async Task<ThemeMp3OperationResult> ProcessSingleAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var contextResult = await PrepareContextAsync(query, allowPreview: false, ct);
        if (contextResult.Error != null)
            return contextResult.Error;

        var (folder, themePath, videoFile, series) = contextResult.Data!.Value;

        ThemeSelection? selection = null;
        string? tempPath = null;
        try
        {
            selection = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
            if (selection == null)
            {
                string reason;
                if (!string.IsNullOrWhiteSpace(query.Slug))
                    reason = $"AnimeThemes entry not found for slug '{query.Slug}'.";
                else
                    reason = "AnimeThemes entry not found for this series.";
                return new ThemeMp3OperationResult(folder, "skipped", reason);
            }

            tempPath = await DownloadAudioAsync(selection.AudioUrl, ct);
            var duration = await _ffmpegService.ProbeDurationAsync(tempPath, ct);
            string title = selection.SongTitle;
            if (duration.TotalSeconds < 100 && !string.IsNullOrWhiteSpace(selection.SongTitle))
                title = selection.SongTitle + " (TV Size)";

            await _ffmpegService.ConvertToMp3FileAsync(tempPath, themePath, title, selection.SlugDisplay, selection.Artist, selection.AnimeTitle, ct);
            string? vfsLink = TryLinkIntoVfs(videoFile, series.ID, themePath);

            return new ThemeMp3OperationResult(folder, "ok", null, themePath, vfsLink, selection.AnimeTitle, selection.AnimeSlug, series.ID, selection.SlugDisplay, duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process animethemes for {Folder}", folder);
            return Error(folder, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                { /* ignore cleanup errors */
                }
            }
        }
    }

    public async Task<(ThemePreviewResult? Preview, ThemeMp3OperationResult? Error)> PreviewAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var contextResult = await PrepareContextAsync(query, allowPreview: true, ct);
        if (contextResult.Error != null)
            return (null, contextResult.Error);

        var (folder, _, _, series) = contextResult.Data!.Value;

        ThemeSelection? selection = null;
        string? tempPath = null;
        try
        {
            selection = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
            if (selection == null)
                return (null, Error(folder, "AnimeThemes entry not found for this series."));

            tempPath = await DownloadAudioAsync(selection.AudioUrl, ct);
            var ms = await _ffmpegService.ConvertToMp3StreamAsync(tempPath, ct);
            return (new ThemePreviewResult(ms, "Theme.mp3", "audio/mpeg", selection.SlugDisplay + ": " + selection.SongTitle), null);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to preview animethemes for {Folder}", folder);
            return (null, Error(folder, ex.Message));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch { }
            }
        }
    }

    private Task<(ThemeMp3OperationResult? Error, (string Folder, string ThemePath, IVideoFile VideoFile, IShokoSeries Series)? Data)> PrepareContextAsync(
        AnimeThemesMp3Query query,
        bool allowPreview,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(query.Path))
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error("", "Path is required."), null));

        string folder = ResolvePath(query.Path);
        if (!Directory.Exists(folder))
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error(folder, "Folder not found."), null));

        string themePath = Path.Combine(folder, "Theme.mp3");
        if (!allowPreview && !query.Force && File.Exists(themePath))
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error(folder, "Theme.mp3 already exists; set force=true to overwrite.", "skipped"), null));

        string? videoPath = Directory.EnumerateFiles(folder).FirstOrDefault(f => AnimeThemesConstants.VideoFileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(videoPath))
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error(folder, "No video files found in folder."), null));

        var videoFile = _videoService.GetVideoFileByAbsolutePath(videoPath);
        if (videoFile?.Video == null)
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error(folder, "Video not recognized by Shoko."), null));

        // Derive the series via the first linked episode since videos are linked at the episode level.
        // This means that if there are multiple episodes from different series in the same folder, the result may be non-deterministic.
        var series = videoFile.Video.Episodes?.FirstOrDefault()?.Series;
        if (series == null)
            return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((Error(folder, "Series lookup failed."), null));

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<(ThemeMp3OperationResult?, (string, string, IVideoFile, IShokoSeries)?)>((null, (folder, themePath, videoFile, series)));
    }

    private static ThemeMp3OperationResult Error(string folder, string message, string status = "error")
    {
        return new ThemeMp3OperationResult(folder, status, message);
    }

    private async Task<ThemeSelection?> FetchThemeAsync(int anidbId, string? slugArg, int offset, CancellationToken ct)
    {
        string? normalizedSlug = NormalizeSlug(slugArg);
        if (!string.IsNullOrWhiteSpace(slugArg) && normalizedSlug == null)
            throw new ArgumentException("Invalid slug format. Use values like op, op2, ed, ed2, op1-tv, ed-bd.");
        string slugFilter = normalizedSlug != null ? $"&filter[animetheme][slug]={Uri.EscapeDataString(normalizedSlug)}" : "&filter[animetheme][type]=OP,ED";
        string animeUrl = $"{AnimeThemesConstants.ApiBase}/anime?filter[has]=resources&filter[site]=AniDB&filter[external_id]={anidbId}&include=animethemes{slugFilter}";

        var anime = await GetJsonAsync<AnimeResponse>(animeUrl, ct);
        var animeEntry = anime?.Anime?.ElementAtOrDefault(offset);
        if (animeEntry == null || animeEntry.Animethemes == null || animeEntry.Animethemes.Count == 0)
            return null;

        int idx = 0;
        if (normalizedSlug == null && animeEntry.Animethemes.Count > 1 && string.Equals(animeEntry.Animethemes[1].Slug, "OP1", StringComparison.OrdinalIgnoreCase))
            idx = 1;

        var theme = animeEntry.Animethemes.ElementAtOrDefault(idx);
        if (theme == null)
            return null;

        string themeUrl = $"{AnimeThemesConstants.ApiBase}/animetheme/{theme.Id}?include=animethemeentries.videos,song.artists";
        var themeDetail = await GetJsonAsync<ThemeResponse>(themeUrl, ct);
        var entry = themeDetail?.Animetheme?.Animethemeentries?.FirstOrDefault();
        var videoId = entry?.Videos?.FirstOrDefault()?.Id;
        if (!videoId.HasValue)
            return null;

        string videoUrl = $"{AnimeThemesConstants.ApiBase}/video?filter[video][id]={videoId.Value}&include=audio";
        var video = await GetJsonAsync<VideoResponse>(videoUrl, ct);
        string? audioUrl = video?.Videos?.FirstOrDefault()?.Audio?.Link;
        if (string.IsNullOrWhiteSpace(audioUrl))
            return null;

        string slugDisplay = FormatSlugDisplay(theme.Slug ?? "");
        string songTitle = themeDetail?.Animetheme?.Song?.Title ?? string.Empty;
        string artist = themeDetail?.Animetheme?.Song?.Artists?.FirstOrDefault()?.Name ?? string.Empty;

        return new ThemeSelection(audioUrl, theme.Slug ?? string.Empty, slugDisplay, songTitle, artist, animeEntry.Name ?? string.Empty, animeEntry.Slug ?? string.Empty);
    }

    private static async Task<string> DownloadAudioAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        string ext = Path.GetExtension(url);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5)
            ext = ".ogg";

        string tempPath = Path.Combine(Path.GetTempPath(), $"animethemes-{Guid.NewGuid():N}{ext}");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(tempPath);
        await input.CopyToAsync(output, ct);
        return tempPath;
    }

    private static string FormatSlugDisplay(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string result = raw.ToUpperInvariant();
        foreach (var pair in AnimeThemesConstants.SlugFormatting)
        {
            result = Regex.Replace(result, pair.Key, pair.Value, RegexOptions.IgnoreCase);
        }

        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result.TrimEnd();
    }

    private static string? NormalizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        string s = slug.Trim().ToUpperInvariant();
        if (!AnimeThemesConstants.SlugRegex.IsMatch(s))
            return null;

        if (s is "OP1" or "ED1")
            s = s[..2];

        if (s is "OP" or "ED")
            return $"{s},{s}1";

        return s;
    }

    private static string ResolvePath(string path)
    {
        string resolved = path;
        string basePath = AnimeThemesConstants.BasePath;
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            if (!Path.IsPathRooted(path))
            {
                resolved = Path.Combine(basePath, path);
            }
        }

        return Path.GetFullPath(resolved);
    }

    private static string? TryLinkIntoVfs(IVideoFile location, int seriesId, string source)
    {
        string? importRoot = VfsShared.ResolveImportRootPath(location);
        if (string.IsNullOrWhiteSpace(importRoot))
            return null;

        string seriesFolder = Path.Combine(importRoot, VfsShared.ResolveRootFolderName(), seriesId.ToString());
        try
        {
            Directory.CreateDirectory(seriesFolder);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to create VFS series folder {Folder}", seriesFolder);
            return null;
        }

        string dest = Path.Combine(seriesFolder, "Theme.mp3");
        return VfsShared.TryCreateLink(source, dest, Logger) ? dest : null;
    }

    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return default;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }

    private sealed record AnimeResponse(List<AnimeEntry>? Anime);

    private sealed record AnimeEntry(string? Name, string? Slug, List<AnimeTheme>? Animethemes);

    private sealed record AnimeTheme(int Id, string? Slug, List<AnimeThemeEntry>? Animethemeentries);

    private sealed record AnimeThemeEntry(List<AnimeThemeVideo>? Videos);

    private sealed record AnimeThemeVideo(int Id);

    private sealed record ThemeResponse(ThemeWrapper? Animetheme);

    private sealed record ThemeWrapper(ThemeSong? Song, List<ThemeEntry>? Animethemeentries);

    private sealed record ThemeSong(string? Title, List<ThemeArtist>? Artists);

    private sealed record ThemeArtist(string? Name);

    private sealed record ThemeEntry(List<ThemeVideo>? Videos);

    private sealed record ThemeVideo(int Id);

    private sealed record VideoResponse(List<VideoItem>? Videos);

    private sealed record VideoItem(VideoAudio? Audio);

    private sealed record VideoAudio(string? Link);
}
