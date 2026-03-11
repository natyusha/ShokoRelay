using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.Video;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Parameters used when requesting an MP3 generation or preview.
/// The path is the folder containing the anime episode and Slug/Offset pick a theme when multiple exist while Batch and Force control batch behaviour and overwrite semantics.
/// </summary>
public record AnimeThemesMp3Query
{
    public string? Path { get; init; }
    public string? Slug { get; init; }
    public int Offset { get; init; } = 0;
    public bool Batch { get; init; }
    public bool Force { get; init; }
}

/// <summary>
/// Result for a single MP3 generation operation, containing status, paths and optional metadata about the anime/theme.
/// </summary>
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

/// <summary>
/// Aggregate result returned after processing one or more folders for theme MP3 generation, detailing counts and individual operation outcomes.
/// </summary>
public record ThemeMp3BatchResult(string Root, IReadOnlyList<ThemeMp3OperationResult> Items, int Processed, int Skipped, int Errors);

/// <summary>
/// Represents the data returned when generating a preview MP3, including the stream and associated metadata such as filename and content type.
/// </summary>
public record ThemePreviewResult(Stream Stream, string FileName, string ContentType, string? Title);

internal sealed record ThemeSelection(string AudioUrl, string SlugRaw, string SlugDisplay, string SongTitle, string Artist, string AnimeTitle, string AnimeSlug);

