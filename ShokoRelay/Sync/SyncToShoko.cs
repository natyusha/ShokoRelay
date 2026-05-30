using NLog;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.User.Update;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state from Plex into Shoko.</summary>
public class SyncToShoko(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider, PlexAuth plexAuth)
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private readonly PlexClient _plexClient = plexClient;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IUserDataService _userDataService = userDataService;
    private readonly IUserService _userService = userService;
    private readonly ConfigProvider _configProvider = configProvider;
    private readonly PlexAuth _plexAuth = plexAuth;

    #endregion

    #region Synchronization Logic

    /// <summary>Sync watched-state from Plex into Shoko database.</summary>
    /// <param name="dryRun">If true, skip database writes.</param>
    /// <param name="sinceHours">Optional window to limit processed items.</param>
    /// <param name="includeVotes">Include user ratings.</param>
    /// <param name="includeProgress">Include playback progress.</param>
    /// <param name="userTypeOverride">Optional override for the sync users configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    public async Task<PlexWatchedSyncResult> SyncWatchedAsync(
        bool dryRun,
        int? sinceHours,
        bool? includeVotes = null,
        bool? includeProgress = null,
        SyncUserType? userTypeOverride = null,
        string? libraryName = null,
        CancellationToken cancellationToken = default
    )
    {
        OverrideHelper.Reload(_metadataService);
        var result = new PlexWatchedSyncResult();
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;

        if (userType == SyncUserType.None)
            return result;

        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualProgress = includeProgress ?? auto.ShokoSyncWatchedIncludeProgress;

        if (!_plexClient.IsEnabled || _userService.GetUsers().FirstOrDefault() is not { } defaultUser)
            return result;

        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name)) };
        var appliedIds = new HashSet<int>();
        var targets = _plexClient.GetConfiguredTargets();

        // Session-level cache to prevent redundant database lookups and GUID parsing when the same episode exists in multiple libraries or is watched by multiple users.
        var episodeCache = new Dictionary<string, IShokoEpisode?>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply Library Name Filter
            if (!string.IsNullOrWhiteSpace(libraryName) && !string.Equals(target.Title, libraryName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Fetch user item buckets and automatically handle managed token resolution and user filtering.
            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(_plexAuth, _plexClient, _configProvider, target, userType, extraEntries, false, null, sinceHours, result, cancellationToken)
                .ConfigureAwait(false);
            result = newResult;

            if (actualProgress)
            {
                var (progressBuckets, prResult) = await SyncHelper
                    .FetchUserBucketsAsync(_plexAuth, _plexClient, _configProvider, target, userType, extraEntries, true, true, sinceHours, result, cancellationToken)
                    .ConfigureAwait(false);
                result = prResult;

                foreach (var pb in progressBuckets)
                {
                    var existingIndex = userBuckets.FindIndex(b => b.Name == pb.Name);
                    if (existingIndex >= 0)
                        userBuckets[existingIndex].Items.AddRange(pb.Items);
                    else
                        userBuckets.Add(pb);
                }
            }

            foreach (var (uName, items, _) in userBuckets)
            {
                foreach (var item in items)
                {
                    if (item.LibrarySectionId.HasValue && item.LibrarySectionId != target.SectionId)
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    result = SyncHelper.IncProcessed(result, result.PerUser, uName);

                    // Check the session cache before hitting the database.
                    if (!episodeCache.TryGetValue(item.Guid, out var ep))
                    {
                        var epId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                        ep = epId.HasValue ? _metadataService.GetShokoEpisodeByID(epId.Value) : null;
                        episodeCache[item.Guid] = ep;
                    }

                    if (ep == null || appliedIds.Contains(ep.ID))
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    var epUserData = _userDataService.GetEpisodeUserData(ep, defaultUser);
                    bool alreadyWatched = epUserData?.LastPlayedAt != null;

                    bool isWatchedInPlex = item.ViewCount > 0;
                    bool hasProgressInPlex = item.ViewOffset > 0;

                    bool wouldMark = isWatchedInPlex && !alreadyWatched && (ep.VideoList?.Count > 0);
                    bool wouldUpdateProgress = false;

                    if (!isWatchedInPlex && hasProgressInPlex && !alreadyWatched && (ep.VideoList?.Count > 0))
                    {
                        wouldUpdateProgress = true;
                        var existingData = _userDataService.GetVideoUserData(ep.VideoList.First(), defaultUser);
                        if (existingData != null)
                        {
                            var diff = Math.Abs(existingData.ProgressPosition.TotalMilliseconds - item.ViewOffset!.Value);
                            if (diff < 5000)
                                wouldUpdateProgress = false;
                        }
                    }

                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);

                    if (wouldMark)
                    {
                        if (!dryRun)
                            await _userDataService.SetEpisodeWatchedStatus(ep, defaultUser, true, watchedAt, videoReason: VideoUserDataSaveReason.UserInteraction).ConfigureAwait(false);
                        appliedIds.Add(ep.ID);
                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                        s_logger.Info("WatchedSyncService: {0}Plex -> Shoko: {1} marked {2} S{3}E{4}", logPrefix, uName, ep.Series?.PreferredTitle?.Value, ep.SeasonNumber, ep.EpisodeNumber);
                    }
                    else if (wouldUpdateProgress)
                    {
                        if (!dryRun)
                        {
                            foreach (var video in ep.VideoList!)
                            {
                                var videoData = _userDataService.GetVideoUserData(video, defaultUser);
                                var update = videoData != null ? new VideoUserDataUpdate(videoData) : new VideoUserDataUpdate();
                                update.ProgressPosition = TimeSpan.FromMilliseconds(item.ViewOffset!.Value);
                                update.LastUpdatedAt = DateTime.UtcNow;
                                await _userDataService.SaveVideoUserData(video, defaultUser, update).ConfigureAwait(false);
                            }
                        }
                        appliedIds.Add(ep.ID);
                        result = SyncHelper.IncProgressUpdated(result, result.PerUser, uName);
                        s_logger.Info(
                            "WatchedSyncService: {0}Plex -> Shoko: {1} updated progress {2} S{3}E{4} to {5}",
                            logPrefix,
                            uName,
                            ep.Series?.PreferredTitle?.Value,
                            ep.SeasonNumber,
                            ep.EpisodeNumber,
                            TimeSpan.FromMilliseconds(item.ViewOffset!.Value)
                        );
                    }
                    else
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);

                    SyncHelper.AddPerUserChange(
                        result.PerUserChanges,
                        uName,
                        SyncHelper.MakeChange(
                            uName,
                            ep.ID,
                            ep.Series?.PreferredTitle?.Value,
                            ep.PreferredTitle?.Value,
                            ep.SeasonNumber,
                            ep.EpisodeNumber,
                            item.RatingKey,
                            ep.GetPlexGuid(),
                            null,
                            watchedAt,
                            wouldMark || wouldUpdateProgress,
                            alreadyWatched,
                            wouldUpdateProgress ? "progress_updated" : (wouldMark ? null : (alreadyWatched ? "already_watched" : "no_files"))
                        )
                    );

                    if (actualVotes && item.UserRating.HasValue)
                    {
                        result = SyncHelper.IncVotesFound(result);
                        if (epUserData?.UserRating == null || Math.Abs(epUserData.UserRating.Value - item.UserRating.Value) > 0.05)
                        {
                            if (!dryRun)
                                await _userDataService.RateEpisode(ep, defaultUser, item.UserRating.Value).ConfigureAwait(false);
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                        else
                            result = SyncHelper.IncVotesSkipped(result);
                    }
                }
            }
        }
        return result;
    }

    #endregion
}
