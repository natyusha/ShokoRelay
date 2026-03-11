using System.Globalization;
using NLog;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.User;
using Shoko.Abstractions.UserData;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync;

/// <summary>
/// Synchronize watched-state (and optional votes) from Shoko -> Plex.
/// Matches are performed only by GUID (Shoko episode/series IDs).
/// Extra Plex user tokens are acquired transiently via Plex Home and not persisted.
/// </summary>
public class SyncToPlex
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new HttpClient();

    private readonly PlexClient _plexClient;
    private readonly IMetadataService _metadataService;
    private readonly IUserDataService _userDataService;
    private readonly IUserService _userService;
    private readonly ConfigProvider _configProvider;
    private readonly PlexAuth _plexAuth;

    /// <summary>
    /// Initialize a <see cref="SyncToPlex"/> instance with necessary services.
    /// </summary>
    public SyncToPlex(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider, PlexAuth plexAuth)
    {
        _plexClient = plexClient;
        _metadataService = metadataService;
        _userDataService = userDataService;
        _userService = userService;
        _configProvider = configProvider;
        _plexAuth = plexAuth;
    }

    /// <summary>
    /// Sync watched-state from the first Shoko user into configured Plex libraries.
    /// - Only applies matches by GUID (no filepath heuristics).
    /// - Optionally syncs votes/ratings when includeVotes=true.
    /// </summary>
    /// <param name="dryRun">When <c>true</c>, report changes without actually applying them.</param>
    /// <param name="sinceHours">Optional lookback window in hours; <c>null</c> means no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PlexWatchedSyncResult"/> with aggregate and per-user statistics.</returns>
    public async Task<PlexWatchedSyncResult> SyncWatchedAsync(bool dryRun = true, int? sinceHours = null, CancellationToken cancellationToken = default)
    {
        bool includeVotes = ShokoRelay.Settings.Automation.ShokoSyncWatchedIncludeRatings;
        bool excludeAdmin = ShokoRelay.Settings.Automation.ShokoSyncWatchedExcludeAdmin;

        var result = new PlexWatchedSyncResult();
        Logger.Info("SyncToPlex: starting (dryRun={Dry}, sinceHours={Since}, votes={Votes})", dryRun, sinceHours, includeVotes);

        if (!_plexClient.IsEnabled)
        {
            Logger.Warn("SyncToPlex: Plex is not configured/enabled.");
            return result;
        }

        var targets = _plexClient.GetConfiguredTargets();
        if (targets == null || targets.Count == 0)
        {
            Logger.Warn("SyncToPlex: No Plex library targets configured.");
            return result;
        }

        // default Shoko user (keeps behavior consistent with SyncToShoko)
        IUser? shokoUser = _userService.GetUsers().FirstOrDefault();
        if (shokoUser == null)
        {
            Logger.Warn("SyncToPlex: no Shoko users available to export watched-state.");
            return result with { Errors = result.Errors + 1 };
        }

        // gather watched episodes from Shoko user
        var epUserData = _userDataService.GetEpisodeUserDataForUser(shokoUser).Where(e => e.IsWatched).ToList();
        Logger.Info("SyncToPlex: retrieved {Count} watched episode records from Shoko", epUserData.Count);
        if (sinceHours.HasValue && sinceHours.Value > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-sinceHours.Value);
            epUserData = epUserData.Where(e => (e.LastPlayedAt ?? DateTime.MinValue) >= cutoff).ToList();
            Logger.Info("SyncToPlex: filtered to {Count} episodes since {Hours} hours", epUserData.Count, sinceHours.Value);
        }

        // Track which Shoko episodes exist in at least one configured Plex target. Episodes not found
        // in any target will be treated as a global "skipped" after scanning all targets.
        var episodesFoundInAnyTarget = new HashSet<int>();

        // optional: gather series votes when includeVotes
        var seriesUserData = includeVotes ? _userDataService.GetSeriesUserDataForUser(shokoUser).Where(s => s.HasUserRating).ToList() : new List<ISeriesUserData>();

        // prepare per-Plex-user result buckets (admin + configured extra usernames)
        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        var perUserResults = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name));

        // Prepare list of Plex user targets once (admin + extras). Resolved once per-run and reused for each target.
        var plexUserTargets = new List<(string Name, string? Token)>();
        if (!excludeAdmin)
            plexUserTargets.Add(("admin", null));
        foreach (var ex in extraEntries)
        {
            // fetch a transient token via Plex Home switch; do not persist
            var token = await SyncHelper.FetchManagedUserTokenAsync(_plexAuth, _configProvider, ex.Name, ex.Pin, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                Logger.Info("SyncToPlex: no transient token available for extra Plex user '{User}', skipping", ex.Name);
                continue;
            }
            plexUserTargets.Add((ex.Name, token));
        }
        Logger.Info("SyncToPlex: will iterate Plex users: {Count}", plexUserTargets.Count);

        // process each Plex target (section/server)
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Logger.Info("SyncToPlex: scanning target {Server}:{Section} for Plex mapping", target.ServerUrl, target.SectionId);

            // Per-target cache to remember whether an extra Plex user token can access this section
            var userAccessCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Episode Synchronization: perform a GUID search per-library and per-plex-user (unwatched=1)
            //  - For each Shoko-watched episode we query Plex for the Shoko GUID using the requesting
            //  - user's token and `unwatched=1`. If the search returns a metadata item we extract the
            //  - ratingKey and scrobble that exact ratingKey on behalf of the requesting user.
            //  - This guarantees per-library + per-user behavior (exact match to spec).

            // also build series GUID -> ratingKey lookup for shows (used for series votes)
            List<PlexMetadataItem> sectionShows;
            try
            {
                sectionShows = await _plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                sectionShows = new List<PlexMetadataItem>();
            }

            var seriesGuidMap = new Dictionary<int, string>();
            foreach (var s in sectionShows)
            {
                if (string.IsNullOrWhiteSpace(s.Guid) || string.IsNullOrWhiteSpace(s.RatingKey))
                    continue;
                var shokoSeriesId = SyncHelper.TryParseShokoSeriesIdFromGuid(s.Guid);
                if (shokoSeriesId.HasValue && int.TryParse(s.RatingKey, out var _))
                    seriesGuidMap.TryAdd(shokoSeriesId.Value, s.RatingKey!);
            }

            async Task<bool> SendPlexGetAsync(HttpRequestMessage req)
            {
                using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }

            // iterate per-plex-user rather than per-episode to avoid switching tokens every loop
            foreach (var (plexUserName, plexToken) in plexUserTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // skip extra users without a token (count them all as skipped up front)
                if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(plexToken))
                {
                    // increment global and per‑user skipped counters by the number of candidate episodes
                    result = result with
                    {
                        Skipped = result.Skipped + epUserData.Count,
                    };
                    if (perUserResults.TryGetValue(plexUserName, out var pu))
                        perUserResults[plexUserName] = pu with { Skipped = pu.Skipped + epUserData.Count };
                    continue;
                }

                // ensure extra user has access to this library/section once per-target
                bool hasAccess = true;
                if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase))
                {
                    hasAccess = await SyncHelper.UserHasAccessToSectionAsync(_plexClient, target, plexUserName, plexToken, userAccessCache, cancellationToken).ConfigureAwait(false);
                    if (!hasAccess)
                    {
                        // no access to this section – treat every episode as skipped
                        result = result with
                        {
                            Skipped = result.Skipped + epUserData.Count,
                        };
                        if (perUserResults.TryGetValue(plexUserName, out var pu))
                            perUserResults[plexUserName] = pu with { Skipped = pu.Skipped + epUserData.Count };
                        continue;
                    }
                }

                // fetch the complete list of unwatched episodes for this user/section in one shot
                var unwatched = await _plexClient.GetSectionEpisodesAsync(target, plexToken, cancellationToken, onlyUnwatched: true, guidFilter: null).ConfigureAwait(false);
                int unwatchedCount = unwatched?.Count ?? 0;
                Logger.Info("SyncToPlex: user {User} retrieved {Count} unwatched episodes from Plex", plexUserName, unwatchedCount);
                var plexMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (unwatched != null)
                {
                    foreach (var item in unwatched)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Guid) && !string.IsNullOrWhiteSpace(item.RatingKey))
                            plexMap.TryAdd(item.Guid, item.RatingKey);
                    }
                }

                int epCountUser = 0;
                foreach (var ep in epUserData)
                {
                    epCountUser++;
                    if (epCountUser % 500 == 0)
                    {
                        Logger.Info("SyncToPlex: user {User} processing episode {Index}/{Total}", plexUserName, epCountUser, epUserData.Count);
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ep.EpisodeID <= 0)
                        continue;

                    string guid = SyncHelper.MakeEpisodeGuid(ep.EpisodeID);

                    if (!plexMap.TryGetValue(guid, out var ratingKeyStr))
                    {
                        result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                        continue;
                    }

                    // we have a ratingKey available
                    episodesFoundInAnyTarget.Add(ep.EpisodeID);

                    // Dry-run or actual scrobble
                    if (dryRun)
                    {
                        result = SyncHelper.IncMarkedWatched(result, perUserResults, plexUserName);
                        // ep.Series getter can throw if the underlying SeriesID is 0; access safely
                        string? seriesTitle = null;
                        int? seasonNum = null;
                        int? episodeNum = null;
                        try
                        {
                            var s = ep.Series; // may throw
                            if (s != null)
                            {
                                seriesTitle = s.PreferredTitle?.Value;
                                seasonNum = ep.Episode?.SeasonNumber;
                                episodeNum = ep.Episode?.EpisodeNumber;
                            }
                        }
                        catch
                        { /* ignore invalid state */
                        }

                        var change = SyncHelper.MakeChange(
                            plexUser: plexUserName,
                            shokoEpisodeId: ep.EpisodeID,
                            seriesTitle: seriesTitle,
                            episodeTitle: ep.Episode?.PreferredTitle?.Value,
                            seasonNumber: seasonNum,
                            episodeNumber: episodeNum,
                            ratingKey: ratingKeyStr,
                            guid: guid,
                            filePath: null,
                            lastViewedAt: ep.LastPlayedAt,
                            wouldMark: true,
                            alreadyWatchedInShoko: true
                        );
                        SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, change);
                        if (includeVotes && ep.HasUserRating)
                        {
                            result = SyncHelper.IncVotesFound(result);
                            result = SyncHelper.IncVotesUpdated(result);
                            result.PerUserChanges[plexUserName][^1] = result.PerUserChanges[plexUserName][^1] with { PlexUserRating = ep.UserRating, ShokoUserRating = ep.UserRating };
                        }
                        continue;
                    }

                    if (!int.TryParse(ratingKeyStr, out var ratingKey))
                    {
                        result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                        continue;
                    }

                    string scrobblePath = $"/:/scrobble?identifier=com.plexapp.plugins.library&key={ratingKey}";
                    using var scrobbleReq = _plexClient.CreateRequest(HttpMethod.Get, scrobblePath, target.ServerUrl, plexToken);
                    var scrobbleOk = await SendPlexGetAsync(scrobbleReq).ConfigureAwait(false);
                    if (scrobbleOk)
                    {
                        result = SyncHelper.IncMarkedWatched(result, perUserResults, plexUserName);
                        // as above, guard series access
                        string? seriesTitleReal = null;
                        int? seasonNumReal = null;
                        int? episodeNumReal = null;
                        try
                        {
                            var s = ep.Series;
                            if (s != null)
                            {
                                seriesTitleReal = s.PreferredTitle?.Value;
                                seasonNumReal = ep.Episode?.SeasonNumber;
                                episodeNumReal = ep.Episode?.EpisodeNumber;
                            }
                        }
                        catch { }
                        var changeReal = SyncHelper.MakeChange(
                            plexUser: plexUserName,
                            shokoEpisodeId: ep.EpisodeID,
                            seriesTitle: seriesTitleReal,
                            episodeTitle: ep.Episode?.PreferredTitle?.Value,
                            seasonNumber: seasonNumReal,
                            episodeNumber: episodeNumReal,
                            ratingKey: ratingKeyStr,
                            guid: guid,
                            filePath: null,
                            lastViewedAt: ep.LastPlayedAt,
                            wouldMark: true,
                            alreadyWatchedInShoko: true
                        );
                        SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, changeReal);
                        if (includeVotes && ep.HasUserRating)
                        {
                            try
                            {
                                result = SyncHelper.IncVotesFound(result);
                                var rating = ep.UserRating!.Value;
                                string ratePath = $"/:/rate?identifier=com.plexapp.plugins.library&key={ratingKey}&rating={rating.ToString(CultureInfo.InvariantCulture)}";
                                using var rateReq = _plexClient.CreateRequest(HttpMethod.Get, ratePath, target.ServerUrl, plexToken);
                                var okRate = await SendPlexGetAsync(rateReq).ConfigureAwait(false);
                                if (okRate)
                                {
                                    result = SyncHelper.IncVotesUpdated(result);
                                    result.PerUserChanges[plexUserName][^1] = result.PerUserChanges[plexUserName][^1] with { PlexUserRating = rating, ShokoUserRating = ep.UserRating };
                                }
                                else
                                {
                                    result = SyncHelper.IncVotesSkipped(result);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "SyncToPlex: failed to apply episode rating for ep {Ep}", ep.EpisodeID);
                                result = result with { Errors = result.Errors + 1 };
                            }
                        }
                    }
                    else
                    {
                        result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                    }
                }
            }

            // Sync series-level votes when requested
            if (includeVotes && seriesUserData.Count > 0)
            {
                foreach (var s in seriesUserData)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (s.SeriesID <= 0 || !s.HasUserRating)
                        continue;

                    if (!seriesGuidMap.TryGetValue(s.SeriesID, out var showRatingKey) || !int.TryParse(showRatingKey, out var showKeyInt))
                    {
                        result = result with { Skipped = result.Skipped + 1 };

                        // per-user bookkeeping for this skipped series vote
                        foreach (var (pName, _) in plexUserTargets)
                            perUserResults[pName] = perUserResults[pName] with { Skipped = perUserResults[pName].Skipped + 1 };

                        continue;
                    }

                    foreach (var (plexUserName, plexToken) in plexUserTargets)
                    {
                        if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(plexToken))
                        {
                            perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };
                            continue;
                        }

                        // Ensure extra users actually have access to this library/section before attempting ratings
                        if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase))
                        {
                            var hasAccess = await SyncHelper.UserHasAccessToSectionAsync(_plexClient, target, plexUserName, plexToken, userAccessCache, cancellationToken).ConfigureAwait(false);

                            if (!hasAccess)
                            {
                                result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                                if (dryRun)
                                {
                                    var changeNoAccess = SyncHelper.MakeChange(
                                        plexUser: plexUserName,
                                        shokoEpisodeId: 0,
                                        seriesTitle: s.Series?.PreferredTitle?.Value,
                                        ratingKey: showRatingKey,
                                        guid: SyncHelper.MakeShowGuid(s.SeriesID),
                                        plexUserRating: s.UserRating,
                                        shokoUserRating: s.UserRating,
                                        reason: "no_access"
                                    );
                                    SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, changeNoAccess);
                                }

                                continue;
                            }
                        }

                        if (dryRun)
                        {
                            // count rating candidate (per-Plex-user)
                            result = SyncHelper.IncVotesFound(result);
                            result = SyncHelper.IncVotesUpdated(result);
                            var change = SyncHelper.MakeChange(
                                plexUser: plexUserName,
                                shokoEpisodeId: 0,
                                seriesTitle: s.Series?.PreferredTitle?.Value,
                                ratingKey: showRatingKey,
                                guid: SyncHelper.MakeShowGuid(s.SeriesID),
                                plexUserRating: s.UserRating,
                                shokoUserRating: s.UserRating
                            );
                            SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, change);
                            continue;
                        }

                        try
                        {
                            // set rating on the show
                            // count rating candidate (per-Plex-user)
                            result = SyncHelper.IncVotesFound(result);

                            var rating = s.UserRating!.Value;
                            string ratePath = $"/:/rate?identifier=com.plexapp.plugins.library&key={showKeyInt}&rating={rating.ToString(CultureInfo.InvariantCulture)}";
                            using var rateReq = _plexClient.CreateRequest(HttpMethod.Get, ratePath, target.ServerUrl, plexToken);
                            var okRate = await SendPlexGetAsync(rateReq).ConfigureAwait(false);
                            if (okRate)
                            {
                                result = SyncHelper.IncVotesUpdated(result);
                                var change = SyncHelper.MakeChange(
                                    plexUser: plexUserName,
                                    shokoEpisodeId: 0,
                                    seriesTitle: s.Series?.PreferredTitle?.Value,
                                    ratingKey: showRatingKey,
                                    guid: SyncHelper.MakeShowGuid(s.SeriesID),
                                    plexUserRating: rating,
                                    shokoUserRating: s.UserRating
                                );
                                SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, change);
                            }
                            else
                            {
                                result = SyncHelper.IncVotesSkipped(result);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "SyncToPlex: failed to apply series rating for series {SeriesId}", s.SeriesID);
                            result = result with { Errors = result.Errors + 1 };
                        }
                    }
                }
            }
        }

        // After scanning all targets: any Shoko-watched episode that wasn't found in *any*
        // configured Plex target should be counted as skipped (global missing mapping).
        var notFound = epUserData.Where(e => !episodesFoundInAnyTarget.Contains(e.EpisodeID)).ToList();
        if (notFound.Count > 0)
        {
            result = result with { Skipped = result.Skipped + notFound.Count };
            foreach (var (pName, _) in plexUserTargets)
                perUserResults[pName] = perUserResults[pName] with { Skipped = perUserResults[pName].Skipped + notFound.Count };
        }

        // Record aggregate processed count for Shoko->Plex runs (number of watched episodes considered)
        result = result with
        {
            Processed = epUserData.Count,
            PerUser = perUserResults,
        };
        return result;
    }
}
