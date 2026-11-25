using System.Collections.Concurrent;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Services;

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

/// <summary>Aggregated results of an MP3 audit operation.</summary>
/// <param name="Processed">Number of non-OP1 themes evaluated.</param>
/// <param name="UpgradesFound">Number of non-standard themes found on AnimeThemes.</param>
/// <param name="MissingSlugsFixed">Number of missing slugs repaired via local ID3 tag reading.</param>
/// <param name="Upgrades">List of non-standard theme notification strings.</param>
/// <param name="OverriddenOps">List of overridden opening themes (e.g. OP2, OP3).</param>
/// <param name="OverriddenEds">List of overridden ending themes (e.g. ED2, ED3).</param>
/// <param name="ErrorsList">List of specific error messages.</param>
public record ThemeMp3AuditResult(int Processed, int UpgradesFound, int MissingSlugsFixed, List<string> Upgrades, List<string> OverriddenOps, List<string> OverriddenEds, List<string> ErrorsList);

#endregion

/// <summary>Provides functionality for fetching, converting and previewing anime theme audio from the AnimeThemes API.</summary>
/// <param name="apiClient">AnimeThemes API client.</param>
/// <param name="httpClient">HTTP client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="logger">Logger instance.</param>
public class AnimeThemesMp3Generator(
    AnimeThemesApi apiClient,
    HttpClient httpClient,
    IMetadataService metadataService,
    IVideoService videoService,
    IFileSystem fileSystem,
    ConfigProvider configProvider,
    FfmpegService ffmpegService,
    PlexClient plexClient,
    ILogger<AnimeThemesMp3Generator> logger
)
{
    #region Setup & Cache

    /// <summary>HTTP client for interacting with the AnimeThemes API.</summary>
    private readonly AnimeThemesApi _apiClient = apiClient;

    /// <summary>In-memory cache mapping physical folder paths to their associated theme slugs.</summary>
    private ConcurrentDictionary<string, string>? _themeMp3Cache;

    /// <summary>Lock object used to synchronize access to the MP3 cache.</summary>
    private readonly Lock _cacheLock = new();

    /// <summary>Gets the absolute file path to the persistent Theme.mp3 cache on disk.</summary>
    private string ThemeCacheFilePath => Path.Combine(configProvider.CacheDirectory, ShokoRelayConstants.FileAtMp3Cache);

    #endregion

    #region Cache Management

    /// <summary>Returns the cached dictionary of folders and slugs containing Theme.mp3 files.</summary>
    /// <returns>A read-only dictionary of folder paths to slugs.</returns>
    public IReadOnlyDictionary<string, string> GetCachedThemeMp3s()
    {
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            return _themeMp3Cache ?? new(VfsShared.PathComparer);
        }
    }

    /// <summary>Forces a re-scan of all managed folder roots and rebuilds the cache.</summary>
    public void RefreshThemeMp3Cache()
    {
        lock (_cacheLock)
            RefreshThemeMp3CacheInternal();
    }

    /// <summary>Adds or updates a folder path in the Theme.mp3 cache.</summary>
    /// <param name="folderPath">Absolute path of the folder to add.</param>
    /// <param name="slug">The theme slug (e.g. OP1).</param>
    public void AddToThemeMp3Cache(string folderPath, string slug = "")
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            _themeMp3Cache ??= new(VfsShared.PathComparer);
            _themeMp3Cache[folderPath] = $"{slug}|"; // clear any upgrade on new generation!
            SaveCacheToFile();
        }
    }

    /// <summary>Adds or updates multiple folder paths in the Theme.mp3 cache and persists once.</summary>
    /// <param name="entries">Collection of folder paths and standardized slugs to store.</param>
    public void AddBatchToThemeMp3Cache(IEnumerable<(string Folder, string Slug)> entries)
    {
        var list = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Folder)).ToList();
        if (list.Count == 0)
            return;

        lock (_cacheLock)
        {
            if (_themeMp3Cache == null)
                LoadCacheFromFile();
            _themeMp3Cache ??= new(VfsShared.PathComparer);
            foreach (var (folderPath, slug) in list)
                _themeMp3Cache[folderPath] = $"{slug}|";
            SaveCacheToFile();
        }
    }

    /// <summary>Updates a folder entry in the Theme.mp3 cache under the cache lock.</summary>
    /// <param name="folderPath">Absolute path of the folder to update.</param>
    /// <param name="value">The serialized slug and upgrade metadata value.</param>
    private void SetCacheEntry(string folderPath, string value)
    {
        lock (_cacheLock)
            _themeMp3Cache?[folderPath] = value;
    }

    /// <summary>Forces a re-scan of all managed folder roots and rebuilds the dictionary cache.</summary>
    private void RefreshThemeMp3CacheInternal()
    {
        logger.LogInformation("AnimeThemes MP3: Building Theme.mp3 cache -> scanning all managed folders...");
        var excluded = VfsShared.GetIgnoredFolderNames(Settings);
        try
        {
            var roots = VfsShared
                .GetManagedFolderPaths(videoService, requireExists: true, fileSystem: fileSystem)
                .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToList();

            var newCache = new ConcurrentDictionary<string, string>(VfsShared.PathComparer);

            Parallel.ForEach(roots, root =>
            {
                var listingOptions = new FileSystemListingOptions
                {
                    Recursive = true,
                    Extensions = [".mp3"],
                    Filter = entry =>
                        (entry.IsDirectory || entry.Name.Equals("Theme.mp3", StringComparison.OrdinalIgnoreCase))
                        && !VfsShared.IsPathIgnored(entry.Path, videoService, Settings, excluded, relativeTo: root, isDirectory: entry.IsDirectory, fileSystem: fileSystem),
                };

                foreach (var file in fileSystem.GetFilePaths(root!, listingOptions))
                    newCache.TryAdd(Path.GetDirectoryName(file)!, "|");
            });

            _themeMp3Cache = newCache;
            SaveCacheToFile();
            logger.LogInformation("AnimeThemes MP3: Theme.mp3 cache refreshed -> {FolderCount} folders found across {RootCount} roots", _themeMp3Cache.Count, roots.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AnimeThemes MP3: RefreshThemeMp3Cache -> Repositories not ready");
            _themeMp3Cache ??= new(VfsShared.PathComparer);
        }
    }

    /// <summary>Loads the Theme.mp3 folder paths from the local cache file, constructing a dictionary.</summary>
    private void LoadCacheFromFile()
    {
        if (File.Exists(ThemeCacheFilePath))
            try
            {
                var dict = new ConcurrentDictionary<string, string>(
                    File.ReadLines(ThemeCacheFilePath)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => line.Split('|', 3))
                        .Where(parts => parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                        .ToDictionary(parts => parts[0], parts => $"{(parts.Length > 1 ? parts[1] : "")}|{(parts.Length > 2 ? parts[2] : "")}", VfsShared.PathComparer)
                );
                if (!dict.IsEmpty)
                {
                    _themeMp3Cache = dict;
                    return;
                }
            }
            catch
            {
                logger.LogWarning("AnimeThemes MP3: Failed to read {FileName} -> Attempting full scan", ShokoRelayConstants.FileAtMp3Cache);
            }
        RefreshThemeMp3CacheInternal();
    }

    /// <summary>Save the current cached Theme.mp3 dictionary to the local cache file.</summary>
    private void SaveCacheToFile()
    {
        try
        {
            if (_themeMp3Cache == null)
                return;
            VfsShared.WriteLinesAtomic(ThemeCacheFilePath, _themeMp3Cache.Select(entry => string.IsNullOrEmpty(entry.Value) ? entry.Key : $"{entry.Key}|{entry.Value}"));
        }
        catch { }
    }

    #endregion

    #region MP3 Generation

    /// <summary>Processes a folder (and optionally subfolders) to generate MP3s for anime themes.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A batch result object.</returns>
    public async Task<ThemeMp3BatchResult> ProcessBatchAsync(AnimeThemesMp3Query query, CancellationToken cancellationToken)
    {
        string root = query.Path ?? "";
        if (!fileSystem.DirectoryExists(root))
        {
            logger.LogWarning("AnimeThemes MP3: Batch root not found -> {Root}", root);
            return new ThemeMp3BatchResult(root, [new(root, "error", "Batch root not found.")], 0, 0, 1);
        }

        if (VfsHelper.IsInVfsRoot(root, out string vfsRoot))
        {
            string message = $"Cannot execute batch generation inside the VFS directory '{vfsRoot}'. Target your physical managed folder instead.";
            logger.LogWarning("AnimeThemes MP3: {Message}", message);
            return new ThemeMp3BatchResult(root, [new(root, "error", message)], 0, 0, 1);
        }

        logger.LogInformation("AnimeThemes MP3: Starting batch generation for root -> {Root}", root);
        var (results, processed, skipped, errors) = (new List<ThemeMp3OperationResult>(), 0, 0, 0);

        // Scan recursively for directories that hold videos directly (or via immediate Season/Specials subfolders), pruning ignored/VFS paths before descending
        string? managedRoot = VfsShared
            .GetManagedFolderPaths(videoService)
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .OrderByDescending(path => path.Length)
            .FirstOrDefault(path => string.Equals(root, path, VfsShared.PathComparison) || VfsShared.IsPathUnderDirectory(root, path));
        string ignoreBaseRoot = managedRoot ?? root;

        var ignoredNames = VfsShared.GetIgnoredFolderNames(Settings);
        var candidateDirs = new List<string>();
        if (!VfsShared.IsPathIgnored(root, videoService, Settings, ignoredNames, relativeTo: ignoreBaseRoot, isDirectory: true, fileSystem: fileSystem))
        {
            candidateDirs.Add(root);
            candidateDirs.AddRange(
                fileSystem.GetDirectoryPaths(
                    root,
                    new FileSystemListingOptions
                    {
                        Recursive = true,
                        Filter = entry => !VfsShared.IsPathIgnored(entry.Path, videoService, Settings, ignoredNames, relativeTo: ignoreBaseRoot, isDirectory: true, fileSystem: fileSystem),
                    },
                    cancellationToken
                )
            );
        }
        var candidateSet = candidateDirs.ToHashSet(VfsShared.PathComparer);

        var folders = candidateDirs
            .Where(dir =>
            {
                if (VfsHelper.IsSeasonSubfolder(Path.GetFileName(dir)) && Path.GetDirectoryName(dir) is { Length: > 0 } parentDir && candidateSet.Contains(parentDir))
                    return false;

                bool IsEligibleVideo(string file) =>
                    videoService.IsAllowedVideoExtension(file) && !VfsShared.IsPathIgnored(file, videoService, Settings, ignoredNames, relativeTo: ignoreBaseRoot, fileSystem: fileSystem);

                return fileSystem.GetFilePaths(dir, cancellationToken: cancellationToken).Any(IsEligibleVideo)
                    || fileSystem
                        .GetDirectoryPaths(dir, cancellationToken: cancellationToken)
                        .Where(subDir =>
                            VfsHelper.IsSeasonSubfolder(Path.GetFileName(subDir))
                            && !VfsShared.IsPathIgnored(subDir, videoService, Settings, ignoredNames, relativeTo: ignoreBaseRoot, isDirectory: true, fileSystem: fileSystem)
                        )
                        .SelectMany(subDir => fileSystem.GetFilePaths(subDir, cancellationToken: cancellationToken))
                        .Any(IsEligibleVideo);
            })
            .ToList();

        var processedSeries = new ConcurrentDictionary<int, byte>();
        var newlyGenerated = new ConcurrentBag<(string Folder, string Slug)>();
        bool wasCancelled = false;

        try
        {
            await Parallel.ForEachAsync(folders, DefaultParallelOptions(cancellationToken), async (folder, token) =>
            {
                var processResult = await ProcessSingleAsync(query with { Path = folder, Batch = true }, processedSeries, token).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(processResult);
                    if (processResult.Status == "ok")
                    {
                        processed++;
                        if (!string.IsNullOrWhiteSpace(processResult.Folder))
                            newlyGenerated.Add((processResult.Folder, AnimeThemesHelper.StandardizeSlug(processResult.Slug)));
                    }
                    else if (processResult.Status == "skipped")
                        skipped++;
                    else
                        errors++;
                }
            });
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            logger.LogInformation("AnimeThemes MP3: Batch generation cancelled -> flushing {Count} generated theme(s) to cache...", newlyGenerated.Count);
            lock (results)
                results.Add(new(root, "cancelled", "Batch generation was cancelled."));
        }
        finally
        {
            if (!newlyGenerated.IsEmpty)
                AddBatchToThemeMp3Cache(newlyGenerated);
        }

        logger.LogInformation("AnimeThemes MP3: Batch generation {Status} -> {Processed} processed, {Skipped} skipped, {Errors} errors", wasCancelled ? "cancelled" : "finished", processed, skipped, errors);
        return new ThemeMp3BatchResult(root, results, processed, skipped, errors);
    }

    /// <summary>Handles a single folder request, downloading and converting the selected theme to an MP3.</summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="batchProcessedSeries">Active batch-processed series tracker dictionary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An operation result object.</returns>
    public async Task<ThemeMp3OperationResult> ProcessSingleAsync(AnimeThemesMp3Query query, ConcurrentDictionary<int, byte>? batchProcessedSeries, CancellationToken cancellationToken)
    {
        string folder = query.Path ?? "";
        if (string.IsNullOrWhiteSpace(folder))
            return new("", "error", "Path is required.");

        logger.LogDebug("AnimeThemes MP3: Preparing context for folder -> {Folder}", folder);
        if (!fileSystem.DirectoryExists(folder))
            return new(folder, "error", "Folder not found.");

        if (VfsHelper.IsInVfsRoot(folder, out string vfsRoot))
            return new(folder, "error", $"Cannot generate Theme.mp3 inside the VFS directory '{vfsRoot}'. Target your physical managed folder instead.");

        string? videoPath = FindFirstVideoInFolder(folder, !query.Batch, cancellationToken);

        if (videoPath == null)
        {
            logger.LogDebug("AnimeThemes MP3: No recognized video files in folder -> {Folder}", folder);
            return new(folder, "error", "No video files found.");
        }

        var videoFile = videoService.GetVideoFileByAbsolutePath(videoPath);
        var series = videoFile?.Video?.Episodes?.FirstOrDefault()?.Series;
        if (series == null)
        {
            logger.LogWarning("AnimeThemes MP3: Series lookup failed for video -> {Video} in {Folder}", videoPath, folder);
            return new(folder, "error", videoFile == null ? "Video not recognized." : "Series lookup failed.");
        }

        logger.LogDebug("AnimeThemes MP3: Folder {Folder} maps to series -> {Title} [{LocalId}] (AniDB: {AnidbId})", folder, series.GetDisplayTitle(), series.LocalID, series.AnidbAnimeID);

        // Season Filter: Intentionally targets series that started airing in the current season (with a one-month early buffer) to avoid overly aggressive downloading
        // Only applied when Batch is true; ignored for individual folder requests
        if (query.Batch && query.Seasonal)
        {
            var now = DateTime.Now;
            int year = now.Year;
            var (start, end) = now.Month switch
            {
                >= 1 and <= 3 => (new DateTime(year - 1, 12, 1), new DateTime(year, 3, 31)),
                >= 4 and <= 6 => (new DateTime(year, 3, 1), new DateTime(year, 6, 30)),
                >= 7 and <= 9 => (new DateTime(year, 6, 1), new DateTime(year, 9, 30)),
                _ => (new DateTime(year, 9, 1), new DateTime(year, 12, 31)),
            };
            if (!series.AirDate.HasValue || series.AirDate.Value < start || series.AirDate.Value > end)
            {
                string skipMessage = "Series does not match the current season filter.";
                logger.LogDebug("AnimeThemes MP3: Skipped series -> {Title} [{LocalId}] ({Message})", series.GetDisplayTitle(), series.LocalID, skipMessage);
                return new(folder, "skipped", skipMessage);
            }
        }

        if (batchProcessedSeries != null && !batchProcessedSeries.TryAdd(series.LocalID, 0))
            return new(folder, "skipped", "Theme already processed for this series in another directory.");

        string themePath = Path.Combine(folder, "Theme.mp3");
        if (!query.Force && fileSystem.FileExists(themePath))
            return new(folder, "skipped", "Theme.mp3 already exists.");

        string? temp = null;
        string? tempMp3 = null;
        try
        {
            if (!query.Batch)
                logger.LogInformation("AnimeThemes MP3: Generating Theme.mp3 for series -> {Title} [{LocalId}] in {Folder}", series.GetDisplayTitle() ?? series.LocalID.ToString(), series.LocalID, folder);

            if (!string.IsNullOrWhiteSpace(query.Slug) && !AnimeThemesHelper.SlugRegex().IsMatch(query.Slug))
                throw new ArgumentException("Invalid slug format.");

            logger.LogDebug("AnimeThemes MP3: Fetching metadata for AniDB ID -> {AnidbId} (Slug: {Slug}, Offset: {Offset})", series.AnidbAnimeID, query.Slug ?? "Auto", query.Offset);
            var anime = await _apiClient.FetchAnimeThemesAsync(series.AnidbAnimeID, cancellationToken).ConfigureAwait(false);
            var entry = anime?.ElementAtOrDefault(query.Offset);

            string? audioUrl = null;
            var (slugRaw, slugDisplay, songTitle, artist, animeTitle, animeSlug) = ("", "", "", "", "", "");

            if (entry?.Animethemes != null && entry.Animethemes.Count > 0)
            {
                var validThemes = entry
                    .Animethemes.Where(theme =>
                    {
                        if (string.IsNullOrEmpty(query.Slug))
                            return theme.Slug != null && (AnimeThemesHelper.IsOp(theme.Slug) || AnimeThemesHelper.IsEd(theme.Slug));
                        var (baseSlug, _) = AnimeThemesHelper.ParseSlug(theme.Slug ?? "");
                        var (queryBase, _) = AnimeThemesHelper.ParseSlug(query.Slug);
                        return string.Equals(baseSlug, queryBase, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(baseSlug, queryBase + "1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(baseSlug + "1", queryBase, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                int index = 0;
                if (string.IsNullOrEmpty(query.Slug))
                {
                    var (bestTheme, bestIndex, _, _) = validThemes
                        .Select(
                            (theme, themeIndex) =>
                            {
                                string slug = theme.Slug ?? "";
                                var (baseSlug, _) = AnimeThemesHelper.ParseSlug(slug);
                                int numberValue = AnimeThemesHelper.ExtractSlugNumber(baseSlug);

                                int group = AnimeThemesHelper.IsOp(baseSlug) ? 1 : (AnimeThemesHelper.IsEd(baseSlug) ? 2 : 3);
                                return (Theme: theme, OriginalIndex: themeIndex, Group: group, Number: numberValue);
                            }
                        )
                        .OrderBy(item => item.Group)
                        .ThenBy(item => item.Number)
                        .ThenBy(item => item.OriginalIndex)
                        .FirstOrDefault();

                    if (bestTheme != null)
                        index = bestIndex;
                }

                if (validThemes.Count > index)
                {
                    var themeDetail = await _apiClient.FetchAnimeThemeWithArtistsAsync(validThemes[index].Id, cancellationToken).ConfigureAwait(false);
                    audioUrl = themeDetail?.Animethemeentries?.FirstOrDefault()?.Videos?.Nodes?.FirstOrDefault()?.Audio?.Link;

                    if (!string.IsNullOrEmpty(audioUrl))
                    {
                        var (basePart, suffixPart) = AnimeThemesHelper.ParseSlug(themeDetail!.Slug ?? "");
                        slugRaw = themeDetail.Slug ?? "";
                        slugDisplay = $"{(basePart.StartsWith("OP", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending")} {basePart[2..]}".Trim() + AnimeThemesHelper.FormatSlugTag(suffixPart);
                        songTitle = themeDetail.Song?.Title?.Romaji ?? "";

                        var artists = themeDetail.Song?.Performances?.Select(performance => performance.Artist).ToList();
                        artist =
                            artists?.Count > 1 ? string.Join("; ", artists.Select(artistItem => artistItem?.Name?.Main).Where(name => !string.IsNullOrEmpty(name))) : artists?.FirstOrDefault()?.Name?.Main ?? "";

                        animeTitle = entry.Title?.Romaji ?? "";
                        animeSlug = entry.Slug ?? "";

                        logger.LogDebug("AnimeThemes MP3: Selected theme for series -> {Title} [{LocalId}] ({Slug} - {SongTitle})", series.GetDisplayTitle(), series.LocalID, slugRaw, songTitle);
                    }
                }
            }

            if (string.IsNullOrEmpty(audioUrl))
            {
                string skipMessage = string.IsNullOrWhiteSpace(query.Slug) ? "Entry not found." : $"No entry for slug '{query.Slug}'.";
                if (!query.Batch)
                    logger.LogInformation("AnimeThemes MP3: Skipped series -> {Title} [{LocalId}] ({Message})", series.GetDisplayTitle(), series.LocalID, skipMessage);

                return new(folder, "skipped", skipMessage);
            }

            // Download an audio file to a temporary location on disk
            logger.LogDebug("AnimeThemes MP3: Downloading audio from {Url}", audioUrl);
            using (var response = await httpClient.GetAsync(audioUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string audioExtension = Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri) ? Path.GetExtension(audioUri.AbsolutePath) : Path.GetExtension(audioUrl);
                temp = Path.Combine(Path.GetTempPath(), $"at-{Guid.NewGuid():N}{audioExtension}");
                await using var streamIn = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var streamOut = File.Create(temp);
                await streamIn.CopyToAsync(streamOut, cancellationToken).ConfigureAwait(false);
            }

            var duration = await ffmpegService.ProbeDurationAsync(temp, cancellationToken).ConfigureAwait(false);
            string title = duration.TotalSeconds < 100 && !string.IsNullOrEmpty(songTitle) ? songTitle + " (TV Size)" : songTitle;

            logger.LogDebug("AnimeThemes MP3: Converting audio for -> {Title} [{LocalId}] ({Slug})", series.GetDisplayTitle(), series.LocalID, slugDisplay);
            tempMp3 = Path.Combine(Path.GetTempPath(), $"at-{Guid.NewGuid():N}.mp3");
            await ffmpegService.ConvertToMp3FileAsync(temp, tempMp3, title, slugDisplay, artist, animeTitle, cancellationToken).ConfigureAwait(false);
            fileSystem.MoveFile(tempMp3, themePath, overwrite: true);

            // Create a relative symbolic link for the Theme.mp3 in the Shoko VFS directories
            string? vfsLink = null;
            foreach (var vfsPath in VfsShared.ResolveSeriesVfsPaths(series, metadataService))
            {
                if (!Settings.Advanced.DisableVfsGeneration)
                    fileSystem.CreateDirectory(vfsPath);
                string dest = Path.Combine(vfsPath, "Theme.mp3");

                logger.LogDebug("AnimeThemes MP3: Linking Theme.mp3 to VFS -> {Destination}", dest);
                if (VfsShared.TryCreateLink(themePath, dest, logger, fileSystem))
                    vfsLink = dest; // Track the last successful link to trigger the Plex refresh
            }

            // After a successful VFS link is created, trigger a Plex metadata refresh to ensure the new Theme.mp3 is picked up by the server.
            if (!string.IsNullOrEmpty(vfsLink) && plexClient.IsEnabled && !Settings.Advanced.DisableVfsGeneration)
                VfsShared.RunDetached(async () =>
                {
                    try
                    {
                        int bufferSeconds = Settings.Advanced.PlexScanDelay;
                        if (bufferSeconds > 0)
                            await Task.Delay(TimeSpan.FromSeconds(bufferSeconds)).ConfigureAwait(false);

                        logger.LogDebug("AnimeThemes MP3: Refreshing Plex metadata for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                        await plexClient.RefreshSeriesMetadataAsync(series.LocalID, metadataService).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "AnimeThemes MP3: Failed to trigger Plex refresh for series -> {Title} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                    }
                });

            if (!query.Batch)
                AddToThemeMp3Cache(folder, AnimeThemesHelper.StandardizeSlug(slugRaw));

            logger.LogInformation("AnimeThemes MP3: Successfully generated Theme.mp3 -> {Title} [{LocalId}] ({Slug})", series.GetDisplayTitle(), series.LocalID, slugDisplay);
            return new(folder, "ok", null, themePath, vfsLink, animeTitle, animeSlug, series.LocalID, slugRaw, duration.TotalSeconds);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AnimeThemes MP3: Failed to process -> {Folder}", folder);
            return new(folder, "error", exception.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
                try
                {
                    logger.LogTrace("AnimeThemes MP3: Cleaning up temporary file -> {TempFile}", temp);
                    File.Delete(temp);
                }
                catch { }
            if (!string.IsNullOrEmpty(tempMp3) && File.Exists(tempMp3))
                try
                {
                    logger.LogTrace("AnimeThemes MP3: Cleaning up temporary MP3 file -> {TempFile}", tempMp3);
                    File.Delete(tempMp3);
                }
                catch { }
        }
    }

    /// <summary>Audits the cache for non-OP themes, querying the AnimeThemes API to identify available OP upgrades, and rectifying missing local slugs via ID3 tags.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An audit result summary.</returns>
    public async Task<ThemeMp3AuditResult> AuditAsync(CancellationToken cancellationToken)
    {
        var upgrades = new ConcurrentBag<string>();
        var overriddenOps = new ConcurrentBag<string>();
        var overriddenEds = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();
        var (processed, fixedCount) = (0, 0);
        int cacheUpdated = 0;

        var cache = GetCachedThemeMp3s();
        var videoFileLookup = new Lazy<Dictionary<string, IShokoSeries>>(() =>
        {
            var dict = new Dictionary<string, IShokoSeries>(VfsShared.PathComparer);
            foreach (var videoFile in videoService.GetAllVideoFiles() ?? [])
                if (!string.IsNullOrWhiteSpace(videoFile.Path) && videoFile.Video?.Episodes?.FirstOrDefault()?.Series is { } series)
                    dict.TryAdd(videoFile.Path, series);
            return dict;
        });

        logger.LogInformation("AnimeThemes MP3: Starting audit of {Count} cached themes...", cache.Count);

        await Parallel.ForEachAsync(cache, DefaultParallelOptions(cancellationToken), async (entry, token) =>
        {
            string folder = entry.Key;
            var parts = (entry.Value ?? "").Split('|', 2);
            string slug = parts[0];
            string upgrade = parts.Length > 1 ? parts[1] : "";

            string themePath = Path.Combine(folder, "Theme.mp3");
            if (!fileSystem.FileExists(themePath))
                return;

            if (string.IsNullOrEmpty(slug))
                try
                {
                    var tags = AnimeThemesHelper.ReadId3v2Tags(themePath, fileSystem);
                    if (tags.TryGetValue("Slug", out var rawSlug) && !string.IsNullOrEmpty(rawSlug))
                    {
                        slug = AnimeThemesHelper.StandardizeSlug(rawSlug);
                        SetCacheEntry(folder, $"{slug}|{upgrade}");
                        Interlocked.Increment(ref fixedCount);
                        Interlocked.Exchange(ref cacheUpdated, 1);
                    }
                }
                catch { }

            logger.LogDebug("AnimeThemes MP3: Auditing file -> {ThemePath} (Slug: {Slug})", themePath, string.IsNullOrEmpty(slug) ? "Unknown" : slug);

            if (!string.IsNullOrEmpty(slug))
            {
                bool isOp = AnimeThemesHelper.IsOp(slug);
                bool isOp1 = AnimeThemesHelper.IsOp1(slug);
                bool isEd = AnimeThemesHelper.IsEd(slug);
                bool isEd1 = AnimeThemesHelper.IsEd1(slug);
                int currentSlugNumber = AnimeThemesHelper.ExtractSlugNumber(slug);

                if (isOp && !isOp1)
                    overriddenOps.Add($"{folder} | {slug}");
                else if (isEd && !isEd1)
                    overriddenEds.Add($"{folder} | {slug}");

                if (!isOp1)
                {
                    Interlocked.Increment(ref processed);
                    try
                    {
                        string? videoPath = FindFirstVideoInFolder(folder, recursive: true, token);
                        if (videoPath == null || !videoFileLookup.Value.TryGetValue(videoPath, out var series))
                            return;

                        var anime = await _apiClient.FetchAnimeThemesAsync(series.AnidbAnimeID, token).ConfigureAwait(false);
                        var animethemes = anime?.FirstOrDefault()?.Animethemes ?? [];
                        var firstOpTheme = animethemes
                            .Where(theme => theme.Slug != null && AnimeThemesHelper.IsOp(theme.Slug))
                            .OrderBy(theme => AnimeThemesHelper.ExtractSlugNumber(theme.Slug!))
                            .FirstOrDefault();
                        var firstEdTheme = animethemes
                            .Where(theme => theme.Slug != null && AnimeThemesHelper.IsEd(theme.Slug))
                            .OrderBy(theme => AnimeThemesHelper.ExtractSlugNumber(theme.Slug!))
                            .FirstOrDefault();

                        int? firstOpNumber = firstOpTheme?.Slug != null ? AnimeThemesHelper.ExtractSlugNumber(firstOpTheme.Slug) : null;
                        int? firstEdNumber = firstEdTheme?.Slug != null ? AnimeThemesHelper.ExtractSlugNumber(firstEdTheme.Slug) : null;

                        bool hasOpUpgrade = firstOpTheme != null && (isEd || (isOp && firstOpNumber.HasValue && firstOpNumber.Value < currentSlugNumber));
                        bool hasEdUpgrade = !isOp && isEd && !isEd1 && firstOpTheme == null && firstEdTheme != null && firstEdNumber.HasValue && firstEdNumber.Value < currentSlugNumber;

                        if (hasOpUpgrade || hasEdUpgrade)
                        {
                            string newUpgrade = (hasOpUpgrade ? firstOpTheme!.Slug : firstEdTheme!.Slug)!;
                            if (newUpgrade != upgrade)
                            {
                                upgrade = newUpgrade;
                                SetCacheEntry(folder, $"{slug}|{upgrade}");
                                Interlocked.Exchange(ref cacheUpdated, 1);
                            }
                            upgrades.Add($"{series.GetDisplayTitle() ?? series.LocalID.ToString()} [{series.LocalID}] (Currently: {slug}, Available: {newUpgrade})");
                        }
                        else if (!string.IsNullOrEmpty(upgrade))
                        {
                            upgrade = "";
                            SetCacheEntry(folder, $"{slug}|");
                            Interlocked.Exchange(ref cacheUpdated, 1);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"Failed to audit {folder}: {exception.Message}");
                        logger.LogWarning(exception, "AnimeThemes MP3: Failed to audit -> {Folder}", folder);
                    }
                }
            }
        });

        if (cacheUpdated != 0)
            lock (_cacheLock)
                SaveCacheToFile();

        logger.LogInformation("AnimeThemes MP3: Audit complete -> {Processed} non-default themes checked, {Upgrades} upgrades found, {Fixes} missing slugs fixed", processed, upgrades.Count, fixedCount);
        return new ThemeMp3AuditResult(processed, upgrades.Count, fixedCount, [.. upgrades], [.. overriddenOps], [.. overriddenEds], [.. errors]);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Finds the first allowed, non-ignored video file inside the specified folder.</summary>
    /// <param name="folder">The folder path to search.</param>
    /// <param name="recursive">Whether to search subdirectories recursively.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved physical video path, or <c>null</c> if none was found.</returns>
    private string? FindFirstVideoInFolder(string folder, bool recursive, CancellationToken cancellationToken)
    {
        var listingOptions = new FileSystemListingOptions
        {
            Recursive = recursive,
            Filter = entry => !VfsShared.IsPathIgnored(entry.Path, videoService, Settings, relativeTo: folder, isDirectory: entry.IsDirectory, fileSystem: fileSystem),
        };
        return fileSystem
            .GetFilePaths(folder, listingOptions, cancellationToken)
            .FirstOrDefault(file => videoService.IsAllowedVideoExtension(file) && !VfsShared.IsPathIgnored(file, videoService, Settings, fileSystem: fileSystem));
    }

    #endregion
}
