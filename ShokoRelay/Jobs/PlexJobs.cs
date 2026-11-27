using Newtonsoft.Json;
using Shoko.QueueProcessor.Acquisition.Attributes;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;
using Shoko.QueueProcessor.Workers;
using ShokoRelay.Controllers;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Jobs;

#region Discovery

/// <summary>Queue job refreshing discovered Plex servers and Shoko Relay libraries.</summary>
[NetworkRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class PlexDiscoveryJob(PlexAuth plexAuth, ConfigProvider configProvider, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<PlexDiscoveryJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Plex Discovery";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => $"{ShokoRelayConstants.Name}: Refresh Plex Libraries";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskPlexAuthRefresh,
            $"{ShokoRelayConstants.TaskPlexAuthRefresh}.report",
            ReportHelper.BuildDiscoveryReport,
            async () =>
            {
                await configProvider.RefreshAdminUsername(plexAuth, CancellationToken).ConfigureAwait(false);
                Progress.Report(40);
                var discovery = await plexAuth.DiscoverShokoLibrariesAsync(configProvider.GetPlexToken(), configProvider.GetPlexClientIdentifier(), CancellationToken).ConfigureAwait(false);
                PlexAuth.PersistDiscoveryResults(configProvider, discovery);
                return new PlexDiscoveryResult(PlexAuth.CollectDiscoveredLibraries(discovery.ShokoLibraries), configProvider.GetSettings().Advanced.PlexServerUrl);
            },
            semaphore: SyncHelper.SyncLock
        );
}

#endregion

#region Plex Automations

/// <summary>Queue job creating and updating Plex collections and collection artwork.</summary>
[DatabaseRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class PlexCollectionsJob(
    CollectionService collectionService,
    IMetadataService metadataService,
    IJobCancellationAccessor cancellation,
    IJobProgressAccessor progress,
    ILogger<PlexCollectionsJob> logger
) : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Comma-separated primary Shoko series IDs, or empty for all series.</summary>
    [JobKeyIgnore]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Compact dedup key segment representing <see cref="Filter"/>.</summary>
    [JobKeyMember("filter", 0)]
    public string FilterKey { get; set; } = "all";

    /// <summary>Whether to update collection item assignments.</summary>
    [JobKeyMember("assign", 1)]
    public bool Assignment { get; set; } = true;

    /// <summary>Whether to prune stale cached Plex collection artwork.</summary>
    [JobKeyMember("clean", 2)]
    public bool Clean { get; set; } = true;

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Build Plex Collections";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => string.IsNullOrWhiteSpace(Filter) ? $"{ShokoRelayConstants.Name}: Build Plex Collections" : $"{ShokoRelayConstants.Name}: Build Plex Collections ({FilterKey})";

    /// <inheritdoc/>
    public override Task Process()
    {
        var filterIds = ParsePrimaryFilterIds(Filter);
        var seriesList = filterIds.Count > 0 ? filterIds.Select(metadataService.GetShokoSeriesByID).ToList() : null;
        return ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskPlexCollectionsBuild,
            $"{ShokoRelayConstants.TaskPlexCollectionsBuild}.report",
            ReportHelper.BuildCollectionsReport,
            () => collectionService.BuildCollectionsAsync(seriesList, Assignment, Clean, CancellationToken, Progress),
            semaphore: SyncHelper.SyncLock
        );
    }
}

/// <summary>Queue job applying configured AniDB or TMDB critic ratings to Plex libraries.</summary>
[DatabaseRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class PlexRatingsJob(CriticRatingService criticRatingService, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<PlexRatingsJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Comma-separated primary Shoko series IDs, or empty for all series.</summary>
    [JobKeyIgnore]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Compact dedup key segment representing <see cref="Filter"/>.</summary>
    [JobKeyMember("filter", 0)]
    public string FilterKey { get; set; } = "all";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Apply Critic Ratings";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => string.IsNullOrWhiteSpace(Filter) ? $"{ShokoRelayConstants.Name}: Apply Plex Critic Ratings" : $"{ShokoRelayConstants.Name}: Apply Plex Critic Ratings ({FilterKey})";

    /// <inheritdoc/>
    public override Task Process()
    {
        var filterIds = ParsePrimaryFilterIds(Filter);
        return ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskPlexRatingsApply,
            $"{ShokoRelayConstants.TaskPlexRatingsApply}.report",
            ReportHelper.BuildRatingsReport,
            () => criticRatingService.ApplyRatingsAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken, Progress),
            semaphore: SyncHelper.SyncLock
        );
    }
}

