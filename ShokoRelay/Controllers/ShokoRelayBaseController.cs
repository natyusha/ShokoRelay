using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.QueueProcessor.Abstractions;
using Shoko.QueueProcessor.Builder;
using ShokoRelay.Jobs;

namespace ShokoRelay.Controllers;

#region Auth Helpers

/// <summary>Issues and validates signed HttpOnly session cookies for Shoko Relay administrator authentication.</summary>
/// <remarks>
///   - Cookie-authenticated requests do not carry a Shoko <see cref="ApiToken"/>, so Shoko's <see cref="IActorContext.Current"/> remains <c>null</c> (system) on triggered events.
///   - Pass Shoko's <c>apikey</c> header when per-token actor attribution is required.
/// </remarks>
internal static class RelayAuthSession
{
    private const string CookieName = "shoko-relay-auth";
    private const string ProtectorPurpose = "ShokoRelay.AdminAuth.v1";
    private static readonly TimeSpan s_persistentLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan s_sessionLifetime = TimeSpan.FromDays(1);
    private static readonly byte[] s_fallbackKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Issues a signed HttpOnly session cookie for the authenticated administrator.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="user">The authenticated Shoko administrator.</param>
    /// <param name="rememberMe">When <c>true</c>, persists the cookie across browser restarts for 30 days; otherwise issues a browser-session cookie.</param>
    public static void SignIn(HttpContext context, IUser user, bool rememberMe = false)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(rememberMe ? s_persistentLifetime : s_sessionLifetime);
        byte[] payload = Encoding.UTF8.GetBytes($"{user.LocalID}:{expiresAt.ToUnixTimeSeconds()}");
        byte[] protectedBytes = context.RequestServices.GetService<IDataProtectionProvider>() is { } dataProtection
            ? dataProtection.CreateProtector(ProtectorPurpose).Protect(payload)
            : SignWithFallback(payload);

        context.Response.Cookies.Append(
            CookieName,
            Convert.ToBase64String(protectedBytes),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = "/",
                Expires = rememberMe ? expiresAt : null,
            }
        );
    }

    /// <summary>Deletes any active Shoko Relay authentication session cookie.</summary>
    /// <param name="context">The current HTTP context.</param>
    public static void SignOut(HttpContext context) =>
        context.Response.Cookies.Delete(
            CookieName,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = "/",
            }
        );

    /// <summary>Attempts to resolve an authenticated Shoko user from the session cookie.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="userService">The Shoko user service.</param>
    /// <returns>The resolved <see cref="IUser"/> if the cookie is valid and unexpired; otherwise, <c>null</c>.</returns>
    public static IUser? GetUser(HttpContext context, IUserService userService)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            byte[] raw = Convert.FromBase64String(token);
            byte[]? payload = context.RequestServices.GetService<IDataProtectionProvider>() is { } dataProtection ? dataProtection.CreateProtector(ProtectorPurpose).Unprotect(raw) : VerifyWithFallback(raw);
            if (payload == null)
                return null;

            string text = Encoding.UTF8.GetString(payload);
            int separatorIndex = text.IndexOf(':');
            return
                separatorIndex <= 0
                || !int.TryParse(text[..separatorIndex], out int userId)
                || !long.TryParse(text[(separatorIndex + 1)..], out long expiresUnix)
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnix
                ? null
                : userService.GetUserByID(userId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Prepends an HMAC-SHA256 signature to the payload bytes when ASP.NET Data Protection is unavailable.</summary>
    /// <param name="payload">The raw UTF-8 session payload bytes.</param>
    /// <returns>The signed byte array containing the 32-byte MAC followed by the payload.</returns>
    private static byte[] SignWithFallback(byte[] payload)
    {
        byte[] mac = HMACSHA256.HashData(s_fallbackKey, payload);
        byte[] combined = new byte[mac.Length + payload.Length];
        mac.CopyTo(combined, 0);
        payload.CopyTo(combined, mac.Length);
        return combined;
    }

    /// <summary>Verifies an HMAC-SHA256 signed token and extracts the payload bytes.</summary>
    /// <param name="signed">The combined MAC and payload byte array.</param>
    /// <returns>The verified payload bytes, or <c>null</c> if verification fails.</returns>
    private static byte[]? VerifyWithFallback(byte[] signed)
    {
        if (signed.Length <= 32)
            return null;

        ReadOnlySpan<byte> mac = signed.AsSpan(0, 32);
        byte[] payload = signed[32..];
        byte[] expected = HMACSHA256.HashData(s_fallbackKey, payload);
        return CryptographicOperations.FixedTimeEquals(mac, expected) ? payload : null;
    }
}

