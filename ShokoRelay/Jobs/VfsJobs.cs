using Newtonsoft.Json;
using Shoko.Abstractions.Utilities;
using Shoko.QueueProcessor.Acquisition.Attributes;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;
using Shoko.QueueProcessor.Workers;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Controllers;
using ShokoRelay.Services;

namespace ShokoRelay.Jobs;

#region VFS Build

/// <summary>Queue job building or rebuilding the Shoko Relay VFS and restoring AnimeThemes VFS links.</summary>
[DatabaseRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class VfsBuildJob(
    VfsBuilder vfsBuilder,
    AnimeThemesMapping atMapping,
    VfsWatcher vfsWatcher,
    PlexClient plexClient,
    IJobCancellationAccessor cancellation,
    IJobProgressAccessor progress,
    ILogger<VfsBuildJob> logger
) : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Comma-separated primary Shoko series IDs to limit the build, or empty for all series.</summary>
    [JobKeyIgnore]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Compact dedup key segment representing <see cref="Filter"/>.</summary>
    [JobKeyMember("filter", 0)]
    public string FilterKey { get; set; } = "all";

    /// <summary>Whether to clean existing VFS roots before building.</summary>
    [JobKeyMember("clean", 1)]
    public bool Clean { get; set; } = true;

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Build VFS";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title =>
        string.IsNullOrWhiteSpace(Filter) ? (Clean ? $"{ShokoRelayConstants.Name}: Clean & Build VFS" : $"{ShokoRelayConstants.Name}: Build VFS") : $"{ShokoRelayConstants.Name}: Build VFS ({FilterKey})";

    /// <inheritdoc/>
    [JsonIgnore]
    public override Dictionary<string, object> Details => string.IsNullOrWhiteSpace(Filter) ? new() { ["Clean"] = Clean } : new() { ["Filter"] = Filter, ["Clean"] = Clean };

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskVfsBuild,
            $"{ShokoRelayConstants.TaskVfsBuild}.report",
            ReportHelper.BuildVfsReport,
            async () =>
            {
                var filterIds = ParsePrimaryFilterIds(Filter);
                var result =
                    filterIds.Count > 0 ? vfsBuilder.Build(filterIds, Clean, CancellationToken, Progress.Slice(0m, 70m)) : vfsBuilder.Build((int?)null, Clean, CancellationToken, Progress.Slice(0m, 70m));

                await atMapping.ApplyMappingIfExistsAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken, Progress.Slice(70m, 100m)).ConfigureAwait(false);

                if (plexClient.IsEnabled && filterIds.Count > 0)
                    foreach (int id in filterIds)
                        vfsWatcher.TriggerPlexUpdates(id);

                return result;
            },
            semaphore: VfsShared.VfsLock
        );
}

#endregion

#region VFS Audit

/// <summary>Queue job auditing VFS directories to remove broken symlinks and orphaned folders.</summary>
[DatabaseRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class VfsAuditJob(VfsBuilder vfsBuilder, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<VfsAuditJob> logger) : RelayJobBase(cancellation, progress, logger)
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Audit VFS";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => $"{ShokoRelayConstants.Name}: Audit VFS";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskVfsAudit,
            $"{ShokoRelayConstants.TaskVfsAudit}.report",
            ReportHelper.BuildVfsAuditReport,
            () => Task.Run(() => vfsBuilder.Audit(CancellationToken, Progress), CancellationToken),
            semaphore: VfsShared.VfsLock
        );
}

#endregion

#region AnimeThemes VFS

/// <summary>Queue job applying AnimeThemes cross-reference mappings to VFS Shorts folders.</summary>
[DatabaseRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class AtVfsBuildJob(AnimeThemesMapping atMapping, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<AtVfsBuildJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Comma-separated primary Shoko series IDs to limit linking, or empty for all series.</summary>
    [JobKeyIgnore]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Compact dedup key segment representing <see cref="Filter"/>.</summary>
    [JobKeyMember("filter", 0)]
    public string FilterKey { get; set; } = "all";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Apply AnimeThemes VFS";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => string.IsNullOrWhiteSpace(Filter) ? $"{ShokoRelayConstants.Name}: Apply AnimeThemes VFS" : $"{ShokoRelayConstants.Name}: Apply AnimeThemes VFS ({FilterKey})";

    /// <inheritdoc/>
    [JsonIgnore]
    public override Dictionary<string, object> Details => string.IsNullOrWhiteSpace(Filter) ? [] : new() { ["Filter"] = Filter };

    /// <inheritdoc/>
    public override Task Process()
    {
        var filterIds = ParsePrimaryFilterIds(Filter);
        return ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskAtVfsBuild,
            $"{ShokoRelayConstants.TaskAtVfsBuild}.report",
            (builder, report) => ReportHelper.BuildAtVfsBuildReport(builder, report, filterIds),
            () => atMapping.ApplyMappingAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken, Progress),
            semaphore: VfsShared.VfsLock
        );
    }
}

