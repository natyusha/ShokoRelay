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

        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraUsers.Select(e => e.Name), includeAdmin) };
        var appliedWatchedIds = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var appliedProgressIds = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var tokenCache = new SyncHelper.PlexUserTokenCache();
        var shokoUserLookup = new Dictionary<string, IUser?>(StringComparer.OrdinalIgnoreCase);

        var targets = plexClient.GetConfiguredTargets().ToList();
        bool isLegacyFallback = false;

        // Apply Library Name Filter
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
        var userDataCache = new Dictionary<(int ShokoUserId, int EpisodeId), IEpisodeUserData?>();
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
                bool progressIncludeAdmin = includeAdmin && userBuckets.Any(b => string.Equals(b.Name, "admin", StringComparison.OrdinalIgnoreCase));
                var progressExtraUsers = extraUsers.Where(e => userBuckets.Any(b => string.Equals(b.Name, e.Name, StringComparison.OrdinalIgnoreCase))).ToList();

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

                    foreach (var pb in progressBuckets)
                    {
                        var existingIndex = userBuckets.FindIndex(b => string.Equals(b.Name, pb.Name, StringComparison.OrdinalIgnoreCase));
                        if (existingIndex >= 0)
                            userBuckets[existingIndex].Items.AddRange(pb.Items);
                        else
                            userBuckets.Add(pb);
                    }
                }
            }

            foreach (var bucket in userBuckets)
            {
                string uName = bucket.Name;
                var items = bucket.Items;
                if (ResolveTargetShokoUser(bucket.ShokoUsername, uName) is not { } targetShokoUser)
                    continue;

                string? mappedLabel = string.IsNullOrWhiteSpace(bucket.ShokoUsername) ? null : targetShokoUser.Username;
                var seriesToUpdateStats = new Dictionary<int, IShokoSeries>();

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

                    if (GetCachedEpisode(item) is not { } ep || appliedWatchedIds.Contains((targetShokoUser.LocalID, ep.LocalID)))
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    var epUserData = GetCachedUserData(ep, targetShokoUser);
                    bool alreadyWatched = epUserData?.IsWatched == true;

                    bool isWatchedInPlex = item.ViewCount > 0;
                    bool hasProgressInPlex = item.ViewOffset > 0;

                    bool wouldMark = isWatchedInPlex && !alreadyWatched && (ep.Videos?.Count > 0);
                    bool wouldUpdateProgress = false;

                    if (!isWatchedInPlex && hasProgressInPlex && !alreadyWatched && !appliedProgressIds.Contains((targetShokoUser.LocalID, ep.LocalID)) && ep.Videos?.Count > 0)
                        wouldUpdateProgress =
                            userDataService.GetVideoUserData(ep.Videos.First(), targetShokoUser) is not { } existingData
                            || Math.Abs(existingData.ProgressPosition.TotalMilliseconds - item.ViewOffset!.Value) >= 5000;

                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);

                    string? prefId = null;
                    if (ep.Series != null && !prefIdCache.TryGetValue(ep.ShokoSeriesID, out prefId))
                        prefIdCache[ep.ShokoSeriesID] = prefId = MapHelper.GetPreferredTmdbOrderingId(ep.Series);

                    var coords = PlexMapping.GetPlexCoordinates(ep, prefId);
                    string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movie" : "episode";

                    if (wouldMark)
                    {
                        if (!dryRun)
                        {
                            await userDataService
                                .SetEpisodeWatchedStatus(ep, targetShokoUser, true, watchedAt, videoReason: VideoUserDataSaveReason.UserInteraction, noVideoPropagation: false, updateStatsNow: false)
                                .ConfigureAwait(false);

                            if (ep.Series is { } parentSeries)
                                seriesToUpdateStats.TryAdd(parentSeries.LocalID, parentSeries);
                        }
                        appliedWatchedIds.Add((targetShokoUser.LocalID, ep.LocalID));
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
                            var update = new VideoUserDataUpdate { ProgressPosition = TimeSpan.FromMilliseconds(item.ViewOffset!.Value), LastUpdatedAt = DateTime.UtcNow };
                            foreach (var video in ep.Videos!)
                                await userDataService.SaveVideoUserData(video, targetShokoUser, update, VideoUserDataSaveReason.PlaybackProgress).ConfigureAwait(false);
                        }
                        appliedProgressIds.Add((targetShokoUser.LocalID, ep.LocalID));
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
                            shokoUser: mappedLabel,
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
                        double clampedRating = Math.Clamp(item.UserRating.Value, 1.0, 10.0);
                        if (epUserData?.UserRating == null || Math.Abs(epUserData.UserRating.Value - clampedRating) > 0.05)
                        {
                            if (!dryRun)
                                await userDataService.RateEpisode(ep, targetShokoUser, clampedRating).ConfigureAwait(false);
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
