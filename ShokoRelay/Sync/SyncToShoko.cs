using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.User;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync
{
    public class SyncToShoko
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly PlexClient _plexClient;
        private readonly IMetadataService _metadataService;
        private readonly IUserDataService _userDataService;
        private readonly IUserService _userService;
        private readonly IVideoService _videoService;
        private readonly ConfigProvider _configProvider;
        private readonly PlexAuth _plexAuth;

        public SyncToShoko(
            PlexClient plexClient,
            IMetadataService metadataService,
            IUserDataService userDataService,
            IUserService userService,
            IVideoService videoService,
            ConfigProvider configProvider,
            PlexAuth plexAuth
        )
        {
            _plexClient = plexClient;
            _metadataService = metadataService;
            _userDataService = userDataService;
            _userService = userService;
            _videoService = videoService;
            _configProvider = configProvider;
            _plexAuth = plexAuth;
        }

        /// <summary>
        /// Backwards-compatible public entry (no votes).
        /// </summary>
        public Task<PlexWatchedSyncResult> SyncWatchedAsync(bool dryRun = false, int? sinceHours = null, CancellationToken cancellationToken = default) =>
            SyncWatchedInternalAsync(dryRun, sinceHours, false, cancellationToken);

        /// <summary>
        /// Public entry supporting optional vote/rating sync in addition to watched-state.
        /// </summary>
        public Task<PlexWatchedSyncResult> SyncWatchedAsync(bool dryRun, int? sinceHours, bool includeVotes, CancellationToken cancellationToken = default) =>
            SyncWatchedInternalAsync(dryRun, sinceHours, includeVotes, cancellationToken);

        private async Task<PlexWatchedSyncResult> SyncWatchedInternalAsync(bool dryRun, int? sinceHours, bool includeVotes, CancellationToken cancellationToken = default)
        {
            var result = new PlexWatchedSyncResult();
            Logger.Info("WatchedSyncService: starting SyncWatched (dryRun={Dry}, sinceHours={SinceHours}, votes={Votes})", dryRun, sinceHours?.ToString() ?? "<none>", includeVotes);

            if (!_plexClient.IsEnabled)
            {
                Logger.Warn("WatchedSyncService: Plex is not configured/enabled.");
                return result with { Skipped = 0 };
            }

            var targets = _plexClient.GetConfiguredTargets();
            if (targets == null || targets.Count == 0)
            {
                Logger.Warn("WatchedSyncService: No Plex library targets configured.");
                return result;
            }

            // Choose a user to apply watched-state to; default to first user if available.
            IUser? defaultUser = _userService.GetUsers().FirstOrDefault();

            // prepare Extra Plex usernames from config (may be empty). Support optional PINs: "user;pin, otheruser;pin"
            var extraEntries = _configProvider.GetExtraPlexUserEntries();

            // track which Shoko episodes we've already marked during this sync to avoid duplicate writes
            var appliedShokoEpisodeIds = new HashSet<int>();

            // ensure per-user result buckets exist (include admin as "admin")
            var perUserResults = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name));

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Logger.Info("WatchedSyncService: scanning Plex target {Server}:{Section} for watched episodes", target.ServerUrl, target.SectionId);

                // fetch admin view (the configured Plex token owner)
                List<PlexMetadataItem> adminEpisodes;
                try
                {
                    adminEpisodes = await _plexClient.GetSectionEpisodesAsync(target, null, cancellationToken).ConfigureAwait(false);

                    // Apply lookback filter (if requested) to limit to recently-watched items only
                    if (sinceHours.HasValue && sinceHours.Value > 0)
                    {
                        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600);
                        adminEpisodes = adminEpisodes.Where(i => i.LastViewedAt.HasValue && i.LastViewedAt.Value >= cutoff).ToList();
                        Logger.Info("WatchedSyncService: filtered admin episodes to last {Hours} hours -> {Count} items", sinceHours.Value, adminEpisodes.Count);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to fetch episodes from Plex target {Server}:{Section}", target.ServerUrl, target.SectionId);
                    result = SyncHelper.RecordError(result, null, null, $"Failed to fetch episodes from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
                    continue;
                }

                // fetch per-managed-user views (if any extra usernames configured)
                var extraUserEpisodes = new Dictionary<string, List<PlexMetadataItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var exEntry in extraEntries)
                {
                    var exName = exEntry.Name;
                    var exPin = exEntry.Pin;

                    try
                    {
                        var (episodes, error) = await SyncHelper
                            .FetchManagedUserSectionEpisodesAsync(_plexAuth, _plexClient, _configProvider, target, exName, exPin, sinceHours, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            result = SyncHelper.RecordError(result, null, null, error);
                        }

                        extraUserEpisodes[exName] = episodes;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to fetch episodes for Plex user '{User}' on {Server}:{Section}", exName, target.ServerUrl, target.SectionId);
                        result = SyncHelper.RecordError(result, null, null, $"Failed to fetch episodes for Plex user '{exName}' from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
                        extraUserEpisodes[exName] = new List<PlexMetadataItem>();
                    }
                }

                // Process admin and extra users separately so per-user stats are recorded
                var userBuckets = new List<(string UserName, IEnumerable<PlexMetadataItem> Items)>() { ("admin", adminEpisodes) };
                userBuckets.AddRange(extraUserEpisodes.Select(kv => (kv.Key, (IEnumerable<PlexMetadataItem>)kv.Value)));

                foreach (var (plexUserName, items) in userBuckets)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var item in items)
                    {
                        // count this item for the per-user summary if Plex reports it as watched for that user
                        bool plexWatched = (item.ViewCount ?? 0) > 0 || (item.LastViewedAt ?? 0) > 0;
                        if (!plexWatched)
                            continue;

                        // Defensive: ensure returned metadata belongs to the current section (when library id is present)
                        if (item.LibrarySectionId.HasValue && item.LibrarySectionId.Value != target.SectionId)
                        {
                            // skip and record diagnostic info
                            result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);

                            if (dryRun)
                            {
                                var change = SyncHelper.MakeChange(
                                    plexUser: plexUserName,
                                    shokoEpisodeId: 0,
                                    episodeTitle: item.Title,
                                    ratingKey: item.RatingKey,
                                    guid: item.Guid,
                                    filePath: null,
                                    lastViewedAt: SyncHelper.UnixSecondsToDateTime(item.LastViewedAt),
                                    wouldMark: false,
                                    alreadyWatchedInShoko: false,
                                    reason: "not_in_section"
                                );

                                SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, change);
                            }

                            continue;
                        }

                        // increment processed counters
                        result = SyncHelper.IncProcessed(result, perUserResults, plexUserName);

                        try
                        {
                            // 1) Try GUID mapping
                            int? shokoEpisodeId = SyncHelper.TryParseShokoEpisodeIdFromGuid(item.Guid);
                            IShokoEpisode? shokoEpisode = null;

                            if (shokoEpisodeId.HasValue)
                            {
                                shokoEpisode = _metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
                            }

                            // If no Shoko GUID is present or it doesn't resolve to a Shoko episode, skip the item.
                            if (shokoEpisode == null)
                            {
                                // record a skipped candidate (no Shoko GUID present / not resolvable)
                                result = result with
                                {
                                    Skipped = result.Skipped + 1,
                                };
                                perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };

                                // record audit entry for dry-run / diagnostics
                                if (dryRun)
                                {
                                    var change = SyncHelper.MakeChange(
                                        plexUser: plexUserName,
                                        shokoEpisodeId: 0,
                                        seriesTitle: null,
                                        episodeTitle: item.Title,
                                        seasonNumber: null,
                                        episodeNumber: item.Index,
                                        ratingKey: item.RatingKey,
                                        guid: item.Guid,
                                        filePath: null,
                                        lastViewedAt: SyncHelper.UnixSecondsToDateTime(item.LastViewedAt),
                                        wouldMark: false,
                                        alreadyWatchedInShoko: false,
                                        reason: "no_shoko_guid"
                                    );

                                    SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, change);
                                }

                                continue;
                            }

                            // if we've already applied this Shoko episode during this run, skip the write but count the processed per-user
                            if (appliedShokoEpisodeIds.Contains(shokoEpisode.ID))
                                continue;

                            var user = defaultUser;
                            if (user == null)
                            {
                                Logger.Warn("WatchedSyncService: no Shoko users available to apply watched-state.");
                                result = SyncHelper.RecordError(result, perUserResults, plexUserName, $"No Shoko users available to mark episode {shokoEpisode.ID} as watched");
                                continue;
                            }

                            DateTime? watchedAt = null;
                            if (item.LastViewedAt.HasValue && item.LastViewedAt.Value > 0)
                            {
                                try
                                {
                                    watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);
                                }
                                catch { }
                            }

                            bool updated = false;
                            bool alreadyWatchedInShoko = false;

                            // Determine if this episode is already watched in Shoko for the default user.
                            try
                            {
                                if (user != null)
                                {
                                    var epUser = _userDataService.GetEpisodeUserData(shokoEpisode, user);
                                    if (epUser != null && (epUser.LastPlayedAt != null || epUser.PlaybackCount > 0))
                                    {
                                        alreadyWatchedInShoko = true;
                                    }
                                }
                            }
                            catch
                            {
                                // ignore diagnostic failures we'll conservatively assume not watched
                            }

                            // compute whether we'd actually mark this episode (used for both dry-run and real runs)
                            bool wouldMark = !alreadyWatchedInShoko && !appliedShokoEpisodeIds.Contains(shokoEpisode.ID) && (shokoEpisode.VideoList?.Count > 0);

                            // prepare log prefix for NLog messages when in dry-run mode
                            var logPrefix = dryRun ? "DRYRUN: " : string.Empty;

                            // We'll compute the final audit entry after attempting the write (if applicable)
                            bool finalWouldMark = false;
                            string? finalReason = null;

                            if (wouldMark)
                            {
                                if (dryRun)
                                {
                                    // dry-run: simulate apply and update counters
                                    appliedShokoEpisodeIds.Add(shokoEpisode.ID);
                                    result = SyncHelper.IncMarkedWatched(result, perUserResults, plexUserName);

                                    finalWouldMark = true;

                                    // noisy info only when would be marked
                                    Logger.Info(
                                        logPrefix + "{User} -> wouldMark={Would} ShokoEpisode={EpId} ({Series} - {EpTitle}) file={File} lastViewed={LastViewed} alreadyWatchedInShoko={Already}",
                                        plexUserName,
                                        finalWouldMark,
                                        shokoEpisode.ID,
                                        shokoEpisode.Series?.PreferredTitle?.Value,
                                        shokoEpisode.PreferredTitle?.Value,
                                        null,
                                        watchedAt,
                                        alreadyWatchedInShoko
                                    );
                                }
                                else
                                {
                                    // real run: attempt to apply only when wouldMark is true
                                    var savedResult = await _userDataService.SetEpisodeWatchedStatus(shokoEpisode, user!, true, watchedAt, true).ConfigureAwait(false);
                                    updated = savedResult != null;

                                    if (updated)
                                    {
                                        appliedShokoEpisodeIds.Add(shokoEpisode.ID);
                                        result = SyncHelper.IncMarkedWatched(result, perUserResults, plexUserName);

                                        finalWouldMark = true;
                                    }
                                    else
                                    {
                                        // write failed to apply — mark as skipped in audit/counters
                                        finalWouldMark = false;
                                        finalReason = "apply_failed";
                                        result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                                    }
                                }
                            }
                            else
                            {
                                // not eligible to be marked — count as skipped for both dry-run and real runs
                                finalWouldMark = false;
                                finalReason = alreadyWatchedInShoko ? "already_watched" : (shokoEpisode.VideoList?.Count == 0 ? "no_files" : "duplicate");
                                result = SyncHelper.IncSkipped(result, perUserResults, plexUserName);
                            }

                            // build and append the immutable audit/change entry with final values
                            var changeFinal = SyncHelper.MakeChange(
                                plexUser: plexUserName,
                                shokoEpisodeId: shokoEpisode.ID,
                                seriesTitle: shokoEpisode.Series?.PreferredTitle?.Value,
                                episodeTitle: shokoEpisode.PreferredTitle?.Value,
                                seasonNumber: shokoEpisode.SeasonNumber,
                                episodeNumber: shokoEpisode.EpisodeNumber,
                                ratingKey: item.RatingKey,
                                guid: item.Guid,
                                filePath: null,
                                lastViewedAt: watchedAt,
                                wouldMark: finalWouldMark,
                                alreadyWatchedInShoko: alreadyWatchedInShoko,
                                reason: finalReason
                            );

                            SyncHelper.AddPerUserChange(result.PerUserChanges, plexUserName, changeFinal);

                            // ——— Vote/rating sync (optional, attach after audit entry) ———
                            if (includeVotes && item.UserRating.HasValue)
                            {
                                try
                                {
                                    // record that a rating candidate was encountered (counts per-Plex-user)
                                    result = SyncHelper.IncVotesFound(result);

                                    var epUserData = _userDataService.GetEpisodeUserData(shokoEpisode, user!);
                                    double? shokoRating = epUserData?.UserRating;
                                    double plexRating = item.UserRating!.Value;

                                    // Only apply when different
                                    if (!shokoRating.HasValue || Math.Abs(shokoRating.Value - plexRating) > 0.05)
                                    {
                                        if (dryRun)
                                        {
                                            result = SyncHelper.IncVotesUpdated(result);
                                            result.PerUserChanges[plexUserName][^1] = result.PerUserChanges[plexUserName][^1] with { PlexUserRating = plexRating, ShokoUserRating = shokoRating };
                                        }
                                        else
                                        {
                                            await _userDataService.RateEpisode(shokoEpisode, user!, plexRating).ConfigureAwait(false);
                                            result = SyncHelper.IncVotesUpdated(result);
                                            result.PerUserChanges[plexUserName][^1] = result.PerUserChanges[plexUserName][^1] with { PlexUserRating = plexRating, ShokoUserRating = shokoRating };
                                        }
                                    }
                                    else
                                    {
                                        result = SyncHelper.IncVotesSkipped(result);
                                        result.PerUserChanges[plexUserName][^1] = result.PerUserChanges[plexUserName][^1] with { PlexUserRating = plexRating, ShokoUserRating = shokoRating };
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Failed to sync vote for episode {EpisodeId}", shokoEpisode.ID);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Error syncing Plex watched item {RatingKey}", item.RatingKey);
                            result = result with { Errors = result.Errors + 1 };
                            perUserResults[plexUserName] = perUserResults[plexUserName] with { Errors = perUserResults[plexUserName].Errors + 1 };
                            result.ErrorsList.Add($"{item.RatingKey ?? item.Guid ?? "unknown"}: {ex.Message}");
                        }
                    }
                }
            }

            // attach per-user summaries to the final result
            result = result with
            {
                PerUser = perUserResults,
            };

            // After processing aggregated watched-state: respect plugin abstraction boundary.
            // Plugin code MUST NOT call server internals; scheduling server-side jobs is the server's responsibility.
            try
            {
                int scheduled = 0;
                // Intentionally do not schedule server-side jobs here — watched-state writes are applied above
                // via IUserDataService.SetEpisodeWatchedStatus(...) and the server may run any follow-up tasks itself.
                result = result with
                {
                    ScheduledJobs = scheduled,
                };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Post-sync scheduling check failed");
            }

            return result;
        }
    }
}
