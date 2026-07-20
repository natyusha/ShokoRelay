using Shoko.Abstractions.Video.Services;
using ShokoRelay.Vfs;

namespace ShokoRelay.Services;

#region Interface

/// <summary>Service responsible for triggering server-side import and housekeeping actions.</summary>
public interface IShokoImportService
{
    /// <summary>Trigger import scans for every managed folder to find new or unrecognized files.</summary>
    /// <returns>A read-only list of folder names that were scheduled for scanning.</returns>
    Task<IReadOnlyList<string>> TriggerImportAsync();

    /// <summary>Scan for video file entries whose physical file has disappeared or is now in an ignored location, and optionally remove those records.</summary>
    /// <param name="dryRun">When <c>true</c>, list missing files without deleting them.</param>
    /// <returns>A read-only list of paths for files that were identified as missing or ignored.</returns>
    Task<IReadOnlyList<string>> PurgeMissingFilesAsync(bool dryRun = false);
}

#endregion

/// <summary>Default implementation of <see cref="IShokoImportService"/>.</summary>
public class ShokoImportService(IVideoService videoService, IVideoReleaseService releaseService) : IShokoImportService
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion

    #region Import Logic

    /// <summary>Trigger import scans for every managed folder to find new or unrecognized files.</summary>
    /// <returns>A read-only list of folder names that were scheduled for scanning.</returns>
    public async Task<IReadOnlyList<string>> TriggerImportAsync()
    {
        List<string> folders = [];
        try
        {
            var mf = videoService.GetAllManagedFolders();
            if (mf != null)
                folders = [.. mf.Select(f => f.Name ?? f.Path ?? string.Empty).Where(s => !string.IsNullOrEmpty(s))];
        }
        catch (Exception ex)
        {
            s_logger.Warn(ex, "ShokoImportService: failed to query managed folders");
        }

        try
        {
            await videoService.ScheduleScanForManagedFolders(onlyDropSources: false, forceScan: true).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<string>> PurgeMissingFilesAsync(bool dryRun = false)
    {
        const string TaskName = ShokoRelayConstants.TaskShokoPurgeMissing;
        if (!dryRun)
            TaskHelper.StartTask(TaskName);

        s_logger.Info("ShokoImportService: Starting purge missing files task (Mode: {0})", dryRun ? "Dry Run" : "Live");

        try
        {
            var all = videoService.GetAllVideoFiles() ?? [];
            var ignoredNames = VfsShared.GetIgnoredFolderNames(Settings);

            // A file is considered "missing" if it doesn't exist on disk OR if its path is now blocked by Relay ignore rules.
            var toDelete = all.Where(f => !File.Exists(f.Path) || VfsShared.IsPathIgnored(f.Path, ignoredNames)).ToList();

            if (!dryRun && toDelete.Count > 0)
            {
                s_logger.Info("ShokoImportService: Removing {0} missing files from database...", toDelete.Count);

                // Remove the file records from Shoko
                await videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);

                // Purge unused releases from DB and remove from AniDB MyList
                await releaseService.PurgeUnusedReleases(providerNames: null).ConfigureAwait(false);

                s_logger.Info("ShokoImportService: Database and MyList cleanup complete");
            }

            return [.. toDelete.Select(f => f.Path)];
        }
        finally
        {
            if (!dryRun)
                TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion
}
