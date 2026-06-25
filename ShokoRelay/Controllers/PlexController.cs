using System.Diagnostics;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.User.Update;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Controllers;

#region Data Models

/// <summary>Result of a full Plex automation run containing elapsed times and individual sub-task results.</summary>
/// <param name="TotalElapsed">The total elapsed time for the entire automation run.</param>
/// <param name="CollectionsElapsed">The elapsed time for the collection building sub-task.</param>
/// <param name="CollectionsResult">The result stats returned by the collection building service.</param>
/// <param name="RatingsElapsed">The elapsed time for the critic rating application sub-task.</param>
/// <param name="RatingsResult">The result stats returned by the critic rating application service.</param>
/// <param name="ImageSyncElapsed">The optional elapsed time for the Plex image synchronization sub-task.</param>
/// <param name="ImageSyncResult">The optional result stats returned by the Plex image synchronization service.</param>
public sealed record PlexAutomationRunResult(
    TimeSpan TotalElapsed,
    TimeSpan CollectionsElapsed,
    BuildCollectionsResult CollectionsResult,
    TimeSpan RatingsElapsed,
    ApplyRatingsResult RatingsResult,
    TimeSpan? ImageSyncElapsed,
    ImageSyncResult? ImageSyncResult
);

#endregion

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
    #region Authentication

    /// <summary>Initiates the PIN‑based Plex authentication flow.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Auth response containing code and URL.</returns>
    [HttpGet("plex/auth")]
    public async Task<IActionResult> StartPlexAuth(CancellationToken cancellationToken = default)
    {
        try
        {
            PlexPinResponse pin = await plexAuth.CreatePinAsync(true, cancellationToken);
            if (string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Code))
                return StatusCode(502, new RelayResponse<object>(Status: "error", Message: "Plex response missing id/code."));

            string authUrl = plexAuth.BuildAuthUrl(pin.Code, ShokoRelayConstants.Name);
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
            var pin = await plexAuth.GetPinAsync(pinId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin.AuthToken))
                return Ok(new RelayResponse<object>(Status: "pending"));

            Logger.Info("Plex: Authentication successful -> Saving token and discovering libraries...");
            ConfigProvider.UpdatePlexTokenInfo(token: pin.AuthToken);

            try
            {
                await ConfigProvider.RefreshAdminUsername(plexAuth, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Plex: Failed to fetch admin name {ex.Message}");
            }

            try
            {
                var discovery = await plexAuth.DiscoverShokoLibrariesAsync(pin.AuthToken, ConfigProvider.GetPlexClientIdentifier(), cancellationToken).ConfigureAwait(false);
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
                LogHelper.BuildDiscoveryReport,
                async () =>
                {
                    Logger.Info("Plex: Refreshing servers and libraries...");
                    await ConfigProvider.RefreshAdminUsername(plexAuth, HttpContext.RequestAborted).ConfigureAwait(false);
                    var discovery = await plexAuth.DiscoverShokoLibrariesAsync(ConfigProvider.GetPlexToken(), ConfigProvider.GetPlexClientIdentifier(), HttpContext.RequestAborted).ConfigureAwait(false);
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
        await plexAuth.RevokePlexTokenAsync(token, ConfigProvider.GetPlexClientIdentifier(), cancellationToken).ConfigureAwait(false);
        ConfigProvider.DeleteTokenFile();

        return Ok(new RelayResponse<object>());
    }

    #endregion

    #region Automation

    /// <summary>Triggers a partial library scan in Plex for specific series.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <returns>A response containing the count of successful refresh requests sent.</returns>
    [HttpGet("plex/library/refresh")]
    public async Task<IActionResult> RefreshPlexSeries([FromQuery] string filter)
    {
        if (ValidateFilterOrBadRequest(filter, out var ids) is { } validation)
            return validation;
        if (!PlexLibrary.IsEnabled)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex is not configured."));

        int triggeredCount = 0;
        foreach (var id in ids)
            if (MetadataService.GetShokoSeriesByID(id) is { } series)
                foreach (var path in VfsShared.ResolveSeriesVfsPaths(series, MetadataService))
                    if (await PlexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false))
                        triggeredCount++;

        return Ok(new RelayResponse<object>(Data: new { triggered = triggeredCount }));
    }

    /// <summary>Triggers the generation of Plex collections.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB IDs.</param>
    /// <param name="assignment">If false, skips assigning series to collections and only applies posters.</param>
    /// <param name="clean">If true, prunes old cached custom posters from Plex's local metadata directory.</param>
    /// <returns>A collection build report.</returns>
    [HttpGet("plex/collections/build")]
    public Task<IActionResult> BuildPlexCollections([FromQuery] string? filter = null, [FromQuery] bool assignment = true, [FromQuery] bool clean = true) =>
        ValidatePlexFilterRequest(filter, out var seriesList, out _) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexCollectionsBuild,
                LogHelper.BuildCollectionsReport,
                () => collectionService.BuildCollectionsAsync(seriesList, assignment, clean, CancellationToken.None),
                SyncHelper.SyncLock
            );

    /// <summary>Updates ratings in Plex based on Shoko metadata.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <returns>A ratings update report.</returns>
    [HttpGet("plex/ratings/apply")]
    public Task<IActionResult> ApplyAudienceRatings([FromQuery] string? filter = null) =>
        ValidatePlexFilterRequest(filter, out var seriesList, out _) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskPlexRatingsApply,
                LogHelper.BuildRatingsReport,
                () => criticRatingService.ApplyRatingsAsync(seriesList.Where(s => s != null).Select(s => s!.ID), CancellationToken.None),
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
                LogHelper.BuildImageSyncReport,
                () => imageSyncService.SyncImagesAsync(cancellationToken: CancellationToken.None),
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
                (sb, r) =>
                {
                    sb.AppendLine($"Plex Automation Run Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine(new string('-', 60));
                    sb.AppendLine();
                    sb.AppendLine($"  Total Elapsed Time         : {r.TotalElapsed.TotalSeconds:F2}s");
                    sb.AppendLine();
                    sb.AppendLine("Tasks Executed:");
                    sb.AppendLine($"  - Collection Generation    : {r.CollectionsElapsed.TotalSeconds:F2}s");
                    sb.AppendLine($"    Log File URL             : {ApiBase}/logs/{ShokoRelayConstants.TaskPlexCollectionsBuild}-report.log");
                    sb.AppendLine($"  - Critic Rating Application: {r.RatingsElapsed.TotalSeconds:F2}s");
                    sb.AppendLine($"    Log File URL             : {ApiBase}/logs/{ShokoRelayConstants.TaskPlexRatingsApply}-report.log");
                    if (r.ImageSyncElapsed.HasValue)
                    {
                        sb.AppendLine($"  - Plex Image Sync          : {r.ImageSyncElapsed.Value.TotalSeconds:F2}s");
                        sb.AppendLine($"    Log File URL             : {ApiBase}/logs/{ShokoRelayConstants.TaskPlexImagesSync}-report.log");
                    }
                },
                async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var allSeries = MetadataService.GetAllShokoSeries()?.Cast<IShokoSeries?>().ToList() ?? [];

                    var swCollections = Stopwatch.StartNew();
                    var collectionRes = await collectionService.BuildCollectionsAsync(allSeries, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    swCollections.Stop();

                    var swRatings = Stopwatch.StartNew();
                    var ratingRes = await criticRatingService.ApplyRatingsAsync(null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    swRatings.Stop();

                    TimeSpan? imageSyncElapsed = null;
                    ImageSyncResult? imageSyncRes = null;
                    if (Settings.Advanced.EnableImageSync)
                    {
                        var swImages = Stopwatch.StartNew();
                        imageSyncRes = await imageSyncService.SyncImagesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        swImages.Stop();
                        imageSyncElapsed = swImages.Elapsed;
                    }
                    sw.Stop();
                    MarkPlexAutomationRunNow();

                    return new PlexAutomationRunResult(sw.Elapsed, swCollections.Elapsed, collectionRes, swRatings.Elapsed, ratingRes, imageSyncElapsed, imageSyncRes);
                },
                SyncHelper.SyncLock
            );

    #endregion

    #region Webhook

    /// <summary>Handles scrobble, rating, and progress events from Plex webhooks.</summary>
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
        bool isProgress = string.Equals(evt.Event, "media.stop", StringComparison.OrdinalIgnoreCase) || string.Equals(evt.Event, "media.pause", StringComparison.OrdinalIgnoreCase);

        if (!(isScrobble || (isProgress && Settings.Automation.ShokoSyncWatchedIncludeProgress) || (isRate && Settings.Automation.ShokoSyncWatchedIncludeRatings)))
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

        var user = userService.GetUsers().FirstOrDefault();
        if (user == null)
            return Ok(new { status = "ignored", reason = "no_shoko_user" });

        string seriesName = evt.Metadata.GrandparentTitle ?? shokoEpisode.Series?.PreferredTitle?.Value ?? "Unknown Series";
        string seasonEp = $"S{evt.Metadata.ParentIndex ?? 0:D2}E{evt.Metadata.Index ?? 0:D2}";

        if (isRate)
        {
            if (evt.Metadata.UserRating.HasValue)
            {
                await userDataService.RateEpisode(shokoEpisode, user, evt.Metadata.UserRating.Value).ConfigureAwait(false);
                Logger.Info("Plex: Rating applied -> user='{User}', series='{Series}', episode='{SeasonEp}', rating={Rating}", evt.Account?.Title, seriesName, seasonEp, evt.Metadata.UserRating.Value);
            }
            return Ok(new { status = "ok", rated = true });
        }

        DateTime? watchedAt = evt.Metadata.LastViewedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(evt.Metadata.LastViewedAt.Value).UtcDateTime : null;

        if (isProgress)
        {
            if (userDataService.GetEpisodeUserData(shokoEpisode, user)?.LastPlayedAt != null)
                return Ok(new { status = "ignored", reason = "already_watched" });

            if (evt.Metadata.ViewOffset.HasValue && evt.Metadata.ViewOffset.Value > 0)
            {
                foreach (var video in shokoEpisode.VideoList ?? [])
                {
                    var videoData = userDataService.GetVideoUserData(video, user);
                    var update = videoData != null ? new VideoUserDataUpdate(videoData) : new VideoUserDataUpdate();
                    update.ProgressPosition = TimeSpan.FromMilliseconds(evt.Metadata.ViewOffset.Value);
                    update.LastUpdatedAt = DateTime.UtcNow;
                    await userDataService.SaveVideoUserData(video, user, update).ConfigureAwait(false);
                }
                Logger.Info(
                    "Plex: Progress updated -> user='{User}', series='{Series}', episode='{SeasonEp}', offset={Offset}",
                    evt.Account?.Title,
                    seriesName,
                    seasonEp,
                    TimeSpan.FromMilliseconds(evt.Metadata.ViewOffset.Value)
                );
                return Ok(new { status = "ok", progress_updated = true });
            }
            return Ok(new { status = "ignored", reason = "no_view_offset" });
        }

        var saved = await userDataService.SetEpisodeWatchedStatus(shokoEpisode, user, true, watchedAt, videoReason: VideoUserDataSaveReason.PlaybackEnd).ConfigureAwait(false);
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
            return JsonSerializer.Deserialize<PlexWebhookPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
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
            if (ConfigProvider.GetExtraPlexUserEntries().Any(e => string.Equals(e.Name, plexUser, StringComparison.OrdinalIgnoreCase)))
                return (true, "allowed_extra_user");

        if (evt.Owner == true)
        {
            bool adminAllowed = userType is SyncUserType.All or SyncUserType.Admin;
            string? adminName = ConfigProvider.GetAdminUsername();
            if (string.IsNullOrEmpty(adminName))
            {
                await ConfigProvider.RefreshAdminUsername(plexAuth, ct).ConfigureAwait(false);
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
