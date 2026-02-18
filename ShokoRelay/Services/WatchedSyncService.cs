using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using Shoko.Abstractions.User;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Services
{
    public record PlexWatchedUserResult
    {
        public int Processed { get; init; }
        public int MarkedWatched { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
    }

    public record PlexWatchedChange
    {
        public string PlexUser { get; init; } = string.Empty;
        public int ShokoEpisodeId { get; init; }
        public string? SeriesTitle { get; init; }
        public string? EpisodeTitle { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        public string? RatingKey { get; init; }
        public string? Guid { get; init; }
        public string? FilePath { get; init; }
        public DateTime? LastViewedAt { get; init; }
        public bool WouldMark { get; init; }
        public bool AlreadyWatchedInShoko { get; init; }
        public string? Reason { get; init; }
    }

    public record PlexWatchedSyncResult
    {
        public int Processed { get; init; }
        public int MarkedWatched { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
        public int ScheduledJobs { get; init; }
        public Dictionary<string, PlexWatchedUserResult> PerUser { get; init; } = new();
        public Dictionary<string, List<PlexWatchedChange>> PerUserChanges { get; init; } = new();
        public List<string> ErrorsList { get; init; } = new();
    }

    public class WatchedSyncService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly PlexClient _plexClient;
        private readonly IMetadataService _metadataService;
        private readonly IUserDataService _userDataService;
        private readonly IUserService _userService;
        private readonly IVideoService _videoService;
        private readonly ConfigProvider _configProvider;
        private readonly PlexAuth _plexAuth;

        public WatchedSyncService(
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
        /// Synchronize watched-state from configured Plex libraries into Shoko user data.
        /// - Only marks episodes as watched when Plex reports a view (does NOT unwatch).
        /// - Uses first available Shoko user returned by <see cref="IUserService.GetUsers"/> when no mapping is available.
        /// - Tries GUID-based mapping first, falls back to file-path -> Shoko video lookup.
        /// </summary>
        public async Task<PlexWatchedSyncResult> SyncWatchedAsync(bool dryRun = false, int? sinceHours = null, CancellationToken cancellationToken = default)
        {
            var result = new PlexWatchedSyncResult();
            Logger.Info("WatchedSyncService: starting SyncWatched (dryRun={Dry}, sinceHours={SinceHours})", dryRun, sinceHours?.ToString() ?? "<none>");

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
            var cfg = _configProvider.GetSettings();
            var extraEntries = _configProvider.GetExtraPlexUserEntries();

            // track which Shoko episodes we've already marked during this sync to avoid duplicate writes
            var appliedShokoEpisodeIds = new HashSet<int>();

            // ensure per-user result buckets exist (include admin as "admin")
            var perUserResults = new Dictionary<string, PlexWatchedUserResult>(StringComparer.OrdinalIgnoreCase);
            perUserResults["admin"] = new PlexWatchedUserResult();
            foreach (var ex in extraEntries)
                perUserResults[ex.Name] = new PlexWatchedUserResult();

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
                    result = result with { Errors = result.Errors + 1 };
                    result.ErrorsList.Add($"Failed to fetch episodes from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
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
                        // Always fetch a fresh managed-user token at runtime (do NOT persist tokens to the secrets file).
                        // Rationale: managed/home user tokens are session-scoped and may return 401 if reused across sessions.
                        string? userToken = null;

                        var adminToken = cfg.PlexLibrary.Token;
                        if (!string.IsNullOrWhiteSpace(adminToken))
                        {
                            try
                            {
                                var homeUsers = await _plexAuth.GetHomeUsersAsync(adminToken, cancellationToken).ConfigureAwait(false);
                                // Try multiple matching strategies: match title OR username, substring on title, numeric id, or uuid
                                var matched = homeUsers.FirstOrDefault(u =>
                                    (!string.IsNullOrWhiteSpace(u.Title) && string.Equals(u.Title.Trim(), exName, StringComparison.OrdinalIgnoreCase))
                                    || (!string.IsNullOrWhiteSpace(u.Title) && u.Title.IndexOf(exName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    || (!string.IsNullOrWhiteSpace(u.Username) && string.Equals(u.Username.Trim(), exName, StringComparison.OrdinalIgnoreCase))
                                    || (int.TryParse(exName, out var exId) && u.Id == exId)
                                    || (!string.IsNullOrWhiteSpace(u.Uuid) && string.Equals(u.Uuid, exName, StringComparison.OrdinalIgnoreCase))
                                );

                                if (matched != null)
                                {
                                    var fetched = await _plexAuth.SwitchHomeUserAsync(matched.Id, adminToken, exPin, cancellationToken).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(fetched))
                                    {
                                        userToken = fetched;
                                        // DO NOT persist; token is intentionally transient
                                        Logger.Info("WatchedSyncService: fetched transient token for managed Plex user '{User}' (id={Id}); not persisted", exName, matched.Id);
                                    }
                                    else
                                    {
                                        Logger.Info("WatchedSyncService: SwitchHomeUser returned no token for managed user '{User}' (id={Id})", exName, matched.Id);
                                    }
                                }
                                else
                                {
                                    Logger.Debug("WatchedSyncService: no matching managed/home user found for '{User}'", exName);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "WatchedSyncService: failed to auto-fetch token for managed Plex user '{User}'", exName);
                            }
                        }
                        else
                        {
                            Logger.Info("WatchedSyncService: admin Plex token missing; cannot auto-fetch managed-user token for '{User}'", exName);
                        }

                        // If still no token, skip to avoid mirrored admin results
                        if (string.IsNullOrWhiteSpace(userToken))
                        {
                            extraUserEpisodes[exName] = new List<PlexMetadataItem>();
                            continue;
                        }

                        // Resolve server-specific access token for this managed user (preferred). If unavailable, fall back to the raw user token.
                        string? serverAccessToken = null;
                        try
                        {
                            var clientIdentifier = cfg.PlexLibrary.ClientIdentifier ?? cfg.PlexAuth.ClientIdentifier ?? string.Empty;
                            var plexServerList = await _plexAuth.GetPlexServerListAsync(userToken!, clientIdentifier, cancellationToken).ConfigureAwait(false);
                            var devices = plexServerList.Devices ?? new List<PlexDevice>();

                            // Match device by any connection URI that equals/starts-with our target.ServerUrl
                            foreach (var dev in devices)
                            {
                                if (dev?.Connections == null)
                                    continue;

                                foreach (var c in dev.Connections)
                                {
                                    if (string.IsNullOrWhiteSpace(c?.Uri) || string.IsNullOrWhiteSpace(target.ServerUrl))
                                        continue;

                                    try
                                    {
                                        var connUri = new Uri(c.Uri.TrimEnd('/'));
                                        var tgtUri = new Uri(target.ServerUrl.TrimEnd('/'));
                                        if (string.Equals(connUri.Host, tgtUri.Host, StringComparison.OrdinalIgnoreCase) && connUri.Port == tgtUri.Port)
                                        {
                                            serverAccessToken = dev.AccessToken;
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                        // ignore malformed connection URIs and continue matching by string
                                        if (c.Uri.TrimEnd('/').Equals(target.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                                        {
                                            serverAccessToken = dev.AccessToken;
                                            break;
                                        }
                                    }
                                }

                                if (!string.IsNullOrWhiteSpace(serverAccessToken))
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug(ex, "WatchedSyncService: failed to resolve server access token for managed user; falling back to user token");
                        }

                        var effectiveToken = !string.IsNullOrWhiteSpace(serverAccessToken) ? serverAccessToken : userToken;

                        var list = await _plexClient.GetSectionEpisodesAsync(target, effectiveToken, cancellationToken).ConfigureAwait(false);

                        if (sinceHours.HasValue && sinceHours.Value > 0)
                        {
                            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600);
                            list = list.Where(i => i.LastViewedAt.HasValue && i.LastViewedAt.Value >= cutoff).ToList();
                            Logger.Info("WatchedSyncService: filtered user {User} episodes to last {Hours}h -> {Count}", exName, sinceHours.Value, list.Count);
                        }

                        extraUserEpisodes[exName] = list;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to fetch episodes for Plex user '{User}' on {Server}:{Section}", exName, target.ServerUrl, target.SectionId);
                        result = result with { Errors = result.Errors + 1 };
                        result.ErrorsList.Add($"Failed to fetch episodes for Plex user '{exName}' from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
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
                            result = result with
                            {
                                Skipped = result.Skipped + 1,
                            };
                            perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };

                            if (dryRun)
                            {
                                var change = new PlexWatchedChange
                                {
                                    PlexUser = plexUserName,
                                    ShokoEpisodeId = 0,
                                    EpisodeTitle = item.Title,
                                    RatingKey = item.RatingKey,
                                    Guid = item.Guid,
                                    FilePath = item.Media?.SelectMany(m => m.Part ?? Enumerable.Empty<PlexPart>()).FirstOrDefault()?.File,
                                    LastViewedAt = item.LastViewedAt.HasValue && item.LastViewedAt.Value > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.LastViewedAt.Value).UtcDateTime : null,
                                    WouldMark = false,
                                    AlreadyWatchedInShoko = false,
                                    Reason = "not_in_section",
                                };

                                if (!result.PerUserChanges.TryGetValue(plexUserName, out var l))
                                {
                                    l = new List<PlexWatchedChange>();
                                    result.PerUserChanges[plexUserName] = l;
                                }
                                l.Add(change);
                            }

                            continue;
                        }

                        // increment processed counters
                        result = result with
                        {
                            Processed = result.Processed + 1,
                        };
                        perUserResults[plexUserName] = perUserResults[plexUserName] with { Processed = perUserResults[plexUserName].Processed + 1 };

                        try
                        {
                            // 1) Try GUID mapping
                            int? shokoEpisodeId = TryParseShokoEpisodeIdFromGuid(item.Guid);
                            IShokoEpisode? shokoEpisode = null;

                            if (shokoEpisodeId.HasValue)
                            {
                                shokoEpisode = _metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
                            }

                            // Only map by Shoko GUID; do NOT fall back to file-path or other heuristics.
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
                                    var change = new PlexWatchedChange
                                    {
                                        PlexUser = plexUserName,
                                        ShokoEpisodeId = 0,
                                        SeriesTitle = null,
                                        EpisodeTitle = item.Title,
                                        SeasonNumber = null,
                                        EpisodeNumber = item.Index,
                                        RatingKey = item.RatingKey,
                                        Guid = item.Guid,
                                        FilePath = item.Media?.SelectMany(m => m.Part ?? Enumerable.Empty<PlexPart>()).FirstOrDefault()?.File,
                                        LastViewedAt = item.LastViewedAt.HasValue && item.LastViewedAt.Value > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.LastViewedAt.Value).UtcDateTime : null,
                                        WouldMark = false,
                                        AlreadyWatchedInShoko = false,
                                        Reason = "no_shoko_guid",
                                    };

                                    if (!result.PerUserChanges.TryGetValue(plexUserName, out var list))
                                    {
                                        list = new List<PlexWatchedChange>();
                                        result.PerUserChanges[plexUserName] = list;
                                    }
                                    list.Add(change);
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
                                result = result with { Errors = result.Errors + 1 };
                                perUserResults[plexUserName] = perUserResults[plexUserName] with { Errors = perUserResults[plexUserName].Errors + 1 };
                                result.ErrorsList.Add($"No Shoko users available to mark episode {shokoEpisode.ID} as watched");
                                continue;
                            }

                            DateTime? watchedAt = null;
                            if (item.LastViewedAt.HasValue && item.LastViewedAt.Value > 0)
                            {
                                try
                                {
                                    watchedAt = DateTimeOffset.FromUnixTimeSeconds(item.LastViewedAt.Value).UtcDateTime;
                                }
                                catch { }
                            }

                            bool updated = false;
                            bool alreadyWatchedInShoko = false;

                            // Determine if this episode is already watched in Shoko for the default user (best-effort)
                            try
                            {
                                var firstVideo = shokoEpisode.VideoList?.FirstOrDefault();
                                if (firstVideo != null && user != null)
                                {
                                    var vud = _userDataService.GetVideoUserData(firstVideo, user);
                                    if (vud != null && (vud.LastPlayedAt != null || vud.PlaybackCount > 0))
                                    {
                                        alreadyWatchedInShoko = true;
                                    }
                                }
                            }
                            catch
                            {
                                // ignore diagnostic failures — we'll conservatively assume not watched
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
                                    result = result with { MarkedWatched = result.MarkedWatched + 1 };
                                    perUserResults[plexUserName] = perUserResults[plexUserName] with { MarkedWatched = perUserResults[plexUserName].MarkedWatched + 1 };

                                    finalWouldMark = true;

                                    // noisy info only when would be marked
                                    Logger.Info(
                                        logPrefix + "{User} -> wouldMark={Would} ShokoEpisode={EpId} ({Series} - {EpTitle}) file={File} lastViewed={LastViewed} alreadyWatchedInShoko={Already}",
                                        plexUserName,
                                        finalWouldMark,
                                        shokoEpisode.ID,
                                        shokoEpisode.Series?.PreferredTitle?.Value,
                                        shokoEpisode.PreferredTitle?.Value,
                                        item.Media?.SelectMany(m => m.Part ?? Enumerable.Empty<PlexPart>()).FirstOrDefault()?.File,
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
                                        result = result with { MarkedWatched = result.MarkedWatched + 1 };
                                        perUserResults[plexUserName] = perUserResults[plexUserName] with { MarkedWatched = perUserResults[plexUserName].MarkedWatched + 1 };

                                        finalWouldMark = true;
                                    }
                                    else
                                    {
                                        // write failed to apply — mark as skipped in audit/counters
                                        finalWouldMark = false;
                                        finalReason = "apply_failed";
                                        result = result with { Skipped = result.Skipped + 1 };
                                        perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };
                                    }
                                }
                            }
                            else
                            {
                                // not eligible to be marked — count as skipped for both dry-run and real runs
                                finalWouldMark = false;
                                finalReason = alreadyWatchedInShoko ? "already_watched" : (shokoEpisode.VideoList?.Count == 0 ? "no_files" : "duplicate");
                                result = result with { Skipped = result.Skipped + 1 };
                                perUserResults[plexUserName] = perUserResults[plexUserName] with { Skipped = perUserResults[plexUserName].Skipped + 1 };
                            }

                            // build and append the immutable audit/change entry with final values
                            var changeFinal = new PlexWatchedChange
                            {
                                PlexUser = plexUserName,
                                ShokoEpisodeId = shokoEpisode.ID,
                                SeriesTitle = shokoEpisode.Series?.PreferredTitle?.Value,
                                EpisodeTitle = shokoEpisode.PreferredTitle?.Value,
                                SeasonNumber = shokoEpisode.SeasonNumber,
                                EpisodeNumber = shokoEpisode.EpisodeNumber,
                                RatingKey = item.RatingKey,
                                Guid = item.Guid,
                                FilePath = item.Media?.SelectMany(m => m.Part ?? Enumerable.Empty<PlexPart>()).FirstOrDefault()?.File,
                                LastViewedAt = watchedAt,
                                WouldMark = finalWouldMark,
                                AlreadyWatchedInShoko = alreadyWatchedInShoko,
                                Reason = finalReason,
                            };

                            if (!result.PerUserChanges.TryGetValue(plexUserName, out var listFinal))
                            {
                                listFinal = new List<PlexWatchedChange>();
                                result.PerUserChanges[plexUserName] = listFinal;
                            }
                            listFinal.Add(changeFinal);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Error syncing Plex watched item {RatingKey}", item.RatingKey);
                            result = result with { Errors = result.Errors + 1 };
                            perUserResults[plexUserName] = perUserResults[plexUserName] with { Errors = perUserResults[plexUserName].Errors + 1 };
                            result.ErrorsList.Add($"{item.RatingKey ?? item.Guid ?? item.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File ?? "unknown"}: {ex.Message}");
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

        private static int? TryParseShokoEpisodeIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            // GUIDs in Plex can be concatenated or have multiple entries; search for the Shoko agent GUID.
            // Expected form: {agentScheme}://episode/e{episodeId}[p{part}]
            var agent = ShokoRelayInfo.AgentScheme + "://episode/" + Plex.PlexConstants.EpisodePrefix;
            int idx = guid.IndexOf(agent, System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            idx += agent.Length; // position at the first digit of episode id
            if (idx >= guid.Length)
                return null;

            int end = idx;
            while (end < guid.Length && char.IsDigit(guid[end]))
                end++;

            var idStr = guid.Substring(idx, end - idx);
            if (int.TryParse(idStr, out int id))
                return id;

            return null;
        }
    }
}
