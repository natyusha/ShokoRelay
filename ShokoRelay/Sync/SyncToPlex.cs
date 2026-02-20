using System.Globalization;
using NLog;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.User;
using Shoko.Abstractions.UserData;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync
{
    /// <summary>
    /// Synchronize watched-state (and optional votes) from Shoko -> Plex.
    /// Matches are performed only by GUID (Shoko episode/series IDs).
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

        public SyncToPlex(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider)
        {
            _plexClient = plexClient;
            _metadataService = metadataService;
            _userDataService = userDataService;
            _userService = userService;
            _configProvider = configProvider;
        }

        /// <summary>
        /// Sync watched-state from the first Shoko user into configured Plex libraries.
        /// - Only applies matches by GUID (no filepath heuristics).
        /// - Optionally syncs votes/ratings when includeVotes=true.
        /// </summary>
        public async Task<PlexWatchedSyncResult> SyncWatchedAsync(
            bool dryRun = true,
            int? sinceHours = null,
            bool includeVotes = false,
            bool excludeAdmin = false,
            CancellationToken cancellationToken = default
        )
        {
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
            if (sinceHours.HasValue && sinceHours.Value > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-sinceHours.Value);
                epUserData = epUserData.Where(e => (e.LastPlayedAt ?? DateTime.MinValue) >= cutoff).ToList();
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
                var token = _configProvider.GetExtraPlexUserToken(ex.Name);
                plexUserTargets.Add((ex.Name, token));
            }

            // process each Plex target (section/server)
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Logger.Info("SyncToPlex: scanning target {Server}:{Section} for Plex mapping", target.ServerUrl, target.SectionId);

                // Per-target cache to remember whether an extra Plex user token can access this section
                var userAccessCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                // --- episode synchronization: perform a GUID search per-library and per-plex-user (unwatched=1) ---
                // For each Shoko-watched episode we query Plex for the Shoko GUID using the requesting
                // user's token and `unwatched=1`. If the search returns a metadata item we extract the
                // ratingKey and scrobble that exact ratingKey on behalf of the requesting user.
                // This guarantees per-library + per-user behavior (exact match to spec).

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
                    if (shokoSeriesId.HasValue && !seriesGuidMap.ContainsKey(shokoSeriesId.Value))
                    {
                        if (int.TryParse(s.RatingKey, out var _))
                            seriesGuidMap[shokoSeriesId.Value] = s.RatingKey!;
                    }
                }

                async Task<bool> SendPlexGetAsync(HttpRequestMessage req)
                {
                    using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    return resp.IsSuccessStatusCode;
                }

                foreach (var ep in epUserData)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ep.EpisodeID <= 0)
                        continue;

                    // build the Shoko GUID for this episode
                    string guid = SyncHelper.MakeEpisodeGuid(ep.EpisodeID);

                    // apply to each configured Plex user (admin + extras)
                    foreach (var (plexUserName, plexToken) in plexUserTargets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // skip extra users without a token
                        if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(plexToken))
                        {
                            perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };
                            continue;
                        }

                        // ensure extra user has access to this library/section
                        if (!string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase))
                        {
                            var hasAccess = await SyncHelper.UserHasAccessToSectionAsync(_plexClient, target, plexUserName, plexToken, userAccessCache, cancellationToken).ConfigureAwait(false);
                            if (!hasAccess)
                            {
                                result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                                continue;
                            }
                        }

                        // Search this section for the Shoko GUID as the requesting user and only include unwatched items
                        try
                        {
                            string requestPath = $"/library/sections/{target.SectionId}/all?type={Plex.PlexConstants.TypeEpisode}&unwatched=1&guid={Uri.EscapeDataString(guid)}";
                            using var searchReq = _plexClient.CreateRequest(HttpMethod.Get, requestPath, target.ServerUrl, plexToken);
                            using var searchResp = await Http.SendAsync(searchReq, cancellationToken).ConfigureAwait(false);

                            if (!searchResp.IsSuccessStatusCode)
                            {
                                // treat as not found for this user/section
                                result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                                continue;
                            }

                            var container = await Plex.PlexApi.ReadContainerAsync(searchResp, cancellationToken).ConfigureAwait(false);
                            var item = container?.Metadata?.FirstOrDefault();
                            if (item == null || string.IsNullOrWhiteSpace(item.RatingKey))
                            {
                                // nothing unwatched for this user/section with that GUID
                                result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                                continue;
                            }

                            // We found an unwatched item for this user in this section.
                            episodesFoundInAnyTarget.Add(ep.EpisodeID);

                            // Dry-run: simulate the scrobble
                            if (dryRun)
                            {
                                result = SyncHelper.IncMarkedWatched(result, perUserResults, plexUserName);

                                var change = SyncHelper.MakeChange(
                                    plexUser: plexUserName,
                                    shokoEpisodeId: ep.EpisodeID,
                                    seriesTitle: ep.Series?.PreferredTitle?.Value,
                                    episodeTitle: ep.Episode?.PreferredTitle?.Value,
                                    seasonNumber: ep.Episode?.SeasonNumber,
                                    episodeNumber: ep.Episode?.EpisodeNumber,
                                    ratingKey: item.RatingKey,
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

                            // Real run: scrobble using ratingKey on behalf of the requesting user
                            if (!int.TryParse(item.RatingKey, out var ratingKey))
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

                                var changeReal = SyncHelper.MakeChange(
                                    plexUser: plexUserName,
                                    shokoEpisodeId: ep.EpisodeID,
                                    seriesTitle: ep.Series?.PreferredTitle?.Value,
                                    episodeTitle: ep.Episode?.PreferredTitle?.Value,
                                    seasonNumber: ep.Episode?.SeasonNumber,
                                    episodeNumber: ep.Episode?.EpisodeNumber,
                                    ratingKey: item.RatingKey,
                                    guid: guid,
                                    filePath: null,
                                    lastViewedAt: ep.LastPlayedAt,
                                    wouldMark: true,
                                    alreadyWatchedInShoko: true
                                );
                                SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, changeReal);

                                // optional: apply rating to Plex episode when includeVotes==true and Shoko has a rating
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
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "SyncToPlex: failed to search/scrobble for ep {Ep} on {Server}:{Section}", ep.EpisodeID, target.ServerUrl, target.SectionId);
                            result = SyncHelper.RecordError(result, null, null, $"Failed search/scrobble ep {ep.EpisodeID} on {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
                        }
                    }

                    // continue to next episode (we still need to process all targets/users)
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
}
