using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Image.Options;
using Shoko.Abstractions.ScheduledActions.Services;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.Actions.Scheduled;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Handles Shoko-specific automation tasks including VFS construction and housekeeping.</summary>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="plexLibrary">Plex client.</param>
/// <param name="vfsBuilder">VFS builder.</param>
/// <param name="shokoImportService">Shoko import service.</param>
/// <param name="watchedSyncService">Watched state sync service (Plex to Shoko).</param>
/// <param name="syncToPlexService">Watched state sync service (Shoko to Plex).</param>
/// <param name="sourceLinkService">Source link service.</param>
/// <param name="atMapping">AnimeThemes mapping service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="imageManager">Shoko image manager.</param>
/// <param name="imageSyncService">Shoko image sync service.</param>
/// <param name="vfsWatcher">VFS filesystem event watcher.</param>
/// <param name="plexMetadata">Plex metadata mapper.</param>
/// <param name="scheduledActionService">Shoko scheduled action service.</param>
/// <param name="logger">Logger instance.</param>
[ApiController]
[RelayAdminAuthorize]
[Route(ShokoRelayConstants.BasePath)]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
public class ShokoController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    VfsBuilder vfsBuilder,
    ShokoImportService shokoImportService,
    SyncToShoko watchedSyncService,
    SyncToPlex syncToPlexService,
    SourceLinkService sourceLinkService,
    AnimeThemesMapping atMapping,
    IVideoService videoService,
    IImageManager imageManager,
    ImageSyncService imageSyncService,
    VfsWatcher vfsWatcher,
    PlexMetadata plexMetadata,
    IScheduledActionService scheduledActionService,
    ILogger<ShokoController> logger
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary, logger)
{
    #region Virtual File System

    /// <summary>Cached serialized JSON payload of the VFS tree to bypass redundant disk and LINQ processing.</summary>
    private static string? s_cachedTreeJson;

    /// <summary>Last write timestamp of the blueprint file used to invalidate the cached JSON.</summary>
    private static DateTime s_lastBlueprintWriteUtc = DateTime.MinValue;

    /// <summary>Last blueprint save version used to invalidate the cached JSON.</summary>
    private static long s_lastBlueprintVersion = -1;

    /// <summary>Lock object for synchronizing tree cache updates.</summary>
    private static readonly Lock s_treeCacheLock = new();

    /// <summary>Builds the VFS symlink tree for configured managed folders.</summary>
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
            ReportHelper.BuildVfsReport,
            async () =>
            {
                var result = filterIds.Count > 0 ? vfsBuilder.Build(filterIds, clean) : vfsBuilder.Build((int?)null, clean);

                // Restore AnimeThemes links after the VFS build (filtered or global) if a mapping file exists
                await atMapping.ApplyMappingIfExistsAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken.None).ConfigureAwait(false);

                // Trigger standard debounced Plex updates if this is a targeted manual rebuild
                if (PlexLibrary.IsEnabled && filterIds.Count > 0)
                    foreach (var id in filterIds)
                        vfsWatcher.TriggerPlexUpdates(id);

                return result;
            },
            VfsShared.VfsLock
        );

    /// <summary>Audits the VFS to find and remove orphaned series folders and broken symlinks.</summary>
    /// <returns>A task representing the result of the audit operation.</returns>
    [HttpGet("vfs/audit")]
    public Task<IActionResult> AuditVfs() =>
        ExecuteTrackedTaskAsync(ShokoRelayConstants.TaskVfsAudit, ReportHelper.BuildVfsAuditReport, () => Task.Run(() => vfsBuilder.Audit(CancellationToken.None)), VfsShared.VfsLock);

    /// <summary>Updates the local VFS overrides CSV file.</summary>
    /// <param name="content">Raw CSV text.</param>
    /// <returns>Success or error response.</returns>
    [HttpPost("vfs/overrides")]
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            Logger.LogInformation("Shoko: Updating VFS overrides file...");
            VfsShared.WriteTextAtomic(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides), content ?? string.Empty);
            OverrideHelper.Reload();
            plexMetadata.ClearCache();
            return Ok(new RelayResponse<object>());
        }
        catch (Exception exception)
        {
            return BadRequest(new RelayResponse<object>(Status: "error", Message: exception.Message));
        }
    }

    /// <summary>Returns a hierarchical representation of the VFS structure grouped by import root friendly names.</summary>
    /// <param name="refresh">When <c>true</c>, re-resolves series display titles from current provider settings and updates the blueprint cache before returning.</param>
    /// <returns>A JSON object containing the folder and file hierarchy.</returns>
    [HttpGet("vfs/tree")]
    public async Task<IActionResult> GetVfsTree([FromQuery] bool refresh = false)
    {
        SetNoCacheHeaders();

        IActionResult EmptyTree() =>
            Content( /*lang=json,strict*/
                "{\"roots\":[]}",
                "application/json"
            );

        if (!IoFile.Exists(VfsShared.BlueprintFilePath))
            return EmptyTree();

        if (refresh)
        {
            await VfsShared.VfsLock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            try
            {
                var blueprint = VfsShared.LoadBlueprint();
                bool updated = false;
                foreach (var rootDict in blueprint.Values)
                foreach (var seriesEntry in rootDict.ToList())
                    if (MetadataService.GetShokoSeriesByID(seriesEntry.Key) is { } shokoSeries)
                    {
                        string latestTitle = TextHelper.ResolveFullSeriesTitles(shokoSeries).DisplayTitle;
                        if (!string.Equals(seriesEntry.Value.Title, latestTitle, StringComparison.Ordinal))
                        {
                            rootDict[seriesEntry.Key] = seriesEntry.Value with { Title = latestTitle };
                            updated = true;
                        }
                    }

                if (updated)
                    VfsShared.SaveBlueprint(blueprint);
            }
            finally
            {
                VfsShared.VfsLock.Release();
            }
        }

        var lastWrite = IoFile.GetLastWriteTimeUtc(VfsShared.BlueprintFilePath);
        var version = VfsShared.BlueprintVersion;
        lock (s_treeCacheLock)
            if (s_cachedTreeJson != null && s_lastBlueprintWriteUtc == lastWrite && s_lastBlueprintVersion == version)
                return Content(s_cachedTreeJson, "application/json");

        try
        {
            // Normalize Managed Folder paths to remove trailing slashes for consistent comparison with blueprint keys. Filter out folders that are disabled for VFS.
            var managedFolders = videoService
                .GetAllManagedFolders()
                ?.Where(VfsShared.IsVfsEnabledFolder)
                .Select(folder => new { Path = folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), folder.Name })
                .OrderByDescending(folder => folder.Path.Length)
                .ToList()
                ?? [];

            using var stream = new FileStream(VfsShared.BlueprintFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, Dictionary<string, VfsBlueprintSeries>>>(stream, cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
            if (data == null)
                return EmptyTree();

            // Map blueprint entries to their friendly Shoko names and group them to prevent redundant tabs for sub-directories.
            var roots = data.Select(root =>
                {
                    var parentDir = Path.GetDirectoryName(root.Key)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
                    var match = managedFolders.FirstOrDefault(folder => string.Equals(parentDir, folder.Path, StringComparison.OrdinalIgnoreCase));
                    var fallback = Path.GetFileName(parentDir);
                    string tabName = match?.Name ?? (string.IsNullOrEmpty(fallback) ? "Unknown" : fallback);

                    return new { Name = tabName, Series = root.Value.Values };
                })
                .GroupBy(rootGroup => rootGroup.Name)
                .Select(group =>
                {
                    // Safely merge TV and Movie blueprint entries sharing the same Shoko Series ID within a unified root tab
                    var mergedSeries = group
                        .SelectMany(entry => entry.Series)
                        .GroupBy(series => series.SeriesId)
                        .Select(seriesGroup =>
                        {
                            var first = seriesGroup.First();
                            if (seriesGroup.Count() == 1)
                                return first;

                            var allRootFiles = seriesGroup.SelectMany(series => series.RootFiles).DistinctBy(file => file.Name).ToList();
                            var allSeasons = seriesGroup
                                .SelectMany(series => series.Seasons)
                                .GroupBy(season => season.Name)
                                .Select(seasonGroup =>
                                {
                                    var firstSeason = seasonGroup.First();
                                    return seasonGroup.Count() == 1 ? firstSeason : (firstSeason with { Files = [.. seasonGroup.SelectMany(season => season.Files).DistinctBy(file => file.Name)] });
                                })
                                .ToList();

                            return first with
                            {
                                RootFiles = allRootFiles,
                                Seasons = allSeasons,
                            };
                        })
                        .OrderBy(series => series.Title ?? "")
                        .ToList();

                    return new { name = group.Key, series = mergedSeries };
                })
                .OrderBy(root => root.name)
                .ToList();

            var json = JsonSerializer.Serialize(new { roots });
            var currentLastWrite = IoFile.GetLastWriteTimeUtc(VfsShared.BlueprintFilePath);
            var currentVersion = VfsShared.BlueprintVersion;
            lock (s_treeCacheLock)
            {
                if (currentLastWrite == lastWrite && currentVersion == version)
                {
                    s_cachedTreeJson = json;
                    s_lastBlueprintWriteUtc = lastWrite;
                    s_lastBlueprintVersion = version;
                }
            }

            // Bypass MVC's default Newtonsoft.Json formatter and return explicit System.Text.Json to preserve attribute-based casing
            return Content(json, "application/json");
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Shoko: Failed to parse VFS blueprint cache");
            return EmptyTree();
        }
    }

    #endregion

    #region Automation

    /// <summary>Result data for a Purge Missing Files operation.</summary>
    /// <param name="DryRun">Whether the operation was a dry run.</param>
    /// <param name="TrashOnly">Whether only Plex trash was processed.</param>
    /// <param name="Processed">Count of Shoko database records removed.</param>
    /// <param name="Removed">List of Shoko file paths removed.</param>
    /// <param name="PlexRemoved">List of Plex trashed item display names.</param>
    /// <param name="PlexMessages">List of Plex trash status messages.</param>
    public record PurgeMissingResult(bool DryRun, bool TrashOnly, int Processed, List<string> Removed, List<string> PlexRemoved, List<string> PlexMessages);

    /// <summary>Removes records for video files that no longer exist on disk from Shoko, or safely empties Plex trash.</summary>
    /// <param name="dryRun">Whether to skip writes.</param>
    /// <param name="trashOnly">If true, skips Shoko database purging and only evaluates/empties Plex trash.</param>
    /// <param name="threshold">Optional percentage threshold override (1-100) for Plex trash empty.</param>
    /// <returns>A task representing the result of the purge/empty operation.</returns>
    [HttpGet]
    [HttpPost]
    [Route("shoko/purge-missing")]
    public Task<IActionResult> PurgeMissingFiles([FromQuery] bool dryRun = true, [FromQuery] bool trashOnly = false, [FromQuery] int? threshold = null) =>
        ExecuteTrackedTaskAsync(
            ShokoRelayConstants.TaskShokoPurgeMissing,
            (builder, report) => ReportHelper.BuildPurgeMissingReport(builder, report.DryRun, report.Removed, report.PlexRemoved, report.PlexMessages),
            async () =>
            {
                List<string> removed = trashOnly ? [] : [.. await shokoImportService.PurgeMissingFilesAsync(dryRun).ConfigureAwait(false)];

                var plexRemoved = new List<string>();
                var plexMessages = new List<string>();
                int effectiveThreshold = threshold ?? Settings.Advanced.EmptyPlexTrashThreshold;

                if (effectiveThreshold > 0 && PlexLibrary.IsEnabled)
                {
                    foreach (var target in PlexLibrary.GetConfiguredTargets())
                    {
                        var (_, trashed, message) = await PlexLibrary.EmptyTrashWithSafetyAsync(target, effectiveThreshold, dryRun).ConfigureAwait(false);
                        plexMessages.Add($"[{target.Title}] {message}");
                        if (trashed.Count > 0)
                            plexRemoved.AddRange(trashed.Select(item => $"[{target.Title}] {item}"));
                    }
                }
                else if (trashOnly)
                    plexMessages.Add("Empty Plex Trash is disabled. Configure a threshold under Provider Settings > Advanced or pass 'threshold=N'.");

                return new PurgeMissingResult(dryRun, trashOnly, removed.Count, removed, plexRemoved, plexMessages);
            },
            VfsShared.VfsLock
        );

    /// <summary>Triggers an import scan in Shoko.</summary>
    /// <returns>Scanned folder list.</returns>
    [HttpPost("shoko/import")]
    public async Task<IActionResult> RunShokoImport()
    {
        Logger.LogInformation("Shoko: Import scan triggered manually");
        var scanned = await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        return Ok(new RelayResponse<object>(Data: new { scanned, scannedCount = scanned?.Count ?? 0 }));
    }

    /// <summary>Triggers an immediate import scan and returns the current scheduled action status.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow()
    {
        var scanned = await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        var actionInfo = scheduledActionService.GetScheduledAction<ShokoImportAction>();
        return Ok(
            new RelayResponse<object>(
                Data: new
                {
                    triggered = true,
                    scheduled = actionInfo?.Triggers.Count > 0,
                    nextRun = actionInfo?.NextRunAt,
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
    /// <param name="progress">Whether to include playback progress in the sync. Defaults to configuration.</param>
    /// <param name="import">Direction: <c>true</c> for Plex←Shoko (Import to Plex), <c>false</c> for Plex→Shoko (Sync to Shoko).</param>
    /// <param name="users">Optional override for the sync users configuration (0: All, 1: Admin, 2: Extra, 3: Advanced, 4: None). Defaults to configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library by name.</param>
    /// <returns>A task representing the result of the synchronization.</returns>
    [HttpGet]
    [HttpPost]
    [Route("sync-watched")]
    public Task<IActionResult> SyncPlexWatched(
        [FromQuery] bool dryRun = true,
        [FromQuery] int? sinceHours = null,
        [FromQuery] bool? ratings = null,
        [FromQuery] bool? progress = null,
        [FromQuery] bool import = false,
        [FromQuery] SyncUserType? users = null,
        [FromQuery] string? libraryName = null
    ) =>
        !PlexLibrary.IsEnabled
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskShokoSyncWatched,
                (builder, report) => ReportHelper.BuildSyncWatchedReport(builder, report, report.Direction, report.DryRun, ratings ?? Settings.Automation.ShokoSyncWatchedIncludeRatings),
                async () =>
                {
                    var result = import
                        ? await syncToPlexService.SyncWatchedAsync(dryRun, sinceHours, ratings, users, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false)
                        : await watchedSyncService.SyncWatchedAsync(dryRun, sinceHours, ratings, progress, users, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return result with { Direction = import ? "Plex<-Shoko" : "Plex->Shoko" };
                },
                SyncHelper.SyncLock
            );

    /// <summary>Triggers an immediate watched-state sync and returns the current scheduled action status.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow()
    {
        if (!await SyncHelper.SyncLock.WaitAsync(0).ConfigureAwait(false))
            return Conflict(new RelayResponse<object>(Status: "busy", Message: "A conflicting operation is already in progress. Please wait for item to complete."));

        try
        {
            var settings = ConfigProvider.GetSettings();
            int freqHours = settings.Automation.ShokoSyncWatchedFrequencyHours;
            var result = await watchedSyncService.SyncWatchedAsync(false, freqHours > 0 ? freqHours + 1 : null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var actionInfo = scheduledActionService.GetScheduledAction<PlexWatchedSyncAction>();
            return Ok(
                new RelayResponse<object>(
                    Data: new
                    {
                        triggered = true,
                        scheduled = actionInfo?.Triggers.Count > 0,
                        nextRun = actionInfo?.NextRunAt,
                        result,
                    }
                )
            );
        }
        catch (Exception exception)
        {
            return StatusCode(500, new RelayResponse<object>(Status: "error", Message: exception.Message));
        }
        finally
        {
            SyncHelper.SyncLock.Release();
        }
    }

    #endregion

    #region Reports

    /// <summary>Generates a CSV report of all tags across Shoko series after applying default tag overrides.</summary>
    /// <returns>A plain-text CSV report listing source, tag, and series count.</returns>
    [HttpGet("reports/tags")]
    public IActionResult GetTagReport()
    {
        Response.Headers.ContentDisposition = $"inline; filename=\"{ShokoRelayConstants.FileTagsReport}\"";
        return Content(TagHelper.BuildTagReport(MetadataService), "text/csv; charset=utf-8");
    }

    /// <summary>Generates a CSV report of all credits and studios across Shoko series after applying default credit overrides.</summary>
    /// <returns>A plain-text CSV report listing source, credit, and series count.</returns>
    [HttpGet("reports/credits")]
    public IActionResult GetCreditReport()
    {
        Response.Headers.ContentDisposition = $"inline; filename=\"{ShokoRelayConstants.FileCreditsReport}\"";
        return Content(CastHelper.BuildCreditReport(MetadataService), "text/csv; charset=utf-8");
    }

    #endregion

    #region Source Linking

    /// <summary>Processes pending source symlinks or purges existing links from import roots.</summary>
    /// <param name="mapFile">The relative path to the mapping file.</param>
    /// <param name="purgeLinks">If true, all symlinks in the import roots (outside of the VFS) will be removed.</param>
    /// <returns>A task representing the result of the link processing.</returns>
    [HttpPost("map-symlinks")]
    public Task<IActionResult> ProcessSourceLinks([FromQuery] string? mapFile = null, [FromQuery] bool purgeLinks = false) =>
        !purgeLinks && string.IsNullOrWhiteSpace(mapFile)
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "mapFile parameter is required when not purging.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskMapSymlinks,
                ReportHelper.BuildSourceLinkReport,
                () =>
                {
                    if (purgeLinks)
                        Logger.LogInformation("Shoko: Starting manual purge of library symlinks...");
                    else
                        Logger.LogInformation("Shoko: Starting source link processing using map {MapFile}", mapFile);
                    return sourceLinkService.ProcessLinksAsync(mapFile ?? string.Empty, purgeLinks);
                },
                VfsShared.VfsLock
            );

    #endregion

    #region Image Management

    /// <summary>Wipes and purges all custom user-submitted posters and Plex-generated episode screenshots uploaded by Shoko Relay.</summary>
    /// <returns>A JSON response with the total number of purged images.</returns>
    [HttpPost("shoko/purge-relay-custom-images")]
    public async Task<IActionResult> PurgeLocalImages()
    {
        Logger.LogInformation("Shoko: Starting a manual purge of all Relay-owned images...");
        int purgedCount = await imageSyncService.PurgeAllRelayImagesAsync().ConfigureAwait(false);
        Logger.LogInformation("Shoko: Purging complete. Purged {Count} images.", purgedCount);
        return Ok(new RelayResponse<object>(Data: new { purged = purgedCount }));
    }

    /// <summary>LEGACY: Wipes and purges all custom user-submitted posters and Plex-generated episode screenshots from Shoko. Use with caution!</summary>
    /// <returns>A JSON response with the total number of purged images.</returns>
    [HttpPost("shoko/purge-all-custom-images")]
    public async Task<IActionResult> PurgeAllLocalImages()
    {
        Logger.LogWarning("Shoko: Starting a legacy manual purge of ALL user and locally-generated images. This will remove images not uploaded by Shoko Relay!");
        int purgedCount = 0;
        var localImages = imageManager
            .GetAllImages(new ImageFilteringOptions { ImageSource = MetadataSource.Generated })
            .Concat(imageManager.GetAllImages(new ImageFilteringOptions { ImageSource = MetadataSource.User }))
            .ToList();
        foreach (var image in localImages)
            if (await imageManager.PurgeImage(image).ConfigureAwait(false))
                purgedCount++;

        Logger.LogInformation("Shoko: Legacy purging complete. Purged {Count} images.", purgedCount);
        return Ok(new RelayResponse<object>(Data: new { purged = purgedCount }));
    }

    #endregion
}
