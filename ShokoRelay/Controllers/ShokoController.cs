using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Controllers;

/// <summary>
/// Handles Shoko-specific automation tasks, including Virtual File System (VFS) construction,
/// file database maintenance, source imports, and watched-state synchronization with Plex.
/// </summary>
public class ShokoController : ShokoRelayBaseController
{
    private readonly VfsBuilder _vfsBuilder;
    private readonly Services.ShokoImportService _shokoImportService;
    private readonly SyncToShoko _watchedSyncService;
    private readonly SyncToPlex _syncToPlexService;

    public ShokoController(
        ConfigProvider configProvider,
        IMetadataService metadataService,
        PlexClient plexLibrary,
        VfsBuilder vfsBuilder,
        Services.ShokoImportService shokoImportService,
        SyncToShoko watchedSyncService,
        SyncToPlex syncToPlexService
    )
        : base(configProvider, metadataService, plexLibrary)
    {
        _vfsBuilder = vfsBuilder;
        _shokoImportService = shokoImportService;
        _watchedSyncService = watchedSyncService;
        _syncToPlexService = syncToPlexService;
    }

    #region Virtual File System

    /// <summary>
    /// Builds (or previews) the VFS symlink tree for the configured import folders.
    /// </summary>
    /// <param name="clean">If true, clears the existing root before building.</param>
    /// <param name="run">Must be true to actually execute the link creation.</param>
    /// <param name="filter">Optional comma-separated list of Shoko series IDs to restrict processing.</param>
    /// <returns>A summary of series processed and links created.</returns>
    [HttpGet("vfs")]
    public IActionResult BuildVfs([FromQuery] bool clean = true, [FromQuery] bool run = false, [FromQuery] string? filter = null)
    {
        var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
        if (validation != null)
            return validation;

        if (!run)
            return Ok(
                new
                {
                    status = "skipped",
                    message = "Set run=true to build the VFS",
                    filter = filterIds.Count > 0 ? string.Join(',', filterIds) : null,
                    clean,
                }
            );

        var result = filterIds.Count > 0 ? _vfsBuilder.Build(filterIds, clean) : _vfsBuilder.Build((int?)null, clean);

        if (_plexLibrary.IsEnabled && ShokoRelay.Settings.Automation.ScanOnVfsRefresh && filterIds.Count > 0)
        {
            var toProcess = ResolveSeriesList(null, filterIds).Where(s => s != null).Cast<IShokoSeries>().ToList();
            _ = SchedulePlexRefreshForSeriesAsync(toProcess);
        }

        return Ok(
            new
            {
                status = "ok",
                root = result.RootPath,
                seriesProcessed = result.SeriesProcessed,
                linksCreated = result.CreatedLinks,
                plannedLinks = result.PlannedLinks,
                skipped = result.Skipped,
                errors = result.Errors,
                logUrl = $"{ApiBase}/logs/vfs-report.log",
            }
        );
    }

    /// <summary>
    /// Accepts raw text for an anidb_vfs_overrides.csv file and overwrites the local copy.
    /// </summary>
    /// <param name="content">The full content of the CSV file.</param>
    /// <returns>200 OK on success.</returns>
    [HttpPost("vfs/overrides")]
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            System.IO.File.WriteAllText(Path.Combine(ShokoRelay.ConfigDirectory, "anidb_vfs_overrides.csv"), content ?? string.Empty);
            OverrideHelper.EnsureLoaded();
            return Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    #endregion

    #region Automation

    /// <summary>
    /// Removes records for video files that no longer exist on disk from the Shoko database.
    /// </summary>
    /// <param name="dryRun">If true, only returns a report without deleting anything.</param>
    [HttpGet("shoko/remove-missing")]
    [HttpPost("shoko/remove-missing")]
    public async Task<IActionResult> RemoveMissingFiles([FromQuery] bool? dryRun = null)
    {
        var (data, error) = await PerformRemoveMissingFilesAsync(dryRun);
        return error != null ? StatusCode(500, new { status = "error", message = error }) : Ok(data);
    }

    /// <summary>
    /// Triggers a Shoko source import scan.
    /// </summary>
    [HttpPost("shoko/import")]
    public async Task<IActionResult> RunShokoImport()
    {
        var (data, error) = await PerformShokoImportAsync(false);
        return error != null ? StatusCode(500, new { status = "error", message = error }) : Ok(data);
    }

