using System.Net;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.AnimeThemes;

#region Data Models

/// <summary>Query parameters for WebM download requests.</summary>
/// <param name="Name">Optional filter for a specific anime name.</param>
/// <param name="Year">Optional filter for a specific broadcast year.</param>
/// <param name="Season">Optional filter for a specific broadcast season.</param>
/// <param name="Recent">Optional count of most recently added videos on AnimeThemes to download (1-100).</param>
/// <param name="Force">Whether to overwrite existing .webm files.</param>
/// <param name="Destination">Optional absolute path to a specific managed folder to download into.</param>
public record AnimeThemesWebmQuery(string? Name, int? Year, string? Season, int? Recent = null, bool Force = false, string? Destination = null);

/// <summary>Aggregated results of a WebM download operation.</summary>
/// <param name="Downloaded">Count of successful downloads.</param>
/// <param name="Skipped">Count of skipped files.</param>
/// <param name="Errors">Count of encountered errors.</param>
/// <param name="Downloads">List of filenames successfully downloaded.</param>
/// <param name="Messages">List of operation messages.</param>
public record WebmDownloadResult(int Downloaded, int Skipped, int Errors, List<string> Downloads, List<string> Messages);

#endregion

/// <summary>Provides functionality for bulk downloading and organizing AnimeThemes WebM files.</summary>
/// <param name="apiClient">AnimeThemes API client.</param>
/// <param name="httpClient">HTTP client.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="mappingService">AnimeThemes mapping service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class AnimeThemesWebmDownloader(
    AnimeThemesApi apiClient,
    HttpClient httpClient,
    IVideoService videoService,
    IFileSystem fileSystem,
    AnimeThemesMapping mappingService,
    ConfigProvider configProvider,
    ILogger<AnimeThemesWebmDownloader> logger
)
{
    #region Setup

    private const int NameFilterMaxDownloads = 10;
    private const int NameFilterMaxPages = 5;
    private const int RecentPageSize = 25;

    private readonly AnimeThemesApi _apiClient = apiClient;

    /// <summary>Signals an unrecoverable rate-limit failure that should abort the download loop.</summary>
    /// <param name="message">The failure reason.</param>
    private sealed class RateLimitAbortException(string message) : InvalidOperationException(message);

    #endregion

    #region Download Logic

    /// <summary>Downloads AnimeThemes WebM files based on specific filters or recent additions and organizes them by Year/Season.</summary>
    /// <param name="query">The search filters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A download result summary.</returns>
    public async Task<WebmDownloadResult> DownloadAsync(AnimeThemesWebmQuery query, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var downloads = new List<string>();
        var downloadedItems = new List<(string RelativePath, string FileName)>();
        var (downloaded, skipped, errors) = (0, 0, 0);

        string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
        var managedFolders = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true);
        string? targetRoot = null;
        if (!string.IsNullOrWhiteSpace(query.Destination))
        {
            string normalizedDestination = Path.GetFullPath(VfsShared.NormalizeSeparators(query.Destination)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            targetRoot = managedFolders.FirstOrDefault(folderPath =>
                string.Equals(Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedDestination, VfsShared.PathComparison)
            );
            if (targetRoot == null)
            {
                messages.Add($"Destination '{query.Destination}' is not a VFS-enabled Shoko managed folder.");
                return new WebmDownloadResult(0, 0, 1, downloads, messages);
            }
        }

        targetRoot ??= managedFolders.FirstOrDefault(folderPath => fileSystem.DirectoryExists(Path.Combine(folderPath!, themeRootName))) ?? managedFolders.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            messages.Add("No suitable destination managed folder found.");
            return new WebmDownloadResult(0, 0, 1, downloads, messages);
        }

        string baseThemePath = Path.Combine(targetRoot, themeRootName);
        logger.LogInformation("AnimeThemes WebM: Starting download operation to -> {Path}", baseThemePath);

        // Scan targetRoot first, then other VFS-enabled roots so files relocated via Move mode are still treated as existing
        var existingFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanRoots = managedFolders.OrderByDescending(folderPath => string.Equals(folderPath, targetRoot, VfsShared.PathComparison));
        var webmListingOptions = new FileSystemListingOptions { Recursive = true, Extensions = [".webm"] };
        foreach (var root in scanRoots)
        {
            string themeDir = Path.Combine(root, themeRootName);
            if (!fileSystem.DirectoryExists(themeDir))
                continue;
            try
            {
                foreach (var file in fileSystem.GetFilePaths(themeDir, webmListingOptions, cancellationToken))
                    existingFiles.TryAdd(Path.GetFileName(file), "/" + Path.GetRelativePath(themeDir, file).Replace('\\', '/'));
            }
            catch { }
        }

        // Cache files that already have a valid AniDB ID mapping to avoid redundant processing
        var mappedFilesWithAnidb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
        await VfsShared.VfsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(mapPath))
                try
                {
                    var parsed = AnimeThemesHelper.ParseMappingContentWithComments(await VfsShared.ReadAllTextSharedAsync(mapPath, cancellationToken).ConfigureAwait(false)).Entries;
                    foreach (var entry in parsed.Where(entry => entry.AniDbId > 0))
                        mappedFilesWithAnidb.Add(Path.GetFileName(entry.FilePath));
                }
                catch { }
        }
        finally
        {
            VfsShared.VfsLock.Release();
        }

        async Task DownloadSingleVideoAsync(string? rawBasename, string? videoUrl, int? animeYear, string? animeSeason, object itemId)
        {
            if (string.IsNullOrWhiteSpace(videoUrl) || string.IsNullOrWhiteSpace(rawBasename))
                return;

            string safeBasename = Path.GetFileName(rawBasename.Trim());
            if (string.IsNullOrWhiteSpace(safeBasename) || safeBasename is "." or "..")
                return;

            // Format the year into a decade string if before 2000, otherwise use the exact year
            string yearFolder = !animeYear.HasValue ? "Unknown" : (animeYear.Value >= 2000 ? animeYear.Value.ToString() : $"{animeYear.Value / 10 * 10 % 100:D2}s");
            string seasonFolder = string.IsNullOrWhiteSpace(animeSeason) ? "Unknown" : char.ToUpper(animeSeason[0]) + animeSeason[1..].ToLowerInvariant();
            string targetPath = Path.Combine(baseThemePath, yearFolder, seasonFolder, safeBasename);
            string relativePath = $"/{yearFolder}/{seasonFolder}/{safeBasename}";

            if (!query.Force && existingFiles.TryGetValue(safeBasename, out var existingRelativePath))
            {
                if (mappedFilesWithAnidb.Contains(safeBasename))
                {
                    logger.LogDebug("AnimeThemes WebM: Skipping already mapped theme -> {Basename}...", safeBasename);
                    skipped++;
                    return;
                }

                downloadedItems.Add((existingRelativePath, safeBasename));
                logger.LogInformation("AnimeThemes WebM: Found unmapped local theme -> {Basename}, checking for AniDB ID...", safeBasename);
                return;
            }

            fileSystem.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            logger.LogDebug("AnimeThemes WebM: Downloading -> {Basename}...", safeBasename);

            async Task TryDownloadVideoAsync()
            {
                using var videoResponse = await httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                videoResponse.EnsureSuccessStatusCode();

                await using var fileStream = fileSystem.OpenFile(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await videoResponse.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

                downloads.Add(safeBasename);
                downloadedItems.Add((relativePath, safeBasename));
                existingFiles[safeBasename] = relativePath; // Add to cache so it isn't downloaded again if it appears in another anime
                downloaded++;
            }

            void DeletePartialFile(string path)
            {
                if (fileSystem.FileExists(path))
                    try
                    {
                        fileSystem.DeleteFile(path);
                    }
                    catch { }
            }

            try
            {
                await TryDownloadVideoAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                DeletePartialFile(targetPath);
                logger.LogWarning("AnimeThemes WebM: Rate limited (503/429) on item -> {ItemId}. Waiting 90 seconds before retrying...", itemId);
                await Task.Delay(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

                try
                {
                    await TryDownloadVideoAsync().ConfigureAwait(false);
                }
                catch (Exception retryException)
                {
                    DeletePartialFile(targetPath);
                    logger.LogWarning(retryException, "AnimeThemes WebM: Failed to download item -> {ItemId}", itemId);
                    throw new RateLimitAbortException($"Task aborted. Rate limit retry failed for {itemId}: {retryException.Message}");
                }
            }
            catch (Exception exception) when (exception is not RateLimitAbortException)
            {
                DeletePartialFile(targetPath);
                errors++;
                messages.Add($"Failed to download {itemId}: {exception.Message}");
                logger.LogWarning(exception, "AnimeThemes WebM: Failed to download item -> {ItemId}", itemId);
            }
        }

        try
        {
            if (query.Recent is > 0)
            {
                int targetCount = Math.Clamp(query.Recent.Value, 1, 100);
                int remaining = targetCount;
                int page = 1;
                bool hasNext = true;
                string[] seasonOrder = ["Winter", "Spring", "Summer", "Fall"];

                while (hasNext && remaining > 0 && !cancellationToken.IsCancellationRequested)
                {
                    var response = await _apiClient.FetchRecentVideosAsync(Math.Min(remaining, RecentPageSize), page, cancellationToken).ConfigureAwait(false);
                    var videos = response?.Data;
                    if (videos == null || videos.Count == 0)
                        break;

                    foreach (var video in videos)
                    {
                        if (remaining <= 0 || cancellationToken.IsCancellationRequested)
                            break;
                        remaining--;

                        var primaryAnime = video
                            .Animethemeentries?.Nodes?.Select(node => node.Animetheme?.Anime)
                            .Where(anime => anime != null)
                            .OrderBy(anime => anime!.Year ?? 9999)
                            .ThenBy(anime => anime!.Season != null ? Array.IndexOf(seasonOrder, anime.Season) : 9999)
                            .FirstOrDefault();

                        await DownloadSingleVideoAsync(video.Basename, video.Link, primaryAnime?.Year, primaryAnime?.Season, $"video {video.Basename ?? video.Id.ToString()}").ConfigureAwait(false);
                    }

                    hasNext = response?.PaginatorInfo?.HasMorePages ?? false;
                    page++;
                }
            }
            else
            {
                bool nameLimitActive = !string.IsNullOrWhiteSpace(query.Name);
                int page = 1;
                bool hasNext = true;

                while (hasNext && !cancellationToken.IsCancellationRequested)
                {
                    if (nameLimitActive && (downloaded >= NameFilterMaxDownloads || page > NameFilterMaxPages))
                        break;

                    var response = await _apiClient.FetchAnimePageAsync(query.Year, query.Season, query.Name, page, cancellationToken).ConfigureAwait(false);
                    var animeList = response?.Data;
                    if (animeList == null || animeList.Count == 0)
                        break;

                    foreach (var anime in animeList)
                    {
                        if (nameLimitActive && downloaded >= NameFilterMaxDownloads)
                            break;

                        foreach (var theme in anime.Animethemes ?? [])
                        {
                            if ((nameLimitActive && downloaded >= NameFilterMaxDownloads) || cancellationToken.IsCancellationRequested)
                                break;

                            try
                            {
                                var details = await _apiClient.FetchAnimeThemeWithArtistsAsync(theme.Id, cancellationToken).ConfigureAwait(false);
                                var video = details?.Animethemeentries?.FirstOrDefault()?.Videos?.Nodes?.FirstOrDefault();
                                if (video != null)
                                    await DownloadSingleVideoAsync(video.Basename, video.Link, anime.Year, anime.Season, $"theme ID {theme.Id}").ConfigureAwait(false);
                            }
                            catch (Exception exception) when (exception is not RateLimitAbortException)
                            {
                                errors++;
                                messages.Add($"Failed to process theme ID {theme.Id}: {exception.Message}");
                                logger.LogWarning(exception, "AnimeThemes WebM: Failed to process theme ID -> {ThemeId}", theme.Id);
                            }
                        }
                    }

                    hasNext = response?.PaginatorInfo?.HasMorePages ?? false;
                    page++;
                }

                if (nameLimitActive && downloaded >= NameFilterMaxDownloads)
                    messages.Add($"Stopped after downloading {NameFilterMaxDownloads} themes (Name filter download limit reached).");
            }
        }
        catch (RateLimitAbortException exception)
        {
            errors++;
            messages.Add(exception.Message);
        }
        finally
        {
            if (await mappingService.AppendEntriesToMappingFileAsync(downloadedItems, CancellationToken.None).ConfigureAwait(false) is int mapped && mapped > 0)
                messages.Add($"Auto-mapped {mapped} new entries into {ShokoRelayConstants.FileAtMapping}");
        }

        logger.LogInformation("AnimeThemes WebM: Download operation finished -> {Downloaded} downloaded, {Skipped} skipped, {Errors} errors", downloaded, skipped, errors);
        return new WebmDownloadResult(downloaded, skipped, errors, downloads, messages);
    }

    #endregion
}
