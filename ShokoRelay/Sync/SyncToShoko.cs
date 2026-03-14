using NLog;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Sync;

/// <summary>Synchronizes watched-state from Plex into Shoko.</summary>
public class SyncToShoko(PlexClient plexClient, IMetadataService metadataService, IUserDataService userDataService, IUserService userService, ConfigProvider configProvider, PlexAuth plexAuth)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly PlexClient _plexClient = plexClient;
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IUserDataService _userDataService = userDataService;
    private readonly IUserService _userService = userService;
    private readonly ConfigProvider _configProvider = configProvider;
    private readonly PlexAuth _plexAuth = plexAuth;

    /// <summary>Sync watched-state from Plex into Shoko database.</summary>
    /// <param name="dryRun">If true, skip database writes.</param>
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

        if (!_plexClient.IsEnabled || _userService.GetUsers().FirstOrDefault() is not { } defaultUser)
            return result;

        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        result = result with { PerUser = SyncHelper.CreatePerUserBuckets(extraEntries.Select(e => e.Name)) };
        var appliedIds = new HashSet<int>();
        var targets = _plexClient.GetConfiguredTargets();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adminItems = await _plexClient
                .GetSectionEpisodesAsync(target, null, cancellationToken, false, null, sinceHours > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (sinceHours.Value * 3600) : null)
                .ConfigureAwait(false);
            var userBuckets = new List<(string Name, List<PlexMetadataItem> Items)>();
            if (!actualExclude)
                userBuckets.Add(("admin", adminItems ?? []));
            foreach (var (Name, Pin) in extraEntries)
            {
                var (eps, err) = await SyncHelper.FetchManagedUserSectionEpisodesAsync(_plexAuth, _plexClient, _configProvider, target, Name, Pin, sinceHours, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(err))
                    result = SyncHelper.RecordError(result, result.PerUser, Name, err);
                userBuckets.Add((Name, eps));
            }

            foreach (var (uName, items) in userBuckets)
            {
                foreach (var item in items)
                {
                    if (item.LibrarySectionId.HasValue && item.LibrarySectionId != target.SectionId)
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }
                    result = SyncHelper.IncProcessed(result, result.PerUser, uName);
                    var ep = SyncHelper.TryParseShokoEpisodeIdFromGuid(item.Guid) is { } id ? _metadataService.GetShokoEpisodeByID(id) : null;
                    if (ep == null || appliedIds.Contains(ep.ID))
                    {
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);
                        continue;
                    }

                    var epUserData = _userDataService.GetEpisodeUserData(ep, defaultUser);
                    bool alreadyWatched = epUserData?.LastPlayedAt != null;
                    bool wouldMark = !alreadyWatched && (ep.VideoList?.Count > 0);
                    DateTime? watchedAt = SyncHelper.UnixSecondsToDateTime(item.LastViewedAt);

                    if (wouldMark)
                    {
                        if (!dryRun)
                            await _userDataService
                                .SetEpisodeWatchedStatus(ep, defaultUser, true, watchedAt, videoReason: Shoko.Abstractions.UserData.Enums.VideoUserDataSaveReason.UserInteraction)
                                .ConfigureAwait(false);
                        appliedIds.Add(ep.ID);
                        result = SyncHelper.IncMarkedWatched(result, result.PerUser, uName);
                        Logger.Info("{0}Plex->Shoko: {1} marked {2} S{3}E{4}", logPrefix, uName, ep.Series?.PreferredTitle?.Value, ep.SeasonNumber, ep.EpisodeNumber);
                    }
                    else
                        result = SyncHelper.IncSkipped(result, result.PerUser, uName);

                    SyncHelper.AddPerUserChange(
                        result.PerUserChanges,
                        uName,
                        SyncHelper.MakeChange(
                            uName,
                            ep.ID,
                            ep.Series?.PreferredTitle?.Value,
                            ep.PreferredTitle?.Value,
                            ep.SeasonNumber,
                            ep.EpisodeNumber,
                            item.RatingKey,
                            item.Guid,
                            null,
                            watchedAt,
                            appliedIds.Contains(ep.ID),
                            alreadyWatched,
                            wouldMark ? null : (alreadyWatched ? "already_watched" : "no_files")
                        )
                    );

                    if (actualVotes && item.UserRating.HasValue)
                    {
                        result = SyncHelper.IncVotesFound(result);
                        if (epUserData?.UserRating == null || Math.Abs(epUserData.UserRating.Value - item.UserRating.Value) > 0.05)
                        {
                            if (!dryRun)
                                await _userDataService.RateEpisode(ep, defaultUser, item.UserRating.Value).ConfigureAwait(false);
                            result = SyncHelper.IncVotesUpdated(result);
                        }
                        else
                            result = SyncHelper.IncVotesSkipped(result);
                    }
                }
            }
        }
        return result;
    }
}