/// <summary>Marks an endpoint as accessible without authentication when <c>Advanced.RequireAuth</c> is set to <see cref="RequireAuthMode.AllowPlayerAccess"/>.</summary>
/// <remarks>Player endpoints intentionally omit <c>IUser.IsAllowedToSee</c> restricted-tag filtering so the standalone player remains anonymous under this mode.</remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowPlayerAccessAttribute : Attribute;

/// <summary>Enforces Shoko administrator authorization on decorated controllers or actions when <c>Advanced.RequireAuth</c> is enabled.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RelayAdminAuthorizeAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>Evaluates whether the current request is permitted under the configured authentication settings.</summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            return;

        var configProvider = context.HttpContext.RequestServices.GetRequiredService<ConfigProvider>();
        var authMode = configProvider.GetSettings().Advanced.RequireAuth;
        if (authMode == RequireAuthMode.Disabled)
            return;

        if (authMode == RequireAuthMode.AllowPlayerAccess && context.ActionDescriptor.EndpointMetadata.OfType<AllowPlayerAccessAttribute>().Any())
            return;

        var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
        var user = userService.GetUserFromHttpContext(context.HttpContext) ?? RelayAuthSession.GetUser(context.HttpContext, userService);
        if (user == null)
            context.Result = new UnauthorizedObjectResult(new { status = "error", message = "Authentication required." });
        else if (!user.IsAdmin)
            context.Result = new ObjectResult(new { status = "error", message = "Administrator privileges required." }) { StatusCode = StatusCodes.Status403Forbidden };
    }
}

#endregion

/// <summary>Provides the foundational infrastructure for all Shoko Relay controllers.</summary>
/// <remarks>Contains shared logic for logging, validation, response formatting, and Plex discovery.</remarks>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="logger">Logger instance.</param>
[ApiController]
[Route(ShokoRelayConstants.BasePath)]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
public abstract class ShokoRelayBaseController(ConfigProvider configProvider, IMetadataService metadataService, PlexClient plexClient, ILogger logger) : ControllerBase
{
    #region Setup

    /// <summary>Logger instance for controller operations.</summary>
    protected readonly ILogger Logger = logger;

    /// <summary>Service used for reading and persisting plugin settings and secrets.</summary>
    protected readonly ConfigProvider ConfigProvider = configProvider;

    /// <summary>Service for querying the Shoko metadata database.</summary>
    protected readonly IMetadataService MetadataService = metadataService;

    /// <summary>Client used for interacting with configured Plex server instances.</summary>
    protected readonly PlexClient PlexClient = plexClient;

    /// <summary>Returns the absolute base URL of the plugin's API on the current host, honouring reverse-proxy forwarding headers.</summary>
    protected string ApiBase =>
        ConfigProvider.DetectRequestBaseUrl() is { } detectedBase ? $"{detectedBase}{ShokoRelayConstants.BasePath}" : $"{Request.Scheme}://{Request.Host}{Request.PathBase}{ShokoRelayConstants.BasePath}";

    #endregion

    #region Data Models

    /// <summary>Standardized response envelope for all Shoko Relay API endpoints.</summary>
    /// <typeparam name="T">The type of the data payload.</typeparam>
    /// <param name="Status">The result status of the request (e.g., "ok", "error", "skipped").</param>
    /// <param name="Message">An optional descriptive message or error detail.</param>
    /// <param name="Data">The actual result data object.</param>
    /// <param name="ReportUrl">An optional URL to a generated task report file.</param>
    public record RelayResponse<T>(string Status = "ok", string? Message = null, T? Data = default, string? ReportUrl = null);

    #endregion

    #region Report & Queue Helpers

