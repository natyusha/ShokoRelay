using NLog;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.Services;

/// <summary>Helper for triggering server-side import and housekeeping actions using Shoko's internal service abstractions.</summary>
public class ShokoImportService(IVideoService videoService, IVideoReleaseService releaseService)
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
    private readonly IVideoReleaseService _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));

    #endregion

    #region Import Logic

    /// <summary>Trigger import scans for every managed folder to find new or unrecognized files.</summary>
    /// <returns>A read-only list of folder names that were scheduled for scanning.</returns>
    public async Task<IReadOnlyList<string>> TriggerImportAsync()
    {
        List<string> folders = [];
        try
        {
            var mf = _videoService.GetAllManagedFolders();
            if (mf != null)
                folders = [.. mf.Select(f => f.Name ?? f.Path ?? string.Empty).Where(s => !string.IsNullOrEmpty(s))];
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "ShokoImportService: failed to query managed folders");
        }

        try
        {
            await _videoService.ScheduleScanForManagedFolders(onlyDropSources: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "ShokoImportService: failed to schedule folder scan");
        }

        return folders;
    }

    #endregion

    #region Housekeeping Logic

    /// <summary>Scan for video file entries whose physical file has disappeared or is now in an ignored location, and optionally remove those records.</summary>
    /// <param name="dryRun">When <c>true</c>, list missing files without deleting them.</param>
    /// <returns>A read-only list of paths for files that were identified as missing or ignored.</returns>
    public async Task<IReadOnlyList<string>> RemoveMissingFilesAsync(bool dryRun = false)
    {
        const string TaskName = ShokoRelayConstants.TaskShokoRemoveMissing;
        if (!dryRun)
            TaskHelper.StartTask(TaskName);

        s_logger.Info("ShokoImportService: Starting remove missing files task (Mode: {0})", dryRun ? "Dry Run" : "Live");

        try
        {
            var all = _videoService.GetAllVideoFiles() ?? [];
            // A file is considered "missing" if it doesn't exist on disk OR if its path is now blocked by Relay ignore rules.
            var missing = all.Where(f => !File.Exists(f.Path) || VfsShared.IsPathIgnored(f.Path)).Select(f => f.Path).ToHashSet();

            if (!dryRun && missing.Count > 0)
            {
                s_logger.Info("ShokoImportService: Removing {0} missing files from database...", missing.Count);
                var toDelete = all.Where(f => missing.Contains(f.Path)).ToList();

                // Remove the file records from Shoko
                await _videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);

                // Purge unused releases from DB and remove from AniDB MyList
                await _releaseService.PurgeUnusedReleases(providerNames: null, removeFromMylist: true).ConfigureAwait(false);

                s_logger.Info("ShokoImportService: Database and MyList cleanup complete");
            }
            return [.. missing];
        }
        finally
        {
            if (!dryRun)
                TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion
}
