using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Controllers;

/// <summary>Handles Shoko-specific automation tasks including VFS construction and housekeeping.</summary>
[ApiController]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
[Route(ShokoRelayConstants.BasePath)]
public class ShokoController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    VfsBuilder vfsBuilder,
    IShokoImportService shokoImportService,
    SyncToShoko watchedSyncService,
    SyncToPlex syncToPlexService,
    SourceLinkService sourceLinkService,
    AnimeThemesMapping atMapping,
    IVideoService videoService,
    IImageManager imageManager
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Fields

    private readonly VfsBuilder _vfsBuilder = vfsBuilder;
    private readonly IShokoImportService _shokoImportService = shokoImportService;
    private readonly SyncToShoko _watchedSyncService = watchedSyncService;
    private readonly SyncToPlex _syncToPlexService = syncToPlexService;
    private readonly SourceLinkService _sourceLinkService = sourceLinkService;
    private readonly AnimeThemesMapping _atMapping = atMapping;
    private readonly IVideoService _videoService = videoService;

    #endregion

    #region Virtual File System

    /// <summary>Builds the VFS symlink tree for configured import folders.</summary>
    /// <param name="clean">Whether to clear the existing root.</param>
    /// <param name="run">Flag required to execute build.</param>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <returns>A task representing the result of the build outcome.</returns>
    [HttpGet("vfs")]
    public Task<IActionResult> BuildVfs([FromQuery] bool clean = true, [FromQuery] bool run = false, [FromQuery] string? filter = null) =>
        ValidateFilterOrBadRequest(filter, out var filterIds) is { } guard ? Task.FromResult(guard)
        : !run ? Task.FromResult<IActionResult>(Ok(new RelayResponse<object>(Status: "skipped", Message: "Set run=true to build the VFS")))
        : ExecuteTrackedTaskAsync(
            ShokoRelayConstants.TaskVfsBuild,
            ShokoRelayConstants.LogVfs,
            LogHelper.BuildVfsReport,
            async () =>
            {
                var result = filterIds.Count > 0 ? _vfsBuilder.Build(filterIds, clean) : _vfsBuilder.Build((int?)null, clean);

                // Restore AnimeThemes links after the VFS build (filtered or global) if a mapping file exists
                if (System.IO.File.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                    await _atMapping.ApplyMappingAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken.None).ConfigureAwait(false);

                if (PlexLibrary.IsEnabled && Settings.Automation.ScanOnVfsRefresh && filterIds.Count > 0)
                    _ = SchedulePlexRefreshForSeriesAsync(ResolveSeriesList(null, filterIds).Where(s => s != null).Cast<IShokoSeries>());

                return result;
            },
            VfsShared.VfsLock
        );

    /// <summary>Updates the local VFS overrides CSV file.</summary>
    /// <param name="content">Raw CSV text.</param>
    /// <returns>Success or error response.</returns>
    [HttpPost("vfs/overrides")]
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            Logger.Info("Shoko: Updating VFS overrides file...");
            string path = Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);
            System.IO.File.WriteAllText(path, content ?? string.Empty);
            OverrideHelper.Reload(MetadataService); // Pass the service to trigger TMDB discovery
            return Ok(new RelayResponse<object>());
        }
        catch (Exception ex)
        {
            return BadRequest(new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Returns a hierarchical representation of the VFS structure grouped by import root friendly names.</summary>
    /// <returns>A JSON object containing the folder and file hierarchy.</returns>
    [HttpGet("vfs/tree")]
    public IActionResult GetVfsTree()
    {
        var path = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileVfsBlueprintCache);
        if (!System.IO.File.Exists(path))
            return Ok(new { roots = Array.Empty<object>() });

        // Normalize Managed Folder paths to remove trailing slashes for consistent comparison with blueprint keys. Filter out folders marked as Source since the VFS only builds in none or destination folders.
        var managedFolders =
            _videoService
                .GetAllManagedFolders()
                ?.Where(f => !f.DropFolderType.HasFlag(DropFolderType.Source))
                .Select(f => new { Path = f.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), f.Name })
                .OrderByDescending(f => f.Path.Length)
                .ToList()
            ?? [];

        var rawJson = System.IO.File.ReadAllText(path);
        var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, JObject>>>(rawJson);
        if (data == null)
            return Ok(new { roots = Array.Empty<object>() });

        // Map blueprint entries to their friendly Shoko names and group them to prevent redundant tabs for sub-directories.
        var roots = data.Select(root =>
            {
                var parentDir = Path.GetDirectoryName(root.Key)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
                var match = managedFolders.FirstOrDefault(f => string.Equals(parentDir, f.Path, StringComparison.OrdinalIgnoreCase));
                var fallback = Path.GetFileName(parentDir);
                return new { Name = match?.Name ?? (string.IsNullOrEmpty(fallback) ? "Unknown" : fallback), Series = root.Value.Values };
            })
            .GroupBy(x => x.Name)
            .Select(g => new { name = g.Key, series = g.SelectMany(x => x.Series).OrderBy(s => s["title"]?.ToString() ?? "").ToList() })
            .OrderBy(r => r.name)
            .ToList();

        return Ok(new { roots });
    }

    #endregion

    #region Automation

    /// <summary>Removes records for video files that no longer exist on disk from the Shoko database and Anidb MyList.</summary>
    /// <param name="dryRun">Whether to skip writes.</param>
    /// <returns>A task representing the result of the removal operation.</returns>
    [Route("shoko/remove-missing")]
    [HttpGet]
    [HttpPost]
    public Task<IActionResult> RemoveMissingFiles([FromQuery] bool dryRun = true) =>
        ExecuteTrackedTaskAsync(
            ShokoRelayConstants.TaskShokoRemoveMissing,
            ShokoRelayConstants.LogRemoveMissing,
            (sb, r) => LogHelper.BuildRemoveMissingReport(sb, r.DryRun, r.Removed),
            async () =>
            {
                var removed = await _shokoImportService.RemoveMissingFilesAsync(dryRun).ConfigureAwait(false);
                return new
                {
                    DryRun = dryRun,
                    Processed = removed.Count,
                    Removed = removed,
                };
            },
            VfsShared.VfsLock
        );

    /// <summary>Triggers an import scan in Shoko.</summary>
    /// <returns>Scanned folder list.</returns>
    [HttpPost("shoko/import")]
    public async Task<IActionResult> RunShokoImport()
    {
        Logger.Info("Shoko: Import scan triggered manually");
        var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        return Ok(new RelayResponse<object>(Data: new { scanned, scannedCount = scanned?.Count ?? 0 }));
    }

    /// <summary>Triggers import and resets automation schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow()
    {
        var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        MarkImportRunNow();
        var freqHours = Settings.Automation.ShokoImportFrequencyHours;
        return Ok(
            new RelayResponse<object>(
                Data: new
                {
                    triggered = true,
                    scheduled = freqHours > 0,
                    nextRunInHours = freqHours,
                    scanned,
                }
            )
        );
    }

    #endregion

    #region Watched Sync

    /// <summary>Synchronizes watched status between Plex and Shoko.</summary>
    /// <param name="dryRun">Whether to skip writes.</param>
    /// <param name="sinceHours">Optional lookback window in hours to limit processed items.</param>
    /// <param name="ratings">Whether to include ratings in the sync. Defaults to configuration.</param>
    /// <param name="import">Direction: <c>true</c> for Plex←Shoko (Import to Plex), <c>false</c> for Plex→Shoko (Sync to Shoko).</param>
    /// <param name="users">Optional override for the sync users configuration (0: All, 1: Admin, 2: Extra, 3: None). Defaults to configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library by name.</param>
    /// <returns>A task representing the result of the synchronization.</returns>
    [Route("sync-watched")]
    [HttpGet]
    [HttpPost]
    public Task<IActionResult> SyncPlexWatched(
        [FromQuery] bool dryRun = true,
        [FromQuery] int? sinceHours = null,
        [FromQuery] bool? ratings = null,
        [FromQuery] bool import = false,
        [FromQuery] SyncUserType? users = null,
        [FromQuery] string? libraryName = null
    )
    {
        if (!PlexLibrary.IsEnabled)
            return Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing.")));

        bool includeRatings = ratings ?? Settings.Automation.ShokoSyncWatchedIncludeRatings;
        SyncUserType userType = users ?? Settings.Automation.ShokoSyncWatchedUserType;
        string direction = import ? "Plex<-Shoko" : "Plex->Shoko";

        return ExecuteTrackedTaskAsync(
            ShokoRelayConstants.TaskShokoSyncWatched,
            ShokoRelayConstants.LogShokoSyncWatched,
            (sb, r) => LogHelper.BuildSyncWatchedReport(sb, r, r.Direction, r.DryRun, includeRatings),
            async () =>
            {
                var result = import
                    ? await _syncToPlexService.SyncWatchedAsync(dryRun, sinceHours, includeRatings, userType, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false)
                    : await _watchedSyncService.SyncWatchedAsync(dryRun, sinceHours, includeRatings, userType, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return result with { Direction = direction };
            },
            SyncHelper.SyncLock
        );
    }

    /// <summary>Triggers immediate sync and resets schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow()
    {
        int freqHours = Settings.Automation.ShokoSyncWatchedFrequencyHours;
        try
        {
            var result = await _watchedSyncService.SyncWatchedAsync(false, freqHours, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            MarkSyncRunNow();
            return Ok(
                new RelayResponse<object>(
                    Data: new
                    {
                        triggered = true,
                        scheduled = freqHours > 0,
                        nextRunInHours = freqHours,
                        result,
                    }
                )
            );
        }
        catch (Exception ex)
        {
            return StatusCode(500, new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    #endregion

    #region Source Linking

    /// <summary>Processes pending source symlinks or purges existing links from import roots.</summary>
    /// <param name="mapFile">The relative path to the mapping file.</param>
    /// <param name="purgeLinks">If true, all symlinks in the import roots (outside of the VFS) will be removed.</param>
    /// <returns>A task representing the result of the link processing.</returns>
    [HttpPost("map-symlinks")]
    public Task<IActionResult> ProcessSourceLinks([FromQuery] string? mapFile = null, [FromQuery] bool purgeLinks = false) =>
        (!purgeLinks && string.IsNullOrWhiteSpace(mapFile))
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "mapFile parameter is required when not purging.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskMapSymlinks,
                ShokoRelayConstants.LogMapSymlinks,
                LogHelper.BuildSourceLinkReport,
                async () =>
                {
                    if (purgeLinks)
                        Logger.Info("Shoko: Starting manual purge of library symlinks...");
                    else
                        Logger.Info("Shoko: Starting source link processing using map {0}", mapFile);
                    return await _sourceLinkService.ProcessLinksAsync(mapFile ?? string.Empty, purgeLinks).ConfigureAwait(false);
                },
                VfsShared.VfsLock
            );

    #endregion

    #region Temporary

    /// <summary>Wipes and purges all custom user-submitted posters and Plex-generated episode screenshots from Shoko.</summary>
    /// <returns>A JSON response with the total number of purged images.</returns>
    [HttpPost("shoko/purge-custom-images")]
    public async Task<IActionResult> PurgeLocalImages()
    {
        Logger.Info("Shoko: Starting a manual purge of all user and locally-generated images...");
        var images = imageManager.GetAllImages().Where(img => img.Source is DataSource.LocallyGenerated or DataSource.User).ToList();

        int purgedCount = 0;
        foreach (var img in images)
            if (await imageManager.PurgeImage(img).ConfigureAwait(false))
                purgedCount++;

        Logger.Info("Shoko: Purging complete. Purged {0} images.", purgedCount);
        return Ok(new RelayResponse<object>(Data: new { purged = purgedCount }));
    }

    /// <summary>Wipes and purges all default non-locally-generated episode backdrops from Shoko.</summary>
    /// <returns>A JSON response with the total number of purged backdrops.</returns>
    [HttpPost("shoko/purge-episode-images")]
    public async Task<IActionResult> PurgeEpisodeImages()
    {
        Logger.Info("Shoko: Starting a manual purge of all default (non-LocallyGenerated) episode backdrops...");
        var xrefs = imageManager.GetAllImageCrossReferences(imageType: ImageEntityType.Backdrop, entityType: DataEntityType.Episode).Where(x => x.ImageSource != DataSource.LocallyGenerated).ToList();

        var distinctImageIds = xrefs.Select(x => x.ImageID).Distinct().ToList();
        int purgedCount = 0;

        foreach (var imageId in distinctImageIds)
            if (imageManager.GetImageByID(imageId) is { } img && await imageManager.PurgeImage(img).ConfigureAwait(false))
                purgedCount++;

        Logger.Info("Shoko: Episode backdrop purging complete. Purged {0} images.", purgedCount);
        return Ok(new RelayResponse<object>(Data: new { purged = purgedCount }));
    }

    #endregion

    #region Private Helpers

    private Task SchedulePlexRefreshForSeriesAsync(IEnumerable<IShokoSeries> series) =>
        Task.Run(async () =>
        {
            foreach (var s in series)
            {
                try
                {
                    var roots = new HashSet<string>(VfsShared.PathComparer);
                    string rootName = VfsShared.ResolveRootFolderName();
                    foreach (var mapping in MapHelper.GetSeriesFileData(s, MetadataService).Mappings)
                    {
                        var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
                        if (location != null && VfsShared.ResolveImportRootPath(location) is string importRoot)
                            roots.Add(Path.Combine(importRoot, rootName, s.ID.ToString()));
                    }
                    foreach (var path in roots)
                        await PlexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false);
                }
                catch { }
            }
        });

    #endregion
}
