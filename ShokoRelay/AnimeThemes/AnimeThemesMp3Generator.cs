using NLog;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

#region Data Models

/// <summary>Query parameters for MP3 generation requests.</summary>
/// <param name="Path">The filesystem path to the folder or root directory.</param>
/// <param name="Slug">Optional specific theme slug filter (e.g., OP1, ED).</param>
/// <param name="Offset">The index offset to use when multiple themes match.</param>
/// <param name="Batch">Whether to process all subfolders recursively.</param>
/// <param name="Force">Whether to force generation even if Theme.mp3 exists.</param>
public record AnimeThemesMp3Query(string? Path, string? Slug, int Offset = 0, bool Batch = false, bool Force = false);

/// <summary>Result of a single Theme.mp3 generation attempt.</summary>
/// <param name="Folder">The directory processed.</param>
/// <param name="Status">The status code (ok, skipped, error).</param>
/// <param name="Message">Optional error or status message.</param>
/// <param name="ThemePath">The local path to the generated MP3.</param>
/// <param name="VfsLinkPath">The path where the VFS symlink was created.</param>
/// <param name="AnimeTitle">The resolved anime title.</param>
/// <param name="AnimeSlug">The internal AnimeThemes slug for the anime.</param>
/// <param name="SeriesId">The Shoko Series ID.</param>
/// <param name="Slug">The specific theme slug (e.g. Opening 1).</param>
/// <param name="DurationSeconds">The duration of the resulting audio file.</param>
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

/// <summary>Aggregated results of a batch MP3 generation operation.</summary>
/// <param name="Root">The root directory for the batch.</param>
/// <param name="Items">List of individual operation results.</param>
/// <param name="Processed">Count of successful generations.</param>
/// <param name="Skipped">Count of skipped folders.</param>
/// <param name="Errors">Count of encountered errors.</param>
public record ThemeMp3BatchResult(string Root, IReadOnlyList<ThemeMp3OperationResult> Items, int Processed, int Skipped, int Errors);

/// <summary>Result of an in-memory theme conversion for preview.</summary>
/// <param name="Stream">The audio data stream.</param>
/// <param name="FileName">Suggested filename for the response.</param>
/// <param name="ContentType">MIME type (audio/mpeg).</param>
/// <param name="Title">The song title metadata.</param>
public record ThemePreviewResult(Stream Stream, string FileName, string ContentType, string? Title);

/// <summary>Internal record representing a selected theme's metadata and audio link.</summary>
internal sealed record ThemeSelection(string AudioUrl, string SlugRaw, string SlugDisplay, string SongTitle, string Artist, string AnimeTitle, string AnimeSlug);

#endregion

