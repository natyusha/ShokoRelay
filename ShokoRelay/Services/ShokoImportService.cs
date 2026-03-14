using NLog;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Services;

namespace ShokoRelay.Services;

/// <summary>Helper for triggering import actions using internal service abstractions.</summary>
public class ShokoImportService(IVideoService videoService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IVideoService _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));

    /// <summary>Trigger import scans for all managed folder sources.</summary>
    /// <returns>A list of folders scheduled for scanning.</returns>
    public async Task<IReadOnlyList<string>> TriggerImportAsync()
    {
        List<string> folders = [];
        try
        {
            var mf = _videoService.GetAllManagedFolders();
            if (mf != null)
                folders = [.. mf.Where(f => f.DropFolderType.HasFlag(DropFolderType.Source)).Select(f => f.Name ?? f.Path ?? string.Empty).Where(s => !string.IsNullOrEmpty(s))];
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

    /// <summary>Scans for and removes records for missing video files.</summary>
    /// <param name="dryRun">If true, skip deletion.</param>
    /// <returns>A list of identified missing paths.</returns>
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
