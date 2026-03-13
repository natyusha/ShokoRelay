using NLog;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Services;

namespace ShokoRelay.Services;

/// <summary>
/// Helper for triggering server-side import and housekeeping actions using Shoko's internal service abstractions. No API key or HTTP calls are required when running as a plugin.
/// </summary>
public class ShokoImportService(IVideoService videoService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));

    /// <summary>
    /// Trigger import scans for every managed folder marked as a "Source". Returns the names of folders that were scheduled for scanning, which is useful for UI feedback.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of folder names that were scheduled for scanning.</returns>
    public async Task<IReadOnlyList<string>> TriggerImportAsync()
    {
        List<string> folders = [];
        try
        {
            var mf = _videoService.GetAllManagedFolders();
            if (mf != null)
            {
                folders = [.. mf.Where(f => f.DropFolderType.HasFlag(DropFolderType.Source)).Select(f => f.Name ?? f.Path ?? string.Empty).Where(s => !string.IsNullOrEmpty(s))];
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "TriggerImportAsync: failed to query managed folders");
            // fall through with empty list; no import will be scheduled but caller will see no folders
        }

        // schedule the scan; the work executes asynchronously inside Shoko
        try
        {
            await _videoService.ScheduleScanForManagedFolders(onlyDropSources: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "TriggerImportAsync: failed to schedule folder scan");
        }

        return folders;
    }

    /// <summary>
    /// Scan for video file entries whose physical file has disappeared and optionally remove those records from the database.
    /// The <paramref name="dryRun"/> flag controls whether deletion occurs; in either case the list of missing paths is returned. Database deletions never touch disk files.
    /// </summary>
    /// <param name="removeFromMyList">Whether to also remove the entry from AniDB MyList.</param>
    /// <param name="dryRun">When <c>true</c>, list missing files without deleting them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of paths for files that were identified as missing.</returns>
    public async Task<IReadOnlyList<string>> RemoveMissingFilesAsync(bool dryRun = false)
    {
        var all = _videoService.GetAllVideoFiles() ?? [];
        var missing = all.Where(f => !File.Exists(f.Path)).Select(f => f.Path).ToHashSet();
        if (!dryRun && missing.Count > 0)
        {
            var toDelete = all.Where(f => missing.Contains(f.Path)).ToList();
            await _videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);
        }
        return [.. missing];
    }
}
