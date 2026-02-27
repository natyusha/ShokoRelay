using NLog;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync
{
    /// <summary>
    /// Per‑Plex‑user counters used during watched-state synchronization.
    /// </summary>
    public record PlexWatchedUserResult
    {
        public int Processed { get; init; }
        public int MarkedWatched { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
    }

    /// <summary>
    /// Represents a single change that would be applied during watched-state sync, capturing details about the Plex user, episode, titles, GUIDs, and reasons.
    /// </summary>
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

        // Vote/rating diagnostics
        public double? PlexUserRating { get; init; }
        public double? ShokoUserRating { get; init; }
    }

    /// <summary>
    /// Aggregate result of a watched-state synchronization run, including totals,
    /// per-user breakdowns, missing mappings, and diagnostics.
    /// </summary>
    public record PlexWatchedSyncResult
    {
        public int Processed { get; init; }
        public int MarkedWatched { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
        public int ScheduledJobs { get; init; }
        public int VotesFound { get; init; }
        public int VotesUpdated { get; init; }
        public int VotesSkipped { get; init; }

        // Number of Shoko-watched episodes that were matched to at least one Plex target
        public int Matched { get; init; }

        // Shoko episode IDs that had no matching Plex metadata in any configured target
        public List<int> MissingMappings { get; init; } = new();

        // Per-episode diagnostics for missing mappings: maps ShokoEpisodeId -> per-target diagnostic messages
        public Dictionary<int, List<string>> MissingMappingsDiagnostics { get; init; } = new();

        public Dictionary<string, PlexWatchedUserResult> PerUser { get; init; } = new();
        public Dictionary<string, List<PlexWatchedChange>> PerUserChanges { get; init; } = new();
        public List<string> ErrorsList { get; init; } = new();
    }

    /// <summary>
    /// Shared helpers used by the watched-state sync services.
    /// </summary>
    public static class SyncHelper
    {
        /// <summary>
        /// Parse a Shoko episode id from a Plex GUID string. Expected GUID fragment: {agentScheme}://episode/e{episodeId}[p{part}]
        /// </summary>
        public static int? TryParseShokoEpisodeIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var agent = ShokoRelayInfo.AgentScheme + "://episode/" + PlexConstants.EpisodePrefix;
            int idx = guid.IndexOf(agent, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Parse a Shoko series id from a Plex GUID string. Expected GUID fragment: {agentScheme}://show/{seriesId}
        /// </summary>
        public static int? TryParseShokoSeriesIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var agent = ShokoRelayInfo.AgentScheme + "://show/";
            int idx = guid.IndexOf(agent, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            idx += agent.Length; // position at the first digit of series id
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

        /// <summary>
        /// Construct a dictionary mapping Plex user names ("admin" plus any extras) to empty <see cref="PlexWatchedUserResult"/> instances. Used to aggregate per-user sync statistics.
        /// </summary>
        /// <param name="extraUserNames">Additional Plex usernames to include.</param>
        public static Dictionary<string, PlexWatchedUserResult> CreatePerUserBuckets(IEnumerable<string> extraUserNames)
        {
            var perUser = new Dictionary<string, PlexWatchedUserResult>(StringComparer.OrdinalIgnoreCase);
            perUser["admin"] = new PlexWatchedUserResult();
            foreach (var n in extraUserNames ?? Array.Empty<string>())
                perUser[n] = new PlexWatchedUserResult();
            return perUser;
        }

        /// <summary>
        /// Record a watched change in the provided per-user <paramref name="dict"/> only if <paramref name="change"/> would actually be applied.
        /// </summary>
        public static void AddPerUserChange(Dictionary<string, List<PlexWatchedChange>> dict, string plexUser, PlexWatchedChange change)
        {
            if (change == null)
                return;
            // Skip items that would not be marked to reduce response size and focus on actual changes.
            if (!change.WouldMark)
                return;

            if (!dict.TryGetValue(plexUser, out var list))
            {
                list = new List<PlexWatchedChange>();
                dict[plexUser] = list;
            }
            list.Add(change);
        }

        /// <summary>
        /// Factory helper for <see cref="PlexWatchedChange"/> accepting many optional parameters so callers can specify only the values they care about.
        /// </summary>
        public static PlexWatchedChange MakeChange(
            string plexUser,
            int shokoEpisodeId = 0,
            string? seriesTitle = null,
            string? episodeTitle = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            string? ratingKey = null,
            string? guid = null,
            string? filePath = null,
            DateTime? lastViewedAt = null,
            bool wouldMark = false,
            bool alreadyWatchedInShoko = false,
            string? reason = null,
            double? plexUserRating = null,
            double? shokoUserRating = null
        ) =>
            new PlexWatchedChange
            {
                PlexUser = plexUser,
                ShokoEpisodeId = shokoEpisodeId,
                SeriesTitle = seriesTitle,
                EpisodeTitle = episodeTitle,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
                RatingKey = ratingKey,
                Guid = guid,
                FilePath = filePath,
                LastViewedAt = lastViewedAt,
                WouldMark = wouldMark,
                AlreadyWatchedInShoko = alreadyWatchedInShoko,
                Reason = reason,
                PlexUserRating = plexUserRating,
                ShokoUserRating = shokoUserRating,
            };

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.Processed"/> counter on <paramref name="result"/> and optionally update the per-user bucket for <paramref name="plexUser"/>.
        /// </summary>
        /// <param name="result">Aggregate sync result to modify.</param>
        /// <param name="perUser">Optional per-user dictionary to update.</param>
        /// <param name="plexUser">Name of the plex user whose bucket to increment.</param>
        public static PlexWatchedSyncResult IncProcessed(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { Processed = result.Processed + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { Processed = perUser[plexUser!].Processed + 1 };
            return result;
        }

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.MarkedWatched"/> counter on <paramref name="result"/> and optionally adjust the per-user bucket for <paramref name="plexUser"/>.
        /// </summary>
        /// <param name="result">Aggregate sync result to modify.</param>
        /// <param name="perUser">Optional per-user dictionary to update.</param>
        /// <param name="plexUser">Plex username whose bucket should be updated.</param>
        public static PlexWatchedSyncResult IncMarkedWatched(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { MarkedWatched = result.MarkedWatched + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { MarkedWatched = perUser[plexUser!].MarkedWatched + 1 };
            return result;
        }

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.Skipped"/> count on <paramref name="result"/> and optionally update the per-user bucket for <paramref name="plexUser"/>.
        /// </summary>
        /// <param name="result">Aggregate sync result to modify.</param>
        /// <param name="perUser">Optional per-user dictionary to update.</param>
        /// <param name="plexUser">Plex username whose bucket should be updated.</param>
        public static PlexWatchedSyncResult IncSkipped(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { Skipped = result.Skipped + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { Skipped = perUser[plexUser!].Skipped + 1 };
            return result;
        }

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.VotesFound"/> counter on <paramref name="result"/>.
        /// </summary>
        /// <param name="result">Sync result to update.</param>
        public static PlexWatchedSyncResult IncVotesFound(PlexWatchedSyncResult result)
        {
            return result with { VotesFound = result.VotesFound + 1 };
        }

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.VotesUpdated"/> counter on <paramref name="result"/>.
        /// </summary>
        /// <param name="result">Sync result to update.</param>
        public static PlexWatchedSyncResult IncVotesUpdated(PlexWatchedSyncResult result)
        {
            return result with { VotesUpdated = result.VotesUpdated + 1 };
        }

        /// <summary>
        /// Increment the <see cref="PlexWatchedSyncResult.VotesSkipped"/> counter on <paramref name="result"/>.
        /// </summary>
        /// <param name="result">Sync result to update.</param>
        public static PlexWatchedSyncResult IncVotesSkipped(PlexWatchedSyncResult result)
        {
            return result with { VotesSkipped = result.VotesSkipped + 1 };
        }

        /// <summary>
        /// Increment error counters in <paramref name="result"/> and optional per-user buckets, appending an optional <paramref name="message"/>.
        /// </summary>
        public static PlexWatchedSyncResult RecordError(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser = null, string? plexUser = null, string? message = null)
        {
            result = result with { Errors = result.Errors + 1 };
            if (!string.IsNullOrWhiteSpace(message))
                result.ErrorsList.Add(message!);
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser) && perUser.ContainsKey(plexUser!))
                perUser[plexUser!] = perUser[plexUser!] with { Errors = perUser[plexUser!].Errors + 1 };
            return result;
        }

        /// <summary>
        /// Obtain a transient Plex auth token for a managed/home <paramref name="userName"/> using <paramref name="plexAuth"/> and <paramref name="configProvider"/>.
        /// Returns null if unavailable.
        /// </summary>
        public static async Task<string?> FetchManagedUserTokenAsync(PlexAuth plexAuth, ConfigProvider configProvider, string userName, string? pin, CancellationToken cancellationToken = default)
        {
            var logger = LogManager.GetCurrentClassLogger();
            string? userToken = null;

            var adminToken = configProvider.GetPlexToken();
            if (!string.IsNullOrWhiteSpace(adminToken))
            {
                try
                {
                    var homeUsers = await plexAuth.GetHomeUsersAsync(adminToken, cancellationToken).ConfigureAwait(false);
                    var matched = homeUsers.FirstOrDefault(u =>
                        (!string.IsNullOrWhiteSpace(u.Title) && string.Equals(u.Title.Trim(), userName, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(u.Title) && u.Title.IndexOf(userName, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (!string.IsNullOrWhiteSpace(u.Username) && string.Equals(u.Username.Trim(), userName, StringComparison.OrdinalIgnoreCase))
                        || (int.TryParse(userName, out var exId) && u.Id == exId)
                        || (!string.IsNullOrWhiteSpace(u.Uuid) && string.Equals(u.Uuid, userName, StringComparison.OrdinalIgnoreCase))
                    );

                    if (matched != null)
                    {
                        var fetched = await plexAuth.SwitchHomeUserAsync(matched.Id, adminToken, pin, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(fetched))
                        {
                            userToken = fetched; // transient
                            logger.Info("WatchedSyncService: fetched transient token for managed Plex user '{User}' (id={Id}); not persisted", userName, matched.Id);
                        }
                        else
                        {
                            logger.Info("WatchedSyncService: SwitchHomeUser returned no token for managed user '{User}' (id={Id})", userName, matched.Id);
                        }
                    }
                    else
                    {
                        logger.Debug("WatchedSyncService: no matching managed/home user found for '{User}'", userName);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "WatchedSyncService: failed to auto-fetch token for managed Plex user '{User}'", userName);
                }
            }
            else
            {
                logger.Info("WatchedSyncService: admin Plex token missing; cannot auto-fetch managed-user token for '{User}'", userName);
            }

            return userToken;
        }

        /// <summary>
        /// Fetch episodes from a Plex library section for a specified managed user.
        /// Returns a tuple containing the episodes list and an optional error message.
        /// </summary>
        public static async Task<(List<PlexMetadataItem> Episodes, string? ErrorMessage)> FetchManagedUserSectionEpisodesAsync(
            PlexAuth plexAuth,
            PlexClient plexClient,
            ConfigProvider configProvider,
            PlexLibraryTarget target,
            string userName,
            string? pin,
            int? sinceHours,
            CancellationToken cancellationToken = default
        )
        {
            var logger = LogManager.GetCurrentClassLogger();

            try
            {
                string? userToken = null;

                var adminToken = configProvider.GetPlexToken();
                if (!string.IsNullOrWhiteSpace(adminToken))
                {
                    try
                    {
                        var homeUsers = await plexAuth.GetHomeUsersAsync(adminToken, cancellationToken).ConfigureAwait(false);
                        var matched = homeUsers.FirstOrDefault(u =>
                            (!string.IsNullOrWhiteSpace(u.Title) && string.Equals(u.Title.Trim(), userName, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrWhiteSpace(u.Title) && u.Title.IndexOf(userName, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (!string.IsNullOrWhiteSpace(u.Username) && string.Equals(u.Username.Trim(), userName, StringComparison.OrdinalIgnoreCase))
                            || (int.TryParse(userName, out var exId) && u.Id == exId)
                            || (!string.IsNullOrWhiteSpace(u.Uuid) && string.Equals(u.Uuid, userName, StringComparison.OrdinalIgnoreCase))
                        );

                        if (matched != null)
                        {
                            var fetched = await plexAuth.SwitchHomeUserAsync(matched.Id, adminToken, pin, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(fetched))
                            {
                                userToken = fetched; // transient
                                logger.Info("WatchedSyncService: fetched transient token for managed Plex user '{User}' (id={Id}); not persisted", userName, matched.Id);
                            }
                            else
                            {
                                logger.Info("WatchedSyncService: SwitchHomeUser returned no token for managed user '{User}' (id={Id})", userName, matched.Id);
                            }
                        }
                        else
                        {
                            logger.Debug("WatchedSyncService: no matching managed/home user found for '{User}'", userName);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "WatchedSyncService: failed to auto-fetch token for managed Plex user '{User}'", userName);
                    }
                }
                else
                {
                    logger.Info("WatchedSyncService: admin Plex token missing; cannot auto-fetch managed-user token for '{User}'", userName);
                }

                if (string.IsNullOrWhiteSpace(userToken))
                {
                    return (new List<PlexMetadataItem>(), null);
                }

                string? serverAccessToken = null;
                try
                {
                    var clientIdentifier = configProvider.GetPlexClientIdentifier();
                    var plexServerList = await plexAuth.GetPlexServerListAsync(userToken!, clientIdentifier, cancellationToken).ConfigureAwait(false);
                    var devices = plexServerList.Devices ?? new List<PlexDevice>();

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
                    logger.Debug(ex, "WatchedSyncService: failed to resolve server access token for managed user; falling back to user token");
                }

                var effectiveToken = !string.IsNullOrWhiteSpace(serverAccessToken) ? serverAccessToken : userToken;

                // managed‑user views: fetch all episodes, optionally limited by lookback.
                long? minLast = null;
                if (sinceHours.HasValue && sinceHours.Value > 0)
                    minLast = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600);

                // likewise request only watched items for managed users. this keeps the  payload small and mirrors the admin query above.
                var list = await plexClient.GetSectionEpisodesAsync(target, effectiveToken, cancellationToken, onlyUnwatched: false, guidFilter: null, minLastViewed: minLast).ConfigureAwait(false);
                logger.Info("WatchedSyncService: fetched {Count} watched episodes for user {User} (since={Since})", list?.Count ?? 0, userName, minLast);

                return (list ?? new List<PlexMetadataItem>(), null);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to fetch episodes for Plex user '{User}' on {Server}:{Section}", userName, target.ServerUrl, target.SectionId);
                return (new List<PlexMetadataItem>(), $"Failed to fetch episodes for Plex user '{userName}' from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
            }
        }

        // Shared HttpClient for helper requests
        private static readonly HttpClient _http = new HttpClient();

        /// <summary>
        /// Determine whether the specified Plex user token has access to the given library <paramref name="target"/> by querying Plex for a small metadata set.
        /// </summary>
        /// <param name="plexAuth">Plex auth helper.</param>
        /// <param name="configProvider">Configuration provider for retrieving admin token.</param>
        /// <param name="plexClient">Plex client for making requests.</param>
        /// <param name="target">Library target to test access against.</param>
        /// <param name="userName">Name of the managed user.</param>
        /// <param name="pin">Optional PIN for switching home user.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<bool> UserHasAccessToSectionAsync(
            PlexClient plexClient,
            PlexLibraryTarget target,
            string plexUserName,
            string? plexToken,
            Dictionary<string, bool> accessCache,
            CancellationToken cancellationToken = default
        )
        {
            if (string.Equals(plexUserName, "admin", StringComparison.OrdinalIgnoreCase))
                return true;

            var accessKey = $"{plexUserName}::{target.ServerUrl}::{target.SectionId}";
            if (accessCache.TryGetValue(accessKey, out var hasAccess))
                return hasAccess;

            try
            {
                using var accessReq = plexClient.CreateRequest(HttpMethod.Get, $"/library/sections/{target.SectionId}/all?X-Plex-Container-Size=1", target.ServerUrl, plexToken);
                using var resp = await _http.SendAsync(accessReq, cancellationToken).ConfigureAwait(false);
                hasAccess = resp.IsSuccessStatusCode;
            }
            catch
            {
                hasAccess = false;
            }

            accessCache[accessKey] = hasAccess;
            return hasAccess;
        }

        public static DateTime? UnixSecondsToDateTime(long? unixSeconds)
        {
            if (!unixSeconds.HasValue || unixSeconds.Value <= 0)
                return null;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Create a Plex GUID string for a Shoko episode with the given ID.
        /// </summary>
        public static string MakeEpisodeGuid(int episodeId) => $"{ShokoRelayInfo.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{episodeId}";

        /// <summary>
        /// Create a Plex GUID string for a Shoko show/series with the given ID.
        /// </summary>
        public static string MakeShowGuid(int seriesId) => $"{ShokoRelayInfo.AgentScheme}://show/{seriesId}";
    }
}
