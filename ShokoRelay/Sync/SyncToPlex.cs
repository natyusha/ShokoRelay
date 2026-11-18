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

        if (!plexClient.IsEnabled || userService.GetUsers().OrderBy(u => u.LocalID).FirstOrDefault() is not { } defaultUser)
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

        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraUsers.Select(e => e.Name), includeAdmin) };

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

            var query = userDataService.GetEpisodeUserDataForUser(shokoUser).Where(e => e.LastPlayedAt != null);
            if (sinceHours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-sinceHours.Value);
                query = query.Where(e => e.LastPlayedAt >= cutoff);
            }

            return shokoWatchedByUser[shokoUser.LocalID] = [
                .. query.Select(sw => (UserData: sw, Episode: metadataService.GetShokoEpisodeByID(sw.EpisodeID))).Where(x => x.Episode != null).Select(x => (x.UserData, Episode: x.Episode!)),
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

            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(plexAuth, plexClient, configProvider, target, includeAdmin, adminShokoUser, extraUsers, tokenCache, true, null, null, result, logger, cancellationToken)
                .ConfigureAwait(false);
            result = newResult;

            foreach (var bucket in userBuckets)
            {
                string uName = bucket.Name;
                var unwatched = bucket.Items;
                string? uToken = bucket.Token;

                if (ResolveTargetShokoUser(bucket.ShokoUsername, uName) is not { } targetShokoUser)
                    continue;

                string? mappedLabel = string.IsNullOrWhiteSpace(bucket.ShokoUsername) ? null : targetShokoUser.Username;
                var userWatched = GetShokoWatchedForUser(targetShokoUser);
                foreach (var (ud, _) in userWatched)
                    activeSourceEpisodes.Add((targetShokoUser.LocalID, ud.EpisodeID));

                result.PerUser[uName] = result.PerUser[uName] with { Processed = Math.Max(result.PerUser[uName].Processed, userWatched.Count) };

                var plexMap = new Dictionary<int, List<PlexMetadataItem>>();
                foreach (var item in unwatched)
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

                    foreach (var plexItem in plexItems.DistinctBy(i => i.RatingKey))
                    {
                        if (!dryRun)
                        {
                            using var req = plexClient.CreateRequest(HttpMethod.Get, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}", target.ServerUrl, uToken);
                            using var resp = await plexClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                            {
                                result = SyncHelper.RecordError(result, result.PerUser, uName, $"Failed to scrobble RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                continue;
                            }
                        }

                        string? prefId = null;
                        if (episode!.Series != null && !prefIdCache.TryGetValue(episode.ShokoSeriesID, out prefId))
                            prefIdCache[episode.ShokoSeriesID] = prefId = MapHelper.GetPreferredTmdbOrderingId(episode.Series);

                        var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                        string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movie" : "episode";

                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                        logger.LogInformation(
                            "WatchedSyncService: {Prefix}Plex <- Shoko: {User} marked {Type} -> {Title} [{SeriesId}] - S{Season:D2}E{Episode:D2} (RatingKey: {RatingKey}) on {Server}",
                            logPrefix,
                            uName,
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
                            uName,
                            SyncHelper.MakeChange(
                                uName,
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

                        if (actualVotes && userData.HasUserRating)
                        {
                            result = SyncHelper.IncVotesFound(result);
                            if (!dryRun)
                            {
                                using var rateReq = plexClient.CreateRequest(
                                    HttpMethod.Get,
                                    $"/:/rate?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}&rating={userData.UserRating.Value.ToString(CultureInfo.InvariantCulture)}",
                                    target.ServerUrl,
                                    uToken
                                );
                                using var rateResp = await plexClient.SendAsync(rateReq, cancellationToken).ConfigureAwait(false);
                                if (!rateResp.IsSuccessStatusCode)
                                {
                                    if (bucket.IsSharedFriend && rateResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                                        logger.LogDebug("WatchedSyncService: Plex shared server token for '{User}' cannot apply personal ratings (HTTP {Status})", uName, rateResp.StatusCode);
                                    else
                                        result = SyncHelper.RecordError(result, result.PerUser, uName, $"Failed to rate RatingKey {plexItem.RatingKey} on {target.ServerName}");
                                    continue;
                                }
                            }
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                    }
                }
            }
        }
        var notFoundCount = activeSourceEpisodes.Count(k => !matchedGlobal.Contains(k));
        result = result with { Processed = activeSourceEpisodes.Count, Skipped = result.Skipped + notFoundCount };
        return result;
    }

    #endregion
}