/// <summary>Provides functionality for fetching, converting and previewing anime theme audio from the AnimeThemes API.</summary>
public class AnimeThemesMp3Generator(HttpClient httpClient, IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider, FfmpegService ffmpegService)
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _http = httpClient;
    private readonly FfmpegService _ffmpegService = ffmpegService;
    private readonly AnimeThemesApi _apiClient = new();
    private List<string>? _themeMp3Cache;
    private readonly Lock _cacheLock = new();

    private string ThemeCacheFilePath
    {
        get => Path.Combine(field, ShokoRelayConstants.FileAtMp3Cache);
    } = configProvider.ConfigDirectory;

    #endregion

    #region Cache Management

    /// <summary>Returns the cached list of folders containing Theme.mp3 files.</summary>
    /// <returns>A read-only list of folder paths.</returns>
    public IReadOnlyList<string> GetCachedThemeMp3Folders()
    {
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            return _themeMp3Cache ?? (IReadOnlyList<string>)[];
        }
    }

    /// <summary>Forces a re-scan of all managed import folder roots and rebuilds the cache.</summary>
    public void RefreshThemeMp3Cache()
    {
        lock (_cacheLock)
            RefreshThemeMp3CacheInternal();
    }

    /// <summary>Adds a folder path to the Theme.mp3 cache if it is not already present.</summary>
    /// <param name="folderPath">Absolute path of the folder to add.</param>
    public void AddToThemeMp3Cache(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            _themeMp3Cache ??= [];
            if (!_themeMp3Cache.Contains(folderPath, VfsShared.PathComparer))
            {
                _themeMp3Cache.Add(folderPath);
                SaveCacheToFile();
            }
        }
    }

    private void RefreshThemeMp3CacheInternal()
    {
        Logger.Info("Building Theme.mp3 cache — scanning all managed import folders...");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VfsShared.ResolveRootFolderName(), VfsShared.ResolveCollectionPostersFolderName(), VfsShared.ResolveAnimeThemesFolderName() };
        try
        {
            var roots = (videoService.GetAllManagedFolders() ?? [])
                .Select(mf => mf.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(VfsShared.PathComparer)
                .ToList();
            _themeMp3Cache =
            [
                .. roots
                    .SelectMany(r =>
                        Directory
                            .EnumerateFiles(r!, "Theme.mp3", SearchOption.AllDirectories)
                            .Where(f => !Path.GetRelativePath(r!, Path.GetDirectoryName(f)!).Split(Path.DirectorySeparatorChar).Any(s => excluded.Contains(s)))
                            .Select(Path.GetDirectoryName)
                    )
                    .OfType<string>(),
            ];
            SaveCacheToFile();
            Logger.Info("Theme.mp3 cache refreshed: {0} folders found across {1} roots.", _themeMp3Cache.Count, roots.Count);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "RefreshThemeMp3Cache: repositories not ready.");
            _themeMp3Cache ??= [];
        }
    }

    private void LoadCacheFromFile()
    {
        if (File.Exists(ThemeCacheFilePath))
        {
            try
            {
                var lines = File.ReadAllLines(ThemeCacheFilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (lines.Count > 0)
                {
                    _themeMp3Cache = lines;
                    return;
                }
            }
            catch
            {
                Logger.Warn("Failed to read {0}, attempting full scan.", ShokoRelayConstants.FileAtMp3Cache);
            }
        }
        RefreshThemeMp3CacheInternal();
    }

    private void SaveCacheToFile()
    {
        try
        {
            if (_themeMp3Cache != null)
                File.WriteAllLines(ThemeCacheFilePath, _themeMp3Cache);
        }
        catch { }
    }

    #endregion

    #region MP3 Generation

    /// <summary>Processes a folder (and optionally subfolders) to generate MP3s for anime themes.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A batch result object.</returns>
    public async Task<ThemeMp3BatchResult> ProcessBatchAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        string root = query.Path ?? "";
        if (!Directory.Exists(root))
        {
            Logger.Warn("AnimeThemes MP3: Batch root not found: {0}", root);
            return new ThemeMp3BatchResult(root, [new(root, "error", "Batch root not found.")], 0, 0, 1);
        }

        Logger.Info("AnimeThemes MP3: Starting batch generation for root: {0}", root);
        var (results, p, s, e) = (new List<ThemeMp3OperationResult>(), 0, 0, 0);
        var folders = Directory.EnumerateDirectories(root).Prepend(root).Where(f => query.Force || !File.Exists(Path.Combine(f, "Theme.mp3"))).ToList();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VfsShared.ResolveRootFolderName(), VfsShared.ResolveCollectionPostersFolderName(), VfsShared.ResolveAnimeThemesFolderName() };

        await Parallel.ForEachAsync(
            folders,
            new ParallelOptions { MaxDegreeOfParallelism = ShokoRelay.GetMaxParallelism(), CancellationToken = ct },
            async (folder, token) =>
            {
                if (excluded.Contains(Path.GetFileName(folder)))
                {
                    lock (results)
                    {
                        s++;
                        results.Add(new(folder, "skipped", "Excluded system folder."));
                    }
                    return;
                }
                var res = await ProcessSingleAsync(query with { Path = folder, Batch = false }, token).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(res);
                    if (res.Status == "ok")
                        p++;
                    else if (res.Status == "skipped")
                        s++;
                    else
                        e++;
                }
            }
        );

        Logger.Info("AnimeThemes MP3: Batch generation finished. {0} processed, {1} skipped, {2} errors.", p, s, e);
        return new ThemeMp3BatchResult(root, results, p, s, e);
    }

    /// <summary>Handles a single folder request, downloading and converting the selected theme to an MP3.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An operation result object.</returns>
    public async Task<ThemeMp3OperationResult> ProcessSingleAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var (Error, Data) = PrepareContext(query, false);
        if (Error != null)
            return Error;
        var (folder, themePath, videoFile, series) = Data!.Value;
        string? temp = null;
        try
        {
            Logger.Info("AnimeThemes MP3: Generating Theme.mp3 for series '{0}' in {1}", series.PreferredTitle?.Value ?? series.ID.ToString(), folder);
            var sel = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
            if (sel == null)
            {
                string skipMsg = string.IsNullOrWhiteSpace(query.Slug) ? "Entry not found." : $"No entry for slug '{query.Slug}'.";
                Logger.Info("AnimeThemes MP3: Skipped series '{0}' ({1})", series.PreferredTitle?.Value, skipMsg);
                return new(folder, "skipped", skipMsg);
            }

            temp = await DownloadAudioAsync(sel.AudioUrl, ct);
            var dur = await _ffmpegService.ProbeDurationAsync(temp, ct);
            string title = dur.TotalSeconds < 100 && !string.IsNullOrEmpty(sel.SongTitle) ? sel.SongTitle + " (TV Size)" : sel.SongTitle;

            Logger.Debug("AnimeThemes MP3: Converting audio for '{0}' ({1})", series.PreferredTitle?.Value, sel.SlugDisplay);
            await _ffmpegService.ConvertToMp3FileAsync(temp, themePath, title, sel.SlugDisplay, sel.Artist, sel.AnimeTitle, ct);

            int primaryId = OverrideHelper.GetPrimary(series.ID, metadataService);
            string? vfsLink = TryLinkIntoVfs(videoFile, primaryId, themePath);

            Logger.Info("AnimeThemes MP3: Successfully generated '{0}' ({1})", series.PreferredTitle?.Value, sel.SlugDisplay);
            return new(folder, "ok", null, themePath, vfsLink, sel.AnimeTitle, sel.AnimeSlug, series.ID, sel.SlugDisplay, dur.TotalSeconds);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AnimeThemes MP3: Failed to process for {0}", folder);
            return new(folder, "error", ex.Message);
        }
        finally
        {
            CleanupTempFile(temp);
        }
    }

    /// <summary>Returns an in‑memory MP3 stream rather than writing a file.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple containing a preview result or an error result.</returns>
    public async Task<(ThemePreviewResult? Preview, ThemeMp3OperationResult? Error)> PreviewAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var (Error, Data) = PrepareContext(query, true);
        if (Error != null)
            return (null, Error);
        var (folder, _, _, series) = Data!.Value;
        string? temp = null;
        try
        {
            Logger.Info("AnimeThemes MP3: Previewing theme for series '{0}'", series.PreferredTitle?.Value);
            var sel = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
            if (sel == null)
                return (null, new(folder, "error", "Entry not found."));
            temp = await DownloadAudioAsync(sel.AudioUrl, ct);
            return (new(await _ffmpegService.ConvertToMp3StreamAsync(temp, ct), "Theme.mp3", "audio/mpeg", $"{sel.SlugDisplay}: {sel.SongTitle}"), null);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AnimeThemes MP3: Failed to preview for {0}", folder);
            return (null, new(folder, "error", ex.Message));
        }
        finally
        {
            CleanupTempFile(temp);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Validates the directory and resolves the Shoko series for the request.</summary>
    private (ThemeMp3OperationResult? Error, (string Folder, string ThemePath, IVideoFile VideoFile, IShokoSeries Series)? Data) PrepareContext(AnimeThemesMp3Query q, bool preview)
    {
        if (string.IsNullOrWhiteSpace(q.Path))
            return (new("", "error", "Path is required."), null);
        string folder = q.Path;

        Logger.Debug("AnimeThemes MP3: Preparing context for folder: {0}", folder);
        if (!Directory.Exists(folder))
            return (new(folder, "error", "Folder not found."), null);

        string themePath = Path.Combine(folder, "Theme.mp3");
        if (!preview && !q.Force && File.Exists(themePath))
            return (new(folder, "skipped", "Theme.mp3 already exists."), null);

        string? vid = Directory.EnumerateFiles(folder).FirstOrDefault(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f)));
        if (vid == null)
        {
            Logger.Debug("AnimeThemes MP3: No recognized video files in {0}", folder);
            return (new(folder, "error", "No video files found."), null);
        }

        var vf = videoService.GetVideoFileByAbsolutePath(vid);
        var s = vf?.Video?.Episodes?.FirstOrDefault()?.Series;

        if (s == null)
        {
            Logger.Warn("AnimeThemes MP3: Series lookup failed for video {0} in {1}", vid, folder);
            return (new(folder, "error", vf == null ? "Video not recognized." : "Series lookup failed."), null);
        }

        Logger.Debug("AnimeThemes MP3: Folder {0} maps to series '{1}' (AniDB: {2})", folder, s.PreferredTitle?.Value, s.AnidbAnimeID);
        return (null, (folder, themePath, vf!, s));
    }

    /// <summary>Queries the AnimeThemes API for a specific series theme.</summary>
    private async Task<ThemeSelection?> FetchThemeAsync(int aid, string? slugArg, int offset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(slugArg) && !AnimeThemesHelper.SlugRegex.IsMatch(slugArg))
            throw new ArgumentException("Invalid slug format.");

        Logger.Debug("AnimeThemes MP3: Fetching metadata for AniDB ID {0} (Slug: {1}, Offset: {2})", aid, slugArg ?? "Auto", offset);
        var (parsedBase, _) = AnimeThemesHelper.ParseSlug(slugArg ?? "");
        string filter = string.IsNullOrEmpty(slugArg)
            ? "&filter[animetheme][type]=OP,ED"
            : $"&filter[animetheme][slug]={Uri.EscapeDataString(parsedBase is "OP" or "ED" ? $"{parsedBase},{parsedBase}1" : parsedBase)}";

        var anime = await _apiClient.FetchAnimeThemesAsync(aid, filter, ct);
        var entry = anime?.Anime?.ElementAtOrDefault(offset);
        if (entry?.Animethemes == null || entry.Animethemes.Count == 0)
            return null;

        int idx = (string.IsNullOrEmpty(slugArg) && entry.Animethemes.Count > 1 && string.Equals(entry.Animethemes[1].Slug, "OP1", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        var themeDetail = await _apiClient.FetchAnimeThemeWithArtistsAsync(entry.Animethemes[idx].Id, ct);
        var audio = themeDetail?.Animetheme?.Animethemeentries?.FirstOrDefault()?.Videos?.FirstOrDefault()?.Audio?.Link;

        if (string.IsNullOrEmpty(audio))
            return null;

        var (bp, sp) = AnimeThemesHelper.ParseSlug(themeDetail!.Animetheme!.Slug ?? "");
        string display = $"{(bp.StartsWith("OP", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending")} {bp[2..]}".Trim() + AnimeThemesHelper.FormatSlugTag(sp);
        var artists = themeDetail.Animetheme.Song?.Artists;

        Logger.Debug("AnimeThemes MP3: Selected theme: {0} - {1}", display, themeDetail.Animetheme.Song?.Title);
        return new ThemeSelection(
            audio,
            themeDetail.Animetheme.Slug ?? "",
            display.Trim(),
            themeDetail.Animetheme.Song?.Title ?? "",
            artists?.Count > 1 ? string.Join("; ", artists.Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name)) : artists?.FirstOrDefault()?.Name ?? "",
            entry.Name ?? "",
            entry.Slug ?? ""
        );
    }

    /// <summary>Downloads an audio file to a temporary location.</summary>
    private async Task<string> DownloadAudioAsync(string url, CancellationToken ct)
    {
        Logger.Debug("AnimeThemes MP3: Downloading audio from {0}", url);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string temp = Path.Combine(Path.GetTempPath(), $"at-{Guid.NewGuid():N}{Path.GetExtension(url)}");
        using (var i = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        using (var o = File.Create(temp))
            await i.CopyToAsync(o, ct).ConfigureAwait(false);
        return temp;
    }

    /// <summary>Creates a symbolic link for the Theme.mp3 in the Shoko VFS directory.</summary>
    private string? TryLinkIntoVfs(IVideoFile loc, int sid, string src)
    {
        string? root = VfsShared.ResolveImportRootPath(loc);
        if (root == null)
            return null;

        string destDir = Path.Combine(root, VfsShared.ResolveRootFolderName(), sid.ToString());
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, "Theme.mp3");

        Logger.Debug("AnimeThemes MP3: Linking Theme.mp3 to VFS: {0}", dest);
        return VfsShared.TryCreateLink(src, dest, Logger) ? dest : null;
    }

    /// <summary>Deletes a temporary file from disk.</summary>
    private static void CleanupTempFile(string? path)
    {
        try
        {
            if (File.Exists(path))
            {
                Logger.Trace("AnimeThemes MP3: Cleaning up temporary file: {0}", path);
                File.Delete(path);
            }
        }
        catch { }
    }

    #endregion
}
