using NLog;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers;

namespace ShokoRelay.Services;

/// <summary>
/// Helper for triggering server-side import and housekeeping actions using Shoko's internal service abstractions.
/// </summary>
public class ShokoImportService(IVideoService videoService, IVideoReleaseService releaseService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
    private readonly IVideoReleaseService _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));

    /// <summary>
    /// Trigger import scans for every managed folder marked as a "Source".
    /// </summary>
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
        }

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
    /// Scan for video file entries whose physical file has disappeared and optionally remove those records from the database and release info (MyList).
    /// </summary>
    /// <param name="dryRun">When <c>true</c>, list missing files without deleting them.</param>
    /// <returns>A read-only list of paths for files that were identified as missing.</returns>
    public async Task<IReadOnlyList<string>> RemoveMissingFilesAsync(bool dryRun = false)
    {
        const string taskName = "shoko-remove-missing";
        if (!dryRun)
            TaskHelper.StartTask(taskName);

        try
        {
            var all = _videoService.GetAllVideoFiles() ?? [];
            var missing = all.Where(f => !File.Exists(f.Path)).Select(f => f.Path).ToHashSet();

            if (!dryRun && missing.Count > 0)
            {
                Logger.Info("Shoko Housekeeping: Removing {0} missing files from database...", missing.Count);
                var toDelete = all.Where(f => missing.Contains(f.Path)).ToList();

                // Remove the file records from Shoko
                await _videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);

                // Purge unused releases and remove from AniDB MyList
                await _releaseService.PurgeUnusedReleases(providerNames: null, removeFromMylist: true).ConfigureAwait(false);

                Logger.Info("Shoko Housekeeping: Database and MyList cleanup complete.");
            }
            return [.. missing];
        }
        finally
        {
            if (!dryRun)
                TaskHelper.FinishTask(taskName);
        }
    }
}