/// <summary>Queue job synchronizing unique local artwork and Plex thumbnails back to Shoko.</summary>
[DatabaseRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class PlexImagesSyncJob(ImageSyncService imageSyncService, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<PlexImagesSyncJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Comma-separated primary Shoko series IDs, or empty for all series.</summary>
    [JobKeyIgnore]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Compact dedup key segment representing <see cref="Filter"/>.</summary>
    [JobKeyMember("filter", 0)]
    public string FilterKey { get; set; } = "all";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Sync Plex Images";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => string.IsNullOrWhiteSpace(Filter) ? $"{ShokoRelayConstants.Name}: Sync Plex Images" : $"{ShokoRelayConstants.Name}: Sync Plex Images ({FilterKey})";

    /// <inheritdoc/>
    public override Task Process()
    {
        var filterIds = ParsePrimaryFilterIds(Filter);
        return ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskPlexImagesSync,
            $"{ShokoRelayConstants.TaskPlexImagesSync}.report",
            ReportHelper.BuildImageSyncReport,
            () => imageSyncService.SyncImagesAsync(filterIds.Count > 0 ? filterIds : null, CancellationToken, Progress),
            semaphore: SyncHelper.SyncLock
        );
    }
}

/// <summary>Queue job running Collections, Critic Ratings, Image Sync, and safe Trash emptying sequentially.</summary>
[DatabaseRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class PlexAutomationJob(
    PlexAutomationService plexAutomationService,
    ConfigProvider configProvider,
    IJobCancellationAccessor cancellation,
    IJobProgressAccessor progress,
    ILogger<PlexAutomationJob> logger
) : RelayJobBase(cancellation, progress, logger)
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Plex Automation";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => $"{ShokoRelayConstants.Name}: Run Plex Automation";

    /// <inheritdoc/>
    public override Task Process()
    {
        string resolvedApiBase = string.IsNullOrWhiteSpace(ApiBase) ? $"{configProvider.ServerBaseUrl}{ShokoRelayConstants.BasePath}" : ApiBase;
        return ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskPlexAutomationRun,
            $"{ShokoRelayConstants.TaskPlexAutomationRun}.report",
            (builder, report) => ReportHelper.BuildPlexAutomationReport(builder, report, resolvedApiBase),
            () => plexAutomationService.RunAutomationAsync(Progress, CancellationToken),
            semaphore: SyncHelper.SyncLock
        );
    }
}

#endregion

#region Watch Sync

/// <summary>Queue job synchronizing watched states, progress, and ratings between Plex and Shoko.</summary>
[DatabaseRequired]
[LimitConcurrency(1)]
[DisallowConcurrencyGroup("ShokoRelay_Plex")]
[JobKeyGroup("ShokoRelay")]
public sealed class WatchSyncJob(SyncToShoko syncToShoko, SyncToPlex syncToPlex, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<WatchSyncJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Whether to preview changes without modifying Shoko or Plex.</summary>
    [JobKeyMember("dryRun", 0)]
    public bool DryRun { get; set; } = true;

    /// <summary>Optional lookback window in hours.</summary>
    [JobKeyMember("since", 1)]
    public int? SinceHours { get; set; }

    /// <summary>Optional override for rating synchronization.</summary>
    [JobKeyMember("ratings", 2)]
    public bool? Ratings { get; set; }

    /// <summary>Optional override for playback progress synchronization.</summary>
    [JobKeyMember("progress", 3)]
    public bool? SyncProgress { get; set; }

    /// <summary>Direction: <c>true</c> for Shoko to Plex; <c>false</c> for Plex to Shoko.</summary>
    [JobKeyMember("import", 4)]
    public bool Import { get; set; }

    /// <summary>Optional override for the target user group.</summary>
    [JobKeyMember("users", 5)]
    public SyncUserType? Users { get; set; }

    /// <summary>Optional Plex library title filter.</summary>
    [JobKeyMember("library", 6)]
    public string? LibraryName { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => $"{ShokoRelayConstants.Name}: Watch Sync";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => $"{ShokoRelayConstants.Name}: {(Import ? "Watch Sync (Shoko -> Plex)" : "Watch Sync (Plex -> Shoko)")}{(DryRun ? " [Dry Run]" : "")}";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskShokoSyncWatched,
            $"{ShokoRelayConstants.TaskShokoSyncWatched}.report",
            (builder, report) => ReportHelper.BuildSyncWatchedReport(builder, report, report.Direction, report.DryRun, Ratings ?? Settings.Automation.ShokoSyncWatchedIncludeRatings),
            async () =>
            {
                var result = Import
                    ? await syncToPlex.SyncWatchedAsync(DryRun, SinceHours, Ratings, SyncProgress, Users, LibraryName, CancellationToken, Progress).ConfigureAwait(false)
                    : await syncToShoko.SyncWatchedAsync(DryRun, SinceHours, Ratings, SyncProgress, Users, LibraryName, CancellationToken, Progress).ConfigureAwait(false);
                return result with { Direction = Import ? "Plex<-Shoko" : "Plex->Shoko" };
            },
            semaphore: SyncHelper.SyncLock
        );
}

#endregion
