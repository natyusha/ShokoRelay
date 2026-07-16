using System.Collections.Concurrent;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image.Options;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;
using IoFile = System.IO.File;

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
            LogHelper.BuildVfsReport,
            async () =>
            {
                var result = filterIds.Count > 0 ? vfsBuilder.Build(filterIds, clean) : vfsBuilder.Build((int?)null, clean);

                // Restore AnimeThemes links after the VFS build (filtered or global) if a mapping file exists
                if (IoFile.Exists(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileAtMapping)))
                    await atMapping.ApplyMappingAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken.None).ConfigureAwait(false);

                if (PlexLibrary.IsEnabled && Settings.Automation.ScanOnVfsRefresh && filterIds.Count > 0)
                    _ = SchedulePlexRefreshForSeriesAsync(ResolveSeriesList(null, filterIds).Where(s => s != null).Cast<IShokoSeries>());

                return result;
            },
            VfsShared.VfsLock
        );

    /// <summary>Audits the VFS to find and remove orphaned series folders and broken symlinks.</summary>
    /// <returns>A task representing the result of the audit operation.</returns>
    [HttpGet("vfs/audit")]
    public async Task<IActionResult> AuditVfs() =>
        await ExecuteTrackedTaskAsync(ShokoRelayConstants.TaskVfsAudit, LogHelper.BuildVfsAuditReport, () => Task.Run(() => vfsBuilder.Audit(CancellationToken.None)), VfsShared.VfsLock).ConfigureAwait(false);

    /// <summary>Updates the local VFS overrides CSV file.</summary>
    /// <param name="content">Raw CSV text.</param>
    /// <returns>Success or error response.</returns>
    [HttpPost("vfs/overrides")]
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            Logger.Info("Shoko: Updating VFS overrides file...");
            IoFile.WriteAllText(Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides), content ?? string.Empty);
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
    public async Task<IActionResult> GetVfsTree()
    {
        IActionResult EmptyTree() =>
            Content( /*lang=json,strict*/
                "{\"roots\":[]}",
                "application/json"
            );

        if (!IoFile.Exists(VfsShared.BlueprintFilePath))
            return EmptyTree();

        try
        {
            // Normalize Managed Folder paths to remove trailing slashes for consistent comparison with blueprint keys. Filter out folders marked strictly as Source (without Destination).
            var managedFolders =
                videoService
                    .GetAllManagedFolders()
                    ?.Where(f => !VfsShared.IsSourceOnly(f))
                    .Select(f => new { Path = f.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), f.Name })
                    .OrderByDescending(f => f.Path.Length)
                    .ToList()
                ?? [];

            using var stream = new FileStream(VfsShared.BlueprintFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, Dictionary<string, VfsBlueprintSeries>>>(stream);
            if (data == null)
                return EmptyTree();

            // Map blueprint entries to their friendly Shoko names and group them to prevent redundant tabs for sub-directories.
            var roots = data.Select(root =>
                {
                    var parentDir = Path.GetDirectoryName(root.Key)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
                    var match = managedFolders.FirstOrDefault(f => string.Equals(parentDir, f.Path, StringComparison.OrdinalIgnoreCase));
                    var fallback = Path.GetFileName(parentDir);
                    return new { Name = match?.Name ?? (string.IsNullOrEmpty(fallback) ? "Unknown" : fallback), Series = root.Value.Values };
                })
                .GroupBy(x => x.Name)
                .Select(g => new { name = g.Key, series = g.SelectMany(x => x.Series).OrderBy(s => s.Title ?? "").ToList() })
                .OrderBy(r => r.name)
                .ToList();

            // Bypass MVC's default Newtonsoft.Json formatter and return explicit System.Text.Json to preserve attribute-based casing
            return Content(JsonSerializer.Serialize(new { roots }), "application/json");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShokoController: Failed to parse VFS blueprint cache");
            return EmptyTree();
        }
    }

    #endregion

    #region Automation

    /// <summary>Removes records for video files that no longer exist on disk from the Shoko database and Anidb MyList.</summary>
    /// <param name="dryRun">Whether to skip writes.</param>
    /// <returns>A task representing the result of the removal operation.</returns>
    [Route("shoko/purge-missing")]
    [HttpGet]
    [HttpPost]
    public Task<IActionResult> PurgeMissingFiles([FromQuery] bool dryRun = true) =>
        ExecuteTrackedTaskAsync(
            ShokoRelayConstants.TaskShokoPurgeMissing,
            (sb, r) => LogHelper.BuildPurgeMissingReport(sb, r.DryRun, r.Removed),
            async () =>
            {
                var removed = await shokoImportService.PurgeMissingFilesAsync(dryRun).ConfigureAwait(false);
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
        var scanned = await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
        return Ok(new RelayResponse<object>(Data: new { scanned, scannedCount = scanned?.Count ?? 0 }));
    }

    /// <summary>Triggers import and resets automation schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow()
    {
        var scanned = await shokoImportService.TriggerImportAsync().ConfigureAwait(false);
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
    /// <param name="progress">Whether to include playback progress in the sync. Defaults to configuration.</param>
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
        [FromQuery] bool? progress = null,
        [FromQuery] bool import = false,
        [FromQuery] SyncUserType? users = null,
        [FromQuery] string? libraryName = null
    ) =>
        !PlexLibrary.IsEnabled
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskShokoSyncWatched,
                (sb, r) => LogHelper.BuildSyncWatchedReport(sb, r, r.Direction, r.DryRun, ratings ?? Settings.Automation.ShokoSyncWatchedIncludeRatings),
                async () =>
                {
                    var result = import
                        ? await syncToPlexService.SyncWatchedAsync(dryRun, sinceHours, ratings, users, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false)
                        : await watchedSyncService.SyncWatchedAsync(dryRun, sinceHours, ratings, progress, users, libraryName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return result with { Direction = import ? "Plex<-Shoko" : "Plex->Shoko" };
                },
                SyncHelper.SyncLock
            );

    /// <summary>Triggers immediate sync and resets schedule.</summary>
    /// <returns>Trigger result.</returns>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow()
    {
        int freqHours = Settings.Automation.ShokoSyncWatchedFrequencyHours;
        try
        {
            var result = await watchedSyncService.SyncWatchedAsync(false, freqHours, cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
        !purgeLinks && string.IsNullOrWhiteSpace(mapFile)
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "mapFile parameter is required when not purging.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskMapSymlinks,
                LogHelper.BuildSourceLinkReport,
                async () =>
                {
                    if (purgeLinks)
                        Logger.Info("Shoko: Starting manual purge of library symlinks...");
                    else
                        Logger.Info("Shoko: Starting source link processing using map {0}", mapFile);
                    return await sourceLinkService.ProcessLinksAsync(mapFile ?? string.Empty, purgeLinks).ConfigureAwait(false);
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
        int purgedCount = 0;
        foreach (var img in imageManager.GetAllImages().Where(img => img.Source is DataSource.LocallyGenerated or DataSource.User).ToList())
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
        var xrefs = imageManager
            .GetAllImageCrossReferences(new ImageCrossReferenceFilteringOptions { ImageType = ImageEntityType.Backdrop })
            .Where(x => x.EntityType == DataEntityType.Episode && x.ImageSource != DataSource.LocallyGenerated);

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

    private static readonly ConcurrentDictionary<int, CancellationTokenSource> s_manualPlexRefreshes = new();

    /// <summary>Schedules a debounced background task to scan and subsequently refresh Plex metadata for a specific set of series.</summary>
    /// <param name="series">The collection of Shoko series metadata objects to refresh.</param>
    /// <returns>A background task representing the asynchronous refresh operation.</returns>
    private Task SchedulePlexRefreshForSeriesAsync(IEnumerable<IShokoSeries> series)
    {
        foreach (var s in series)
        {
            if (s_manualPlexRefreshes.TryRemove(s.ID, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            s_manualPlexRefreshes[s.ID] = cts;

            _ = Task.Run(async () =>
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
                        await PlexLibrary.RefreshSectionPathAsync(path, cts.Token).ConfigureAwait(false);

                    int bufferSeconds = Settings.Advanced.PlexScanDelay;
                    if (bufferSeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(bufferSeconds), cts.Token).ConfigureAwait(false);

                    var targets = PlexLibrary.GetConfiguredTargets();
                    foreach (var target in targets)
                    {
                        try
                        {
                            var ratingKey = await PlexLibrary.FindRatingKeyForShokoSeriesInSectionAsync(s.ID, target, cts.Token).ConfigureAwait(false);
                            if (ratingKey.HasValue)
                                await PlexLibrary.RefreshMetadataAsync(ratingKey.Value, target, cts.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (s_manualPlexRefreshes.TryRemove(new KeyValuePair<int, CancellationTokenSource>(s.ID, cts)))
                        cts.Dispose();
                }
            });
        }
        return Task.CompletedTask;
    }

    #endregion
}
