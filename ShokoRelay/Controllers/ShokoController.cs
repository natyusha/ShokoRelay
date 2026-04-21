using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Controllers;

/// <summary>Handles Shoko-specific automation tasks including VFS construction and housekeeping.</summary>
[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayConstants.BasePath)]
public class ShokoController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    VfsBuilder vfsBuilder,
    Services.ShokoImportService shokoImportService,
    SyncToShoko watchedSyncService,
    SyncToPlex syncToPlexService,
    Services.SourceLinkService sourceLinkService
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Fields

    private readonly VfsBuilder _vfsBuilder = vfsBuilder;
    private readonly Services.ShokoImportService _shokoImportService = shokoImportService;
    private readonly SyncToShoko _watchedSyncService = watchedSyncService;
    private readonly SyncToPlex _syncToPlexService = syncToPlexService;
    private readonly Services.SourceLinkService _sourceLinkService = sourceLinkService;

    #endregion

    #region Virtual File System

    /// <summary>Builds the VFS symlink tree for configured import folders.</summary>
    /// <param name="clean">Whether to clear the existing root.</param>
    /// <param name="run">Flag required to execute build.</param>
    /// <param name="filter">Optional list of series IDs.</param>
    /// <returns>A summary of the build outcome.</returns>
    [HttpGet("vfs")]
    public IActionResult BuildVfs([FromQuery] bool clean = true, [FromQuery] bool run = false, [FromQuery] string? filter = null)
    {
        var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
        if (validation != null)
            return validation;

        if (!run)
            return Ok(new RelayResponse<object>(Status: "skipped", Message: "Set run=true to build the VFS"));

        const string taskName = ShokoRelayConstants.TaskVfsBuild;
        TaskHelper.StartTask(taskName);
        try
        {
            var result = filterIds.Count > 0 ? _vfsBuilder.Build(filterIds, clean) : _vfsBuilder.Build((int?)null, clean);

            if (_plexLibrary.IsEnabled && ShokoRelay.Settings.Automation.ScanOnVfsRefresh && filterIds.Count > 0)
            {
                var toProcess = ResolveSeriesList(null, filterIds).Where(s => s != null).Cast<IShokoSeries>().ToList();
                _ = SchedulePlexRefreshForSeriesAsync(toProcess);
            }

            var actionResult = LogAndReturn(ShokoRelayConstants.LogVfs, result, LogHelper.BuildVfsReport);
            TaskHelper.CompleteTask(taskName, (actionResult as OkObjectResult)?.Value!);
            return actionResult;
        }
        catch (Exception ex)
        {
            var err = new { status = "error", message = ex.Message };
            TaskHelper.CompleteTask(taskName, err);
            return BadRequest(new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Updates the local VFS overrides CSV file.</summary>
    /// <param name="content">Raw CSV text.</param>
    /// <returns>Success or error response.</returns>
    [HttpPost("vfs/overrides")]
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            Logger.Info("Shoko: Updating VFS overrides file...");
            string path = Path.Combine(ShokoRelay.ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);
            System.IO.File.WriteAllText(path, content ?? string.Empty);
            OverrideHelper.EnsureLoaded();
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
    [HttpGet("shoko/remove-missing")]
    [HttpPost("shoko/remove-missing")]
    public async Task<IActionResult> RemoveMissingFiles([FromQuery] bool? dryRun = null)
    {
        bool doDry = dryRun ?? true;
        const string taskName = ShokoRelayConstants.TaskShokoRemoveMissing;

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
                TaskHelper.CompleteTask(taskName, (actionResult as OkObjectResult)?.Value!);
            return actionResult;
        }
        catch (Exception ex)
        {
            var err = new { status = "error", message = ex.Message };
            if (!doDry)
                TaskHelper.CompleteTask(taskName, err);
            return BadRequest(new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Triggers an import scan in Shoko.</summary>
    /// <returns>Scanned folder list.</returns>
    [HttpPost("shoko/import")]
    public async Task<IActionResult> RunShokoImport()
    {
        Logger.Info("Shoko: Import scan triggered manually.");
        var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        return Ok(new RelayResponse<object>(Data: new { scanned, scannedCount = scanned?.Count ?? 0 }));
    }

    /// <summary>Triggers import and resets automation schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow()
    {
        var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        ShokoRelay.MarkImportRunNow();
        var freqHours = ShokoRelay.Settings.Automation.ShokoImportFrequencyHours;
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
    /// <param name="sinceHours">Lookback window.</param>
    /// <param name="ratings">Include ratings.</param>
    /// <param name="import">Direction: true for Plex-to-Shoko.</param>
    /// <param name="excludeAdmin">Ignore admin user.</param>
    /// <returns>Sync report result.</returns>
    [HttpGet("sync-watched")]
    [HttpPost("sync-watched")]
    public async Task<IActionResult> SyncPlexWatched(
        [FromQuery] string? dryRun = null,
        [FromQuery] int? sinceHours = null,
        [FromQuery] bool? ratings = null,
        [FromQuery] bool? import = null,
        [FromQuery] bool? excludeAdmin = null
    )
    {
        if (!_plexLibrary.IsEnabled)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing."));
        var (parsedDry, err) = ParseDryRunParam(dryRun);
        if (err != null)
            return err;

        bool doImport = import.GetValueOrDefault(false);
        bool includeRatings = ratings.GetValueOrDefault(false);
        string direction = doImport ? "Plex<-Shoko" : "Plex->Shoko";

        try
        {
            PlexWatchedSyncResult result = doImport
                ? await _syncToPlexService.SyncWatchedAsync(parsedDry, sinceHours, ratings, excludeAdmin, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false)
                : await _watchedSyncService.SyncWatchedAsync(parsedDry, sinceHours, ratings, excludeAdmin, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);

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
        int freqHours = ShokoRelay.Settings.Automation.ShokoSyncWatchedFrequencyHours;
        try
        {
            var result = await _watchedSyncService.SyncWatchedAsync(false, freqHours, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
            ShokoRelay.MarkSyncRunNow();
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
    /// <param name="purgeLinks">If true, all symlinks in the import roots (outside VFS) will be removed.</param>
    /// <returns>The number of successful link operations.</returns>
    [HttpPost("map-symlinks")]
    public async Task<IActionResult> ProcessSourceLinks([FromQuery] string? mapFile = null, [FromQuery] bool purgeLinks = false)
    {
        if (!purgeLinks && string.IsNullOrWhiteSpace(mapFile))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "mapFile parameter is required when not purging."));

        if (purgeLinks)
            Logger.Info("Shoko: Starting manual purge of library symlinks...");
        else
            Logger.Info("Shoko: Starting source link processing using map: {0}", mapFile);

        int count = await _sourceLinkService.ProcessLinksAsync(mapFile ?? string.Empty, purgeLinks);

        if (purgeLinks)
            Logger.Info("Shoko: Purge complete. {0} items removed.", count);
        else
            Logger.Info("Shoko: Source link processing complete. {0} links created/updated.", count);

        return Ok(new RelayResponse<object>(Data: new { count }));
    }

    #endregion

    #region Private Helpers

    private Task SchedulePlexRefreshForSeriesAsync(IEnumerable<IShokoSeries> series)
    {
        return Task.Run(async () =>
        {
            foreach (var s in series)
            {
                try
                {
                    var roots = new HashSet<string>(VfsShared.PathComparer);
                    string rootName = VfsShared.ResolveRootFolderName();
                    var fileData = MapHelper.GetSeriesFileData(s);
                    foreach (var mapping in fileData.Mappings)
                    {
                        var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
                        if (location == null)
                            continue;
                        string? importRoot = VfsShared.ResolveImportRootPath(location);
                        if (!string.IsNullOrWhiteSpace(importRoot))
                            roots.Add(Path.Combine(importRoot, rootName, s.ID.ToString()));
                    }
                    foreach (var path in roots)
                        await _plexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false);
                }
                catch { }
            }
        });
    }

    #endregion
}
