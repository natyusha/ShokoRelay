using NLog;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Services;

namespace ShokoRelay.Services
{
    /// <summary>
    /// Helper for triggering server-side import and housekeeping actions using
    /// Shoko's internal service abstractions.  No API key or HTTP calls are required
    /// when running as a plugin.
    /// </summary>
    public class ShokoImportService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IVideoService _videoService;

        public ShokoImportService(IVideoService videoService)
        {
            _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
        }

        /// <summary>
        /// Trigger import scans for every managed folder marked as a "Source".
        /// Returns the names of folders that were scheduled for scanning, which is
        /// useful for UI feedback.
        /// </summary>
        public async Task<IReadOnlyList<string>> TriggerImportAsync(CancellationToken ct = default)
        {
            var folders = _videoService
                .GetAllManagedFolders()
                .Where(f => f.DropFolderType.HasFlag(DropFolderType.Source))
                .Select(f => f.Name ?? f.Path ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // schedule the scan; the work executes asynchronously inside Shoko
            await _videoService.ScheduleScanForManagedFolders(onlyDropSources: true).ConfigureAwait(false);
            return folders;
        }

        /// <summary>
        /// Scan for video file entries whose physical file has disappeared and
        /// optionally remove those records from the database.  The <paramref name="dryRun"/>
        /// flag controls whether deletion occurs; in either case the list of missing
        /// paths is returned.  Database deletions never touch disk files.
        /// </summary>
        public async Task<IReadOnlyList<string>> RemoveMissingFilesAsync(bool removeFromMyList = false, bool dryRun = false, CancellationToken ct = default)
        {
            var all = _videoService.GetAllVideoFiles() ?? Array.Empty<Shoko.Abstractions.Video.IVideoFile>();
            var missing = all.Where(f => !File.Exists(f.Path)).Select(f => f.Path).ToList();
            if (!dryRun && missing.Count > 0)
            {
                var toDelete = all.Where(f => missing.Contains(f.Path)).ToList();
                await _videoService.DeleteVideoFiles(toDelete, removeFiles: false, removeFolders: false).ConfigureAwait(false);
            }
            return missing;
        }
    }
}
