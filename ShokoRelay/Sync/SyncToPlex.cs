using System.Globalization;
using System.Net;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Utilities;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state (and optional votes) from Shoko -> Plex.</summary>
/// <param name="plexClient">Plex client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="userDataService">Shoko user data service.</param>
/// <param name="userService">Shoko user service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="plexAuth">Plex authentication service.</param>
/// <param name="logger">Logger instance.</param>
public class SyncToPlex(
    PlexClient plexClient,
    IMetadataService metadataService,
    IUserDataService userDataService,
    IUserService userService,
    ConfigProvider configProvider,
    PlexAuth plexAuth,
    ILogger<SyncToPlex> logger
)
{
    #region Synchronization Logic

    /// <summary>Sync watched-state from Shoko into configured Plex libraries.</summary>
    /// <param name="dryRun">If true, skip scrobble execution.</param>
    /// <param name="sinceHours">Optional window to limit processed items.</param>
    /// <param name="includeVotes">Include user ratings.</param>
    /// <param name="includeProgress">Include playback progress.</param>
    /// <param name="userTypeOverride">Optional override for the sync users configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>Execution result.</returns>
    public async Task<PlexWatchedSyncResult> SyncWatchedAsync(
        bool dryRun,
        int? sinceHours,
        bool? includeVotes = null,
        bool? includeProgress = null,
        SyncUserType? userTypeOverride = null,
        string? libraryName = null,
        CancellationToken cancellationToken = default,
        IProgress<decimal>? progress = null
    )
    {
        progress?.Report(0);
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;
        var (syncContext, earlyResult) = await SyncHelper.InitializeSyncRunAsync("Plex<-Shoko", dryRun, userType, plexClient, userService, configProvider, plexAuth, cancellationToken).ConfigureAwait(false);
        if (syncContext == null)
            return earlyResult;

        var (result, defaultUser, includeAdmin, adminShokoUser, extraUsers, logPrefix) = syncContext;
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualProgress = includeProgress ?? auto.ShokoSyncWatchedIncludeProgress;

        var shokoWatchedByUser = new Dictionary<int, List<(IEpisodeUserData UserData, IShokoEpisode Episode)>>();
        var shokoUserLookup = new Dictionary<string, IUser?>(StringComparer.OrdinalIgnoreCase);
        var tokenCache = new SyncHelper.PlexUserTokenCache();

        List<(IEpisodeUserData UserData, IShokoEpisode Episode)> GetShokoWatchedForUser(IUser shokoUser)
        {
            if (shokoWatchedByUser.TryGetValue(shokoUser.LocalID, out var cached))
                return cached;

            var query = userDataService.GetEpisodeUserDataForUser(shokoUser).Where(entry => entry.IsWatched || (actualVotes && entry.HasUserRating));
            if (sinceHours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-sinceHours.Value);
                query = query.Where(entry => actualVotes ? entry.LastUpdatedAt >= cutoff || entry.LastPlayedAt >= cutoff : entry.LastPlayedAt >= cutoff || (entry.IsWatched && entry.LastUpdatedAt >= cutoff));
            }

            return shokoWatchedByUser[shokoUser.LocalID] = [
                .. query
                    .Select(entry => (UserData: entry, Episode: metadataService.GetShokoEpisodeByID(entry.EpisodeID)))
                    .Where(item => item.Episode != null)
                    .Select(item => (item.UserData, Episode: item.Episode!)),
            ];
        }

        var matchedGlobal = new HashSet<(string PlexUser, int EpisodeId)>();
        var activeSourceEpisodes = new HashSet<(string PlexUser, int EpisodeId)>();
        var preferredOrderingIdCache = new Dictionary<int, string?>();
        var targets = plexClient.GetConfiguredTargets();
        if (targets.Count == 0)
            return syncContext.Result;

        var targetStages = progress.InStages([.. Enumerable.Repeat(1m, Math.Max(1, targets.Count))]);
        targetStages.Report(0);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply Library Name Filter
            if (!string.IsNullOrWhiteSpace(libraryName) && !string.Equals(target.Title, libraryName, StringComparison.OrdinalIgnoreCase))
            {
                targetStages.NextStage();
                continue;
            }

            // Keep onlyUnwatched=true when rating sync is disabled so Plex queries remain fast; only fetch already-watched items when ratings must be synced to them
            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(
                    plexAuth,
                    plexClient,
                    configProvider,
                    target,
                    includeAdmin,
                    adminShokoUser,
                    extraUsers,
                    tokenCache,
                    actualVotes ? null : true,
                    null,
                    null,
                    result,
                    logger,
                    cancellationToken
                )
                .ConfigureAwait(false);
            result = newResult;

            var bucketStages = targetStages.InStages([.. Enumerable.Repeat(1m, Math.Max(1, userBuckets.Count))]);
            bucketStages.Report(0);

            foreach (var bucket in userBuckets)
            {
                string userName = bucket.Name;
                var items = bucket.Items;
                string? userToken = bucket.Token;

                if (SyncHelper.ResolveTargetShokoUser(userService, defaultUser, bucket.ShokoUsername, userName, shokoUserLookup, ref result, logger) is not { } targetShokoUser)
                {
                    bucketStages.NextStage();
                    continue;
                }

                string? mappedLabel = string.IsNullOrWhiteSpace(bucket.ShokoUsername) ? null : targetShokoUser.Username;
                var userWatched = GetShokoWatchedForUser(targetShokoUser);
                var watchedInShokoIds = new HashSet<int>();
                var activeUserEpisodeCount = userWatched.Count;
                foreach (var (userData, _) in userWatched)
                {
                    activeSourceEpisodes.Add((userName, userData.EpisodeID));
                    if (userData.IsWatched)
                        watchedInShokoIds.Add(userData.EpisodeID);
                }

                var plexMap = new Dictionary<int, List<PlexMetadataItem>>();
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.RatingKey))
                        continue;

                    var plexEpisodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                    if (plexEpisodeId.HasValue)
                    {
                        if (!plexMap.TryGetValue(plexEpisodeId.Value, out var list))
                            plexMap[plexEpisodeId.Value] = list = [];
                        list.Add(item);
                    }
                }

                var itemProgress = bucketStages.Throttle().ForItems(Math.Max(1, userWatched.Count + (actualProgress ? plexMap.Count : 0)));
                itemProgress.Report(0);

                var candidateSkippedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (userData, episode) in userWatched)
                {
                    try
                    {
                        if (!plexMap.TryGetValue(episode.LocalID, out var plexItems))
                            continue;

                        matchedGlobal.Add((userName, episode.LocalID));

                        foreach (var plexItem in plexItems.DistinctBy(item => item.RatingKey))
                        {
                            bool isWatchedInPlex = plexItem.ViewCount > 0;
                            bool shouldScrobble = userData.IsWatched && !isWatchedInPlex;
                            bool scrobbled = false;

                            if (shouldScrobble)
                            {
                                if (!dryRun)
                                {
                                    using var request = plexClient.CreateRequest(HttpMethod.Put, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}", target.ServerUrl, userToken);
                                    using var response = await plexClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                                    if (!response.IsSuccessStatusCode)
                                    {
                                        result = SyncHelper.RecordError(result, result.PerUser, userName, $"Failed to scrobble RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                        continue;
                                    }
                                }
                                scrobbled = true;
                            }

                            string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;

                            var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                            string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movie" : "episode";

                            if (scrobbled)
                            {
                                result = SyncHelper.IncMarkedWatched(result, result.PerUser, userName);
                                logger.LogInformation(
                                    "WatchedSyncService: {Prefix}Plex <- Shoko: {User} marked {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey}) on {Server}",
                                    logPrefix,
                                    userName,
                                    typeLabel,
                                    episode.Series?.GetDisplayTitle(),
                                    episode.ShokoSeriesID,
                                    coords.Season,
                                    coords.Episode,
                                    plexItem.RatingKey,
                                    target.ServerName
                                );
                                SyncHelper.AddPerUserChange(
                                    result.PerUserChanges,
                                    userName,
                                    SyncHelper.MakeChange(
                                        userName,
                                        shokoUser: mappedLabel,
                                        libraryName: target.Title,
                                        episode.LocalID,
                                        $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}]",
                                        plexItem.ParentIndex ?? episode.SeasonNumber,
                                        plexItem.Index ?? episode.EpisodeNumber,
                                        plexItem.RatingKey,
                                        plexItem.Guid ?? (target.LibraryType == PlexLibraryType.Movie ? episode.GetPlexMovieGuid() : episode.GetPlexGuid()),
                                        null,
                                        userData.LastPlayedAt,
                                        true,
                                        true,
                                        plexUserRating: userData.UserRating
                                    )
                                );
                            }

                            bool rated = false;
                            if (actualVotes && userData.HasUserRating)
                            {
                                result = SyncHelper.IncVotesFound(result);
                                bool ratingMatches = plexItem.UserRating.HasValue && Math.Abs(plexItem.UserRating.Value - userData.UserRating.Value) <= 0.05;
                                if (ratingMatches)
                                    result = SyncHelper.IncVotesSkipped(result);
                                else
                                {
                                    bool rateFailed = false;
                                    if (!dryRun)
                                    {
                                        using var rateRequest = plexClient.CreateRequest(
                                            HttpMethod.Put,
                                            $"/:/rate?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}&rating={userData.UserRating.Value.ToString(CultureInfo.InvariantCulture)}",
                                            target.ServerUrl,
                                            userToken
                                        );
                                        using var rateResponse = await plexClient.SendAsync(rateRequest, cancellationToken).ConfigureAwait(false);
                                        if (!rateResponse.IsSuccessStatusCode)
                                        {
                                            rateFailed = true;
                                            if (bucket.IsSharedFriend && rateResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                                                logger.LogDebug("WatchedSyncService: Plex shared server token for '{User}' cannot apply personal ratings (HTTP {Status})", userName, rateResponse.StatusCode);
                                            else
                                                result = SyncHelper.RecordError(result, result.PerUser, userName, $"Failed to rate RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                        }
                                    }

                                    if (!rateFailed)
                                    {
                                        rated = true;
                                        result = SyncHelper.IncVotesUpdated(result);
                                        if (!scrobbled)
                                        {
                                            logger.LogInformation(
                                                "WatchedSyncService: {Prefix}Plex <- Shoko: {User} rated {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} ({Rating:F1}) (RatingKey: {RatingKey}) on {Server}",
                                                logPrefix,
                                                userName,
                                                typeLabel,
                                                episode.Series?.GetDisplayTitle(),
                                                episode.ShokoSeriesID,
                                                coords.Season,
                                                coords.Episode,
                                                userData.UserRating.Value,
                                                plexItem.RatingKey,
                                                target.ServerName
                                            );
                                            SyncHelper.AddPerUserChange(
                                                result.PerUserChanges,
                                                userName,
                                                SyncHelper.MakeChange(
                                                    userName,
                                                    shokoUser: mappedLabel,
                                                    libraryName: target.Title,
                                                    episode.LocalID,
                                                    $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}]",
                                                    plexItem.ParentIndex ?? episode.SeasonNumber,
                                                    plexItem.Index ?? episode.EpisodeNumber,
                                                    plexItem.RatingKey,
                                                    plexItem.Guid ?? (target.LibraryType == PlexLibraryType.Movie ? episode.GetPlexMovieGuid() : episode.GetPlexGuid()),
                                                    null,
                                                    userData.LastPlayedAt,
                                                    true,
                                                    userData.IsWatched,
                                                    reason: "rating_updated",
                                                    plexUserRating: userData.UserRating
                                                )
                                            );
                                        }
                                    }
                                }
                            }

                            if (!scrobbled && !rated && !string.IsNullOrEmpty(plexItem.RatingKey))
                                candidateSkippedKeys.Add(plexItem.RatingKey);
                        }
                    }
                    finally
                    {
                        itemProgress.Increment();
                    }
                }

                if (actualProgress)
                    foreach (var (episodeId, plexItems) in plexMap)
                    {
                        try
                        {
                            if (watchedInShokoIds.Contains(episodeId))
                                continue;

                            var unwatchedItems = plexItems.DistinctBy(item => item.RatingKey).Where(item => !(item.ViewCount > 0)).ToList();
                            if (unwatchedItems.Count == 0 || metadataService.GetShokoEpisodeByID(episodeId) is not { Videos.Count: > 0 } episode)
                                continue;

                            long shokoProgressMs = 0;
                            foreach (var video in episode.Videos)
                                if (userDataService.GetVideoUserData(video, targetShokoUser) is { } videoData && (long)videoData.ProgressPosition.TotalMilliseconds > shokoProgressMs)
                                    shokoProgressMs = (long)videoData.ProgressPosition.TotalMilliseconds;

                            if (shokoProgressMs < 5000)
                                continue;

                            if (activeSourceEpisodes.Add((userName, episode.LocalID)))
                                activeUserEpisodeCount++;
                            matchedGlobal.Add((userName, episode.LocalID));

                            string? preferredOrderingId = episode.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series, preferredOrderingIdCache) : null;
                            var coords = PlexMapping.GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                            string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movie" : "episode";

                            foreach (var plexItem in unwatchedItems)
                            {
                                long durationMs = plexItem.Duration ?? (long)episode.Runtime.TotalMilliseconds;
                                if (durationMs <= 0 || shokoProgressMs >= durationMs || Math.Abs(shokoProgressMs - (plexItem.ViewOffset ?? 0)) < 5000)
                                {
                                    if (!string.IsNullOrEmpty(plexItem.RatingKey))
                                        candidateSkippedKeys.Add(plexItem.RatingKey);
                                    continue;
                                }

                                if (!dryRun)
                                {
                                    string timelinePath =
                                        $"/:/timeline?ratingKey={plexItem.RatingKey}&key={Uri.EscapeDataString($"/library/metadata/{plexItem.RatingKey}")}&state=stopped&time={shokoProgressMs}&duration={durationMs}";
                                    using var timelineRequest = plexClient.CreateRequest(HttpMethod.Post, timelinePath, target.ServerUrl, userToken);
                                    using var timelineResponse = await plexClient.SendAsync(timelineRequest, cancellationToken).ConfigureAwait(false);
                                    if (!timelineResponse.IsSuccessStatusCode)
                                    {
                                        if (bucket.IsSharedFriend && timelineResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                                            logger.LogDebug("WatchedSyncService: Plex shared server token for '{User}' cannot update playback progress (HTTP {Status})", userName, timelineResponse.StatusCode);
                                        else
                                            result = SyncHelper.RecordError(result, result.PerUser, userName, $"Failed to update progress for RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                        continue;
                                    }
                                }

                                if (!string.IsNullOrEmpty(plexItem.RatingKey))
                                    candidateSkippedKeys.Remove(plexItem.RatingKey);
                                result = SyncHelper.IncProgressUpdated(result, result.PerUser, userName);
                                logger.LogInformation(
                                    "WatchedSyncService: {Prefix}Plex <- Shoko: {User} updated progress for {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} to {Offset} (RatingKey: {RatingKey}) on {Server}",
                                    logPrefix,
                                    userName,
                                    typeLabel,
                                    episode.Series?.GetDisplayTitle(),
                                    episode.ShokoSeriesID,
                                    coords.Season,
                                    coords.Episode,
                                    TimeSpan.FromMilliseconds(shokoProgressMs),
                                    plexItem.RatingKey,
                                    target.ServerName
                                );
                                SyncHelper.AddPerUserChange(
                                    result.PerUserChanges,
                                    userName,
                                    SyncHelper.MakeChange(
                                        userName,
                                        shokoUser: mappedLabel,
                                        libraryName: target.Title,
                                        episode.LocalID,
                                        $"{episode.Series?.GetDisplayTitle()} [{episode.ShokoSeriesID}]",
                                        plexItem.ParentIndex ?? episode.SeasonNumber,
                                        plexItem.Index ?? episode.EpisodeNumber,
                                        plexItem.RatingKey,
                                        plexItem.Guid ?? (target.LibraryType == PlexLibraryType.Movie ? episode.GetPlexMovieGuid() : episode.GetPlexGuid()),
                                        null,
                                        null,
                                        true,
                                        false,
                                        reason: "progress_updated"
                                    )
                                );
                            }
                        }
                        finally
                        {
                            itemProgress.Increment();
                        }
                    }

                result.PerUser[userName] = result.PerUser[userName] with { Processed = Math.Max(result.PerUser[userName].Processed, activeUserEpisodeCount) };
                foreach (var _ in candidateSkippedKeys)
                    result = SyncHelper.IncSkipped(result, result.PerUser, userName);

                bucketStages.NextStage();
            }
            bucketStages.Complete();
            targetStages.NextStage();
        }
        targetStages.Complete();
        foreach (var (plexUser, _) in activeSourceEpisodes.Where(key => !matchedGlobal.Contains(key)))
            result = SyncHelper.IncSkipped(result, result.PerUser, plexUser);

        return result with
        {
            Processed = activeSourceEpisodes.Count,
        };
    }

    #endregion
}