#endregion

#region Purge Missing

/// <summary>Queue job purging missing Shoko file records and safely emptying Plex trash.</summary>
[DatabaseRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class PurgeMissingJob(ShokoImportService shokoImportService, PlexClient plexClient, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<PurgeMissingJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Whether to preview without removing records or emptying trash.</summary>
    [JobKeyMember("dryRun", 0)]
    public bool DryRun { get; set; } = true;

    /// <summary>Whether to skip Shoko database purging and only evaluate/empty Plex trash.</summary>
    [JobKeyMember("trashOnly", 1)]
    public bool TrashOnly { get; set; }

    /// <summary>Optional percentage threshold override for Plex trash emptying.</summary>
    [JobKeyMember("threshold", 2)]
    public int? Threshold { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Purge Missing";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => DryRun ? $"{ShokoRelayConstants.Name}: Purge Missing Files (Dry Run)" : $"{ShokoRelayConstants.Name}: Purge Missing Files";

    /// <inheritdoc/>
    [JsonIgnore]
    public override Dictionary<string, object> Details => new() { ["DryRun"] = DryRun, ["TrashOnly"] = TrashOnly };

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskShokoPurgeMissing,
            $"{ShokoRelayConstants.TaskShokoPurgeMissing}.report",
            (builder, report) => ReportHelper.BuildPurgeMissingReport(builder, report.DryRun, report.Removed, report.PlexRemoved, report.PlexMessages),
            async () =>
            {
                List<string> removed = TrashOnly ? [] : [.. await shokoImportService.PurgeMissingFilesAsync(DryRun, CancellationToken).ConfigureAwait(false)];
                Progress.Report(50);

                var plexRemoved = new List<string>();
                var plexMessages = new List<string>();
                int effectiveThreshold = Threshold ?? Settings.Advanced.EmptyPlexTrashThreshold;

                if (effectiveThreshold > 0 && plexClient.IsEnabled)
                    foreach (var target in plexClient.GetConfiguredTargets())
                    {
                        var (_, trashed, message) = await plexClient.EmptyTrashWithSafetyAsync(target, effectiveThreshold, DryRun, CancellationToken).ConfigureAwait(false);
                        plexMessages.Add($"[{target.Title}] {message}");
                        if (trashed.Count > 0)
                            plexRemoved.AddRange(trashed.Select(item => $"[{target.Title}] {item}"));
                    }
                else if (TrashOnly)
                    plexMessages.Add("Empty Plex Trash is disabled. Configure a threshold under Provider Settings > Advanced or pass 'threshold=N'.");

                return new ShokoController.PurgeMissingResult(DryRun, TrashOnly, removed.Count, removed, plexRemoved, plexMessages);
            },
            semaphore: VfsShared.VfsLock
        );
}

#endregion

#region Source Links

/// <summary>Queue job creating or purging relative source symlinks across managed folders.</summary>
[DatabaseRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class SourceLinksJob(SourceLinkService sourceLinkService, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<SourceLinksJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Relative path to the source symlink mapping file.</summary>
    [JobKeyMember("mapFile", 0)]
    public string MapFile { get; set; } = string.Empty;

    /// <summary>Whether to purge symlinks and <c>_attach</c> folders instead of creating links.</summary>
    [JobKeyMember("purge", 1)]
    public bool PurgeLinks { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Source Links";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => PurgeLinks ? $"{ShokoRelayConstants.Name}: Purge Source Symlinks" : $"{ShokoRelayConstants.Name}: Map Source Symlinks ({Path.GetFileName(MapFile)})";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskMapSymlinks,
            $"{ShokoRelayConstants.TaskMapSymlinks}.report",
            ReportHelper.BuildSourceLinkReport,
            () => sourceLinkService.ProcessLinksAsync(MapFile, PurgeLinks, CancellationToken),
            semaphore: VfsShared.VfsLock
        );
}

#endregion
