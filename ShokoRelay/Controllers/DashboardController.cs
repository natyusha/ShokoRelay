using System.Collections;
using System.Collections.Frozen;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.ScheduledActions.Services;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Web.Services;
using ShokoRelay.Actions;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Provides operations for serving the dashboard pages, static assets, and dynamic theme stylesheets.</summary>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="plexLibrary">Plex client.</param>
/// <param name="webThemeService">Shoko web theme service.</param>
/// <param name="applicationPaths">Shoko application paths.</param>
/// <param name="scheduledActionService">Shoko scheduled action service.</param>
/// <param name="userService">Shoko user service.</param>
/// <param name="throttleService">Shoko throttle service.</param>
/// <param name="logger">Logger instance.</param>
[ApiController]
[RelayAdminAuthorize]
[Route(ShokoRelayConstants.BasePath)]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
public class DashboardController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    IWebThemeService webThemeService,
    IApplicationPaths applicationPaths,
    IScheduledActionService scheduledActionService,
    IUserService userService,
    IAuthenticationThrottleService throttleService,
    ILogger<DashboardController> logger
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary, logger)
{
    #region Setup

    private static readonly FileExtensionContentTypeProvider s_contentTypeProvider = new();

    private static readonly FrozenDictionary<string, string> s_templateConstants = typeof(ShokoRelayConstants)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(field => field.IsLiteral && !field.IsInitOnly)
        .ToFrozenDictionary(field => field.Name, field => field.GetValue(null)?.ToString() ?? string.Empty, StringComparer.Ordinal);

    #endregion

    #region Auth & Pages

    /// <summary>Payload containing Shoko user credentials for dashboard authentication.</summary>
    /// <param name="Username">The Shoko username.</param>
    /// <param name="Password">The Shoko password.</param>
    public sealed record LoginRequest(string Username, string? Password);

    /// <summary>Authenticates a Shoko administrator by username and password and issues an HttpOnly session cookie.</summary>
    /// <param name="body">The login credentials payload.</param>
    /// <returns>An authenticated confirmation payload, or 401/403 if credentials are invalid or the user is not an administrator.</returns>
    [AllowAnonymous]
    [HttpPost("auth/login")]
    public IActionResult Login([FromBody] LoginRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Username))
            return BadRequest(new { status = "error", message = "Username is required." });

        string username = body.Username.Trim();
        if (throttleService.ThrottleAuthentication(HttpContext, username) is { } blocked)
            return blocked;

        var user = userService.AuthenticateUser(username, body.Password ?? string.Empty);
        if (user == null)
        {
            throttleService.RegisterFailure(HttpContext);
            return Unauthorized(new { status = "error", message = "Invalid username or password." });
        }

        throttleService.Reset(HttpContext);
        if (!user.IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Administrator privileges required." });

        RelayAuthSession.SignIn(HttpContext, user);
        return Ok(new { authenticated = true, username = user.Username });
    }

    /// <summary>Verifies whether the caller is authorized, optionally enforcing an administrator session even before <c>Advanced.RequireAuth</c> is enabled.</summary>
    /// <param name="force">When <c>true</c>, validates an authenticated administrator session regardless of <c>Advanced.RequireAuth</c>.</param>
    /// <param name="player">When <c>true</c>, allows an unauthenticated status response if <c>Advanced.RequireAuth</c> is set to <see cref="RequireAuthMode.AllowPlayerAccess"/>.</param>
    /// <returns>An authenticated confirmation payload, or 401/403 if the caller is unauthenticated or not an administrator.</returns>
    [AllowAnonymous]
    [HttpGet("auth/verify")]
    public IActionResult VerifyAuth([FromQuery] bool force = false, [FromQuery] bool player = false)
    {
        var mode = Settings.Advanced.RequireAuth;
        if (!force && mode == RequireAuthMode.Disabled)
            return Ok(
                new
                {
                    authenticated = true,
                    required = false,
                    allowPlayerAccess = true,
                    mode,
                }
            );

        var user = userService.GetUserFromHttpContext(HttpContext) ?? RelayAuthSession.GetUser(HttpContext, userService);
        bool isAdmin = user?.IsAdmin == true;

        return !force && player && mode == RequireAuthMode.AllowPlayerAccess
                ? Ok(
                    new
                    {
                        authenticated = isAdmin,
                        required = true,
                        allowPlayerAccess = true,
                        mode,
                    }
                )
            : user == null ? Unauthorized(new { status = "error", message = "Authentication required." })
            : !isAdmin ? StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Administrator privileges required." })
            : Ok(
                new
                {
                    authenticated = true,
                    required = true,
                    allowPlayerAccess = mode == RequireAuthMode.AllowPlayerAccess,
                    mode,
                }
            );
    }

    /// <summary>Serves the main settings dashboard page.</summary>
    /// <returns>The settings dashboard HTML content.</returns>
    [AllowAnonymous]
    [HttpGet("dashboard")]
    public IActionResult GetDashboardPage() => ServePage("dashboard.cshtml");

    /// <summary>Serves the standalone VFS browser page.</summary>
    /// <returns>The VFS browser HTML content.</returns>
    [AllowAnonymous]
    [HttpGet("browser")]
    public IActionResult GetBrowserPage() => ServePage("browser.cshtml");

    /// <summary>Serves the standalone AnimeThemes video player page.</summary>
    /// <returns>The video player HTML content.</returns>
    [AllowAnonymous]
    [HttpGet("player")]
    public IActionResult GetPlayerPage() => ServePage("player.cshtml");

    /// <summary>Serves the static assets (JS, CSS, fonts, images) from the dashboard folder.</summary>
    /// <param name="path">The relative asset path.</param>
    /// <returns>The physical asset file.</returns>
    [AllowAnonymous]
    [HttpGet("dashboard/{*path}")]
    public IActionResult GetAssetFile([FromRoute] string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path) || "player".Equals(path, StringComparison.OrdinalIgnoreCase) || "browser".Equals(path, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        string dashboardDir = Path.GetFullPath(Path.Combine(ConfigProvider.PluginDirectory, "dashboard"));
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, VfsShared.NormalizeSeparators(path)));

        // Resolve the MIME content type for a given file path, prioritizing .cshtml templates
        return !VfsShared.IsPathUnderDirectory(requested, dashboardDir) || !IoFile.Exists(requested) || requested.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
            ? NotFound()
            : PhysicalFile(requested, s_contentTypeProvider.TryGetContentType(requested, out var contentType) ? contentType : "application/octet-stream");
    }

    #endregion

    #region Config Management

    /// <summary>Returns the current configuration payload used by the dashboard UI.</summary>
    /// <returns>A JSON payload containing settings, VFS overrides, and available WebUI themes.</returns>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var payload = ConfigProvider.GetDashboardConfig();
        var themes = webThemeService.GetThemes(forceRefresh: false).Select(theme => new { id = theme.ID, name = theme.Name }).ToList();
        var path = Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);
        return Ok(
            new
            {
                payload,
                overrides = IoFile.Exists(path) ? IoFile.ReadAllText(path) : string.Empty,
                themes,
            }
        );
    }

    /// <summary>Accepts a new configuration object and persists it to disk.</summary>
    /// <param name="config">The configuration payload.</param>
    /// <returns>Success or error status.</returns>
    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] RelayConfig config)
    {
        if (config == null)
            return BadRequest(new { status = "error", message = "Config payload is required." });
        try
        {
            Logger.LogInformation("Dashboard: Saving updated provider settings...");
            ConfigProvider.SaveSettings(config);
            ActionScheduleHelper.SyncTriggers(config, scheduledActionService);
            if (config.Advanced.RequireAuth == RequireAuthMode.Disabled)
                RelayAuthSession.SignOut(HttpContext);
            return Ok(new { status = "ok" });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { status = "error", message = exception.Message });
        }
    }

    /// <summary>Builds and returns a JSON schema representation of the configuration properties.</summary>
    /// <returns>A schema list for dynamic form rendering.</returns>
    [HttpGet("config/schema")]
    public IActionResult GetConfigSchema() => Ok(new { properties = BuildConfigSchema(typeof(RelayConfig), "") });

    /// <summary>Generates and serves a dynamically mapped CSS file combining the selected Shoko WebUI theme with custom Relay variables.</summary>
    /// <returns>A dynamic CSS stylesheet content result.</returns>
    [AllowAnonymous]
    [HttpGet("theme.css")]
    public IActionResult GetDynamicThemeCss()
    {
        string rawThemeId = Settings.Advanced.SelectedTheme;
        if (string.IsNullOrWhiteSpace(rawThemeId) || rawThemeId.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Content(string.Empty, "text/css");

        string themeId = Path.GetFileName(rawThemeId);
        if (!TextHelper.ThemeIdRegex().IsMatch(themeId))
            return Content(string.Empty, "text/css");
        if (string.Equals(themeId, "shoko-gray", StringComparison.OrdinalIgnoreCase))
            return Content(
                """
                /* Shoko Gray Theme Variables */
                :root {
                  --bg-color: #282e38;
                  --panel-color: #2c333e;
                  --border-color: #21242b;
                  --inset-color: #1e2027;
                  --text-color: #cfd8e3;
                  --highlight-color: #44a3ff;
                  --button-color: #44a3ff;
                  --hover-color: #64b3ff;
                  --danger-color: #ff6c6c;
                  --warning-color: #f9c851;
                  --ok-color: #10c469;
                  --logo-outline: #000;
                  --logo-skin: #fdf5e8;
                  --logo-face-shadow: #fe514d;
                  --logo-eye-ref1: #e3e4d6;
                  --logo-eye-ref2: #e8c8bb;
                  --logo-eye-ref3: #ffc2b2;
                  --logo-eye-gradient1: #ae303b;
                  --logo-eye-gradient2: #ec4050;
                  --logo-eye-gradient3: #fd877d;
                  --logo-hair-gradient1: #c33144;
                  --logo-hair-gradient2: #6b8cdb;
                  --logo-hair-gradient3: #79f0f8;
                }
                """,
                "text/css"
            );

        try
        {
            string cssContent = string.Empty;
            if (webThemeService.GetTheme(themeId) is { } themeDefinition && (themeDefinition.Values.Count > 0 || !string.IsNullOrWhiteSpace(themeDefinition.CssContent)))
            {
                var valuesCss =
                    themeDefinition.Values.Count > 0
                        ? $":root {{\n{string.Join("\n", themeDefinition.Values.Select(variable => $"  --{(variable.Key.StartsWith("--", StringComparison.Ordinal) ? variable.Key[2..] : variable.Key)}: {variable.Value};"))}\n}}\n"
                        : string.Empty;
                var rawCss = themeDefinition.CssContent?.Trim() ?? string.Empty;
                string wrappedCss = rawCss;
                if (!string.IsNullOrEmpty(rawCss))
                {
                    int braceIndex = rawCss.IndexOf('{');
                    if (braceIndex < 0)
                        wrappedCss = $":root {{\n{rawCss}\n}}";
                    else if (TextHelper.FindLastTopLevelSemicolon(rawCss[..braceIndex]) is int lastSemicolon && lastSemicolon >= 0 && !rawCss[..lastSemicolon].TrimStart().StartsWith('@'))
                        wrappedCss = $":root {{\n{rawCss[..(lastSemicolon + 1)].Trim()}\n}}\n{rawCss[(lastSemicolon + 1)..].TrimStart()}";
                }
                cssContent = $"{valuesCss}{wrappedCss}";
            }
            else if (Path.Combine(applicationPaths.ThemesPath, $"{themeId}.css") is var cssPath && IoFile.Exists(cssPath))
                cssContent = IoFile.ReadAllText(cssPath);

            return string.IsNullOrWhiteSpace(cssContent)
                ? Content(string.Empty, "text/css")
                : Content(
                    cssContent
                        + """

                        /* Shoko Relay Theme Variable Mapping */
                        :root {
                          --bg-color: var(--panel-background-alt, #0f0f0f);
                          --panel-color: var(--panel-background, #1c1c1c);
                          --border-color: var(--panel-border, #0f0f0f);
                          --inset-color: var(--panel-in, #151515);
                          --text-color: var(--panel-text, #ddd);
                          --highlight-color: var(--panel-icon-action, #94aad1);
                          --button-color: var(--button-primary, #a497b0);
                          --hover-color: var(--button-primary-hover, #ea005e);
                          --danger-color: var(--panel-icon-danger, #cc0000);
                          --warning-color: var(--panel-icon-warning, #f9c851);
                          --ok-color: var(--panel-text-important, #00ab3f);
                        }
                        """,
                    "text/css"
                );
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Dashboard: Failed to generate dynamic mapped CSS for theme {ThemeId}", themeId);
            return Content(string.Empty, "text/css");
        }
    }

    #endregion

    #region Tasks

    /// <summary>Returns a list of currently running tasks for the dashboard UI.</summary>
    /// <returns>A collection of active task identifiers.</returns>
    [HttpGet("tasks/active")]
    public IActionResult GetActiveTasks() => Ok(TaskHelper.ActiveTasks.Keys);

    /// <summary>Returns results of tasks completed since the last check.</summary>
    /// <returns>A dictionary of completed task results keyed by task name.</returns>
    [HttpGet("tasks/completed")]
    public IActionResult GetCompletedTasks() => Ok(TaskHelper.TaskResults);

    /// <summary>Acknowledges and clears a completed task result so it isn't displayed again.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    /// <returns>An empty 200 OK response.</returns>
    [HttpPost("tasks/clear/{taskName}")]
    public IActionResult ClearTaskResult(string taskName)
    {
        TaskHelper.TaskResults.TryRemove(taskName, out _);
        return Ok();
    }

    #endregion

    #region Logs

    /// <summary>Returns a JSON list of all currently generated task reports that exist on disk.</summary>
    /// <returns>A list of report file metadata objects.</returns>
    [HttpGet("logs/list")]
    public IActionResult GetLogsList() =>
        !Directory.Exists(ConfigProvider.ReportsDirectory)
            ? Ok(Array.Empty<object>())
            : Ok(
                Directory
                    .EnumerateFiles(ConfigProvider.ReportsDirectory, "*.report")
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(file => file)
                    .Select(file => new { name = file, friendlyName = TagHelper.TitleCase(Path.GetFileNameWithoutExtension(file).Replace("-", " ", StringComparison.Ordinal)) })
                    .ToList()
            );

    /// <summary>Serves report files from the plugin's reports directory without allowing browser caching or directory traversal.</summary>
    /// <param name="fileName">The report filename.</param>
    /// <returns>The report content as text/plain; charset=utf-8.</returns>
    [HttpGet("logs/{fileName}")]
    public IActionResult GetLog(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { status = "error", message = "fileName is required" });

        string logsDir = Path.GetFullPath(ConfigProvider.ReportsDirectory);
        string safeFileName = Path.GetFileName(fileName);
        string requested = Path.GetFullPath(Path.Combine(logsDir, safeFileName));

        if (!VfsShared.IsPathUnderDirectory(requested, logsDir) || !safeFileName.EndsWith(".report", StringComparison.OrdinalIgnoreCase) || !IoFile.Exists(requested))
            return NotFound(new { status = "error", message = "log not found" });

        SetNoCacheHeaders();
        return PhysicalFile(requested, "text/plain; charset=utf-8");
    }

    #endregion

    #region Internal Helpers

    /// <summary>Serves a razor template page from the dashboard directory, processing constants and injecting the base path.</summary>
    /// <param name="fileName">The filename of the razor template to serve.</param>
    /// <returns>An HTML content result or NotFound if the template does not exist.</returns>
    private IActionResult ServePage(string fileName)
    {
        string dashboardDir = Path.GetFullPath(Path.Combine(ConfigProvider.PluginDirectory, "dashboard"));
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, fileName));

        if (!VfsShared.IsPathUnderDirectory(requested, dashboardDir) || !IoFile.Exists(requested))
            return NotFound();

        // Replace {{ConstantName}} placeholders with values from ShokoRelayConstants
        var html = TextHelper
            .TemplatePlaceholderRegex()
            .Replace(IoFile.ReadAllText(requested), match => s_templateConstants.TryGetValue(match.Groups["name"].Value, out var constantValue) ? constantValue : match.Value);

        if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
            html = html.Replace("<head>", $"<head>\n    <base href=\"{WebUtility.HtmlEncode($"{Request.PathBase}{ShokoRelayConstants.BasePath}/dashboard/")}\">", StringComparison.OrdinalIgnoreCase);
        return Content(html, "text/html");
    }

    /// <summary>Schema definition for a configuration property.</summary>
    /// <param name="Path">The JSON path to the property.</param>
    /// <param name="Type">The UI data type.</param>
    /// <param name="Display">The display name.</param>
    /// <param name="Description">The descriptive tooltip text.</param>
    /// <param name="DefaultValue">The hardcoded default value.</param>
    /// <param name="EnumValues">Possible values for enums.</param>
    /// <param name="Advanced">Whether this is an advanced setting.</param>
    /// <param name="Rebuild">Whether the setting requires a VFS rebuild.</param>
    /// <param name="Language">Whether this is a language setting.</param>
    private sealed record ConfigPropertySchema(string Path, string Type, string? Display, string? Description, object? DefaultValue, object? EnumValues, bool Advanced, bool Rebuild, bool Language);

    /// <summary>Recursively builds a list of property metadata descriptors from a type to expose as a JSON schema.</summary>
    /// <param name="type">The type to reflect over and parse.</param>
    /// <param name="prefix">The dotted JSON prefix path used to represent nested properties.</param>
    /// <param name="isAdvancedBranch">True if the properties should inherit advanced setting visibility.</param>
    /// <returns>A list of schema descriptors representing the type's configuration properties.</returns>
    private static List<ConfigPropertySchema> BuildConfigSchema(Type type, string prefix, bool isAdvancedBranch = false)
    {
        var props = new List<ConfigPropertySchema>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
            if (browsable != null && !browsable.Browsable)
                continue;

            bool isAdvanced = isAdvancedBranch || prop.Name == "Advanced";
            bool needsRebuild = prop.GetCustomAttribute<VfsRebuildAttribute>() != null; // Detect the custom attribute
            bool isLanguage = prop.GetCustomAttribute<LanguageSettingAttribute>() != null;
            string path = string.IsNullOrWhiteSpace(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            var display = prop.GetCustomAttribute<DisplayAttribute>();
            var defaultValue = prop.GetCustomAttribute<DefaultValueAttribute>()?.Value;
            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (propType.IsEnum)
            {
                bool isFlags = propType.IsDefined(typeof(FlagsAttribute), false);
                var values = Enum.GetValues(propType)
                    .Cast<object>()
                    .Select(value =>
                    {
                        int enumValue = Convert.ToInt32(value);
                        string memberName = Enum.GetName(propType, value) ?? enumValue.ToString();
                        return new { name = propType.GetField(memberName)?.GetCustomAttribute<DisplayAttribute>()?.Name ?? memberName, value = enumValue };
                    })
                    .Where(enumItem => !isFlags || (enumItem.value > 0 && (enumItem.value & (enumItem.value - 1)) == 0))
                    .ToArray();
                props.Add(new ConfigPropertySchema(path, isFlags ? "flags" : "enum", display?.Name, display?.Description, defaultValue, values, isAdvanced, needsRebuild, isLanguage));
            }
            else if (propType == typeof(bool))
                props.Add(new ConfigPropertySchema(path, "bool", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild, isLanguage));
            else if (propType == typeof(string))
                props.Add(new ConfigPropertySchema(path, "string", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild, isLanguage));
            else if (propType.IsPrimitive || propType == typeof(decimal))
                props.Add(new ConfigPropertySchema(path, "number", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild, isLanguage));
            else if (typeof(IDictionary).IsAssignableFrom(propType))
                props.Add(new ConfigPropertySchema(path, "json", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild, isLanguage));
            else if (propType.IsClass)
                props.AddRange(BuildConfigSchema(propType, path, isAdvanced));
        }
        return props;
    }

    #endregion
}