    /// <summary>
    /// Triggers a Shoko import and resets the automation schedule.
    /// </summary>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow()
    {
        var (data, error) = await PerformShokoImportAsync(true);
        if (error != null)
            return StatusCode(500, new { status = "error", message = error });
        var freq = ShokoRelay.Settings.Automation.ShokoImportFrequencyHours;
        return Ok(
            new
            {
                status = "ok",
                triggered = true,
                scheduled = freq > 0,
                nextRunInHours = freq,
                scanned = ((dynamic)data!).scanned,
            }
        );
    }

    #endregion

    #region Watched Sync

    /// <summary>
    /// Synchronizes watched status between Plex and Shoko.
    /// </summary>
    /// <param name="dryRun">If true, no changes are written.</param>
    /// <param name="sinceHours">Optional window to limit syncing to recent changes.</param>
    /// <param name="ratings">If true, syncs user ratings/votes.</param>
    /// <param name="import">If true, direction is Plex &lt;- Shoko.</param>
    /// <param name="excludeAdmin">If true, ignores owner activity.</param>
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
            return BadRequest(new { status = "error", message = "Plex missing." });
        var (parsedDry, err) = ParseDryRunParam(dryRun);
        if (err != null)
            return err;

        bool doImport = import.GetValueOrDefault(false);
        bool includeRatings = ratings.GetValueOrDefault(false);
        PlexWatchedSyncResult result;
        string direction = doImport ? "Plex<-Shoko" : "Plex->Shoko";

        try
        {
            if (doImport)
                result = await _syncToPlexService.SyncWatchedAsync(parsedDry, sinceHours, HttpContext.RequestAborted);
            else
                result = await _watchedSyncService.SyncWatchedAsync(parsedDry, sinceHours, HttpContext.RequestAborted);

            return await LogAndReturn("sync-watched-report.log", result, (sb, r) => LogHelper.BuildSyncWatchedReport(sb, r, direction, parsedDry, includeRatings));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    /// <summary>
    /// Triggers an immediate Plex->Shoko sync and resets schedule.
    /// </summary>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow()
    {
        int freq = ShokoRelay.Settings.Automation.ShokoSyncWatchedFrequencyHours;
        try
        {
            var result = await _watchedSyncService.SyncWatchedAsync(false, freq, HttpContext.RequestAborted);
            ShokoRelay.MarkSyncRunNow();
            return Ok(
                new
                {
                    status = "ok",
                    triggered = true,
                    scheduled = freq > 0,
                    nextRunInHours = freq,
                    result,
                }
            );
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    #endregion

    #region Private Helpers

    private async Task<(object? Data, string? Error)> PerformRemoveMissingFilesAsync(bool? dryRun)
    {
        bool doDry = dryRun ?? true;
        var removed = await _shokoImportService.RemoveMissingFilesAsync(true, doDry);
        return (
            new
            {
                status = "ok",
                dryRun = doDry,
                removed,
                count = removed?.Count ?? 0,
            },
            null
        );
    }

    private async Task<(object? Data, string? Error)> PerformShokoImportAsync(bool markSchedule)
    {
        var scanned = await _shokoImportService.TriggerImportAsync();
        if (markSchedule)
            ShokoRelay.MarkImportRunNow();
        return (
            new
            {
                status = "ok",
                scanned,
                scannedCount = scanned?.Count ?? 0,
            },
            null
        );
    }

    private Task SchedulePlexRefreshForSeriesAsync(IEnumerable<IShokoSeries> series)
    {
        return Task.Run(async () =>
        {
            string rootName = VfsShared.ResolveRootFolderName();
            foreach (var s in series)
            {
                var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var data = MapHelper.GetSeriesFileData(s);
                foreach (var m in data.Mappings)
                {
                    var loc = m.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? m.Video.Files.FirstOrDefault();
                    if (loc == null)
                        continue;
                    string? ir = VfsShared.ResolveImportRootPath(loc);
                    if (!string.IsNullOrWhiteSpace(ir))
                        roots.Add(Path.Combine(ir, rootName, s.ID.ToString()));
                }
                foreach (var path in roots)
                    await _plexLibrary.RefreshSectionPathAsync(path);
            }
        });
    }

    #endregion
}
