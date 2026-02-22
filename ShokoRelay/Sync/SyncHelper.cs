using NLog;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync
{
    // Shared public records used by both SyncToShoko and SyncToPlex
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

        // Vote/rating diagnostics
        public double? PlexUserRating { get; init; }
        public double? ShokoUserRating { get; init; }
    }

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
        /// Parse a Shoko episode id from a Plex GUID string.
        /// Expected GUID fragment: {agentScheme}://episode/e{episodeId}[p{part}]
        /// </summary>
        public static int? TryParseShokoEpisodeIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var agent = ShokoRelayInfo.AgentScheme + "://episode/" + Plex.PlexConstants.EpisodePrefix;
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
        /// Parse a Shoko series id from a Plex GUID string.
        /// Expected GUID fragment: {agentScheme}://show/{seriesId}
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

        // Create per-user result buckets (admin + extra user names)
        public static Dictionary<string, PlexWatchedUserResult> CreatePerUserBuckets(IEnumerable<string> extraUserNames)
        {
            var perUser = new Dictionary<string, PlexWatchedUserResult>(StringComparer.OrdinalIgnoreCase);
            perUser["admin"] = new PlexWatchedUserResult();
            foreach (var n in extraUserNames ?? Array.Empty<string>())
                perUser[n] = new PlexWatchedUserResult();
            return perUser;
        }

        // Safe helper to add a PlexWatchedChange into a per-user dictionary.
        // NOTE: only record changes that will actually be applied (WouldMark == true) to keep responses small.
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

        // Factory to create a PlexWatchedChange with named optional parameters (reduces inline boilerplate)
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

        // Increment helpers — update both aggregate result and per-user bucket when provided
        public static PlexWatchedSyncResult IncProcessed(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { Processed = result.Processed + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { Processed = perUser[plexUser!].Processed + 1 };
            return result;
        }

        public static PlexWatchedSyncResult IncMarkedWatched(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { MarkedWatched = result.MarkedWatched + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { MarkedWatched = perUser[plexUser!].MarkedWatched + 1 };
            return result;
        }

        public static PlexWatchedSyncResult IncSkipped(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? plexUser = null)
        {
            result = result with { Skipped = result.Skipped + 1 };
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser))
                perUser[plexUser!] = perUser[plexUser!] with { Skipped = perUser[plexUser!].Skipped + 1 };
            return result;
        }

        public static PlexWatchedSyncResult IncVotesFound(PlexWatchedSyncResult result)
        {
            return result with { VotesFound = result.VotesFound + 1 };
        }

        public static PlexWatchedSyncResult IncVotesUpdated(PlexWatchedSyncResult result)
        {
            return result with { VotesUpdated = result.VotesUpdated + 1 };
        }

        public static PlexWatchedSyncResult IncVotesSkipped(PlexWatchedSyncResult result)
        {
            return result with { VotesSkipped = result.VotesSkipped + 1 };
        }

        // Record an error: increment aggregate Errors, optionally increment per-user Errors, and append to ErrorsList
        public static PlexWatchedSyncResult RecordError(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser = null, string? plexUser = null, string? message = null)
        {
            result = result with { Errors = result.Errors + 1 };
            if (!string.IsNullOrWhiteSpace(message))
                result.ErrorsList.Add(message!);
            if (perUser != null && !string.IsNullOrWhiteSpace(plexUser) && perUser.ContainsKey(plexUser!))
                perUser[plexUser!] = perUser[plexUser!] with { Errors = perUser[plexUser!].Errors + 1 };
            return result;
        }

        // Fetch episodes for a configured managed/home Plex user (returns episodes + optional error message)
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
                var cfg = configProvider.GetSettings();
                string? userToken = null;

                var adminToken = cfg.PlexLibrary.Token;
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
                    var clientIdentifier = cfg.PlexLibrary.ClientIdentifier ?? cfg.PlexAuth.ClientIdentifier ?? string.Empty;
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

                var list = await plexClient.GetSectionEpisodesAsync(target, effectiveToken, cancellationToken).ConfigureAwait(false);

                if (sinceHours.HasValue && sinceHours.Value > 0)
                {
                    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600);
                    list = list.Where(i => i.LastViewedAt.HasValue && i.LastViewedAt.Value >= cutoff).ToList();
                    logger.Info("WatchedSyncService: filtered user {User} episodes to last {Hours}h -> {Count}", userName, sinceHours.Value, list.Count);
                }

                return (list, null);
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
        /// Check (and cache) whether a given Plex user token can access the specified library section.
        /// Returns true for 'admin' without performing a request.
        /// </summary>
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

        public static string MakeEpisodeGuid(int episodeId) => $"{ShokoRelayInfo.AgentScheme}://episode/{Plex.PlexConstants.EpisodePrefix}{episodeId}";

        public static string MakeShowGuid(int seriesId) => $"{ShokoRelayInfo.AgentScheme}://show/{seriesId}";
    }
}
