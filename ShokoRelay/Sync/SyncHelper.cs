using NLog;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync;

/// <summary>Per-Plex-user counters used during synchronization.</summary>
public record PlexWatchedUserResult
{
    /// <summary>Total processed.</summary>
    public int Processed { get; init; }

    /// <summary>Items marked watched.</summary>
    public int MarkedWatched { get; init; }

    /// <summary>Items skipped.</summary>
    public int Skipped { get; init; }

    /// <summary>Errors encountered.</summary>
    public int Errors { get; init; }
}

/// <summary>Represents a single change during sync.</summary>
public record PlexWatchedChange
{
    /// <summary>Plex username.</summary>
    public string PlexUser { get; init; } = string.Empty;

    /// <summary>Shoko episode ID.</summary>
    public int ShokoEpisodeId { get; init; }

    /// <summary>Series title.</summary>
    public string? SeriesTitle { get; init; }

    /// <summary>Episode title.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Season number.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Episode number.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Plex rating key.</summary>
    public string? RatingKey { get; init; }

    /// <summary>Metadata GUID.</summary>
    public string? Guid { get; init; }

    /// <summary>Physical file path.</summary>
    public string? FilePath { get; init; }

    /// <summary>Last viewed timestamp.</summary>
    public DateTime? LastViewedAt { get; init; }

    /// <summary>Whether it would be marked.</summary>
    public bool WouldMark { get; init; }

    /// <summary>Existing state in Shoko.</summary>
    public bool AlreadyWatchedInShoko { get; init; }

    /// <summary>Status reason string.</summary>
    public string? Reason { get; init; }

    /// <summary>Plex user rating.</summary>
    public double? PlexUserRating { get; init; }

    /// <summary>Shoko user rating.</summary>
    public double? ShokoUserRating { get; init; }
}

/// <summary>Aggregate result of a sync run.</summary>
public record PlexWatchedSyncResult
{
    /// <summary>Sync direction.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Whether this was a dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>Global process count.</summary>
    public int Processed { get; init; }

    /// <summary>Global mark count.</summary>
    public int MarkedWatched { get; init; }

    /// <summary>Global skip count.</summary>
    public int Skipped { get; init; }

    /// <summary>Global error count.</summary>
    public int Errors { get; init; }

    /// <summary>Scheduled background jobs count.</summary>
    public int ScheduledJobs { get; init; }

    /// <summary>Total votes identified.</summary>
    public int VotesFound { get; init; }

    /// <summary>Total votes applied.</summary>
    public int VotesUpdated { get; init; }

    /// <summary>Total votes matching current state.</summary>
    public int VotesSkipped { get; init; }

    /// <summary>Total items matched.</summary>
    public int Matched { get; init; }

    /// <summary>List of missing series/episodes.</summary>
    public List<int> MissingMappings { get; init; } = [];

    /// <summary>Diagnostics for missing mappings.</summary>
    public Dictionary<int, List<string>> MissingMappingsDiagnostics { get; init; } = [];

    /// <summary>Per-user stats.</summary>
    public Dictionary<string, PlexWatchedUserResult> PerUser { get; init; } = [];

    /// <summary>Detailed per-user change list.</summary>
    public Dictionary<string, List<PlexWatchedChange>> PerUserChanges { get; init; } = [];

    /// <summary>Encountered error messages.</summary>
    public List<string> ErrorsList { get; init; } = [];
}

/// <summary>Shared helpers used by sync services.</summary>
public static class SyncHelper
{
    #region GUID Parsing

    /// <summary>Parse Shoko episode ID from GUID.</summary>
    public static int? TryParseShokoEpisodeIdFromGuid(string? guid) => ParseGuidId(guid, $"{ShokoRelayInfo.AgentScheme}://episode/{PlexConstants.EpisodePrefix}");

    /// <summary>Parse Shoko series ID from GUID.</summary>
    public static int? TryParseShokoSeriesIdFromGuid(string? guid) => ParseGuidId(guid, $"{ShokoRelayInfo.AgentScheme}://show/");

