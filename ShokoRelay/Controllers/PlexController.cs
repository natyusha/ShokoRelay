using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Services;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Controllers;

/// <summary>Manages Plex-specific integrations including authentication, metadata automation, and image sync.</summary>
[ApiController]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
[Route(ShokoRelayConstants.BasePath)]
public class PlexController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    PlexAuth plexAuth,
    ICollectionService collectionService,
    ICriticRatingService criticRatingService,
    IUserService userService,
    IUserDataService userDataService,
    IImageSyncService imageSyncService
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Fields

    private readonly PlexAuth _plexAuth = plexAuth;
    private readonly ICollectionService _collectionService = collectionService;
    private readonly ICriticRatingService _criticRatingService = criticRatingService;
    private readonly IUserService _userService = userService;
    private readonly IUserDataService _userDataService = userDataService;
    private readonly IImageSyncService _imageSyncService = imageSyncService;

    #endregion

    #region Authentication

    /// <summary>Initiates the PIN‑based Plex authentication flow.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Auth response containing code and URL.</returns>
    [HttpGet("plex/auth")]
    public async Task<IActionResult> StartPlexAuth(CancellationToken cancellationToken = default)
    {
        try
        {
            PlexPinResponse pin = await _plexAuth.CreatePinAsync(true, cancellationToken);
            if (string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Code))
                return StatusCode(502, new RelayResponse<object>(Status: "error", Message: "Plex response missing id/code."));

            string authUrl = _plexAuth.BuildAuthUrl(pin.Code, ShokoRelayConstants.Name);
            return Ok(
                new RelayResponse<object>(
                    Data: new
                    {
                        pinId = pin.Id,
                        code = pin.Code,
                        authUrl,
                    }
                )
            );
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Polls the Plex authentication status for a previously created PIN.</summary>
    /// <param name="pinId">PIN ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Status response.</returns>
    [HttpGet("plex/auth/status")]
    public async Task<IActionResult> GetPlexAuthStatus([FromQuery] string pinId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pinId))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "pinId is required"));

        try
        {
            var pin = await _plexAuth.GetPinAsync(pinId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin.AuthToken))
                return Ok(new RelayResponse<object>(Status: "pending"));

            Logger.Info("Plex: Authentication successful -> Saving token and discovering libraries...");
            ConfigProvider.UpdatePlexTokenInfo(token: pin.AuthToken);

            try
            {
                await ConfigProvider.RefreshAdminUsername(_plexAuth, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Plex: Failed to fetch admin name {ex.Message}");
            }

            try
            {
                string clientIdentifier = ConfigProvider.GetPlexClientIdentifier();
                var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(pin.AuthToken, clientIdentifier, cancellationToken).ConfigureAwait(false);
                PersistDiscoveryResults(discovery);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Plex: Discovery failed {ex.Message}");
            }

            return Ok(new RelayResponse<object>(Data: new { tokenSaved = true }));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Forces a rediscovery of servers and libraries.</summary>
    [HttpPost("plex/auth/refresh")]
    public Task<IActionResult> RefreshPlexLibraries() =>
        string.IsNullOrWhiteSpace(ConfigProvider.GetPlexToken())
            ? Task.FromResult<IActionResult>(Unauthorized(new RelayResponse<object>(Status: "error", Message: "Plex token is missing.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexAuthRefresh,
                ShokoRelayConstants.LogPlexDiscovery,
                LogHelper.BuildDiscoveryReport,
                async () =>
                {
                    Logger.Info("Plex: Refreshing servers and libraries...");
                    await ConfigProvider.RefreshAdminUsername(_plexAuth, HttpContext.RequestAborted).ConfigureAwait(false);
                    var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(ConfigProvider.GetPlexToken(), ConfigProvider.GetPlexClientIdentifier(), HttpContext.RequestAborted).ConfigureAwait(false);
                    PersistDiscoveryResults(discovery);
                    return CollectDiscoveredLibraries(discovery.ShokoLibraries);
                },
                SyncHelper.SyncLock
            );

    /// <summary>Revokes the current Plex token.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success status.</returns>
    [HttpPost("plex/auth/unlink")]
    public async Task<IActionResult> UnlinkPlex(CancellationToken cancellationToken = default)
    {
        var token = ConfigProvider.GetPlexToken();
        if (string.IsNullOrWhiteSpace(token))
            return Ok(new RelayResponse<object>());

        Logger.Info("Plex: Unlinking account and revoking token...");
        string clientIdentifier = ConfigProvider.GetPlexClientIdentifier();
        await _plexAuth.RevokePlexTokenAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);

        ConfigProvider.DeleteTokenFile();
        return Ok(new RelayResponse<object>());
    }

    #endregion

    #region Automation


    /// <summary>Triggers a partial library scan in Plex for specific series.</summary>
    /// <param name="filter">Comma-separated list of series IDs to refresh.</param>
    /// <returns>A response containing the count of successful refresh requests sent.</returns>
    [HttpGet("plex/library/refresh")]
    public async Task<IActionResult> RefreshPlexSeries([FromQuery] string filter)
    {
        var validation = ValidateFilterOrBadRequest(filter, out var ids);
        if (validation != null)
            return validation;

        if (!PlexLibrary.IsEnabled)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex is not configured."));

        int triggeredCount = 0;
        foreach (var id in ids)
        {
            var series = MetadataService.GetShokoSeriesByID(id);
            if (series == null)
                continue;

            foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, MetadataService))
            {
                if (await PlexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false))
                    triggeredCount++;
            }
        }

        return Ok(new RelayResponse<object>(Data: new { triggered = triggeredCount }));
    }

    /// <summary>Triggers the generation of Plex collections.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB IDs.</param>
    /// <returns>A collection build report.</returns>
    [HttpGet("plex/collections/build")]
    public Task<IActionResult> BuildPlexCollections([FromQuery] string? filter = null) =>
        ValidatePlexFilterRequest(filter, out var seriesList, out _) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexCollectionsBuild,
                ShokoRelayConstants.LogPlexCollections,
                LogHelper.BuildCollectionsReport,
                () => _collectionService.BuildCollectionsAsync(seriesList, CancellationToken.None),
                SyncHelper.SyncLock
            );

    /// <summary>Refreshes posters for Plex collections.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB IDs.</param>
    /// <returns>A task representing the result of the poster application.</returns>
    [HttpGet("plex/collections/posters")]
    public Task<IActionResult> ApplyCollectionPosters([FromQuery] string? filter = null) =>
        ValidatePlexFilterRequest(filter, out var seriesList, out _) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexCollectionsPosters,
                ShokoRelayConstants.LogPlexPosters,
                LogHelper.BuildApplyPostersReport,
                () => _collectionService.ApplyCollectionPostersAsync(seriesList, HttpContext.RequestAborted),
                SyncHelper.SyncLock
            );

    /// <summary>Updates ratings in Plex based on Shoko metadata.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB IDs.</param>
    /// <returns>A ratings update report.</returns>
    [HttpGet("plex/ratings/apply")]
    public Task<IActionResult> ApplyAudienceRatings([FromQuery] string? filter = null) =>
        ValidatePlexFilterRequest(filter, out var seriesList, out _) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexRatingsApply,
                ShokoRelayConstants.LogPlexRatings,
                LogHelper.BuildRatingsReport,
                () => _criticRatingService.ApplyRatingsAsync(seriesList.Select(s => s?.ID ?? 0).OfType<int>(), CancellationToken.None),
                SyncHelper.SyncLock
            );

    /// <summary>Synchronizes Plex-generated episode screenshots back to Shoko.</summary>
    /// <returns>A task representing the result of the image synchronization run.</returns>
    [HttpGet("plex/images/sync")]
    public Task<IActionResult> SyncPlexImages() =>
        !PlexLibrary.IsEnabled
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex server configuration is missing or no library selected.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexImagesSync,
                ShokoRelayConstants.LogPlexImages,
                LogHelper.BuildImageSyncReport,
                () => _imageSyncService.SyncImagesAsync(cancellationToken: CancellationToken.None),
                SyncHelper.SyncLock
            );

    /// <summary>Triggers collection, rating, and image sync automation back-to-back.</summary>
    /// <returns>A task representing the result of the automation run.</returns>
    [HttpGet("plex/automation/run")]
    public Task<IActionResult> RunPlexAutomationNow() =>
        !PlexLibrary.IsEnabled
            ? Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex configuration missing.")))
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexAutomationRun,
                ShokoRelayConstants.LogPlexAutomation,
                (sb, _) => sb.AppendLine("Automation run complete."),
                async () =>
                {
                    var allSeries = MetadataService.GetAllShokoSeries()?.Cast<IShokoSeries?>().ToList() ?? [];
                    await _collectionService.BuildCollectionsAsync(allSeries, HttpContext.RequestAborted).ConfigureAwait(false);
                    await _criticRatingService.ApplyRatingsAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
                    if (Settings.Advanced.EnableImageSync)
                        await _imageSyncService.SyncImagesAsync(cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
                    MarkPlexAutomationRunNow();
                    return true;
                },
                SyncHelper.SyncLock
            );

    #endregion

    #region Webhook

    /// <summary>Handles scrobble and rating events from Plex webhooks.</summary>
    /// <returns>Webhook result status.</returns>
    [HttpPost("plex/webhook")]
    public async Task<IActionResult> PluginPlexWebhook()
    {
        if (!Settings.Automation.AutoScrobble)
            return Ok(new { status = "ignored", reason = "auto_scrobble_disabled" });

        var evt = await ExtractPlexWebhookPayloadAsync().ConfigureAwait(false);
        if (evt == null || evt.Metadata == null)
            return BadRequest(new { status = "error", message = "invalid payload" });

        bool isScrobble = string.Equals(evt.Event, "media.scrobble", StringComparison.OrdinalIgnoreCase);
        bool isRate = string.Equals(evt.Event, "media.rate", StringComparison.OrdinalIgnoreCase);

        if (!(isScrobble || (isRate && Settings.Automation.ShokoSyncWatchedIncludeRatings)))
            return Ok(new { status = "ignored", reason = "unsupported_event_type" });

        var (allowed, reason) = await ValidateWebhookSource(evt, Settings, HttpContext.RequestAborted);
        if (!allowed)
        {
            Logger.Info("Plex: Webhook ignored -> {Reason} | User: {User} | Event: {Event}", reason, evt.Account?.Title, evt.Event);
            return Ok(new { status = "ignored", reason });
        }

        int? shokoEpisodeId = PlexHelper.ExtractShokoEpisodeIdFromGuid(evt.Metadata.Guid);
        if (!shokoEpisodeId.HasValue)
            return Ok(new { status = "ignored", reason = "no_shoko_guid" });

        var shokoEpisode = MetadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
        if (shokoEpisode == null)
            return Ok(new { status = "ignored", reason = "episode_not_found" });

        var user = _userService.GetUsers().FirstOrDefault();
        if (user == null)
            return Ok(new { status = "ignored", reason = "no_shoko_user" });

        string seriesName = evt.Metadata.GrandparentTitle ?? shokoEpisode.Series?.PreferredTitle?.Value ?? "Unknown Series";
        string seasonEp = $"S{evt.Metadata.ParentIndex ?? 0:D2}E{evt.Metadata.Index ?? 0:D2}";

        if (isRate)
        {
            double? ratingValue = evt.Metadata.UserRating;
            if (ratingValue.HasValue)
            {
                await _userDataService.RateEpisode(shokoEpisode, user, ratingValue.Value).ConfigureAwait(false);
                Logger.Info("Plex: Rating applied -> user='{User}', series='{Series}', episode='{SeasonEp}', rating={Rating}", evt.Account?.Title, seriesName, seasonEp, ratingValue);
            }
            return Ok(new { status = "ok", rated = true });
        }

        DateTime? watchedAt = evt.Metadata.LastViewedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(evt.Metadata.LastViewedAt.Value).UtcDateTime : null;
        var saved = await _userDataService.SetEpisodeWatchedStatus(shokoEpisode, user, true, watchedAt, videoReason: VideoUserDataSaveReason.PlaybackEnd).ConfigureAwait(false);

        if (saved != null)
            Logger.Info("Plex: Scrobble applied -> user='{User}', series='{Series}', episode='{SeasonEp}'", evt.Account?.Title, seriesName, seasonEp);
        return Ok(new { status = "ok", marked = saved != null });
    }

    #endregion

    #region Webhook Helpers

    /// <summary>Extracts and parses the raw JSON payload from the Plex webhook request stream.</summary>
    /// <returns>A deserialized <see cref="PlexWebhookPayload"/>, or null if empty/malformed.</returns>
    private async Task<PlexWebhookPayload?> ExtractPlexWebhookPayloadAsync()
    {
        string? payloadJson = null;
        if (Request.HasFormContentType && Request.Form.ContainsKey("payload"))
            payloadJson = Request.Form["payload"].ToString();
        else
        {
            using var sr = new StreamReader(Request.Body);
            payloadJson = await sr.ReadToEndAsync().ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            // Case sensitive to prevent a type conflict between "guid" (string) and "Guid" (array).
            return System.Text.Json.JsonSerializer.Deserialize<PlexWebhookPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Plex: Failed to deserialize webhook payload. Raw JSON: {Json}", payloadJson);
            return null;
        }
    }

    /// <summary>Validates the incoming webhook request source against known servers and authorized users.</summary>
    /// <param name="evt">The parsed Plex webhook payload.</param>
    /// <param name="cfg">The active plugin configuration settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple indicating whether the request is authorized (Allowed) and the descriptive reason (Reason) if rejected.</returns>
    private async Task<(bool Allowed, string Reason)> ValidateWebhookSource(PlexWebhookPayload evt, RelayConfig cfg, CancellationToken ct)
    {
        if (!ConfigProvider.IsManagedServer(evt.Server?.Uuid))
            return (false, "unrecognized_server_uuid");
        var plexUser = evt.Account?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(plexUser))
            return (false, "empty_account_title");

        var userType = cfg.Automation.ShokoSyncWatchedUserType;
        if (userType == SyncUserType.None)
            return (false, "sync_users_set_to_none");

        if (userType is SyncUserType.All or SyncUserType.Extra)
        {
            var extraEntries = ConfigProvider.GetExtraPlexUserEntries();
            if (extraEntries.Any(e => string.Equals(e.Name, plexUser, StringComparison.OrdinalIgnoreCase)))
                return (true, "allowed_extra_user");
        }

        if (evt.Owner == true)
        {
            bool adminAllowed = userType is SyncUserType.All or SyncUserType.Admin;
            string? adminName = ConfigProvider.GetAdminUsername();
            if (string.IsNullOrEmpty(adminName))
            {
                await ConfigProvider.RefreshAdminUsername(_plexAuth, ct).ConfigureAwait(false);
                adminName = ConfigProvider.GetAdminUsername();
            }
            if (string.IsNullOrEmpty(adminName))
                return !adminAllowed ? (false, "admin_excluded_identity_unknown") : (true, "allowed_owner_identity_assumed");
            if (string.Equals(plexUser, adminName, StringComparison.OrdinalIgnoreCase))
                return !adminAllowed ? (false, "admin_excluded_by_config") : (true, "allowed_admin");
            return (false, $"unauthorized_managed_user ({plexUser})");
        }
        return (false, $"user_not_authorized ({plexUser})");
    }

    #endregion
}
