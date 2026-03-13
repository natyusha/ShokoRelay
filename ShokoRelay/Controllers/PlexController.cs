using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Sync;

namespace ShokoRelay.Controllers;

/// <summary>
/// Manages Plex-specific integrations, including OAuth authentication, discovered library discovery, collection/rating automation, and real-time scrobble webhooks.
/// </summary>
[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayInfo.BasePath)]
public class PlexController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    PlexAuth plexAuth,
    Services.ICollectionService collectionService,
    Services.ICriticRatingService criticRatingService,
    IUserService userService,
    IUserDataService userDataService
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    private readonly PlexAuth _plexAuth = plexAuth;
    private readonly Services.ICollectionService _collectionService = collectionService;
    private readonly Services.ICriticRatingService _criticRatingService = criticRatingService;
    private readonly IUserService _userService = userService;
    private readonly IUserDataService _userDataService = userDataService;

    #region Authentication

    /// <summary>
    /// Initiates the PIN‑based Plex authentication flow by requesting a PIN from Plex.tv.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A JSON payload containing the PIN id, code, and authorization URL.</returns>
    [HttpGet("plex/auth")]
    public async Task<IActionResult> StartPlexAuth(CancellationToken cancellationToken = default)
    {
        try
        {
            PlexPinResponse pin = await _plexAuth.CreatePinAsync(true, cancellationToken);
            if (string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Code))
                return StatusCode(502, new { status = "error", message = "Plex pin response missing id/code." });

            string authUrl = _plexAuth.BuildAuthUrl(pin.Code, ShokoRelayInfo.Name);
            return Ok(
                new
                {
                    status = "ok",
                    pinId = pin.Id,
                    code = pin.Code,
                    authUrl,
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new { status = "error", message = ex.Message });
        }
    }

    /// <summary>
    /// Polls the Plex authentication status for a previously created PIN.
    /// If completed, persists the token and triggers automatic library discovery.
    /// </summary>
    /// <param name="pinId">The identifier of the PIN to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("plex/auth/status")]
    public async Task<IActionResult> GetPlexAuthStatus([FromQuery] string pinId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pinId))
            return BadRequest(new { status = "error", message = "pinId is required" });

        try
        {
            var pin = await _plexAuth.GetPinAsync(pinId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin.AuthToken))
                return Ok(new { status = "pending" });

            _configProvider.UpdatePlexTokenInfo(token: pin.AuthToken);

            try
            {
                await _configProvider.RefreshAdminUsername(_plexAuth, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to fetch admin name: {ex.Message}");
            }

            try
            {
                string clientIdentifier = _configProvider.GetPlexClientIdentifier();
                var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(pin.AuthToken, clientIdentifier, cancellationToken).ConfigureAwait(false);
                PersistDiscoveryResults(discovery);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Plex discovery failed: {ex.Message}");
            }

            return Ok(new { status = "ok", tokenSaved = true });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new { status = "error", message = ex.Message });
        }
    }

    /// <summary>
    /// Revokes the current Plex token and clears all discovered library information.
    /// </summary>
    [HttpPost("plex/auth/unlink")]
    public async Task<IActionResult> UnlinkPlex(CancellationToken cancellationToken = default)
    {
        var token = _configProvider.GetPlexToken();
        if (string.IsNullOrWhiteSpace(token))
            return Ok(new { status = "ok" });

        string clientIdentifier = _configProvider.GetPlexClientIdentifier();
        await _plexAuth.RevokePlexTokenAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);

        _configProvider.DeleteTokenFile();
        return Ok(new { status = "ok" });
    }

    /// <summary>
    /// Forces a rediscovery of servers and library sections using the existing token.
    /// </summary>
    [HttpPost("plex/auth/refresh")]
    public async Task<IActionResult> RefreshPlexLibraries(CancellationToken cancellationToken = default)
    {
        var token = _configProvider.GetPlexToken();
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { status = "error", message = "Plex token is missing." });

        await _configProvider.RefreshAdminUsername(_plexAuth, cancellationToken);
        string clientIdentifier = _configProvider.GetPlexClientIdentifier();

        try
        {
            var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);
            PersistDiscoveryResults(discovery);
            return Ok(new { status = "ok", libraries = CollectDiscoveredLibraries(discovery.ShokoLibraries) });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to refresh libraries: {ex.Message}");
            return StatusCode(502, new { status = "error", message = "Failed to refresh Plex libraries." });
        }
    }

    #endregion

    #region Automation

    /// <summary>
    /// Triggers the generation of Plex collections for the specified series or filter.
    /// </summary>
    [HttpGet("plex/collections/build")]
    public async Task<IActionResult> BuildPlexCollections([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var targets = _plexLibrary.GetConfiguredTargets();
        if (targets == null || targets.Count == 0)
            return NoPlexTargetsResponse(seriesList);

        var r = await _collectionService.BuildCollectionsAsync(seriesList, cancellationToken).ConfigureAwait(false);
        return LogAndReturn("collections-report.log", r, LogHelper.BuildCollectionsReport);
    }

    /// <summary>
    /// Uploads or refreshes posters for Plex collections associated with the specified series.
    /// </summary>
    [HttpGet("plex/collections/posters")]
    public async Task<IActionResult> ApplyCollectionPosters([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var targets = _plexLibrary.GetConfiguredTargets();
        if (targets == null || targets.Count == 0)
            return NoPlexTargetsResponse(seriesList);

        var r = await _collectionService.ApplyCollectionPostersAsync(seriesList, cancellationToken).ConfigureAwait(false);
        return Ok(
            new
            {
                status = "ok",
                processed = r.Processed,
                uploaded = r.Uploaded,
                skipped = r.Skipped,
                errors = r.Errors,
            }
        );
    }

    /// <summary>
    /// Updates series and episode ratings in Plex based on the configured source (TMDB/AniDB).
    /// </summary>
    [HttpGet("plex/ratings/apply")]
    public async Task<IActionResult> ApplyAudienceRatings([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var allowedIds = new HashSet<int>(seriesList.Select(s => s?.ID ?? 0));
        var result = await _criticRatingService.ApplyRatingsAsync(allowedIds, cancellationToken).ConfigureAwait(false);

        return LogAndReturn("ratings-report.log", result, LogHelper.BuildRatingsReport);
    }

    /// <summary>
    /// Triggers collection building and rating application back-to-back for all series.
    /// </summary>
    [HttpGet("plex/automation/run")]
    public async Task<IActionResult> RunPlexAutomationNow(CancellationToken cancellationToken = default)
    {
        if (!_plexLibrary.IsEnabled)
            return BadRequest(new { status = "error", message = "Plex configuration missing." });

        try
        {
            var allSeries = _metadataService.GetAllShokoSeries()?.Cast<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>().ToList() ?? [];
            if (_collectionService != null)
                await _collectionService.BuildCollectionsAsync(allSeries, cancellationToken).ConfigureAwait(false);
            if (_criticRatingService != null)
                await _criticRatingService.ApplyRatingsAsync(null, cancellationToken).ConfigureAwait(false);

            ShokoRelay.MarkPlexAutomationRunNow();
            return Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    #endregion

    #region Webhook

    /// <summary>
    /// Handles real-time scrobble and rating events delivered by Plex webhooks.
    /// </summary>
    [HttpPost("plex/webhook")]
    public async Task<IActionResult> PluginPlexWebhook()
    {
        if (!ShokoRelay.Settings.Automation.AutoScrobble)
            return Ok(new { status = "ignored", reason = "auto_scrobble_disabled" });

        var evt = await ExtractPlexWebhookPayloadAsync().ConfigureAwait(false);
        if (evt == null || evt.Metadata == null)
            return BadRequest(new { status = "error", message = "invalid payload" });

        bool isScrobble = string.Equals(evt.Event, "media.scrobble", StringComparison.OrdinalIgnoreCase);
        bool isRate = string.Equals(evt.Event, "media.rate", StringComparison.OrdinalIgnoreCase);

        if (!(isScrobble || (isRate && ShokoRelay.Settings.Automation.ShokoSyncWatchedIncludeRatings)))
            return Ok(new { status = "ignored", reason = "unsupported_event_type" });

        var (allowed, reason) = await ValidateWebhookSource(evt, ShokoRelay.Settings, HttpContext.RequestAborted);
        if (!allowed)
        {
            Logger.Info("Plex Webhook Ignored: {Reason} | User: {User} | Event: {Event}", reason, evt.Account?.Title, evt.Event);
            return Ok(new { status = "ignored", reason });
        }

        int? shokoEpisodeId = SyncHelper.TryParseShokoEpisodeIdFromGuid(evt.Metadata.Guid);
        if (!shokoEpisodeId.HasValue)
            return Ok(new { status = "ignored", reason = "no_shoko_guid" });

        var shokoEpisode = _metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
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
                Logger.Info("Plex rating applied: user='{User}', series='{Series}', episode='{SeasonEp}', rating={Rating}", evt.Account?.Title, seriesName, seasonEp, ratingValue);
            }
            return Ok(new { status = "ok", rated = true });
        }

        DateTime? watchedAt = evt.Metadata.LastViewedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(evt.Metadata.LastViewedAt.Value).UtcDateTime : null;
        var saved = await _userDataService.SetEpisodeWatchedStatus(shokoEpisode, user, true, watchedAt, videoReason: Shoko.Abstractions.UserData.Enums.VideoUserDataSaveReason.PlaybackEnd).ConfigureAwait(false);

        if (saved != null)
            Logger.Info("Plex scrobble applied: user='{User}', series='{Series}', episode='{SeasonEp}'", evt.Account?.Title, seriesName, seasonEp);
        return Ok(new { status = "ok", marked = saved != null });
    }

    #endregion

    #region Webhook Helpers

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
            return System.Text.Json.JsonSerializer.Deserialize<PlexWebhookPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Allowed, string Reason)> ValidateWebhookSource(PlexWebhookPayload evt, RelayConfig cfg, CancellationToken ct)
    {
        if (!_configProvider.IsManagedServer(evt.Server?.Uuid))
            return (false, "unrecognized_server_uuid");
        var plexUser = evt.Account?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(plexUser))
            return (false, "empty_account_title");

        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        if (extraEntries.Any(e => string.Equals(e.Name, plexUser, StringComparison.OrdinalIgnoreCase)))
            return (true, "allowed_extra_user");

        if (evt.Owner == true)
        {
            string? adminName = _configProvider.GetAdminUsername();
            if (string.IsNullOrEmpty(adminName))
            {
                await _configProvider.RefreshAdminUsername(_plexAuth, ct);
                adminName = _configProvider.GetAdminUsername();
            }
            if (string.IsNullOrEmpty(adminName))
                return cfg.Automation.ShokoSyncWatchedExcludeAdmin ? (false, "admin_excluded_identity_unknown") : (true, "allowed_owner_identity_assumed");
            if (string.Equals(plexUser, adminName, StringComparison.OrdinalIgnoreCase))
                return cfg.Automation.ShokoSyncWatchedExcludeAdmin ? (false, "admin_excluded_by_config") : (true, "allowed_admin");
            return (false, $"unauthorized_managed_user ({plexUser})");
        }
        return (false, $"user_not_authorized ({plexUser})");
    }

    #endregion
}