    private static int? ParseGuidId(string? guid, string prefix)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;
        int idx = guid.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var idStr = new string([.. guid[(idx + prefix.Length)..].TakeWhile(char.IsDigit)]);
        return int.TryParse(idStr, out int id) ? id : null;
    }

    #endregion

    #region Collection Helpers

    /// <summary>Create result buckets for users.</summary>
    public static Dictionary<string, PlexWatchedUserResult> CreatePerUserBuckets(IEnumerable<string> extraUserNames)
    {
        var perUser = new Dictionary<string, PlexWatchedUserResult>(StringComparer.OrdinalIgnoreCase) { ["admin"] = new() };
        foreach (var n in extraUserNames ?? [])
            perUser[n] = new();
        return perUser;
    }

    /// <summary>Add a change to the tracker.</summary>
    public static void AddPerUserChange(Dictionary<string, List<PlexWatchedChange>> dict, string plexUser, PlexWatchedChange change)
    {
        if (change == null || !change.WouldMark)
            return;
        if (!dict.TryGetValue(plexUser, out var list))
            dict[plexUser] = list = [];
        list.Add(change);
    }

    /// <summary>Factory for watched changes.</summary>
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
        new()
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

    #endregion

    #region Stat Counters

    private static PlexWatchedSyncResult UpdateStat(
        PlexWatchedSyncResult res,
        Dictionary<string, PlexWatchedUserResult>? perUser,
        string? user,
        Func<PlexWatchedSyncResult, PlexWatchedSyncResult> global,
        Func<PlexWatchedUserResult, PlexWatchedUserResult> usr
    )
    {
        res = global(res);
        if (perUser != null && !string.IsNullOrWhiteSpace(user) && perUser.TryGetValue(user, out var stats))
            perUser[user] = usr(stats);
        return res;
    }

    /// <summary>Increment processed count.</summary>
    public static PlexWatchedSyncResult IncProcessed(PlexWatchedSyncResult r, Dictionary<string, PlexWatchedUserResult>? pu, string? u = null) =>
        UpdateStat(r, pu, u, x => x with { Processed = x.Processed + 1 }, x => x with { Processed = x.Processed + 1 });

    /// <summary>Increment marked count.</summary>
    public static PlexWatchedSyncResult IncMarkedWatched(PlexWatchedSyncResult r, Dictionary<string, PlexWatchedUserResult>? pu, string? u = null) =>
        UpdateStat(r, pu, u, x => x with { MarkedWatched = x.MarkedWatched + 1 }, x => x with { MarkedWatched = x.MarkedWatched + 1 });

    /// <summary>Increment skip count.</summary>
    public static PlexWatchedSyncResult IncSkipped(PlexWatchedSyncResult r, Dictionary<string, PlexWatchedUserResult>? pu, string? u = null) =>
        UpdateStat(r, pu, u, x => x with { Skipped = x.Skipped + 1 }, x => x with { Skipped = x.Skipped + 1 });

    /// <summary>Increment found votes.</summary>
    public static PlexWatchedSyncResult IncVotesFound(PlexWatchedSyncResult res) => res with { VotesFound = res.VotesFound + 1 };

    /// <summary>Increment updated votes.</summary>
    public static PlexWatchedSyncResult IncVotesUpdated(PlexWatchedSyncResult res) => res with { VotesUpdated = res.VotesUpdated + 1 };

    /// <summary>Increment skipped votes.</summary>
    public static PlexWatchedSyncResult IncVotesSkipped(PlexWatchedSyncResult res) => res with { VotesSkipped = res.VotesSkipped + 1 };

    /// <summary>Record a sync error.</summary>
    public static PlexWatchedSyncResult RecordError(PlexWatchedSyncResult res, Dictionary<string, PlexWatchedUserResult>? perUser = null, string? user = null, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
            res.ErrorsList.Add(message);
        return UpdateStat(res, perUser, user, x => x with { Errors = x.Errors + 1 }, x => x with { Errors = x.Errors + 1 });
    }

    #endregion

    #region Extra Users

    /// <summary>Fetches a transient token for a managed user.</summary>
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
                        userToken = fetched;
                        logger.Info("WatchedSyncService: fetched transient token for managed Plex user '{User}' (id={Id}); not persisted", userName, matched.Id);
                    }
                    else
                        logger.Info("WatchedSyncService: SwitchHomeUser returned no token for managed user '{User}' (id={Id})", userName, matched.Id);
                }
                else
                    logger.Debug("WatchedSyncService: no matching managed/home user found for '{User}'", userName);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "WatchedSyncService: failed to auto-fetch token for managed Plex user '{User}'", userName);
            }
        }
        else
            logger.Info("WatchedSyncService: admin Plex token missing; cannot auto-fetch managed-user token for '{User}'", userName);
        return userToken;
    }

    /// <summary>Fetches episodes for a managed user section.</summary>
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
            string? userToken = await FetchManagedUserTokenAsync(plexAuth, configProvider, userName, pin, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userToken))
                return ([], null);
            string? serverAccessToken = null;
            try
            {
                var clientIdentifier = configProvider.GetPlexClientIdentifier();
                var (TokenValid, Servers, Devices) = await plexAuth.GetPlexServerListAsync(userToken, clientIdentifier, cancellationToken).ConfigureAwait(false);
                var devices = Devices ?? [];
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
            long? minLast = (sinceHours.HasValue && sinceHours.Value > 0) ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600) : null;
            var list = await plexClient.GetSectionEpisodesAsync(target, effectiveToken, cancellationToken, onlyUnwatched: false, guidFilter: null, minLastViewed: minLast).ConfigureAwait(false);
            logger.Info("WatchedSyncService: fetched {Count} watched episodes for user {User} (since={Since})", list?.Count ?? 0, userName, minLast);
            return (list ?? [], null);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Failed to fetch episodes for Plex user '{User}' on {Server}:{Section}", userName, target.ServerUrl, target.SectionId);
            return (new List<PlexMetadataItem>(), $"Failed to fetch episodes for Plex user '{userName}' from {target.ServerUrl}:{target.SectionId} -> {ex.Message}");
        }
    }

    private static readonly HttpClient _sharedHttp = new();

    /// <summary>Checks if a user has access to a section.</summary>
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
            using var resp = await _sharedHttp.SendAsync(accessReq, cancellationToken).ConfigureAwait(false);
            hasAccess = resp.IsSuccessStatusCode;
        }
        catch
        {
            hasAccess = false;
        }
        accessCache[accessKey] = hasAccess;
        return hasAccess;
    }

    #endregion

    #region Utils

    /// <summary>Converts unix seconds to DateTime.</summary>
    public static DateTime? UnixSecondsToDateTime(long? unixSeconds) => (unixSeconds > 0) ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime : null;

    /// <summary>Makes an episode GUID string.</summary>
    public static string MakeEpisodeGuid(int episodeId) => $"{ShokoRelayInfo.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{episodeId}";

    /// <summary>Makes a show GUID string.</summary>
    public static string MakeShowGuid(int seriesId) => $"{ShokoRelayInfo.AgentScheme}://show/{seriesId}";

    #endregion
}
