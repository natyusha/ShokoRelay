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
        OverrideHelper.Reload();
        var result = new PlexWatchedSyncResult { Direction = "Plex->Shoko" };
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;

        if (userType == SyncUserType.None)
            return result;

        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualProgress = includeProgress ?? auto.ShokoSyncWatchedIncludeProgress;

        if (!plexClient.IsEnabled || userService.GetUsers().OrderBy(usr => usr.LocalID).FirstOrDefault() is not { } defaultUser)
            return result;

        string? adminUsername = configProvider.GetAdminUsername();
        if (string.IsNullOrWhiteSpace(adminUsername) && configProvider.GetAdvancedUserMappingEntries().Count > 0)
        {
            await configProvider.RefreshAdminUsername(plexAuth, cancellationToken).ConfigureAwait(false);
            adminUsername = configProvider.GetAdminUsername();
        }

        var (includeAdmin, adminShokoUser, extraUsers) = SyncHelper.ResolveActiveSyncUsers(configProvider, userType, adminUsername);
        if (!includeAdmin && extraUsers.Count == 0)
            return result;

        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraUsers.Select(extraUser => extraUser.Name), includeAdmin) };
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
        var prefIdCache = new Dictionary<int, string?>();

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
                            string normPath = VfsShared.NormalizeSeparators(filePath);
                            string fileName = Path.GetFileName(normPath);
                            string? parent = Path.GetFileName(Path.GetDirectoryName(normPath));
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

        IUser? ResolveTargetShokoUser(string? mappedUsername, string plexUserName)
        {
            if (string.IsNullOrWhiteSpace(mappedUsername))
                return defaultUser;
            if (!shokoUserLookup.TryGetValue(mappedUsername, out var resolved))
                shokoUserLookup[mappedUsername] = resolved = userService.GetUserByUsername(mappedUsername);
            if (resolved == null)
            {
                string msg = $"Mapped Shoko user '{mappedUsername}' not found for Plex user '{plexUserName}'";
                logger.LogWarning("WatchedSyncService: {Message}", msg);
                result = SyncHelper.RecordError(result, result.PerUser, plexUserName, msg);
            }
            return resolved;
        }

        IEpisodeUserData? GetCachedUserData(IShokoEpisode ep, IUser targetShokoUser)
        {
            var key = (targetShokoUser.LocalID, ep.LocalID);
            if (!userDataCache.TryGetValue(key, out var epUserData))
                userDataCache[key] = epUserData = userDataService.GetEpisodeUserData(ep, targetShokoUser);
            return epUserData;
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
                    var (progressBuckets, prResult) = await SyncHelper
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
                    result = prResult;

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
                if (ResolveTargetShokoUser(bucket.ShokoUsername, userName) is not { } targetShokoUser)
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

                    if (GetCachedEpisode(item) is not { } episode || appliedWatchedIds.Contains((targetShokoUser.LocalID, episode.LocalID)))
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, userName);
                        continue;
                    }

                    var epUserData = GetCachedUserData(episode, targetShokoUser);
                    bool alreadyWatched = epUserData?.IsWatched == true;

                    bool isWatchedInPlex = item.ViewCount > 0;
                    bool hasProgressInPlex = item.ViewOffset > 0;

                    bool wouldMark = isWatchedInPlex && !alreadyWatched && (episode.Videos?.Count > 0);
                    bool wouldUpdateProgress = false;

                    if (!isWatchedInPlex && hasProgressInPlex && !alreadyWatched && !appliedProgressIds.Contains((targetShokoUser.LocalID, episode.LocalID)) && episode.Videos?.Count > 0)
                        wouldUpdateProgress =
                            userDataService.GetVideoUserData(episode.Videos.First(), targetShokoUser) is not { } existingData
                            || Math.Abs(existingData.ProgressPosition.TotalMilliseconds - item.ViewOffset!.Value) >= 5000;

                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);

                    string? prefId = null;
                    if (episode.Series != null && !prefIdCache.TryGetValue(episode.ShokoSeriesID, out prefId))
                        prefIdCache[episode.ShokoSeriesID] = prefId = MapHelper.GetPreferredTmdbOrderingId(episode.Series);

                    var coords = PlexMapping.GetPlexCoordinates(episode, prefId, metadataService: metadataService);
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
                    else
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
                            wouldMark || wouldUpdateProgress,
                            alreadyWatched,
                            wouldUpdateProgress ? "progress_updated" : (wouldMark ? null : (alreadyWatched ? "already_watched" : "no_files"))
                        )
                    );

                    if (actualVotes && item.UserRating.HasValue && appliedRatedIds.Add((targetShokoUser.LocalID, episode.LocalID)))
                    {
                        result = SyncHelper.IncVotesFound(result);
                        double clampedRating = Math.Clamp(item.UserRating.Value, 1.0, 10.0);
                        if (epUserData?.UserRating == null || Math.Abs(epUserData.UserRating.Value - clampedRating) > 0.05)
                        {
                            if (!dryRun)
                                await userDataService.RateEpisode(episode, targetShokoUser, clampedRating).ConfigureAwait(false);
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                        else
                            result = SyncHelper.IncVotesSkipped(result);
                    }
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
