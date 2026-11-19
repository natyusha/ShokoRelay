using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Services;

/// <summary>Service responsible for triggering server-side import and housekeeping actions.</summary>
/// <param name="videoService">Shoko video service.</param>
/// <param name="releaseService">Shoko video release service.</param>
/// <param name="logger">Logger instance.</param>
public class ShokoImportService(IVideoService videoService, IVideoReleaseService releaseService, ILogger<ShokoImportService> logger)
{
    #region Import Logic

    /// <summary>Trigger import scans for every managed folder to find new or unrecognized files.</summary>
    /// <returns>A read-only list of folder names that were scheduled for scanning.</returns>
    public async Task<IReadOnlyList<string>> TriggerImportAsync()
    {
        List<string> folders = [];
        try
        {
            if (videoService.GetAllManagedFolders() is { } managedFolders)
                folders = [.. managedFolders.Select(folder => folder.Name ?? folder.Path ?? string.Empty).Where(name => !string.IsNullOrEmpty(name))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ShokoImportService: failed to query managed folders");
        }

        try
        {
            await videoService.ScheduleScanForManagedFolders(onlyDropSources: false, forceScan: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ShokoImportService: failed to schedule folder scan");
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

        logger.LogInformation("ShokoImportService: Starting purge missing files task (Mode: {Mode})", dryRun ? "Dry Run" : "Live");

        try
        {
            var all = videoService.GetAllVideoFiles() ?? [];
            var ignoredNames = VfsShared.GetIgnoredFolderNames(Settings);
            var managedFolderStatus = new Dictionary<int, bool>();

            bool IsFolderOnline(IManagedFolder? folder)
            {
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                    return false;
                if (!managedFolderStatus.TryGetValue(folder.ID, out bool online))
                {
                    online = folder.AvailableFreeSpace != -1 && Directory.Exists(folder.Path);
                    if (!online)
                        logger.LogWarning("ShokoImportService: Managed folder '{FolderName}' [{FolderId}] is offline/unmounted -> Skipping missing file checks for its contents.", folder.Name, folder.ID);
                    managedFolderStatus[folder.ID] = online;
                }
                return online;
            }

            // A file is considered "missing" if its managed folder is online and it doesn't exist on disk OR if its path is blocked by Relay ignore rules.
            var toDelete = all.Where(videoFile =>
                    IsFolderOnline(videoFile.ManagedFolder)
                    && (!File.Exists(videoFile.Path) || VfsShared.IsPathIgnored(videoFile.Path, videoService, Settings, ignoredNames, relativeTo: videoFile.ManagedFolder.Path))
                )
                .ToList();

            if (!dryRun && toDelete.Count > 0)
            {
                logger.LogInformation("ShokoImportService: Removing {Count} missing files from database...", toDelete.Count);

                // Remove the file records from Shoko
                await videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);

                // Purge unused releases from DB and remove from AniDB MyList
                await releaseService.PurgeUnusedReleases(providerNames: null).ConfigureAwait(false);

                logger.LogInformation("ShokoImportService: Database and MyList cleanup complete");
            }

            return [.. toDelete.Select(videoFile => videoFile.Path)];
        }
        finally
        {
            if (!dryRun)
                TaskHelper.FinishTask(TaskName);
        }
    }

    #endregion
}
