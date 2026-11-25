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
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;
        var (syncContext, earlyResult) = await SyncHelper.InitializeSyncRunAsync("Plex->Shoko", dryRun, userType, plexClient, userService, configProvider, plexAuth, cancellationToken).ConfigureAwait(false);
        if (syncContext == null)
            return earlyResult;

        var (result, defaultUser, includeAdmin, adminShokoUser, extraUsers, logPrefix) = syncContext;
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualProgress = includeProgress ?? auto.ShokoSyncWatchedIncludeProgress;

        var appliedWatchedIds = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var appliedProgressIds = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var appliedRatedIds = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var tokenCache = new SyncHelper.PlexUserTokenCache();
        var shokoUserLookup = new Dictionary<string, IUser?>(StringComparer.OrdinalIgnoreCase);

        var targets = plexClient.GetConfiguredTargets().ToList();
        bool isLegacyFallback = false;

        // Apply Library Name Filter
        if (!string.IsNullOrWhiteSpace(libraryName))
        {
            var matchedTarget = targets.FirstOrDefault(target => string.Equals(target.Title, libraryName, StringComparison.OrdinalIgnoreCase));
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
        var userDataCache = new Dictionary<(int ShokoUserId, int EpisodeId), IEpisodeUserData?>();
        var preferredOrderingIdCache = new Dictionary<int, string?>();

        IShokoEpisode? GetCachedEpisode(PlexMetadataItem item)
        {
            string cacheKey = isLegacyFallback ? (item.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File ?? item.RatingKey!) : (item.Guid ?? item.RatingKey!);
            if (string.IsNullOrWhiteSpace(cacheKey))
                return null;

            if (!episodeCache.TryGetValue(cacheKey, out var episode))
            {
                if (isLegacyFallback)
                {
                    string? filePath = item.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File;
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        string mapped = plexClient.MapPlexPathToShokoPath(filePath);
                        var videoFile = videoService.GetVideoFileByAbsolutePath(mapped);
                        if (videoFile == null)
                        {
                            string normalizedPath = VfsShared.NormalizeSeparators(filePath);
                            string fileName = Path.GetFileName(normalizedPath);
                            string? parent = Path.GetFileName(Path.GetDirectoryName(normalizedPath));
                            string suffix = !string.IsNullOrEmpty(parent) ? $"{Path.DirectorySeparatorChar}{parent}{Path.DirectorySeparatorChar}{fileName}" : $"{Path.DirectorySeparatorChar}{fileName}";

                            videoFile = videoService
                                .GetAllVideoFiles()
                                ?.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Path) && VfsShared.NormalizeSeparators(file.Path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                        }
                        episode = videoFile?.Video?.Episodes?.FirstOrDefault();

                        if (episode == null)
                            logger.LogDebug("WatchedSyncService: Legacy fallback failed to resolve Shoko episode for file '{FilePath}'", filePath);
                    }
                    else
                        logger.LogDebug("WatchedSyncService: Legacy fallback failed because Plex returned no file path for RatingKey {RatingKey}", item.RatingKey);
                }
                else
                    episode = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid) is { } episodeId ? metadataService.GetShokoEpisodeByID(episodeId) : null;

                episodeCache[cacheKey] = episode;
            }
            return episode;
        }

        IEpisodeUserData? GetCachedUserData(IShokoEpisode episode, IUser targetShokoUser)
        {
            var key = (targetShokoUser.LocalID, episode.LocalID);
            if (!userDataCache.TryGetValue(key, out var episodeUserData))
                userDataCache[key] = episodeUserData = userDataService.GetEpisodeUserData(episode, targetShokoUser);
            return episodeUserData;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(plexAuth, plexClient, configProvider, target, includeAdmin, adminShokoUser, extraUsers, tokenCache, false, null, sinceHours, result, logger, cancellationToken)
                .ConfigureAwait(false);
            result = newResult;

            if (actualProgress)
            {
                bool progressIncludeAdmin = includeAdmin && userBuckets.Any(bucket => string.Equals(bucket.Name, "admin", StringComparison.OrdinalIgnoreCase));
                var progressExtraUsers = extraUsers.Where(extraUser => userBuckets.Any(bucket => string.Equals(bucket.Name, extraUser.Name, StringComparison.OrdinalIgnoreCase))).ToList();

                if (progressIncludeAdmin || progressExtraUsers.Count > 0)
                {
                    var (progressBuckets, progressResult) = await SyncHelper
                        .FetchUserBucketsAsync(
                            plexAuth,
                            plexClient,
                            configProvider,
                            target,
                            progressIncludeAdmin,
                            adminShokoUser,
                            progressExtraUsers,
                            tokenCache,
                            true,
                            true,
                            sinceHours,
                            result,
                            logger,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    result = progressResult;

                    foreach (var progressBucket in progressBuckets)
                    {
                        var existingIndex = userBuckets.FindIndex(bucket => string.Equals(bucket.Name, progressBucket.Name, StringComparison.OrdinalIgnoreCase));
                        if (existingIndex >= 0)
                            userBuckets[existingIndex].Items.AddRange(progressBucket.Items);
                        else
                            userBuckets.Add(progressBucket);
                    }
                }
            }

            foreach (var bucket in userBuckets)
            {
                string userName = bucket.Name;
                var items = bucket.Items;
                if (SyncHelper.ResolveTargetShokoUser(userService, defaultUser, bucket.ShokoUsername, userName, shokoUserLookup, ref result, logger) is not { } targetShokoUser)
                    continue;

                string? mappedLabel = string.IsNullOrWhiteSpace(bucket.ShokoUsername) ? null : targetShokoUser.Username;
                var seriesToUpdateStats = new Dictionary<int, IShokoSeries>();

                foreach (var item in items)
                {
                    if (item.LibrarySectionId.HasValue && item.LibrarySectionId != target.SectionId)
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, userName);
                        continue;
                    }

                    if (!isLegacyFallback && string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    result = SyncHelper.IncProcessed(result, result.PerUser, userName);

                    if (GetCachedEpisode(item) is not { } episode)
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, userName);
                        continue;
                    }

                    var episodeUserData = GetCachedUserData(episode, targetShokoUser);
                    bool alreadyWatched = episodeUserData?.IsWatched == true;
                    bool alreadyProcessedWatch = appliedWatchedIds.Contains((targetShokoUser.LocalID, episode.LocalID));

                    bool isWatchedInPlex = item.ViewCount > 0;
                    bool hasProgressInPlex = item.ViewOffset > 0;

                    bool wouldMark = !alreadyProcessedWatch && isWatchedInPlex && !alreadyWatched && (episode.Videos?.Count > 0);
                    bool wouldUpdateProgress = false;

                    if (
                        !alreadyProcessedWatch
                        && !isWatchedInPlex
                        && hasProgressInPlex
                        && !alreadyWatched
                        && !appliedProgressIds.Contains((targetShokoUser.LocalID, episode.LocalID))
                        && episode.Videos?.Count > 0
                    )
                        wouldUpdateProgress =
                            userDataService.GetVideoUserData(episode.Videos.First(), targetShokoUser) is not { } existingData
                            || Math.Abs(existingData.ProgressPosition.TotalMilliseconds - item.ViewOffset!.Value) >= 5000;

                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);

                    string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;

                    var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                    string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movie" : "episode";

                    if (wouldMark)
                    {
                        if (!dryRun)
                        {
                            await userDataService
                                .SetEpisodeWatchedStatus(episode, targetShokoUser, true, watchedAt, videoReason: VideoUserDataSaveReason.UserInteraction, noVideoPropagation: false, updateStatsNow: false)
                                .ConfigureAwait(false);

                            if (episode.Series is { } parentSeries)
                                seriesToUpdateStats.TryAdd(parentSeries.LocalID, parentSeries);
                        }
                        appliedWatchedIds.Add((targetShokoUser.LocalID, episode.LocalID));
                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, userName);
                        logger.LogInformation(
                            "WatchedSyncService: {Prefix}Plex -> Shoko: {User} marked {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey})",
                            logPrefix,
                            userName,
                            typeLabel,
                            episode.Series?.GetDisplayTitle(),
                            episode.ShokoSeriesID,
                            coords.Season,
                            coords.Episode,
                            item.RatingKey
                        );
                    }
                    else if (wouldUpdateProgress)
                    {
                        if (!dryRun)
                        {
                            var update = new VideoUserDataUpdate { ProgressPosition = TimeSpan.FromMilliseconds(item.ViewOffset!.Value), LastUpdatedAt = DateTime.UtcNow };
                            foreach (var video in episode.Videos!)
                                await userDataService.SaveVideoUserData(video, targetShokoUser, update, VideoUserDataSaveReason.PlaybackProgress).ConfigureAwait(false);
                        }
                        appliedProgressIds.Add((targetShokoUser.LocalID, episode.LocalID));
                        result = SyncHelper.IncProgressUpdated(result, result.PerUser, userName);
                        logger.LogInformation(
                            "WatchedSyncService: {Prefix}Plex -> Shoko: {User} updated progress for {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey}) to {Offset}",
                            logPrefix,
                            userName,
                            typeLabel,
                            episode.Series?.GetDisplayTitle(),
                            episode.ShokoSeriesID,
                            coords.Season,
                            coords.Episode,
                            item.RatingKey,
                            TimeSpan.FromMilliseconds(item.ViewOffset!.Value)
                        );
                    }

                    bool rated = false;
                    double? appliedRating = null;
                    if (actualVotes && item.UserRating is > 0 && appliedRatedIds.Add((targetShokoUser.LocalID, episode.LocalID)))
                    {
                        result = SyncHelper.IncVotesFound(result);
                        double clampedRating = Math.Clamp(item.UserRating.Value, 1.0, 10.0);
                        if (episodeUserData?.UserRating == null || Math.Abs(episodeUserData.UserRating.Value - clampedRating) > 0.05)
                        {
                            if (!dryRun)
                                await userDataService.RateEpisode(episode, targetShokoUser, clampedRating).ConfigureAwait(false);
                            rated = true;
                            appliedRating = clampedRating;
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                        else
                            result = SyncHelper.IncVotesSkipped(result);
                    }

                    if (!wouldMark && !wouldUpdateProgress && !rated)
                        result = SyncHelper.IncSkipped(result, result.PerUser, userName);

                    SyncHelper.AddPerUserChange(
                        result.PerUserChanges,
                        userName,
                        SyncHelper.MakeChange(
                            userName,
                            shokoUser: mappedLabel,
                            libraryName: target.Title,
                            episode.LocalID,
                            $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}]",
                            item.ParentIndex ?? 0,
                            item.Index ?? 0,
                            item.RatingKey,
                            item.Guid ?? (target.LibraryType == PlexLibraryType.Movie ? episode.GetPlexMovieGuid() : episode.GetPlexGuid()),
                            null,
                            watchedAt,
                            wouldMark || wouldUpdateProgress || rated,
                            alreadyWatched,
                            wouldUpdateProgress ? "progress_updated"
                                : wouldMark ? null
                                : rated ? "rating_updated"
                                : alreadyWatched ? "already_watched"
                                : "no_files",
                            plexUserRating: appliedRating ?? item.UserRating,
                            shokoUserRating: episodeUserData?.UserRating
                        )
                    );
                }

                if (!dryRun)
                    foreach (var touchedSeries in seriesToUpdateStats.Values)
                        userDataService.UpdateWatchedStats(touchedSeries, targetShokoUser);
            }
        }
        return result;
    }

    #endregion
}
