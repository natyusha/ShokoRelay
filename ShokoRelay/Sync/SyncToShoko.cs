using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.User.Update;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state from Plex into Shoko.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="userDataService">Shoko user data service.</param>
/// <param name="userService">Shoko user service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="plexAuth">Plex authentication service.</param>
/// <param name="logger">Logger instance.</param>
public class SyncToShoko(
    PlexClient plexClient,
    IMetadataService metadataService,
    IVideoService videoService,
    IUserDataService userDataService,
    IUserService userService,
    ConfigProvider configProvider,
    PlexAuth plexAuth,
    ILogger<SyncToShoko> logger
)
{
    #region Synchronization Logic

    /// <summary>Sync watched-state from Plex into Shoko database.</summary>
    /// <param name="dryRun">If true, skip database writes.</param>
    /// <param name="sinceHours">Optional window to limit processed items.</param>
    /// <param name="includeVotes">Include user ratings.</param>
    /// <param name="includeProgress">Include playback progress.</param>
    /// <param name="userTypeOverride">Optional override for the sync users configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
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
        OverrideHelper.Reload(metadataService);
        var result = new PlexWatchedSyncResult();
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;

        if (userType == SyncUserType.None)
            return result;

        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualProgress = includeProgress ?? auto.ShokoSyncWatchedIncludeProgress;

        if (!plexClient.IsEnabled || userService.GetUsers().FirstOrDefault() is not { } defaultUser)
            return result;

        var extraEntries = configProvider.GetExtraPlexUserEntries();
        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name)) };
        var appliedIds = new HashSet<int>();

        var targets = plexClient.GetConfiguredTargets().ToList();
        bool isLegacyFallback = false;

        // Apply Library Name Filter and Legacy Fallback
        if (!string.IsNullOrWhiteSpace(libraryName))
        {
            var matchedTarget = targets.FirstOrDefault(t => string.Equals(t.Title, libraryName, StringComparison.OrdinalIgnoreCase));
            if (matchedTarget != null)
                targets = [matchedTarget];
            else
            {
                var fallbackTarget = await plexClient.GetFallbackLibraryTargetAsync(libraryName, cancellationToken).ConfigureAwait(false);
                if (fallbackTarget != null)
                {
                    targets = [fallbackTarget];
                    isLegacyFallback = true;
                    logger.LogInformation("WatchedSyncService: Target library '{LibraryName}' not found in Shoko Relay targets. Initiating legacy filename matching fallback.", libraryName);
                }
                else
                {
                    logger.LogWarning("WatchedSyncService: Target library '{LibraryName}' could not be found on the Plex server.", libraryName);
                    return result;
                }
            }
        }

        // Session-level cache to prevent redundant database lookups and GUID parsing when the same episode exists in multiple libraries or is watched by multiple users.
        var episodeCache = new Dictionary<string, IShokoEpisode?>(StringComparer.OrdinalIgnoreCase);
        var userDataCache = new Dictionary<int, IEpisodeUserData?>();
        var prefIdCache = new Dictionary<int, string?>();

        IShokoEpisode? GetCachedEpisode(PlexMetadataItem item)
        {
            string cacheKey = isLegacyFallback ? (item.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File ?? item.RatingKey!) : (item.Guid ?? item.RatingKey!);
            if (string.IsNullOrWhiteSpace(cacheKey))
                return null;

            if (!episodeCache.TryGetValue(cacheKey, out var ep))
            {
                if (isLegacyFallback)
                {
                    string? filePath = item.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File;
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        string mapped = plexClient.MapPlexPathToShokoPath(filePath);
                        var vid = videoService.GetVideoFileByAbsolutePath(mapped);
                        if (vid == null)
                        {
                            string normPath = VfsShared.NormalizeSeparators(filePath);
                            string fName = Path.GetFileName(normPath);
                            string? parent = Path.GetFileName(Path.GetDirectoryName(normPath));
                            string suffix = !string.IsNullOrEmpty(parent) ? $"{Path.DirectorySeparatorChar}{parent}{Path.DirectorySeparatorChar}{fName}" : $"{Path.DirectorySeparatorChar}{fName}";

                            vid = videoService
                                .GetAllVideoFiles()
                                ?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Path) && VfsShared.NormalizeSeparators(v.Path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                        }
                        ep = vid?.Video?.Episodes?.FirstOrDefault();

                        if (ep == null)
                            logger.LogDebug("WatchedSyncService: Legacy fallback failed to resolve Shoko episode for file '{FilePath}'", filePath);
                    }
                    else
                        logger.LogDebug("WatchedSyncService: Legacy fallback failed because Plex returned no file path for RatingKey {RatingKey}", item.RatingKey);
                }
                else
                    ep = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid) is { } epId ? metadataService.GetShokoEpisodeByID(epId) : null;

                episodeCache[cacheKey] = ep;
            }
            return ep;
        }

        IEpisodeUserData? GetCachedUserData(IShokoEpisode ep)
        {
            if (!userDataCache.TryGetValue(ep.LocalID, out var epUserData))
                userDataCache[ep.LocalID] = epUserData = userDataService.GetEpisodeUserData(ep, defaultUser);
            return epUserData;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fetch user item buckets and automatically handle managed token resolution and user filtering.
            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(plexAuth, plexClient, configProvider, target, userType, extraEntries, false, null, sinceHours, result, logger, cancellationToken)
                .ConfigureAwait(false);
            result = newResult;

            if (actualProgress)
            {
                var (progressBuckets, prResult) = await SyncHelper
                    .FetchUserBucketsAsync(plexAuth, plexClient, configProvider, target, userType, extraEntries, true, true, sinceHours, result, logger, cancellationToken)
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
                // Pre-resolve and filter which episodes will actually be marked watched to group by SeriesID
                var epsToMark = new List<IShokoEpisode>();
                foreach (var item in items)
                {
                    if (item.LibrarySectionId.HasValue && item.LibrarySectionId != target.SectionId)
                        continue;

                    if (GetCachedEpisode(item) is not { } ep || appliedIds.Contains(ep.LocalID))
                        continue;

                    var epUserData = GetCachedUserData(ep);
                    bool alreadyWatched = epUserData?.LastPlayedAt != null;
                    bool isWatchedInPlex = item.ViewCount > 0;

                    if (isWatchedInPlex && !alreadyWatched && (ep.Videos?.Count > 0))
                        epsToMark.Add(ep);
                }

                var epsToMarkGrouped = epsToMark.GroupBy(e => e.ShokoSeriesID).ToDictionary(g => g.Key, g => g.ToList());
                var processedEpsToMarkCount = new Dictionary<int, int>();

                foreach (var item in items)
                {
                    if (item.LibrarySectionId.HasValue && item.LibrarySectionId != target.SectionId)
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    if (!isLegacyFallback && string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    result = SyncHelper.IncProcessed(result, result.PerUser, uName);

                    // Check the session cache before hitting the database.
                    if (GetCachedEpisode(item) is not { } ep || appliedIds.Contains(ep.LocalID))
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    var epUserData = GetCachedUserData(ep);
                    bool alreadyWatched = epUserData?.LastPlayedAt != null;

                    bool isWatchedInPlex = item.ViewCount > 0;
                    bool hasProgressInPlex = item.ViewOffset > 0;

                    bool wouldMark = isWatchedInPlex && !alreadyWatched && (ep.Videos?.Count > 0);
                    bool wouldUpdateProgress = false;

                    if (!isWatchedInPlex && hasProgressInPlex && !alreadyWatched && (ep.Videos?.Count > 0))
                    {
                        wouldUpdateProgress = true;
                        if (userDataService.GetVideoUserData(ep.Videos.First(), defaultUser) is { } existingData && Math.Abs(existingData.ProgressPosition.TotalMilliseconds - item.ViewOffset!.Value) < 5000)
                            wouldUpdateProgress = false;
                    }

                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);
                    var prefId = ep.Series != null ? (prefIdCache.TryGetValue(ep.ShokoSeriesID, out var pId) ? pId : prefIdCache[ep.ShokoSeriesID] = MapHelper.GetPreferredTmdbOrderingId(ep.Series)) : null;
                    var coords = PlexMapping.GetPlexCoordinates(ep, prefId);
                    string typeLabel = PlexHelper.IsMovieKey(item.RatingKey!) ? "movie" : "episode";

                    if (wouldMark)
                    {
                        if (!dryRun)
                        {
                            int currentCount = processedEpsToMarkCount.TryGetValue(ep.ShokoSeriesID, out int count) ? count + 1 : 1;
                            processedEpsToMarkCount[ep.ShokoSeriesID] = currentCount;

                            bool isLastInSeries = epsToMarkGrouped.TryGetValue(ep.ShokoSeriesID, out var list) && currentCount == list.Count;

                            await userDataService
                                .SetEpisodeWatchedStatus(ep, defaultUser, true, watchedAt, videoReason: VideoUserDataSaveReason.UserInteraction, noVideoPropagation: false, updateStatsNow: isLastInSeries)
                                .ConfigureAwait(false);
                        }
                        appliedIds.Add(ep.LocalID);
                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                        logger.LogInformation(
                            "WatchedSyncService: {Prefix}Plex -> Shoko: {User} marked {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey})",
                            logPrefix,
                            uName,
                            typeLabel,
                            ep.Series?.GetDisplayTitle(),
                            ep.ShokoSeriesID,
                            coords.Season,
                            coords.Episode,
                            item.RatingKey
                        );
                    }
                    else if (wouldUpdateProgress)
                    {
                        if (!dryRun)
                        {
                            foreach (var video in ep.Videos!)
                            {
                                var videoData = userDataService.GetVideoUserData(video, defaultUser);
                                var update = videoData != null ? new VideoUserDataUpdate(videoData) : new VideoUserDataUpdate();
                                update.ProgressPosition = TimeSpan.FromMilliseconds(item.ViewOffset!.Value);
                                update.LastUpdatedAt = DateTime.UtcNow;
                                await userDataService.SaveVideoUserData(video, defaultUser, update, VideoUserDataSaveReason.PlaybackProgress).ConfigureAwait(false);
                            }
                        }
                        appliedIds.Add(ep.LocalID);
                        result = SyncHelper.IncProgressUpdated(result, result.PerUser, uName);
                        logger.LogInformation(
                            "WatchedSyncService: {Prefix}Plex -> Shoko: {User} updated progress for {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey}) to {Offset}",
                            logPrefix,
                            uName,
                            typeLabel,
                            ep.Series?.GetDisplayTitle(),
                            ep.ShokoSeriesID,
                            coords.Season,
                            coords.Episode,
                            item.RatingKey,
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
                            libraryName: target.Title,
                            ep.LocalID,
                            $"{ep.Series?.GetDisplayTitle()} [{ep.ShokoSeriesID}]",
                            item.ParentIndex ?? 0,
                            item.Index ?? 0,
                            item.RatingKey,
                            item.Guid ?? (target.LibraryType == PlexLibraryType.Movie ? ep.GetPlexMovieGuid() : ep.GetPlexGuid()),
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
                                await userDataService.RateEpisode(ep, defaultUser, item.UserRating.Value).ConfigureAwait(false);
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
