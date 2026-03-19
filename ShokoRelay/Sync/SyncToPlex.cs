using System.Globalization;
using NLog;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state (and optional votes) from Shoko -> Plex.</summary>
public class SyncToPlex(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider, PlexAuth plexAuth)
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new();
    private readonly PlexClient _plexClient = plexClient;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IUserDataService _userDataService = userDataService;
    private readonly IUserService _userService = userService;
    private readonly ConfigProvider _configProvider = configProvider;
    private readonly PlexAuth _plexAuth = plexAuth;

    #endregion

    #region Synchronization Logic

    /// <summary>Sync watched-state from Shoko into configured Plex libraries.</summary>
    /// <param name="dryRun">If true, skip scrobble execution.</param>
    /// <param name="sinceHours">Optional window to limit processed items.</param>
    /// <param name="includeVotes">Include user ratings.</param>
    /// <param name="excludeAdmin">Ignore admin account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result result.</returns>
    public async Task<PlexWatchedSyncResult> SyncWatchedAsync(bool dryRun, int? sinceHours, bool? includeVotes = null, bool? excludeAdmin = null, CancellationToken cancellationToken = default)
    {
        var result = new PlexWatchedSyncResult();
        var logPrefix = (result = result with { DryRun = dryRun }).DryRun ? "[DRYRUN] " : "";
        var auto = ShokoRelay.Settings.Automation;

        bool actualVotes = includeVotes ?? auto.ShokoSyncWatchedIncludeRatings;
        bool actualExclude = excludeAdmin ?? auto.ShokoSyncWatchedExcludeAdmin;

        if (!_plexClient.IsEnabled || _userService.GetUsers().FirstOrDefault() is not { } shokoUser)
            return result;
        var targets = _plexClient.GetConfiguredTargets();
        if (targets.Count == 0)
            return result;

        var shokoWatched = _userDataService.GetEpisodeUserDataForUser(shokoUser).Where(e => e.IsWatched).ToList();
        if (sinceHours > 0)
            shokoWatched = [.. shokoWatched.Where(e => (e.LastPlayedAt ?? DateTime.MinValue) >= DateTime.UtcNow.AddHours(-sinceHours.Value))];

        result = result with { Processed = shokoWatched.Count };
        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name)) };

        var plexUsers = new List<(string Name, string? Token)>();
        if (!actualExclude)
            plexUsers.Add(("admin", null));
        foreach (var (Name, Pin) in extraEntries)
        {
            var token = await SyncHelper.FetchManagedUserTokenAsync(_plexAuth, _configProvider, Name, Pin, cancellationToken).ConfigureAwait(false);
            if (token != null)
                plexUsers.Add((Name, token));
        }

        var matchedGlobal = new HashSet<int>();
        var accessCache = new Dictionary<string, bool>();
        foreach (var target in targets)
        {
            foreach (var (uName, uToken) in plexUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await SyncHelper.UserHasAccessToSectionAsync(_plexClient, target, uName, uToken, accessCache, cancellationToken))
                    continue;

                result.PerUser[uName] = result.PerUser[uName] with { Processed = shokoWatched.Count };
                var unwatched = await _plexClient.GetSectionEpisodesAsync(target, uToken, cancellationToken, true).ConfigureAwait(false);
                var plexMap = (unwatched ?? []).ToDictionary(i => i.Guid ?? "", i => i.RatingKey ?? "", StringComparer.OrdinalIgnoreCase);

                foreach (var sw in shokoWatched)
                {
                    var ep = _metadataService.GetShokoEpisodeByID(sw.EpisodeID);
                    if (ep == null)
                        continue;

                    var guid = ep.GetPlexGuid();
                    if (!plexMap.TryGetValue(guid, out var rKey))
                        continue;

                    matchedGlobal.Add(sw.EpisodeID);
                    if (!dryRun)
                    {
                        using var req = _plexClient.CreateRequest(HttpMethod.Get, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={rKey}", target.ServerUrl, uToken);
                        using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            continue;
                    }

                    result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                    Logger.Info("{0}Shoko->Plex: {1} scrobbled ep {2} on {3}", logPrefix, uName, sw.EpisodeID, target.ServerUrl);
                    SyncHelper.AddPerUserChange(
                        result.PerUserChanges,
                        uName,
                        SyncHelper.MakeChange(
                            uName,
                            sw.EpisodeID,
                            ep.Series?.PreferredTitle?.Value,
                            ep.PreferredTitle?.Value,
                            ep.SeasonNumber,
                            ep.EpisodeNumber,
                            rKey,
                            guid,
                            null,
                            sw.LastPlayedAt,
                            true,
                            true,
                            plexUserRating: sw.UserRating
                        )
                    );

                    if (actualVotes && sw.HasUserRating)
                    {
                        result = SyncHelper.IncVotesFound(result);
                        if (!dryRun)
                        {
                            using var rateReq = _plexClient.CreateRequest(
                                HttpMethod.Get,
                                $"/:/rate?identifier=com.plexapp.plugins.library&key={rKey}&rating={sw.UserRating.Value.ToString(CultureInfo.InvariantCulture)}",
                                target.ServerUrl,
                                uToken
                            );
                            await Http.SendAsync(rateReq, cancellationToken).ConfigureAwait(false);
                        }
                        result = SyncHelper.IncVotesUpdated(result);
                    }
                }
            }
        }
        var notFoundCount = shokoWatched.Count(e => !matchedGlobal.Contains(e.EpisodeID));
        result = result with { Skipped = result.Skipped + notFoundCount };
        return result;
    }

    #endregion
}
