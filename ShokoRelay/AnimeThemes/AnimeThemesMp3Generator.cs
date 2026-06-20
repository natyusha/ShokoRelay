using System.Collections.Concurrent;
using NLog;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;
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
/// <param name="Seasonal">Whether to filter processing to the current anime season (with a one-month early buffer).</param>
public record AnimeThemesMp3Query(string? Path, string? Slug, int Offset = 0, bool Batch = false, bool Force = false, bool Seasonal = false);

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

/// <summary>Internal record representing a selected theme's metadata and audio link.</summary>
internal sealed record ThemeSelection(string AudioUrl, string SlugRaw, string SlugDisplay, string SongTitle, string Artist, string AnimeTitle, string AnimeSlug);

#endregion

/// <summary>Provides functionality for fetching, converting and previewing anime theme audio from the AnimeThemes API.</summary>
public class AnimeThemesMp3Generator(HttpClient httpClient, IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider, FfmpegService ffmpegService, PlexClient plexClient)
{
    #region Setup & Cache

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly AnimeThemesApi _apiClient = new(httpClient);
    private HashSet<string>? _themeMp3Cache;
    private readonly Lock _cacheLock = new();

    private string ThemeCacheFilePath => Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMp3Cache);

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
            return _themeMp3Cache != null ? [.. _themeMp3Cache] : [];
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
            _themeMp3Cache ??= new(VfsShared.PathComparer);
            if (_themeMp3Cache.Add(folderPath))
                SaveCacheToFile();
        }
    }

    /// <summary>Forces a re-scan of all managed import folder roots and rebuilds the HashSet cache.</summary>
    private void RefreshThemeMp3CacheInternal()
    {
        s_logger.Info("AnimeThemes: Building Theme.mp3 cache -> scanning all managed import folders...");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VfsShared.ResolveRootFolderName(), VfsShared.ResolveCollectionImagesFolderName(), VfsShared.ResolveAnimeThemesFolderName() };
        try
        {
            var roots = (videoService.GetAllManagedFolders() ?? [])
                .Select(mf => mf.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(VfsShared.PathComparer)
                .ToList();
            _themeMp3Cache = new HashSet<string>(
                roots
                    .SelectMany(r =>
                        Directory
                            .EnumerateFiles(r!, "Theme.mp3", SearchOption.AllDirectories)
                            .Where(f => !Path.GetRelativePath(r!, Path.GetDirectoryName(f)!).Split(Path.DirectorySeparatorChar).Any(s => excluded.Contains(s)))
                            .Select(Path.GetDirectoryName)
                    )
                    .OfType<string>(),
                VfsShared.PathComparer
            );
            SaveCacheToFile();
            s_logger.Info("AnimeThemes: Theme.mp3 cache refreshed -> {0} folders found across {1} roots", _themeMp3Cache.Count, roots.Count);
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "AnimeThemes: RefreshThemeMp3Cache -> Repositories not ready");
            _themeMp3Cache ??= new(VfsShared.PathComparer);
        }
    }

    /// <summary>Loads the Theme.mp3 folder paths from the local cache file, constructing a HashSet for performance.</summary>
    private void LoadCacheFromFile()
    {
        if (File.Exists(ThemeCacheFilePath))
        {
            try
            {
                var lines = File.ReadAllLines(ThemeCacheFilePath).Where(l => !string.IsNullOrWhiteSpace(l));
                var set = new HashSet<string>(lines, VfsShared.PathComparer);
                if (set.Count > 0)
                {
                    _themeMp3Cache = set;
                    return;
                }
            }
            catch
            {
                s_logger.Warn("AnimeThemes: Failed to read {0} -> Attempting full scan", ShokoRelayConstants.FileAtMp3Cache);
            }
        }
        RefreshThemeMp3CacheInternal();
    }

    /// <summary>Save the current cached Theme.mp3 folder list to the local cache file.</summary>
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
            s_logger.Warn("AnimeThemes MP3: Batch root not found -> {0}", root);
            return new ThemeMp3BatchResult(root, [new(root, "error", "Batch root not found.")], 0, 0, 1);
        }

        string vfsRoot = VfsShared.ResolveRootFolderName();
        bool isVfsPath = root.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Contains(vfsRoot, StringComparer.OrdinalIgnoreCase);

        if (isVfsPath)
        {
            string msg = $"Cannot execute batch generation inside the VFS directory '{vfsRoot}'. Target your physical import root instead.";
            s_logger.Warn("AnimeThemes MP3: {0}", msg);
            return new ThemeMp3BatchResult(root, [new(root, "error", msg)], 0, 0, 1);
        }

        s_logger.Info("AnimeThemes MP3: Starting batch generation for root -> {0}", root);
        var (results, p, s, e) = (new List<ThemeMp3OperationResult>(), 0, 0, 0);

        // Scan recursively for all directories, skipping ignored/VFS folders
        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).Where(f => !VfsShared.IsPathIgnored(f)).ToList();

        var processedSeries = new ConcurrentDictionary<int, byte>();

        await Parallel.ForEachAsync(
            folders,
            DefaultParallelOptions(ct),
            async (folder, token) =>
            {
                var res = await ProcessSingleAsync(query with { Path = folder, Batch = true }, processedSeries, token).ConfigureAwait(false);
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

        s_logger.Info("AnimeThemes MP3: Batch generation finished -> {0} processed, {1} skipped, {2} errors", p, s, e);
        return new ThemeMp3BatchResult(root, results, p, s, e);
    }

    /// <summary>Handles a single folder request, downloading and converting the selected theme to an MP3.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="batchProcessedSeries">Active batch-processed series tracker dictionary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An operation result object.</returns>
    public async Task<ThemeMp3OperationResult> ProcessSingleAsync(AnimeThemesMp3Query query, ConcurrentDictionary<int, byte>? batchProcessedSeries, CancellationToken ct)
    {
        var (error, data) = PrepareContext(query, batchProcessedSeries);
        if (error != null)
            return error;
        var (folder, themePath, videoFile, series) = data!.Value;

        // Season Filter: Only applied when Batch is true. Ignored for individual folder requests.
        if (query.Batch && query.Seasonal)
        {
            var (start, end) = AnimeThemesHelper.GetCurrentSeasonRange(DateTime.Now);
            if (!series.AirDate.HasValue || series.AirDate.Value < start || series.AirDate.Value > end)
            {
                string skipMsg = "Series does not match the current season filter.";
                s_logger.Debug("AnimeThemes MP3: Skipped series '{0}' ({1})", series.PreferredTitle?.Value, skipMsg);
                return new(folder, "skipped", skipMsg);
            }
        }

        string? temp = null;
        try
        {
            if (!query.Batch)
                s_logger.Info("AnimeThemes MP3: Generating Theme.mp3 for series '{0}' in {1}", series.PreferredTitle?.Value ?? series.ID.ToString(), folder);

            var sel = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
            if (sel == null)
            {
                string skipMsg = string.IsNullOrWhiteSpace(query.Slug) ? "Entry not found." : $"No entry for slug '{query.Slug}'.";
                if (!query.Batch)
                    s_logger.Info("AnimeThemes MP3: Skipped series '{0}' ({1})", series.PreferredTitle?.Value, skipMsg);

                return new(folder, "skipped", skipMsg);
            }

            temp = await DownloadAudioAsync(sel.AudioUrl, ct);
            var dur = await ffmpegService.ProbeDurationAsync(temp, ct);
            string title = dur.TotalSeconds < 100 && !string.IsNullOrEmpty(sel.SongTitle) ? sel.SongTitle + " (TV Size)" : sel.SongTitle;

            s_logger.Debug("AnimeThemes MP3: Converting audio for '{0}' ({1})", series.PreferredTitle?.Value, sel.SlugDisplay);
            await ffmpegService.ConvertToMp3FileAsync(temp, "Theme.mp3", title, sel.SlugDisplay, sel.Artist, sel.AnimeTitle, ct, folder).ConfigureAwait(false);

            int primaryId = OverrideHelper.GetPrimary(series.ID, metadataService);
            string? vfsLink = TryLinkIntoVfs(videoFile, primaryId, themePath);

            // After a successful VFS link is created, trigger a Plex metadata refresh to ensure the new Theme.mp3 is picked up by the server.
            if (!string.IsNullOrEmpty(vfsLink) && plexClient.IsEnabled)
                TriggerPlexRefresh(series.ID);

            s_logger.Info("AnimeThemes MP3: Successfully generated '{0}' ({1})", series.PreferredTitle?.Value, sel.SlugDisplay);
            return new(folder, "ok", null, themePath, vfsLink, sel.AnimeTitle, sel.AnimeSlug, series.ID, sel.SlugDisplay, dur.TotalSeconds);
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "AnimeThemes MP3: Failed to process for {0}", folder);
            return new(folder, "error", ex.Message);
        }
        finally
        {
            CleanupTempFile(temp);
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Validates the directory and resolves the Shoko series for the request.</summary>
    /// <param name="q">The query parameters of the request.</param>
    /// <param name="batchProcessedSeries">Active batch-processed series tracker dictionary.</param>
    /// <returns>A tuple containing either an error operation result, or resolved context metadata (folder path, target file, and series reference).</returns>
    private (ThemeMp3OperationResult? Error, (string Folder, string ThemePath, IVideoFile VideoFile, IShokoSeries Series)? Data) PrepareContext(
        AnimeThemesMp3Query q,
        ConcurrentDictionary<int, byte>? batchProcessedSeries
    )
    {
        if (string.IsNullOrWhiteSpace(q.Path))
            return (new("", "error", "Path is required."), null);
        string folder = q.Path;

        s_logger.Debug("AnimeThemes MP3: Preparing context for folder -> {0}", folder);
        if (!Directory.Exists(folder))
            return (new(folder, "error", "Folder not found."), null);

        string vfsRoot = VfsShared.ResolveRootFolderName();
        bool isVfsPath = folder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Contains(vfsRoot, StringComparer.OrdinalIgnoreCase);

        if (isVfsPath)
            return (new(folder, "error", $"Cannot generate Theme.mp3 inside the VFS directory '{vfsRoot}'. Target your physical import folder instead."), null);

        // Lazily scan subfolders recursively to find the first video file while ignoring VFS and AnimeThemes system directories
        string? vid = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).FirstOrDefault(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f)) && !VfsShared.IsPathIgnored(f));

        if (vid == null)
        {
            s_logger.Debug("AnimeThemes MP3: No recognized video files in folder -> {0}", folder);
            return (new(folder, "error", "No video files found."), null);
        }

        var vf = videoService.GetVideoFileByAbsolutePath(vid);
        var s = vf?.Video?.Episodes?.FirstOrDefault()?.Series;

        if (s == null)
        {
            s_logger.Warn("AnimeThemes MP3: Series lookup failed for video {0} in {1}", vid, folder);
            return (new(folder, "error", vf == null ? "Video not recognized." : "Series lookup failed."), null);
        }

        // Prevent duplicate processing of the same series during a batch run
        if (batchProcessedSeries != null && !batchProcessedSeries.TryAdd(s.ID, 0))
            return (new(folder, "skipped", "Theme already processed for this series in another directory."), null);

        string themePath = Path.Combine(folder, "Theme.mp3");
        if (!q.Force && File.Exists(themePath))
            return (new(folder, "skipped", "Theme.mp3 already exists."), null);

        s_logger.Debug("AnimeThemes MP3: Folder {0} maps to series '{1}' (AniDB: {2})", folder, s.PreferredTitle?.Value, s.AnidbAnimeID);
        return (null, (folder, themePath, vf!, s));
    }

    /// <summary>Queries the AnimeThemes API for a specific series theme.</summary>
    /// <param name="aid">The AniDB ID of the series.</param>
    /// <param name="slugArg">Optional preferred theme slug identifier (e.g. OP1).</param>
    /// <param name="offset">Offset to use when multiple themes match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A selected theme metadata and audio reference if found; otherwise null.</returns>
    private async Task<ThemeSelection?> FetchThemeAsync(int aid, string? slugArg, int offset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(slugArg) && !AnimeThemesHelper.SlugRegex.IsMatch(slugArg))
            throw new ArgumentException("Invalid slug format.");

        s_logger.Debug("AnimeThemes MP3: Fetching metadata for AniDB ID {0} (Slug: {1}, Offset: {2})", aid, slugArg ?? "Auto", offset);
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

        s_logger.Debug("AnimeThemes MP3: Selected theme: {0} - {1}", display, themeDetail.Animetheme.Song?.Title);
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

    /// <summary>Downloads an audio file to a temporary location on disk.</summary>
    /// <param name="url">The remote audio URL to download.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path to the local temporary file.</returns>
    private async Task<string> DownloadAudioAsync(string url, CancellationToken ct)
    {
        s_logger.Debug("AnimeThemes MP3: Downloading audio from {0}", url);
        using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string temp = Path.Combine(Path.GetTempPath(), $"at-{Guid.NewGuid():N}{Path.GetExtension(url)}");
        using (var i = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        using (var o = File.Create(temp))
            await i.CopyToAsync(o, ct).ConfigureAwait(false);
        return temp;
    }

    /// <summary>Creates a relative symbolic link for the Theme.mp3 in the Shoko VFS directory.</summary>
    /// <param name="loc">The video file reference to determine the import root.</param>
    /// <param name="sid">The Shoko Series ID.</param>
    /// <param name="src">The source Theme.mp3 file path.</param>
    /// <returns>The destination link path if created successfully; otherwise null.</returns>
    private string? TryLinkIntoVfs(IVideoFile loc, int sid, string src)
    {
        string? root = VfsShared.ResolveImportRootPath(loc);
        if (root == null)
            return null;

        string destDir = Path.Combine(root, VfsShared.ResolveRootFolderName(), sid.ToString());
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, "Theme.mp3");

        s_logger.Debug("AnimeThemes MP3: Linking Theme.mp3 to VFS -> {0}", dest);
        return VfsShared.TryCreateLink(src, dest, s_logger) ? dest : null;
    }

    /// <summary>Deletes a temporary file from disk.</summary>
    private static void CleanupTempFile(string? path)
    {
        try
        {
            if (File.Exists(path))
            {
                s_logger.Trace("AnimeThemes MP3: Cleaning up temporary file -> {0}", path);
                File.Delete(path);
            }
        }
        catch { }
    }

    /// <summary>Fires a background task to refresh Plex metadata for a specific series after a new theme is added.</summary>
    /// <param name="seriesId">The Shoko series ID to refresh.</param>
    private void TriggerPlexRefresh(int seriesId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                int bufferSeconds = Settings.Advanced.PlexScanDelay;
                if (bufferSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(bufferSeconds)).ConfigureAwait(false);

                var targets = plexClient.GetConfiguredTargets();
                foreach (var target in targets)
                {
                    var ratingKey = await plexClient.FindRatingKeyForShokoSeriesInSectionAsync(seriesId, target).ConfigureAwait(false);
                    if (ratingKey.HasValue)
                    {
                        s_logger.Debug("AnimeThemes MP3: Refreshing Plex metadata for ratingKey {0} on {1}", ratingKey.Value, target.ServerName);
                        await plexClient.RefreshMetadataAsync(ratingKey.Value, target).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                s_logger.Warn(ex, "AnimeThemes MP3: Failed to trigger Plex refresh for series {0}", seriesId);
            }
        });
    }

    #endregion
}