/// <summary>
/// Provides functionality for fetching, converting and previewing anime theme audio from the AnimeThemes API.
/// This operates on individual folders or batches and integrates with Shoko metadata to locate the correct series and file.
/// </summary>
public class AnimeThemesMp3Generator
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new();
    private readonly FfmpegService _ffmpegService;
    private readonly AnimeThemesApi _apiClient;

    private readonly IVideoService _videoService;
    private readonly IMetadataService _metadataService;
    private readonly string _configDirectory;

    /// <summary>
    /// Constructs a new <see cref="AnimeThemesMp3Generator"/>, wiring in services for metadata lookup and ffmpeg operations.
    /// </summary>
    /// <param name="metadataService">Service used to fetch Shoko metadata.</param>
    /// <param name="videoService">Service used to resolve video file objects.</param>
    /// <param name="configProvider">Provides configuration values such as the plugin directory path for <see cref="FfmpegService"/>.</param>
    public AnimeThemesMp3Generator(IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
    {
        _metadataService = metadataService;
        _videoService = videoService;
        _configDirectory = configProvider.ConfigDirectory;
        _ffmpegService = new FfmpegService(configProvider.PluginDirectory);
        _apiClient = new AnimeThemesApi();
        AnimeThemesHelper.EnsureUserAgent(Http);
    }

    /// <summary>
    /// Cached list of folders containing Theme.mp3 files. Loaded from <c>mp3_animethemes.cache</c> on first access and persisted after every mutation.
    /// </summary>
    private List<string>? _themeMp3Cache;
    private readonly object _cacheLock = new();

    /// <summary>
    /// Full path to the <c>mp3_animethemes.cache</c> file inside the config directory.
    /// </summary>
    private string ThemeCacheFilePath => Path.Combine(_configDirectory, "mp3_animethemes.cache");

    /// <summary>
    /// Returns the cached list of folders containing Theme.mp3 files.
    /// On first call the cache is loaded from the <c>mp3_animethemes.cache</c> file; if the file does not exist an empty list is returned.
    /// </summary>
    /// <returns>A read-only snapshot of cached folder paths.</returns>
    public IReadOnlyList<string> GetCachedThemeMp3Folders()
    {
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            return _themeMp3Cache ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
    }

    /// <summary>
    /// Forces a re-scan of all managed import folder roots, rebuilds the in-memory Theme.mp3 cache and persists it to the <c>mp3_animethemes.cache</c> file.
    /// </summary>
    public void RefreshThemeMp3Cache()
    {
        lock (_cacheLock)
            RefreshThemeMp3CacheInternal();
    }

    /// <summary>
    /// Adds a folder path to the Theme.mp3 cache if it is not already present, then persists the updated cache to the <c>mp3_animethemes.cache</c> file.
    /// If the cache has not been loaded yet, it is loaded from the file first.
    /// </summary>
    /// <param name="folderPath">Absolute path of the folder containing a Theme.mp3.</param>
    public void AddToThemeMp3Cache(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var cache = _themeMp3Cache!;
            if (!cache.Contains(folderPath, comparer))
            {
                cache.Add(folderPath);
                SaveCacheToFile();
            }
        }
    }

    private void RefreshThemeMp3CacheInternal()
    {
        Logger.Info("Building Theme.mp3 cache — scanning all managed import folders...");
        var excludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            VfsShared.ResolveRootFolderName(),
            VfsShared.ResolveCollectionPostersFolderName(),
            VfsShared.ResolveAnimeThemesFolderName(),
        };

        List<string?> roots;
        try
        {
            var managed = _videoService.GetAllManagedFolders();
            roots = (managed ?? Enumerable.Empty<IManagedFolder>())
                .Select(mf => mf.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "RefreshThemeMp3Cache: Shoko repositories not ready yet, will retry on next access.");
            // leave _themeMp3Cache null so the next call retries
            return;
        }

        var result = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root!, "Theme.mp3", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(file)!;
                    // Exclude files that reside anywhere inside a VFS/AT/Collection root folder
                    string relative = dir.Substring(root!.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Any(s => excludedFolders.Contains(s)))
                        continue;
                    result.Add(dir);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RefreshThemeMp3Cache: error scanning {Root}", root);
            }
        }

        _themeMp3Cache = result;
        SaveCacheToFile();
        Logger.Info("Theme.mp3 cache refreshed: {Count} folders found across {Roots} import roots.", result.Count, roots.Count);
    }

    /// <summary>
    /// Loads the in-memory cache from the <c>mp3_animethemes.cache</c> file. If the file does not exist or is empty, a full scan is triggered automatically.
    /// </summary>
    private void LoadCacheFromFile()
    {
        var path = ThemeCacheFilePath;
        if (File.Exists(path))
        {
            try
            {
                var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (lines.Count > 0)
                {
                    _themeMp3Cache = lines;
                    Logger.Info("Theme.mp3 cache loaded from file: {Count} entries from {Path}", lines.Count, path);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read mp3_animethemes.cache file at {Path}, will attempt a full scan.", path);
            }
        }
        // No cache file or it was empty — run a full scan to populate it
        RefreshThemeMp3CacheInternal();
        // If the scan failed (Shoko not ready), _themeMp3Cache stays null and will retry on next access
    }

    /// <summary>
    /// Persists the current in-memory cache to the <c>mp3_animethemes.cache</c> file, one folder path per line.
    /// </summary>
    private void SaveCacheToFile()
    {
        if (_themeMp3Cache == null)
            return;
        try
        {
            File.WriteAllLines(ThemeCacheFilePath, _themeMp3Cache);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to write mp3_animethemes.cache file at {Path}", ThemeCacheFilePath);
        }
    }

    /// <summary>
    /// Processes a folder (and optionally its subfolders) to generate MP3s for anime themes; if <c>query.Batch</c> is set each subdirectory is handled separately.
    /// </summary>
    /// <param name="query">Options controlling paths, slug filters, offsets, batch and force behaviour.</param>
    /// <param name="ct">Cancellation token to abort processing.</param>
    /// <returns>A <see cref="ThemeMp3BatchResult"/> summarising the status of each visited folder.</returns>
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

        // build initial folder list. Include the root itself since batch mode should still operate on the folder passed to it directly
        var folders = new List<string> { root };
        folders.AddRange(Directory.EnumerateDirectories(root));
        if (!query.Force)
            folders = folders.Where(f => !File.Exists(Path.Combine(f, "Theme.mp3"))).ToList();

        // build exclusion set once before the loop
        var excludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            VfsShared.ResolveRootFolderName(),
            VfsShared.ResolveCollectionPostersFolderName(),
            VfsShared.ResolveAnimeThemesFolderName(),
        };

        // process in parallel using the configured parallelism setting
        int maxDop = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism);
        await Parallel
            .ForEachAsync(
                folders,
                new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = ct },
                async (folder, token) =>
                {
                    string folderName = Path.GetFileName(folder);
                    Logger.Info("AnimeThemes MP3 batch: processing folder {Folder}", folder);

                    if (excludedFolders.Contains(folderName))
                    {
                        lock (results)
                        {
                            skipped++;
                            results.Add(new ThemeMp3OperationResult(folder, "skipped", "Excluded system folder."));
                        }
                        Logger.Info("AnimeThemes MP3 batch: skipped system folder {Folder}", folder);
                        return;
                    }

                    var singleQuery = query with { Path = folder, Batch = false };
                    var result = await ProcessSingleAsync(singleQuery, token).ConfigureAwait(false);
                    lock (results)
                    {
                        results.Add(result);
                    }

                    Logger.Info("AnimeThemes MP3 batch: folder {Folder} => {Status}{Message}", folder, result.Status, string.IsNullOrWhiteSpace(result.Message) ? "" : ": " + result.Message);

                    switch (result.Status)
                    {
                        case "ok":
                            Interlocked.Increment(ref processed);
                            break;
                        case "skipped":
                            Interlocked.Increment(ref skipped);
                            break;
                        default:
                            Interlocked.Increment(ref errors);
                            break;
                    }
                }
            )
            .ConfigureAwait(false);

        return new ThemeMp3BatchResult(root, results, processed, skipped, errors);
    }

    /// <summary>
    /// Handles a single folder request, downloading and converting the selected theme to an MP3 and optionally creating a VFS link.
    /// </summary>
    /// <param name="query">Parameters describing which folder to operate on and any slug/offset for theme selection.</param>
    /// <param name="ct">Cancellation token used to abort the operation.</param>
    /// <returns>A <see cref="ThemeMp3OperationResult"/> describing success, skip, or error.</returns>
    public async Task<ThemeMp3OperationResult> ProcessSingleAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var contextResult = PrepareContext(query, allowPreview: false);
        if (contextResult.Error != null)
            return contextResult.Error;

        var (folder, themePath, videoFile, series) = contextResult.Data!.Value;

        string? tempPath = null;
        try
        {
            var selection = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
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
            // determine correct series folder considering any merge overrides
            int linkSeriesId = OverrideHelper.GetPrimary(series.ID, _metadataService);
            string? vfsLink = TryLinkIntoVfs(videoFile, linkSeriesId, themePath);

            return new ThemeMp3OperationResult(folder, "ok", null, themePath, vfsLink, selection.AnimeTitle, selection.AnimeSlug, series.ID, selection.SlugDisplay, duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process animethemes for {Folder}", folder);
            return Error(folder, ex.Message);
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    /// <summary>
    /// Like <see cref="ProcessSingleAsync"/>, but returns an in‑memory MP3 stream rather than writing a file; used for previewing output without disk writes.
    /// </summary>
    /// <param name="query">Query parameters describing the folder, slug, and offset.</param>
    /// <param name="ct">Cancellation token for aborting the preview.</param>
    /// <returns>A tuple with either a <see cref="ThemePreviewResult"/> or an <see cref="ThemeMp3OperationResult"/> on error.</returns>
    public async Task<(ThemePreviewResult? Preview, ThemeMp3OperationResult? Error)> PreviewAsync(AnimeThemesMp3Query query, CancellationToken ct)
    {
        var contextResult = PrepareContext(query, allowPreview: true);
        if (contextResult.Error != null)
            return (null, contextResult.Error);

        var (folder, _, _, series) = contextResult.Data!.Value;

        string? tempPath = null;
        try
        {
            var selection = await FetchThemeAsync(series.AnidbAnimeID, query.Slug, query.Offset, ct);
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
            CleanupTempFile(tempPath);
        }
    }

    /// <summary>
    /// Validates the query, ensures the folder exists, locates a video file and resolves the Shoko series; optionally checks for an existing MP3 when <paramref name="allowPreview"/> is false.
    /// </summary>
    /// <param name="query">Query containing the path and other options.</param>
    /// <param name="allowPreview">If true the caller is only previewing and an existing MP3 file is permitted.</param>
    private (ThemeMp3OperationResult? Error, (string Folder, string ThemePath, IVideoFile VideoFile, IShokoSeries Series)? Data) PrepareContext(AnimeThemesMp3Query query, bool allowPreview)
    {
        if (string.IsNullOrWhiteSpace(query.Path))
            return (Error("", "Path is required."), null);

        string folder = ResolvePath(query.Path);
        if (!Directory.Exists(folder))
            return (Error(folder, "Folder not found."), null);

        string themePath = Path.Combine(folder, "Theme.mp3");
        if (!allowPreview && !query.Force && File.Exists(themePath))
            return (Error(folder, "Theme.mp3 already exists.", "skipped"), null);

        string? videoPath = Directory.EnumerateFiles(folder).FirstOrDefault(f => AnimeThemesHelper.VideoFileExtensions.Contains(Path.GetExtension(f)));
        if (string.IsNullOrWhiteSpace(videoPath))
            return (Error(folder, "No video files found in folder."), null);

        var videoFile = _videoService.GetVideoFileByAbsolutePath(videoPath);
        if (videoFile?.Video == null)
            return (Error(folder, "Video not recognized by Shoko."), null);

        // Derive the series via the first linked episode since videos are linked at the episode level.
        var series = videoFile.Video.Episodes?.FirstOrDefault()?.Series;
        if (series == null)
            return (Error(folder, "Series lookup failed."), null);

        return (null, (folder, themePath, videoFile, series));
    }

    /// <summary>
    /// Creates a <see cref="ThemeMp3OperationResult"/> representing a failure or skipped state.
    /// </summary>
    /// <param name="folder">The folder that was being processed.</param>
    /// <param name="message">Text describing the error or skip reason.</param>
    /// <param name="status">Optional status string (defaults to "error").</param>
    private static ThemeMp3OperationResult Error(string folder, string message, string status = "error")
    {
        return new ThemeMp3OperationResult(folder, status, message);
    }

    /// <summary>
    /// Fetches a specific anime theme from the AnimeThemes API, applies normalization to the slug for display,
    /// and retrieves the associated audio URL and metadata.
    /// </summary>
    /// <param name="anidbId">The AniDB ID of the anime to fetch themes for.</param>
    /// <param name="slugArg">An optional slug filter (e.g., "OP1", "ED2-BD").</param>
    /// <param name="offset">The index offset if multiple anime entries are returned by the API.</param>
    /// <param name="ct">A cancellation token to abort the network request.</param>
    /// <returns>
    /// A ThemeSelection containing the audio URL and formatted metadata if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown if the provided slugArg does not match the required regex format.</exception>
    private async Task<ThemeSelection?> FetchThemeAsync(int anidbId, string? slugArg, int offset, CancellationToken ct)
    {
        // Use the centralized helper to parse the incoming argument for filtering
        var (parsedBase, _) = AnimeThemesHelper.ParseSlug(slugArg ?? "");

        // Validation: Ensure the slug matches the expected format before querying the API
        if (!string.IsNullOrWhiteSpace(slugArg) && !AnimeThemesHelper.SlugRegex.IsMatch(slugArg))
            throw new ArgumentException("Invalid slug format. Use values like OP, OP2, ED, ED2, OP1-TV, ED-BD.");

        // Normalize for the API filter: if it's OP/ED, ensure we search for both "OP" and "OP1"
        string slugFilter;
        if (string.IsNullOrWhiteSpace(slugArg))
        {
            slugFilter = "&filter[animetheme][type]=OP,ED";
        }
        else
        {
            // API requires OP1/ED1 for the first theme, so we check both for standard types
            string apiSlug = (parsedBase is "OP" or "ED") ? $"{parsedBase},{parsedBase}1" : parsedBase;
            slugFilter = $"&filter[animetheme][slug]={Uri.EscapeDataString(apiSlug)}";
        }

        var anime = await _apiClient.FetchAnimeThemesAsync(anidbId, slugFilter, ct);
        var animeEntry = anime?.Anime?.ElementAtOrDefault(offset);
        if (animeEntry == null || animeEntry.Animethemes == null || animeEntry.Animethemes.Count == 0)
            return null;

        // Default to the first theme, or the second if OP1 is explicitly found at index 1
        int idx = 0;
        if (string.IsNullOrWhiteSpace(slugArg) && animeEntry.Animethemes.Count > 1 && string.Equals(animeEntry.Animethemes[1].Slug, "OP1", StringComparison.OrdinalIgnoreCase))
            idx = 1;

        var theme = animeEntry.Animethemes.ElementAtOrDefault(idx);
        if (theme == null)
            return null;

        var themeDetail = await _apiClient.FetchAnimeThemeWithArtistsAsync(theme.Id, ct);
        if (themeDetail?.Animetheme == null)
            return null;

        var entry = themeDetail.Animetheme.Animethemeentries?.FirstOrDefault();
        var video = entry?.Videos?.FirstOrDefault();
        string? audioUrl = video?.Audio?.Link;
        if (string.IsNullOrWhiteSpace(audioUrl))
            return null;

        // Extract base (e.g., "OP", "OP2") and suffix (e.g., "BD") using AnimeThemesHelper
        var (basePart, suffixPart) = AnimeThemesHelper.ParseSlug(theme.Slug ?? "");

        // Expand "OP" to "Opening" and "ED" to "Ending" while maintaining the count (e.g., "OP2" => "Opening 2")
        string typeName = basePart.StartsWith("OP", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending";
        string baseDisplay = $"{typeName} {basePart[2..]}".Trim();

        // Combine with the formatted tag from the helper dictionary
        string slugTag = AnimeThemesHelper.FormatSlugTag(suffixPart);
        string slugDisplay = (baseDisplay + slugTag).Trim();

        string songTitle = themeDetail.Animetheme.Song?.Title ?? string.Empty;
        var artists = themeDetail.Animetheme.Song?.Artists;

        // Join multiple artists with a seicolon, or use the single artist name, or fallback to empty string
        string artist = (artists?.Count > 1) ? string.Join("; ", artists.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name!)) : artists?.FirstOrDefault()?.Name ?? string.Empty;

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

    private static string ResolvePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private string? TryLinkIntoVfs(IVideoFile location, int seriesId, string source)
    {
        string? importRoot = VfsShared.ResolveImportRootPath(location);
        if (string.IsNullOrWhiteSpace(importRoot))
            return null;

        // seriesId should already be the primary for any override group, but do an extra lookup just in case caller passed original id.
        int primaryId = OverrideHelper.GetPrimary(seriesId, _metadataService);

        string seriesFolder = Path.Combine(importRoot, VfsShared.ResolveRootFolderName(), primaryId.ToString());
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

    private static void CleanupTempFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            try
            {
                File.Delete(path);
            }
            catch
            { // ignore cleanup errors
            }
    }
}
