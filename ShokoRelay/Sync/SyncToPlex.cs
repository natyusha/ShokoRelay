using System.Globalization;
using System.Net;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;

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
    /// <param name="userTypeOverride">Optional override for the sync users configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Execution result.</returns>
    public async Task<PlexWatchedSyncResult> SyncWatchedAsync(
        bool dryRun,
        int? sinceHours,
        bool? includeVotes = null,
        SyncUserType? userTypeOverride = null,
        string? libraryName = null,
        CancellationToken cancellationToken = default
    )
    {
        OverrideHelper.Reload();
        var result = new PlexWatchedSyncResult { Direction = "Plex<-Shoko" };
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;

        if (userType == SyncUserType.None)
            return result;

        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;

        if (!plexClient.IsEnabled || userService.GetUsers().OrderBy(usr => usr.LocalID).FirstOrDefault() is not { } defaultUser)
            return result;
        var targets = plexClient.GetConfiguredTargets();
        if (targets.Count == 0)
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

        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraUsers.Select(user => user.Name), includeAdmin) };

        var shokoWatchedByUser = new Dictionary<int, List<(IEpisodeUserData UserData, IShokoEpisode Episode)>>();
        var shokoUserLookup = new Dictionary<string, IUser?>(StringComparer.OrdinalIgnoreCase);
        var tokenCache = new SyncHelper.PlexUserTokenCache();

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

        var matchedGlobal = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var activeSourceEpisodes = new HashSet<(int ShokoUserId, int EpisodeId)>();
        var prefIdCache = new Dictionary<int, string?>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply Library Name Filter
            if (!string.IsNullOrWhiteSpace(libraryName) && !string.Equals(target.Title, libraryName, StringComparison.OrdinalIgnoreCase))
                continue;

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

            foreach (var bucket in userBuckets)
            {
                string userName = bucket.Name;
                var items = bucket.Items;
                string? userToken = bucket.Token;

                if (ResolveTargetShokoUser(bucket.ShokoUsername, userName) is not { } targetShokoUser)
                    continue;

                string? mappedLabel = string.IsNullOrWhiteSpace(bucket.ShokoUsername) ? null : targetShokoUser.Username;
                var userWatched = GetShokoWatchedForUser(targetShokoUser);
                foreach (var (userData, _) in userWatched)
                    activeSourceEpisodes.Add((targetShokoUser.LocalID, userData.EpisodeID));

                result.PerUser[userName] = result.PerUser[userName] with { Processed = Math.Max(result.PerUser[userName].Processed, userWatched.Count) };

                var plexMap = new Dictionary<int, List<PlexMetadataItem>>();
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.RatingKey))
                        continue;

                    var plexEpId = PlexHelper.ExtractShokoEpisodeIdFromGuid(item.Guid);
                    if (plexEpId.HasValue)
                    {
                        if (!plexMap.TryGetValue(plexEpId.Value, out var list))
                            plexMap[plexEpId.Value] = list = [];
                        list.Add(item);
                    }
                }

                foreach (var (userData, episode) in userWatched)
                {
                    if (!plexMap.TryGetValue(episode.LocalID, out var plexItems))
                        continue;

                    matchedGlobal.Add((targetShokoUser.LocalID, episode.LocalID));

                    foreach (var plexItem in plexItems.DistinctBy(item => item.RatingKey))
                    {
                        bool isWatchedInPlex = plexItem.ViewCount > 0;
                        bool shouldScrobble = userData.IsWatched && !isWatchedInPlex;
                        bool scrobbled = false;

                        if (shouldScrobble)
                        {
                            if (!dryRun)
                            {
                                using var req = plexClient.CreateRequest(HttpMethod.Get, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}", target.ServerUrl, userToken);
                                using var resp = await plexClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                                if (!resp.IsSuccessStatusCode)
                                {
                                    result = SyncHelper.RecordError(result, result.PerUser, userName, $"Failed to scrobble RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                    continue;
                                }
                            }
                            scrobbled = true;
                        }

                        string? prefId = null;
                        if (episode!.Series != null && !prefIdCache.TryGetValue(episode.ShokoSeriesID, out prefId))
                            prefIdCache[episode.ShokoSeriesID] = prefId = MapHelper.GetPreferredTmdbOrderingId(episode.Series);

                        var coords = PlexMapping.GetPlexCoordinates(episode, prefId, metadataService: metadataService);
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
                                    using var rateReq = plexClient.CreateRequest(
                                        HttpMethod.Get,
                                        $"/:/rate?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}&rating={userData.UserRating.Value.ToString(CultureInfo.InvariantCulture)}",
                                        target.ServerUrl,
                                        userToken
                                    );
                                    using var rateResp = await plexClient.SendAsync(rateReq, cancellationToken).ConfigureAwait(false);
                                    if (!rateResp.IsSuccessStatusCode)
                                    {
                                        rateFailed = true;
                                        if (bucket.IsSharedFriend && rateResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                                            logger.LogDebug("WatchedSyncService: Plex shared server token for '{User}' cannot apply personal ratings (HTTP {Status})", userName, rateResp.StatusCode);
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

                        if (!scrobbled && !rated)
                            result = SyncHelper.IncSkipped(result, result.PerUser, userName);
                    }
                }
            }
        }
        var notFoundCount = activeSourceEpisodes.Count(key => !matchedGlobal.Contains(key));
        result = result with { Processed = activeSourceEpisodes.Count, Skipped = result.Skipped + notFoundCount };
        return result;
    }

    #endregion
}
