using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

#region Data Models

/// <summary>Query parameters for WebM generation requests.</summary>
/// <param name="Name">Optional filter for a specific anime name.</param>
/// <param name="Year">Optional filter for a specific broadcast year.</param>
/// <param name="Season">Optional filter for a specific broadcast season.</param>
/// <param name="Force">Whether to overwrite existing .webm files.</param>
/// <param name="Destination">Optional absolute path to a specific managed folder to download into.</param>
public record AnimeThemesWebmQuery(string? Name, int? Year, string? Season, bool Force = false, string? Destination = null);

/// <summary>Aggregated results of a WebM download operation.</summary>
/// <param name="Downloaded">Count of successful downloads.</param>
/// <param name="Skipped">Count of skipped files.</param>
/// <param name="Errors">Count of encountered errors.</param>
/// <param name="Messages">List of operation messages.</param>
public record WebmDownloadResult(int Downloaded, int Skipped, int Errors, List<string> Messages);

#endregion

/// <summary>Provides functionality for bulk downloading and organizing AnimeThemes WebM files.</summary>
public class AnimeThemesWebmDownloader(HttpClient httpClient, IVideoService videoService)
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly AnimeThemesApi _api = new(httpClient);

    #endregion

    #region Download Logic

    /// <summary>Downloads AnimeThemes WebM files based on specific filters and organizes them by Year/Season.</summary>
    /// <param name="query">The search filters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A download result summary.</returns>
    public async Task<WebmDownloadResult> DownloadAsync(AnimeThemesWebmQuery query, CancellationToken ct)
    {
        var messages = new List<string>();
        int downloaded = 0,
            skipped = 0,
            errors = 0;

        string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
        var managedFolders = videoService.GetAllManagedFolders()?.Where(f => !VfsShared.IsSourceOnly(f)).Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        string? targetRoot = null;
        if (!string.IsNullOrWhiteSpace(query.Destination))
        {
            string normDest = VfsShared.NormalizeSeparators(query.Destination);
            targetRoot = managedFolders.FirstOrDefault(p => string.Equals(p, normDest, StringComparison.OrdinalIgnoreCase)) ?? (Directory.Exists(normDest) ? normDest : null);
        }

        targetRoot ??= managedFolders.FirstOrDefault(p => Directory.Exists(Path.Combine(p!, themeRootName))) ?? managedFolders.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            messages.Add("No suitable destination managed folder found.");
            return new WebmDownloadResult(0, 0, 1, messages);
        }

        string baseThemePath = Path.Combine(targetRoot, themeRootName);
        s_logger.Info("AnimeThemes WebM: Starting download operation to -> {0}", baseThemePath);

        // Build a cache of existing files to prevent downloading duplicates already organized in different folders
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(baseThemePath))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(baseThemePath, "*.webm", SearchOption.AllDirectories))
                    existingFiles.Add(Path.GetFileName(file));
            }
            catch { }
        }

        int page = 1;
        bool hasNext = true;

        while (hasNext && !ct.IsCancellationRequested)
        {
            var resp = await _api.FetchAnimePageAsync(query.Year, query.Season, query.Name, page, ct).ConfigureAwait(false);
            if (resp?.Anime == null || resp.Anime.Count == 0)
                break;

            foreach (var anime in resp.Anime)
            {
                string yearFolder = FormatYearFolder(anime.Year);
                string seasonFolder = FormatSeasonFolder(anime.Season);

                foreach (var theme in anime.Animethemes ?? [])
                {
                    try
                    {
                        var details = await _api.FetchAnimeThemeWithArtistsAsync(theme.Id, ct).ConfigureAwait(false);
                        var video = details?.Animetheme?.Animethemeentries?.FirstOrDefault()?.Videos?.FirstOrDefault();

                        if (video == null || string.IsNullOrWhiteSpace(video.Link) || string.IsNullOrWhiteSpace(video.Basename))
                            continue;

                        if (!query.Force && existingFiles.Contains(video.Basename))
                        {
                            s_logger.Info("AnimeThemes WebM: Skipping Downloaded Theme -> {0}...", video.Basename);
                            skipped++;
                            continue;
                        }

                        string targetPath = Path.Combine(baseThemePath, yearFolder, seasonFolder, video.Basename);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        s_logger.Info("AnimeThemes WebM: Downloading -> {0}...", video.Basename);

                        try
                        {
                            using var videoResp = await httpClient.GetAsync(video.Link, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            videoResp.EnsureSuccessStatusCode();

                            using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            await videoResp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

                            existingFiles.Add(video.Basename); // Add to cache so we don't download it again if it appears in another anime
                            downloaded++;
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                        {
                            s_logger.Warn("AnimeThemes WebM: Rate limited (503/429) on theme ID -> {0}. Waiting 90 seconds before retrying...", theme.Id);
                            await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);

                            try
                            {
                                using var retryResp = await httpClient.GetAsync(video.Link, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                                retryResp.EnsureSuccessStatusCode();

                                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                await retryResp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

                                existingFiles.Add(video.Basename);
                                downloaded++;
                            }
                            catch (Exception retryEx)
                            {
                                s_logger.Warn(retryEx, "AnimeThemes WebM: Failed to download theme ID -> {0}", theme.Id);
                                throw new InvalidOperationException($"Task aborted. Rate limit retry failed for theme ID {theme.Id}: {retryEx.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            messages.Add($"Failed to download theme ID {theme.Id}: {ex.Message}");
                            s_logger.Warn(ex, "AnimeThemes WebM: Failed to download theme ID -> {0}", theme.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        messages.Add($"Failed to process theme ID {theme.Id}: {ex.Message}");
                        s_logger.Warn(ex, "AnimeThemes WebM: Failed to process theme ID -> {0}", theme.Id);
                    }
                }
            }

            hasNext = !string.IsNullOrWhiteSpace(resp.Links?.Next);
            page++;
        }

        s_logger.Info("AnimeThemes WebM: Download operation finished -> {0} downloaded, {1} skipped, {2} errors", downloaded, skipped, errors);
        return new WebmDownloadResult(downloaded, skipped, errors, messages);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Formats the year into a decade string if before 2000, otherwise returns the exact year.</summary>
    private static string FormatYearFolder(int? year)
    {
        if (!year.HasValue)
            return "Unknown";
        if (year.Value >= 2000)
            return year.Value.ToString();
        int decade = year.Value / 10 * 10;
        return $"{decade % 100:D2}s";
    }

    /// <summary>Formats the season into title case.</summary>
    private static string FormatSeasonFolder(string? season)
    {
        return string.IsNullOrWhiteSpace(season) ? "Unknown" : char.ToUpper(season[0]) + season[1..].ToLowerInvariant();
    }

    #endregion
}
