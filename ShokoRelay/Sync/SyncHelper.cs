using NLog;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync;

#region Data Models

/// <summary>Per-Plex-user counters used during synchronization.</summary>
/// <param name="Processed">Total items processed.</param>
/// <param name="MarkedWatched">Items marked watched.</param>
/// <param name="Skipped">Items skipped.</param>
/// <param name="Errors">Errors encountered.</param>
public record PlexWatchedUserResult(int Processed = 0, int MarkedWatched = 0, int Skipped = 0, int Errors = 0);

/// <summary>Represents a single change during sync.</summary>
/// <param name="PlexUser">Plex username.</param>
/// <param name="ShokoEpisodeId">Shoko episode ID.</param>
/// <param name="SeriesTitle">Series title.</param>
/// <param name="EpisodeTitle">Episode title.</param>
/// <param name="SeasonNumber">Season number.</param>
/// <param name="EpisodeNumber">Episode number.</param>
/// <param name="RatingKey">Plex rating key.</param>
/// <param name="Guid">Metadata GUID.</param>
/// <param name="FilePath">Physical file path.</param>
/// <param name="LastViewedAt">Last viewed timestamp.</param>
/// <param name="WouldMark">Whether it would be marked.</param>
/// <param name="AlreadyWatchedInShoko">Existing state in Shoko.</param>
/// <param name="Reason">Status reason string.</param>
/// <param name="PlexUserRating">Plex user rating.</param>
/// <param name="ShokoUserRating">Shoko user rating.</param>
public record PlexWatchedChange(
    string PlexUser = "",
    int ShokoEpisodeId = 0,
    string? SeriesTitle = null,
    string? EpisodeTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? RatingKey = null,
    string? Guid = null,
    string? FilePath = null,
    DateTime? LastViewedAt = null,
    bool WouldMark = false,
    bool AlreadyWatchedInShoko = false,
    string? Reason = null,
    double? PlexUserRating = null,
    double? ShokoUserRating = null
);

/// <summary>Aggregate result of a sync run.</summary>
/// <param name="Direction">Sync direction.</param>
/// <param name="DryRun">Whether this was a dry run.</param>
/// <param name="Processed">Global process count.</param>
/// <param name="MarkedWatched">Global mark count.</param>
/// <param name="Skipped">Global skip count.</param>
/// <param name="Errors">Global error count.</param>
/// <param name="ScheduledJobs">Scheduled background jobs count.</param>
/// <param name="VotesFound">Total votes identified.</param>
/// <param name="VotesUpdated">Total votes applied.</param>
/// <param name="VotesSkipped">Total votes matching current state.</param>
/// <param name="Matched">Total items matched.</param>
/// <param name="MissingMappings">List of missing series/episodes.</param>
/// <param name="MissingMappingsDiagnostics">Diagnostics for missing mappings.</param>
/// <param name="PerUser">Per-user stats.</param>
/// <param name="PerUserChanges">Detailed per-user change list.</param>
/// <param name="ErrorsList">Encountered error messages.</param>
public record PlexWatchedSyncResult(
    string Direction = "",
    bool DryRun = false,
    int Processed = 0,
    int MarkedWatched = 0,
    int Skipped = 0,
    int Errors = 0,
    int ScheduledJobs = 0,
    int VotesFound = 0,
    int VotesUpdated = 0,
    int VotesSkipped = 0,
    int Matched = 0,
    List<int>? MissingMappings = null,
    Dictionary<int, List<string>>? MissingMappingsDiagnostics = null,
    Dictionary<string, PlexWatchedUserResult>? PerUser = null,
    Dictionary<string, List<PlexWatchedChange>>? PerUserChanges = null,
    List<string>? ErrorsList = null
)
{
    /// <inheritdoc />
    public List<int> MissingMappings { get; init; } = MissingMappings ?? [];

    /// <inheritdoc />
    public Dictionary<int, List<string>> MissingMappingsDiagnostics { get; init; } = MissingMappingsDiagnostics ?? [];

    /// <inheritdoc />
    public Dictionary<string, PlexWatchedUserResult> PerUser { get; init; } = PerUser ?? [];

    /// <inheritdoc />
    public Dictionary<string, List<PlexWatchedChange>> PerUserChanges { get; init; } = PerUserChanges ?? [];

    /// <inheritdoc />
    public List<string> ErrorsList { get; init; } = ErrorsList ?? [];
}

#endregion

/// <summary>Shared helpers used by sync services.</summary>
public static class SyncHelper
{
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

    /// <summary>Checks if a user has access to a section.</summary>
    public static async Task<bool> UserHasAccessToSectionAsync(
        HttpClient httpClient,
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
            using var resp = await httpClient.SendAsync(accessReq, cancellationToken).ConfigureAwait(false);
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
    /// <param name="unixSeconds">The unix timestamp in seconds.</param>
    /// <returns>A UTC DateTime or null if the input is invalid.</returns>
    public static DateTime? UnixSecondsToDateTime(long? unixSeconds) => (unixSeconds > 0) ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime : null;

    #endregion
}
