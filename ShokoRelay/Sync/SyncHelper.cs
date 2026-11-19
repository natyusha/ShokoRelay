namespace ShokoRelay.Sync;

#region Data Models

/// <summary>Per-Plex-user counters used during synchronization.</summary>
/// <param name="Processed">Total items processed.</param>
/// <param name="MarkedWatched">Items marked watched.</param>
/// <param name="ProgressUpdated">Items whose playback progress was updated.</param>
/// <param name="Skipped">Items skipped.</param>
/// <param name="Errors">Errors encountered.</param>
public record PlexWatchedUserResult(int Processed = 0, int MarkedWatched = 0, int ProgressUpdated = 0, int Skipped = 0, int Errors = 0);

/// <summary>Represents a single change during sync.</summary>
/// <param name="PlexUser">Plex username.</param>
/// <param name="ShokoUser">Target Shoko username.</param>
/// <param name="LibraryName">Plex library section title.</param>
/// <param name="ShokoEpisodeId">Shoko episode ID.</param>
/// <param name="SeriesTitle">Series title.</param>
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
    string? ShokoUser = null,
    string? LibraryName = null,
    int ShokoEpisodeId = 0,
    string? SeriesTitle = null,
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
/// <param name="ProgressUpdated">Global progress update count.</param>
/// <param name="Skipped">Global skip count.</param>
/// <param name="Errors">Global error count.</param>
/// <param name="VotesFound">Total votes identified.</param>
/// <param name="VotesUpdated">Total votes applied.</param>
/// <param name="VotesSkipped">Total votes matching current state.</param>
/// <param name="PerUser">Per-user stats.</param>
/// <param name="PerUserChanges">Detailed per-user change list.</param>
/// <param name="ErrorsList">Encountered error messages.</param>
public record PlexWatchedSyncResult(
    string Direction = "",
    bool DryRun = false,
    int Processed = 0,
    int MarkedWatched = 0,
    int ProgressUpdated = 0,
    int Skipped = 0,
    int Errors = 0,
    int VotesFound = 0,
    int VotesUpdated = 0,
    int VotesSkipped = 0,
    Dictionary<string, PlexWatchedUserResult>? PerUser = null,
    Dictionary<string, List<PlexWatchedChange>>? PerUserChanges = null,
    List<string>? ErrorsList = null
)
{
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
    #region Concurrency

    /// <summary>Global semaphore to prevent concurrent watched-state synchronization tasks.</summary>
    public static readonly SemaphoreSlim SyncLock = new(1, 1);

    #endregion

    #region Cache & Buckets

    /// <summary>Caches Plex Home and Shared Server token lookups for the duration of a single sync run.</summary>
    public sealed class PlexUserTokenCache
    {
        /// <summary>Cached list of Plex Home users.</summary>
        public List<PlexHomeUser>? HomeUsers { get; set; }

        /// <summary>Cached list of Plex devices for the admin account.</summary>
        public List<PlexDevice>? AdminDevices { get; set; }

        /// <summary>Cached list of shared server entries keyed by Plex server ID.</summary>
        public Dictionary<string, List<PlexSharedServerUser>> SharedServersByServer { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cached list of Plex friends/users for display-title matching.</summary>
        public List<PlexFriendUser>? Friends { get; set; }

        /// <summary>Cached resolved tokens keyed by (ServerId, PlexUserName).</summary>
        public Dictionary<(string ServerId, string UserName), (string? Token, bool IsSharedFriend)> ResolvedTokens { get; } = [];
    }

    /// <summary>Represents a fetched Plex user bucket paired with its target Shoko username and token metadata.</summary>
    /// <param name="Name">The Plex username or <c>admin</c>.</param>
    /// <param name="ShokoUsername">The mapped Shoko username, or <c>null</c> to use the default Shoko user.</param>
    /// <param name="Items">The fetched Plex metadata items.</param>
    /// <param name="Token">The resolved Plex access token, or <c>null</c> for admin.</param>
    /// <param name="IsSharedFriend">Whether the token came from a non-Home shared server entry.</param>
    public sealed record PlexUserBucket(string Name, string? ShokoUsername, List<PlexMetadataItem> Items, string? Token, bool IsSharedFriend);

    /// <summary>Create result buckets for users.</summary>
    /// <param name="plexUserNames">Configured Plex usernames.</param>
    /// <param name="includeAdmin">Whether the admin bucket should be initialized.</param>
    /// <returns>A case-insensitive dictionary initialized with user result buckets.</returns>
    public static Dictionary<string, PlexWatchedUserResult> CreatePerUserBuckets(IEnumerable<string> plexUserNames, bool includeAdmin = true)
    {
        var perUser = new Dictionary<string, PlexWatchedUserResult>(StringComparer.OrdinalIgnoreCase);
        if (includeAdmin)
            perUser["admin"] = new();
        foreach (var userName in plexUserNames ?? [])
            if (!string.IsNullOrWhiteSpace(userName))
                perUser.TryAdd(userName, new());
        return perUser;
    }

    /// <summary>Add a change to the tracker.</summary>
    /// <param name="dict">The per-user change dictionary.</param>
    /// <param name="plexUser">The Plex username.</param>
    /// <param name="change">The change record to append.</param>
    public static void AddPerUserChange(Dictionary<string, List<PlexWatchedChange>> dict, string plexUser, PlexWatchedChange change)
    {
        if (change == null || !change.WouldMark)
            return;
        if (!dict.TryGetValue(plexUser, out var list))
            dict[plexUser] = list = [];
        list.Add(change);
    }

    /// <summary>Factory for watched changes.</summary>
    /// <param name="plexUser">Plex username.</param>
    /// <param name="shokoUser">Target Shoko username.</param>
    /// <param name="libraryName">Plex library section title.</param>
    /// <param name="shokoEpisodeId">Shoko episode ID.</param>
    /// <param name="seriesTitle">Series title.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="ratingKey">Plex rating key.</param>
    /// <param name="guid">Metadata GUID.</param>
    /// <param name="filePath">Physical file path.</param>
    /// <param name="lastViewedAt">Last viewed timestamp.</param>
    /// <param name="wouldMark">Whether it would be marked.</param>
    /// <param name="alreadyWatchedInShoko">Existing state in Shoko.</param>
    /// <param name="reason">Status reason string.</param>
    /// <param name="plexUserRating">Plex user rating.</param>
    /// <param name="shokoUserRating">Shoko user rating.</param>
    /// <returns>A populated <see cref="PlexWatchedChange"/> record.</returns>
    public static PlexWatchedChange MakeChange(
        string plexUser,
        string? shokoUser = null,
        string? libraryName = null,
        int shokoEpisodeId = 0,
        string? seriesTitle = null,
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
            ShokoUser = shokoUser,
            LibraryName = libraryName,
            ShokoEpisodeId = shokoEpisodeId,
            SeriesTitle = seriesTitle,
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

    /// <summary>Updates global and per-user sync counters.</summary>
    /// <param name="res">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <param name="global">Transform applied to the global sync result.</param>
    /// <param name="usr">Transform applied to the user's result.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
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
    /// <param name="result">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncProcessed(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? user = null) =>
        UpdateStat(result, perUser, user, entry => entry with { Processed = entry.Processed + 1 }, entry => entry with { Processed = entry.Processed + 1 });

    /// <summary>Increment marked count.</summary>
    /// <param name="result">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncMarkedWatched(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? user = null) =>
        UpdateStat(result, perUser, user, entry => entry with { MarkedWatched = entry.MarkedWatched + 1 }, entry => entry with { MarkedWatched = entry.MarkedWatched + 1 });

    /// <summary>Increment progress update count.</summary>
    /// <param name="result">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncProgressUpdated(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? user = null) =>
        UpdateStat(result, perUser, user, entry => entry with { ProgressUpdated = entry.ProgressUpdated + 1 }, entry => entry with { ProgressUpdated = entry.ProgressUpdated + 1 });

    /// <summary>Increment skip count.</summary>
    /// <param name="result">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncSkipped(PlexWatchedSyncResult result, Dictionary<string, PlexWatchedUserResult>? perUser, string? user = null) =>
        UpdateStat(result, perUser, user, entry => entry with { Skipped = entry.Skipped + 1 }, entry => entry with { Skipped = entry.Skipped + 1 });

    /// <summary>Increment found votes.</summary>
    /// <param name="res">Current sync result.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncVotesFound(PlexWatchedSyncResult res) => res with { VotesFound = res.VotesFound + 1 };

    /// <summary>Increment updated votes.</summary>
    /// <param name="res">Current sync result.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncVotesUpdated(PlexWatchedSyncResult res) => res with { VotesUpdated = res.VotesUpdated + 1 };

    /// <summary>Increment skipped votes.</summary>
    /// <param name="res">Current sync result.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult IncVotesSkipped(PlexWatchedSyncResult res) => res with { VotesSkipped = res.VotesSkipped + 1 };

    /// <summary>Record a sync error.</summary>
    /// <param name="res">Current sync result.</param>
    /// <param name="perUser">Per-user counter dictionary.</param>
    /// <param name="user">Target Plex username.</param>
    /// <param name="message">Optional error message to append.</param>
    /// <returns>The updated <see cref="PlexWatchedSyncResult"/>.</returns>
    public static PlexWatchedSyncResult RecordError(PlexWatchedSyncResult res, Dictionary<string, PlexWatchedUserResult>? perUser = null, string? user = null, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
            res.ErrorsList.Add(message);
        return UpdateStat(res, perUser, user, x => x with { Errors = x.Errors + 1 }, x => x with { Errors = x.Errors + 1 });
    }

    #endregion

    #region Resolution & Fetching

    /// <summary>Builds the active list of Plex users (with optional PINs and mapped Shoko usernames) according to the sync user mode.</summary>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="userType">Active sync user mode.</param>
    /// <param name="adminUsername">Optional resolved Plex admin username.</param>
    /// <returns>A tuple indicating whether admin is included and the list of extra Plex users to fetch.</returns>
    public static (bool IncludeAdmin, string? AdminShokoUser, List<(string Name, string? Pin, string? ShokoUser)> ExtraUsers) ResolveActiveSyncUsers(
        ConfigProvider configProvider,
        SyncUserType userType,
        string? adminUsername
    )
    {
        if (userType == SyncUserType.None)
            return (false, null, []);

        var extraEntries = configProvider.GetExtraPlexUserEntries();
        var advancedEntries = configProvider.GetAdvancedUserMappingEntries();

        bool IsAdminAlias(string name) =>
            string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(adminUsername) && string.Equals(name, adminUsername, StringComparison.OrdinalIgnoreCase));

        string? adminShokoUser = null;
        var advMap = new Dictionary<string, (string? Pin, string ShokoUser)>(StringComparer.OrdinalIgnoreCase);
        var advOrder = new List<string>();
        foreach (var group in advancedEntries)
        foreach (var (plexUser, pin) in group.PlexUsers)
        {
            if (IsAdminAlias(plexUser))
                adminShokoUser ??= group.ShokoUser;
            else if (advMap.TryAdd(plexUser, (pin, group.ShokoUser)))
                advOrder.Add(plexUser);
        }

        bool includeAdmin = userType is SyncUserType.All or SyncUserType.Admin || (userType == SyncUserType.Advanced && adminShokoUser != null);
        var extraUsers = new List<(string Name, string? Pin, string? ShokoUser)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (userType is SyncUserType.All or SyncUserType.Extra)
            foreach (var (name, pin) in extraEntries)
            {
                if (IsAdminAlias(name) || !seen.Add(name))
                    continue;
                var hasAdv = advMap.TryGetValue(name, out var adv);
                extraUsers.Add((name, pin ?? (hasAdv ? adv.Pin : null), hasAdv ? adv.ShokoUser : null));
            }

        if (userType is SyncUserType.All or SyncUserType.Advanced)
        {
            var extraPinLookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, pin) in extraEntries)
                if (!extraPinLookup.TryGetValue(name, out var existingPin) || (existingPin == null && pin != null))
                    extraPinLookup[name] = pin;

            foreach (var name in advOrder)
            {
                if (!seen.Add(name))
                    continue;
                var (pin, shokoUser) = advMap[name];
                extraUsers.Add((name, pin ?? extraPinLookup.GetValueOrDefault(name), shokoUser));
            }
        }

        return (includeAdmin, adminShokoUser, extraUsers);
    }

    /// <summary>Resolves a server-scoped Plex access token for a Home user or regular Shared Server friend.</summary>
    /// <param name="plexAuth">Plex Auth service.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="target">Target library.</param>
    /// <param name="userName">Plex username, title, email, or ID.</param>
    /// <param name="pin">Optional 4-digit Home PIN.</param>
    /// <param name="tokenCache">Per-sync-run token cache.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved access token and whether it originated from a shared server entry.</returns>
    public static async Task<(string? Token, bool IsSharedFriend)> ResolvePlexUserTokenAsync(
        PlexAuth plexAuth,
        ConfigProvider configProvider,
        PlexLibraryTarget target,
        string userName,
        string? pin,
        PlexUserTokenCache tokenCache,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        var cacheKey = (target.ServerId, userName);
        if (tokenCache.ResolvedTokens.TryGetValue(cacheKey, out var cached))
            return cached;

        var adminToken = configProvider.GetPlexToken();
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            logger?.LogInformation("WatchedSyncService: admin Plex token missing; cannot fetch token for Plex user '{User}'", userName);
            return tokenCache.ResolvedTokens[cacheKey] = (null, false);
        }

        PlexHomeUser? matchedHome = null;
        try
        {
            tokenCache.HomeUsers ??= await plexAuth.GetHomeUsersAsync(adminToken, cancellationToken).ConfigureAwait(false);
            matchedHome = tokenCache.HomeUsers.FirstOrDefault(u =>
                string.Equals(u.Title?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Username?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Uuid, userName, StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(userName, out var exId) && u.Id == exId)
            );

            if (matchedHome != null)
            {
                var switchedToken = await plexAuth.SwitchHomeUserAsync(matchedHome.Id, adminToken, pin, cancellationToken, matchedHome.Uuid).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(switchedToken))
                {
                    logger?.LogDebug("WatchedSyncService: fetched transient token for Plex Home user '{User}' (id={Id}) not persisted", userName, matchedHome.Id);
                    string? serverAccessToken = null;
                    var targetServerUrlNorm = string.IsNullOrWhiteSpace(target.ServerUrl) ? null : target.ServerUrl.TrimEnd('/');
                    try
                    {
                        var clientIdentifier = configProvider.GetPlexClientIdentifier();
                        var (_, _, allDevices) = await plexAuth.GetPlexServerListAsync(switchedToken, clientIdentifier, cancellationToken).ConfigureAwait(false);
                        foreach (var dev in allDevices ?? [])
                        {
                            if (string.Equals(dev.ClientIdentifier, target.ServerId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dev.AccessToken))
                            {
                                serverAccessToken = dev.AccessToken;
                                break;
                            }
                            foreach (var conn in dev?.Connections ?? [])
                            {
                                if (string.IsNullOrWhiteSpace(conn?.Uri) || targetServerUrlNorm == null)
                                    continue;
                                if (
                                    Uri.TryCreate(conn.Uri.TrimEnd('/'), UriKind.Absolute, out var connUri)
                                    && Uri.TryCreate(targetServerUrlNorm, UriKind.Absolute, out var tgtUri)
                                    && string.Equals(connUri.Host, tgtUri.Host, StringComparison.OrdinalIgnoreCase)
                                    && connUri.Port == tgtUri.Port
                                )
                                {
                                    serverAccessToken = dev!.AccessToken;
                                    break;
                                }
                                if (string.Equals(conn.Uri.TrimEnd('/'), targetServerUrlNorm, StringComparison.OrdinalIgnoreCase))
                                {
                                    serverAccessToken = dev!.AccessToken;
                                    break;
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(serverAccessToken))
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "WatchedSyncService: failed to resolve server access token for Home user; falling back to switched user token");
                    }

                    return tokenCache.ResolvedTokens[cacheKey] = (!string.IsNullOrWhiteSpace(serverAccessToken) ? serverAccessToken : switchedToken, false);
                }

                logger?.LogInformation("WatchedSyncService: SwitchHomeUser returned no token for Home user '{User}' (id={Id}); checking Shared Servers...", userName, matchedHome.Id);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "WatchedSyncService: failed Home lookup for Plex user '{User}'", userName);
        }

        // Fallback for regular non-Home Plex friends (or Home users whose switch failed) via Shared Servers API
        string serverId = target.ServerId;
        if (string.IsNullOrWhiteSpace(serverId) && !string.IsNullOrWhiteSpace(target.ServerUrl))
        {
            try
            {
                tokenCache.AdminDevices ??= (await plexAuth.GetPlexServerListAsync(adminToken, configProvider.GetPlexClientIdentifier(), cancellationToken).ConfigureAwait(false)).Devices;
                string normTarget = target.ServerUrl.TrimEnd('/');
                var ownedServers = tokenCache.AdminDevices.Where(d => d.Owned && d.Provides?.Contains("server", StringComparison.OrdinalIgnoreCase) == true).ToList();
                serverId =
                    ownedServers
                        .FirstOrDefault(d => d.Connections?.Any(c => !string.IsNullOrWhiteSpace(c.Uri) && string.Equals(c.Uri.TrimEnd('/'), normTarget, StringComparison.OrdinalIgnoreCase)) == true)
                        ?.ClientIdentifier
                    ?? (ownedServers.Count == 1 ? ownedServers[0].ClientIdentifier : null)
                    ?? string.Empty;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            try
            {
                if (!tokenCache.SharedServersByServer.TryGetValue(serverId, out var sharedUsers))
                {
                    sharedUsers = await plexAuth.GetSharedServersAsync(serverId, adminToken, cancellationToken).ConfigureAwait(false);
                    tokenCache.SharedServersByServer[serverId] = sharedUsers;
                }

                tokenCache.Friends ??= await plexAuth.GetFriendsAsync(adminToken, cancellationToken).ConfigureAwait(false);
                var matchedFriend = tokenCache.Friends.FirstOrDefault(friend =>
                    string.Equals(friend.Title?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(friend.Username?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(friend.Email?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(friend.Id?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                );

                var sharedMatch = sharedUsers.FirstOrDefault(shared =>
                    !string.IsNullOrWhiteSpace(shared.AccessToken)
                    && (
                        string.Equals(shared.Username?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(shared.Email?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(shared.UserId?.Trim(), userName, StringComparison.OrdinalIgnoreCase)
                        || (
                            matchedFriend != null
                            && (
                                (!string.IsNullOrWhiteSpace(matchedFriend.Username) && string.Equals(shared.Username?.Trim(), matchedFriend.Username.Trim(), StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(matchedFriend.Id) && string.Equals(shared.UserId?.Trim(), matchedFriend.Id.Trim(), StringComparison.OrdinalIgnoreCase))
                            )
                        )
                        || (
                            matchedHome != null
                            && (
                                (!string.IsNullOrWhiteSpace(matchedHome.Username) && string.Equals(shared.Username?.Trim(), matchedHome.Username.Trim(), StringComparison.OrdinalIgnoreCase))
                                || string.Equals(shared.UserId?.Trim(), matchedHome.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                            )
                        )
                    )
                );

                if (sharedMatch != null)
                {
                    logger?.LogDebug("WatchedSyncService: resolved Shared Server token for regular Plex user '{User}'", userName);
                    return tokenCache.ResolvedTokens[cacheKey] = (sharedMatch.AccessToken, true);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "WatchedSyncService: failed Shared Server lookup for Plex user '{User}'", userName);
            }
        }

        logger?.LogInformation("WatchedSyncService: no matching Home or Shared Server token found for Plex user '{User}' on server {ServerId}", userName, serverId);
        return tokenCache.ResolvedTokens[cacheKey] = (null, false);
    }

    /// <summary>Fetches user buckets for sync operations across Admin, Home, and Shared Server Plex users.</summary>
    /// <param name="plexAuth">Plex Auth service.</param>
    /// <param name="plexClient">Plex Client service.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="target">Target library.</param>
    /// <param name="includeAdmin">Whether to include the Plex admin user.</param>
    /// <param name="adminShokoUser">Optional mapped Shoko username for the admin user.</param>
    /// <param name="extraUsers">Active non-admin Plex users, optional PINs, and mapped Shoko usernames.</param>
    /// <param name="tokenCache">Per-sync-run token cache.</param>
    /// <param name="onlyUnwatched">Filter for unwatched items only. Null to return all items.</param>
    /// <param name="hasProgress">Filter for items actively in progress.</param>
    /// <param name="sinceHours">Optional lookback window.</param>
    /// <param name="result">Current sync results state.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the mapped user buckets and the updated sync result.</returns>
    public static async Task<(List<PlexUserBucket> Buckets, PlexWatchedSyncResult Result)> FetchUserBucketsAsync(
        PlexAuth plexAuth,
        PlexClient plexClient,
        ConfigProvider configProvider,
        PlexLibraryTarget target,
        bool includeAdmin,
        string? adminShokoUser,
        List<(string Name, string? Pin, string? ShokoUser)> extraUsers,
        PlexUserTokenCache tokenCache,
        bool? onlyUnwatched,
        bool? hasProgress,
        int? sinceHours,
        PlexWatchedSyncResult result,
        ILogger? logger,
        CancellationToken cancellationToken
    )
    {
        var userBuckets = new List<PlexUserBucket>();
        string typeLabel = target.LibraryType == PlexLibraryType.Movie ? "movies" : "episodes";
        long? minLast = sinceHours > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600) : null;

        if (includeAdmin)
        {
            List<PlexMetadataItem> adminItems =
                target.LibraryType == PlexLibraryType.Movie
                    ? await plexClient.GetSectionMoviesAsync(target, null, cancellationToken, onlyUnwatched, hasProgress, null, minLast).ConfigureAwait(false) ?? []
                    : await plexClient.GetSectionEpisodesAsync(target, null, cancellationToken, onlyUnwatched, hasProgress, null, minLast).ConfigureAwait(false) ?? [];

            logger?.LogInformation(
                "WatchedSyncService: fetched {Count} {TypeLabel} for user admin in library '{Library}' on {Server} (since={Since})",
                adminItems.Count,
                typeLabel,
                target.Title,
                target.ServerName,
                minLast?.ToString() ?? "NULL"
            );

            userBuckets.Add(new PlexUserBucket("admin", adminShokoUser, adminItems, null, false));
        }

        foreach (var (name, pin, shokoUser) in extraUsers)
        {
            try
            {
                var (effectiveToken, isSharedFriend) = await ResolvePlexUserTokenAsync(plexAuth, configProvider, target, name, pin, tokenCache, logger, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(effectiveToken))
                {
                    string msg = $"Could not resolve Plex access token for configured user '{name}' on '{target.ServerName}'";
                    logger?.LogWarning("WatchedSyncService: {Message}", msg);
                    result = RecordError(result, result.PerUser, name, msg);
                    continue;
                }

                List<PlexMetadataItem> list =
                    target.LibraryType == PlexLibraryType.Movie
                        ? await plexClient.GetSectionMoviesAsync(target, effectiveToken, cancellationToken, onlyUnwatched, hasProgress, null, minLast).ConfigureAwait(false) ?? []
                        : await plexClient.GetSectionEpisodesAsync(target, effectiveToken, cancellationToken, onlyUnwatched, hasProgress, null, minLast).ConfigureAwait(false) ?? [];

                logger?.LogInformation(
                    "WatchedSyncService: fetched {Count} {TypeLabel} for user {User} in library '{Library}' on {Server} (since={Since})",
                    list.Count,
                    typeLabel,
                    name,
                    target.Title,
                    target.ServerName,
                    minLast?.ToString() ?? "NULL"
                );
                userBuckets.Add(new PlexUserBucket(name, shokoUser, list, effectiveToken, isSharedFriend));
            }
            catch (Exception ex)
            {
                string msg = $"Failed to fetch {typeLabel} for Plex user '{name}' from {target.ServerUrl}:{target.SectionId}";
                logger?.LogWarning(ex, "WatchedSyncService: {Message}", msg);
                result = RecordError(result, result.PerUser, name, $"{msg} -> {ex.Message}");
            }
        }

        return (userBuckets, result);
    }

    #endregion

    #region Utils

    /// <summary>Converts unix seconds to DateTime.</summary>
    /// <param name="unixSeconds">The unix timestamp in seconds.</param>
    /// <returns>A UTC DateTime or null if the input is invalid.</returns>
    public static DateTime? UnixSecondsToDateTime(long? unixSeconds) => (unixSeconds > 0) ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime : null;

    #endregion
}
