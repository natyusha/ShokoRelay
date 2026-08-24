using System.Globalization;
using Shoko.Abstractions.User.Services;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state (and optional votes) from Shoko -> Plex.</summary>
public class SyncToPlex(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider, PlexAuth plexAuth)
{
    #region Setup

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion

    #region Synchronization Logic

    /// <summary>Sync watched-state from Shoko into configured Plex libraries.</summary>
    /// <param name="dryRun">If true, skip scrobble execution.</param>
    /// <param name="sinceHours">Optional window to limit processed items.</param>
    /// <param name="includeVotes">Include user ratings.</param>
    /// <param name="userTypeOverride">Optional override for the sync users configuration.</param>
    /// <param name="libraryName">Optional filter to restrict sync to a specific Plex library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        OverrideHelper.Reload(metadataService);
        var result = new PlexWatchedSyncResult();
        var auto = Settings.Automation;
        var userType = userTypeOverride ?? auto.ShokoSyncWatchedUserType;

        if (userType == SyncUserType.None)
            return result;

        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;

        if (!plexClient.IsEnabled || userService.GetUsers().FirstOrDefault() is not { } shokoUser)
            return result;
        var targets = plexClient.GetConfiguredTargets();
        if (targets.Count == 0)
            return result;

        // Strict null check on LastPlayedAt bypasses false-positive stub records where IsWatched evaluates to true
        var shokoWatchedQuery = userDataService.GetEpisodeUserDataForUser(shokoUser).Where(e => e.LastPlayedAt != null);
        if (sinceHours > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-sinceHours.Value);
            shokoWatchedQuery = shokoWatchedQuery.Where(e => e.LastPlayedAt >= cutoff);
        }

        var shokoWatched = shokoWatchedQuery.Select(sw => (UserData: sw, Episode: metadataService.GetShokoEpisodeByID(sw.EpisodeID))).Where(x => x.Episode != null).Select(x => (x.UserData, x.Episode)).ToList();

        result = result with { Processed = shokoWatched.Count };
        var extraEntries = configProvider.GetExtraPlexUserEntries();
        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name)) };

        var matchedGlobal = new HashSet<int>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply Library Name Filter
            if (!string.IsNullOrWhiteSpace(libraryName) && !string.Equals(target.Title, libraryName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Fetch user item buckets and automatically handle managed token resolution and user filtering.
            // Pass sinceHours: null here to return ALL unwatched items in Plex. The Shoko list is already filtered by sinceHours.
            var (userBuckets, newResult) = await SyncHelper
                .FetchUserBucketsAsync(plexAuth, plexClient, configProvider, target, userType, extraEntries, true, null, null, result, cancellationToken)
                .ConfigureAwait(false);
            result = newResult;

            foreach (var (uName, unwatched, uToken) in userBuckets)
            {
                result.PerUser[uName] = result.PerUser[uName] with { Processed = shokoWatched.Count };

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

                foreach (var (userData, episode) in shokoWatched)
                {
                    if (!plexMap.TryGetValue(userData.EpisodeID, out var plexItems))
                        continue;

                    matchedGlobal.Add(userData.EpisodeID);

                    foreach (var plexItem in plexItems.DistinctBy(i => i.RatingKey))
                    {
                        if (!dryRun)
                        {
                            using var req = plexClient.CreateRequest(HttpMethod.Get, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={plexItem.RatingKey}", target.ServerUrl, uToken);
                            using var resp = await plexClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                                continue;
                        }

                        var prefId = episode!.Series != null ? MapHelper.GetPreferredTmdbOrderingId(episode.Series) : null;
                        var coords = PlexMapping.GetPlexCoordinates(episode, prefId);
                        string typeLabel = PlexHelper.IsMovieKey(plexItem.RatingKey!) ? "movie" : "episode";

                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                        s_logger.Info(
                            "WatchedSyncService: {0}Plex <- Shoko: {1} marked {2} -> {3} [{4}] - S{5:D2}E{6:D2} (RatingKey: {7}) on {8}",
                            logPrefix,
                            uName,
                            typeLabel,
                            episode.Series?.GetDisplayTitle(),
                            episode.SeriesID,
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
                                libraryName: target.Title,
                                userData.EpisodeID,
                                $"{episode.Series?.GetDisplayTitle()} [{episode.SeriesID}]",
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
                                await plexClient.SendAsync(rateReq, cancellationToken).ConfigureAwait(false);
                            }
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                    }
                }
            }
        }
        var notFoundCount = shokoWatched.Count(e => !matchedGlobal.Contains(e.UserData.EpisodeID));
        result = result with { Skipped = result.Skipped + notFoundCount };
        return result;
    }

    #endregion
}
