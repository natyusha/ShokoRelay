using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
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
    ShokoImportService shokoImportService,
    SyncToShoko watchedSyncService,
    SyncToPlex syncToPlexService,
    SourceLinkService sourceLinkService,
    AnimeThemesMapping atMapping
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Fields

    private readonly VfsBuilder _vfsBuilder = vfsBuilder;
    private readonly ShokoImportService _shokoImportService = shokoImportService;
    private readonly SyncToShoko _watchedSyncService = watchedSyncService;
    private readonly SyncToPlex _syncToPlexService = syncToPlexService;
    private readonly SourceLinkService _sourceLinkService = sourceLinkService;
    private readonly AnimeThemesMapping _atMapping = atMapping;

    #endregion

    #region Virtual File System

    /// <summary>Builds the VFS symlink tree for configured import folders.</summary>
    /// <param name="clean">Whether to clear the existing root.</param>
    /// <param name="run">Flag required to execute build.</param>
    /// <param name="filter">Optional list of series IDs.</param>
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
            }
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

    #endregion

    #region Automation

    /// <summary>Removes records for video files that no longer exist on disk from the Shoko database and Anidb MyList.</summary>
    [Route("shoko/remove-missing")]
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> RemoveMissingFiles([FromQuery] bool? dryRun = null)
    {
        bool doDry = dryRun ?? true;
        const string TaskName = ShokoRelayConstants.TaskShokoRemoveMissing;

        try
        {
            var removed = await _shokoImportService.RemoveMissingFilesAsync(doDry).ConfigureAwait(false);

            // Map 'count' to 'Processed' so toastOperation/summarizeResult works out of the box
            var resultData = new
            {
                dryRun = doDry,
                Processed = removed?.Count ?? 0,
                removed,
            };

            var actionResult = LogAndReturn(ShokoRelayConstants.LogRemoveMissing, resultData, (sb, r) => LogHelper.BuildRemoveMissingReport(sb, r.dryRun, r.removed));

            if (!doDry)
                TaskHelper.CompleteTask(TaskName, (actionResult as OkObjectResult)?.Value!);
            return actionResult;
        }
        catch (Exception ex)
        {
            var err = new { status = "error", message = ex.Message };
            if (!doDry)
                TaskHelper.CompleteTask(TaskName, err);
            return BadRequest(new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

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
    /// <param name="libraryName">Optional filter to restrict the sync to a specific Plex library name.</param>
    /// <returns>A sync report result.</returns>
    [Route("sync-watched")]
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> SyncPlexWatched(
        [FromQuery] bool dryRun = true,
        [FromQuery] int? sinceHours = null,
        [FromQuery] bool? ratings = null,
        [FromQuery] bool import = false,
        [FromQuery] SyncUserType? users = null,
        [FromQuery] string? libraryName = null
    )
    {
        if (!PlexLibrary.IsEnabled)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing."));

        bool includeRatings = ratings ?? Settings.Automation.ShokoSyncWatchedIncludeRatings;
        string direction = import ? "Plex<-Shoko" : "Plex->Shoko";
        SyncUserType userType = users ?? Settings.Automation.ShokoSyncWatchedUserType;

        try
        {
            PlexWatchedSyncResult result = import
                ? await _syncToPlexService.SyncWatchedAsync(dryRun, sinceHours, includeRatings, userType, libraryName, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false)
                : await _watchedSyncService.SyncWatchedAsync(dryRun, sinceHours, includeRatings, userType, libraryName, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);

            result = result with { Direction = direction };
            return LogAndReturn(ShokoRelayConstants.LogSyncWatched, result, (sb, r) => LogHelper.BuildSyncWatchedReport(sb, r, r.Direction, r.DryRun, includeRatings));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Triggers immediate sync and resets schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow()
    {
        int freqHours = Settings.Automation.ShokoSyncWatchedFrequencyHours;
        try
        {
            var result = await _watchedSyncService.SyncWatchedAsync(false, freqHours, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
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
    /// <returns>The number of successful link operations.</returns>
    [HttpPost("map-symlinks")]
    public async Task<IActionResult> ProcessSourceLinks([FromQuery] string? mapFile = null, [FromQuery] bool purgeLinks = false)
    {
        if (!purgeLinks && string.IsNullOrWhiteSpace(mapFile))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "mapFile parameter is required when not purging."));

        if (purgeLinks)
            Logger.Info("Shoko: Starting manual purge of library symlinks...");
        else
            Logger.Info("Shoko: Starting source link processing using map {0}", mapFile);

        int count = await _sourceLinkService.ProcessLinksAsync(mapFile ?? string.Empty, purgeLinks);

        if (purgeLinks)
            Logger.Info("Shoko: Purge complete -> {0} items removed.", count);
        else
            Logger.Info("Shoko: Source link processing complete -> {0} links created/updated.", count);

        return Ok(new RelayResponse<object>(Data: new { count }));
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