    /// <summary>Executes or enqueues a tracked Relay queue job so it appears in Shoko's WebUI Queue with progress and cancellation support.</summary>
    /// <typeparam name="TJob">The concrete <see cref="RelayJobBase"/> queue job type.</typeparam>
    /// <param name="taskName">Unique identifier for dashboard UI tracking.</param>
    /// <param name="queue">When <c>true</c>, enqueues asynchronously and returns immediately; when <c>false</c>, awaits immediate completion.</param>
    /// <param name="configure">Optional delegate configuring job parameters.</param>
    /// <returns>An <see cref="IActionResult"/> indicating queued status or returning the completed job payload.</returns>
    protected async Task<IActionResult> ExecuteQueuedJobAsync<TJob>(string taskName, bool queue, Action<TJob>? configure = null)
        where TJob : RelayJobBase
    {
        var scheduler = HttpContext.RequestServices.GetRequiredService<IQueueScheduler>();
        string apiBase = ApiBase;

        void ConfigureJob(TJob job)
        {
            configure?.Invoke(job);
            job.ApiBase = apiBase;
            job.TrackInDashboard = queue;
        }

        string jobKey = JobKeyBuilder<TJob>.Create().UsingJobData(ConfigureJob).Build();
        void ConfigureWithKey(TJob job)
        {
            ConfigureJob(job);
            job.TrackedJobKey = jobKey;
        }

        try
        {
            if (queue)
            {
                await scheduler.Enqueue<TJob>(ConfigureWithKey, prioritize: true, ct: HttpContext.RequestAborted).ConfigureAwait(false);
                TaskHelper.TrackQueuedJob(taskName, jobKey, storeForDashboard: true, scheduler);
                return Ok(new RelayResponse<object>(Status: "queued", Message: "Queued in Shoko Job Queue.", Data: new { jobKey, queued = true }));
            }

            TaskHelper.TakeJobResult(jobKey);
            await scheduler.EnqueueImmediate<TJob>(ConfigureWithKey, ct: CancellationToken.None).ConfigureAwait(false);
            return TaskHelper.TakeJobResult(jobKey) is { } jobResult ? Ok(jobResult) : Ok(new RelayResponse<object>());
        }
        catch (JobBlockedException exception)
        {
            return Conflict(new RelayResponse<object>(Status: "blocked", Message: exception.Message));
        }
        catch (OperationCanceledException)
        {
            return TaskHelper.TakeJobResult(jobKey) is { } cancelledResult ? Ok(cancelledResult) : Ok(new RelayResponse<object>(Status: "cancelled", Message: "Task was cancelled."));
        }
        catch (Exception exception)
        {
            TaskHelper.TakeJobResult(jobKey);
            return BadRequest(new RelayResponse<object>(Status: "error", Message: exception.Message));
        }
    }

    #endregion

    #region Validation Helpers

    /// <summary>Parse a comma-separated filter string into a list of valid positive integers. Supports 'a' prefix for AniDB IDs.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <param name="errors">Output list of collected parse error messages.</param>
    /// <returns>A list of unique, valid Shoko Series IDs.</returns>
    protected List<int> ParseFilterIds(string? filter, out List<string> errors)
    {
        errors = [];
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(filter))
            return [.. ids];

        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Handle AniDB ID prefix (e.g. "a69" for One Piece)
            if (raw.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(raw[PlexConstants.AniDbPrefix.Length..], out int anidbId) && anidbId > 0)
                {
                    if (MetadataService.GetShokoSeriesByAnidbID(anidbId) is { } series)
                        ids.Add(series.LocalID);
                    else
                        errors.Add($"No Shoko Series found for AniDB ID: {anidbId}");
                }
                else
                    errors.Add($"Invalid AniDB ID format: {raw}");
                continue;
            }

            // Handle standard Shoko Series ID
            if (!int.TryParse(raw, out int id) || id <= 0)
            {
                errors.Add($"Invalid series id: {raw}");
                continue;
            }
            ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>Validates a filter query string and resolves any secondary IDs to their primary equivalents.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <param name="ids">Parsed and resolved list of series IDs (output).</param>
    /// <returns>BadRequest if any ID is invalid; otherwise null.</returns>
    protected IActionResult? ValidateFilterOrBadRequest(string? filter, out List<int> ids)
    {
        ids = ParseFilterIds(filter, out var errors);
        if (errors.Count > 0)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Invalid filter values.", Data: new { errors }));

        ids = [.. ids.Select(id => OverrideHelper.GetPrimary(id, MetadataService)).Distinct()];
        return null;
    }

    /// <summary>Combined guard for Plex automation requests: checks Plex configuration, validates filter, and resolves the target series list.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <param name="seriesList">Resolved Shoko series objects (output).</param>
    /// <param name="filterIds">Resolved numeric IDs (output).</param>
    /// <returns>BadRequest if validation fails; otherwise null.</returns>
    protected IActionResult? ValidatePlexFilterRequest(string? filter, out List<IShokoSeries?> seriesList, out List<int> filterIds)
    {
        seriesList = [];
        filterIds = [];
        if (!PlexClient.IsEnabled)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Plex server configuration is missing or no library selected."));

        var validation = ValidateFilterOrBadRequest(filter, out filterIds);
        if (validation != null)
            return validation;

        seriesList = filterIds.Count > 0 ? [.. filterIds.Distinct().Select(MetadataService.GetShokoSeriesByID)] : [.. MetadataService.GetAllShokoSeries().Cast<IShokoSeries?>()];
        return null;
    }

    #endregion

    #region Response Formatting

    /// <summary>Determines whether the current HTTP request targets the <c>/movie</c> metadata provider route.</summary>
    /// <returns><c>true</c> if the request subpath begins with <c>/movie</c>; otherwise, <c>false</c>.</returns>
    protected bool IsMovieRoute()
    {
        string? path = Request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return false;

        int baseIndex = path.IndexOf(ShokoRelayConstants.BasePath, StringComparison.OrdinalIgnoreCase);
        if (baseIndex < 0)
            return false;

        ReadOnlySpan<char> subPath = path.AsSpan(baseIndex + ShokoRelayConstants.BasePath.Length);
        return subPath.Equals("/movie", StringComparison.OrdinalIgnoreCase) || subPath.StartsWith("/movie/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies response headers preventing browser and proxy caching of dynamic responses.</summary>
    protected void SetNoCacheHeaders()
    {
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
    }

    /// <summary>Wrap a single metadata object in the standard Plex MediaContainer envelope.</summary>
    /// <param name="metadata">The metadata item to embed.</param>
    /// <param name="identifier">Optional provider identifier override.</param>
    /// <returns>Plex-compatible JSON response.</returns>
    protected IActionResult WrapInContainer(object metadata, string? identifier = null) =>
        Ok(
            new
            {
                MediaContainer = new
                {
                    size = 1,
                    totalSize = 1,
                    offset = 0,
                    identifier = identifier ?? (IsMovieRoute() ? ShokoRelayConstants.MovieAgentScheme : ShokoRelayConstants.AgentScheme),
                    Metadata = new[] { metadata },
                },
            }
        );

    /// <summary>Wrap a list of metadata objects in a paged MediaContainer, honouring Plex pagination headers or query parameters.</summary>
    /// <param name="metadataList">Collection of metadata items to page.</param>
    /// <param name="identifier">Optional provider identifier override.</param>
    /// <returns>Paged Plex-compatible JSON response.</returns>
    protected IActionResult WrapInPagedContainer(IEnumerable<object> metadataList, string? identifier = null)
    {
        var (start, size) = GetPaging();
        var allItems = metadataList.ToList();
        var pagedData = allItems.Skip(start).Take(size).ToArray();
        string defaultIdentifier = IsMovieRoute() ? ShokoRelayConstants.MovieAgentScheme : ShokoRelayConstants.AgentScheme;

        Response.Headers["X-Plex-Container-Start"] = start.ToString();
        Response.Headers["X-Plex-Container-Total-Size"] = allItems.Count.ToString();

        return Ok(
            new
            {
                MediaContainer = new
                {
                    offset = start,
                    totalSize = allItems.Count,
                    identifier = identifier ?? defaultIdentifier,
                    size = pagedData.Length,
                    Metadata = pagedData,
                },
            }
        );
    }

    /// <summary>Return an empty Plex match response (zero results).</summary>
    /// <returns>Plex-compatible empty MediaContainer result.</returns>
    protected IActionResult EmptyMatch() =>
        Ok(
            new
            {
                MediaContainer = new
                {
                    offset = 0,
                    totalSize = 0,
                    identifier = IsMovieRoute() ? ShokoRelayConstants.MovieAgentScheme : ShokoRelayConstants.AgentScheme,
                    size = 0,
                    Metadata = Array.Empty<object>(),
                },
            }
        );

    #endregion

    #region Plex Pagination

    /// <summary>Extracts the Plex container paging parameters from the current request.</summary>
    /// <returns>A tuple containing the Start offset and Size limit.</returns>
    protected (int Start, int Size) GetPaging()
    {
        int start = Math.Max(
            0,
            int.TryParse(Request.Headers["X-Plex-Container-Start"], out var startHeader) ? startHeader
                : int.TryParse(Request.Query["X-Plex-Container-Start"], out var startQuery) ? startQuery
                : 0
        );
        int size = Math.Max(
            0,
            int.TryParse(Request.Headers["X-Plex-Container-Size"], out var sizeHeader) ? sizeHeader
                : int.TryParse(Request.Query["X-Plex-Container-Size"], out var sizeQuery) ? sizeQuery
                : 20
        );
        return (start, size);
    }

    #endregion

    #region Plex Helper Logic

    /// <summary>Persists the results of a Plex discovery run into the plugin secrets file.</summary>
    /// <param name="discovery">The discovery result payload containing servers and libraries.</param>
    protected void PersistDiscoveryResults((bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries) discovery) =>
        PlexAuth.PersistDiscoveryResults(ConfigProvider, discovery);

    #endregion
}
