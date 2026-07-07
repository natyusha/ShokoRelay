using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Web.Services;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Provides operations for serving the dashboard pages, static assets, and dynamic theme stylesheets.</summary>
[ApiController]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
[Route(ShokoRelayConstants.BasePath)]
public class DashboardController(ConfigProvider configProvider, IMetadataService metadataService, PlexClient plexLibrary, IWebThemeService webThemeService, IApplicationPaths applicationPaths)
    : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Setup

    private static readonly FileExtensionContentTypeProvider s_contentTypeProvider = new();

    #endregion

    #region Pages & Assets

    /// <summary>Serves the main settings dashboard page.</summary>
    /// <returns>The settings dashboard HTML content.</returns>
    [HttpGet("dashboard")]
    public IActionResult GetDashboardPage() => ServePage("dashboard.cshtml");

    /// <summary>Serves the standalone VFS browser page.</summary>
    /// <returns>The VFS browser HTML content.</returns>
    [HttpGet("browser")]
    public IActionResult GetBrowserPage() => ServePage("browser.cshtml");

    /// <summary>Serves the standalone AnimeThemes video player page.</summary>
    /// <returns>The video player HTML content.</returns>
    [HttpGet("player")]
    public IActionResult GetPlayerPage() => ServePage("player.cshtml");

    /// <summary>Serves the static assets (JS, CSS, fonts, images) from the dashboard folder.</summary>
    /// <param name="path">The relative asset path.</param>
    /// <returns>The physical asset file.</returns>
    [HttpGet("dashboard/{*path}")]
    public IActionResult GetAssetFile([FromRoute] string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path) || "player".Equals(path, StringComparison.OrdinalIgnoreCase) || "browser".Equals(path, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        string dashboardDir = Path.Combine(ConfigProvider.PluginDirectory, "dashboard");
        string safePath = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, safePath));

        return !requested.StartsWith(Path.GetFullPath(dashboardDir), StringComparison.OrdinalIgnoreCase) || !IoFile.Exists(requested) || requested.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
            ? NotFound()
            : PhysicalFile(requested, GetContentType(requested));
    }

    #endregion

    #region Config Management

    /// <summary>Returns the current configuration payload used by the dashboard UI.</summary>
    /// <returns>A JSON payload containing settings, VFS overrides, and available WebUI themes.</returns>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var payload = ConfigProvider.GetDashboardConfig();
        var themes = webThemeService.GetThemes(forceRefresh: false).Select(t => new { id = t.ID, name = t.Name }).ToList();
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
        Logger.Info("Dashboard: Saving updated provider settings...");
        ConfigProvider.SaveSettings(config);
        return Ok(new { status = "ok" });
    }

    /// <summary>Builds and returns a JSON schema representation of the configuration properties.</summary>
    /// <returns>A schema list for dynamic form rendering.</returns>
    [HttpGet("config/schema")]
    public IActionResult GetConfigSchema() => Ok(new { properties = BuildConfigSchema(typeof(RelayConfig), "") });

    /// <summary>Generates and serves a dynamically mapped CSS file combining the selected Shoko WebUI theme with custom Relay variables.</summary>
    /// <returns>A dynamic CSS stylesheet content result.</returns>
    [HttpGet("theme.css")]
    public IActionResult GetDynamicThemeCss()
    {
        string themeId = ConfigProvider.GetSettings().Advanced.SelectedTheme;
        if (string.IsNullOrWhiteSpace(themeId) || themeId.Equals("default", StringComparison.OrdinalIgnoreCase))
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
            string cssPath = Path.Combine(applicationPaths.ThemesPath, $"{themeId}.css");
            return !IoFile.Exists(cssPath)
                ? Content(string.Empty, "text/css")
                : Content(
                    IoFile.ReadAllText(cssPath)
                        + """

                        /* Shoko Relay Theme Variable Mapping */
                        :root {
                          --bg-color: var(--panel-background-alt, #0f0f0f);
                          --panel-color: var(--panel-background, #1c1c1c);
                          --border-color: var(--panel-border, #0f0f0f);
                          --inset-color: var(--panel-input, #151515);
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
        catch (Exception ex)
        {
            Logger.Warn(ex, "Theme: Failed to generate dynamic mapped CSS for theme {0}", themeId);
            return Content(string.Empty, "text/css");
        }
    }

    #endregion

    #region Tasks

    /// <summary>Returns a list of currently running tasks for the dashboard UI.</summary>
    [HttpGet("tasks/active")]
    public IActionResult GetActiveTasks() => Ok(TaskHelper.ActiveTasks.Keys);

    /// <summary>Returns results of tasks completed since the last check.</summary>
    [HttpGet("tasks/completed")]
    public IActionResult GetCompletedTasks() => Ok(TaskHelper.TaskResults);

    /// <summary>Acknowledges and clears a completed task result so it isn't displayed again.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    [HttpPost("tasks/clear/{taskName}")]
    public IActionResult ClearTaskResult(string taskName)
    {
        TaskHelper.TaskResults.TryRemove(taskName, out _);
        return Ok();
    }

    #endregion

    #region Logs

    /// <summary>Returns a JSON list of all currently generated task logs that exist on disk.</summary>
    /// <returns>A list of log file metadata objects.</returns>
    [HttpGet("logs/list")]
    public IActionResult GetLogsList()
    {
        string logsDir = Path.Combine(ConfigProvider.PluginDirectory, "logs");
        return !Directory.Exists(logsDir)
            ? Ok(new { logs = Array.Empty<object>() })
            : Ok(
                Directory
                    .EnumerateFiles(logsDir, "*-report.log")
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(f => f)
                    .Select(file => new { name = file, friendlyName = TagHelper.TitleCase(file.Replace("-report.log", "", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal)) })
                    .ToList()
            );
    }

    /// <summary>Serves report files from the plugin's logs directory without allowing browser caching.</summary>
    /// <param name="fileName">The log filename.</param>
    /// <returns>The log content as text/plain.</returns>
    [HttpGet("logs/{fileName}")]
    public IActionResult GetLog(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { status = "error", message = "fileName is required" });
        var path = Path.Combine(ConfigProvider.PluginDirectory, "logs", fileName);
        if (!IoFile.Exists(path))
            return NotFound(new { status = "error", message = "log not found" });

        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return PhysicalFile(path, "text/plain");
    }

    #endregion

    #region Private Helpers

    /// <summary>Serves a razor template page from the dashboard directory, processing constants and injecting the base path.</summary>
    /// <param name="fileName">The filename of the razor template to serve.</param>
    /// <returns>An HTML content result or NotFound if the template does not exist.</returns>
    private IActionResult ServePage(string fileName)
    {
        string dashboardDir = Path.Combine(ConfigProvider.PluginDirectory, "dashboard");
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, fileName));

        if (!requested.StartsWith(Path.GetFullPath(dashboardDir), StringComparison.OrdinalIgnoreCase) || !IoFile.Exists(requested))
            return NotFound();

        var html = ProcessConstants(IoFile.ReadAllText(requested));
        if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
            html = html.Replace("<head>", $"<head>\n    <base href=\"{WebUtility.HtmlEncode($"{Request.PathBase}{ShokoRelayConstants.BasePath}/dashboard/")}\">", StringComparison.OrdinalIgnoreCase);
        return Content(html, "text/html");
    }

    /// <summary>Resolves the MIME content type for a given file path, prioritizing .cshtml templates.</summary>
    /// <param name="filePath">The physical path of the file to inspect.</param>
    /// <returns>A MIME type string mapping to the file's extension.</returns>
    private string GetContentType(string filePath) =>
        filePath.EndsWith(".cshtml") ? "text/html"
        : s_contentTypeProvider.TryGetContentType(filePath, out var contentType) ? contentType
        : "application/octet-stream";

    /// <summary>Replaces {{ConstantName}} placeholders with values from ShokoRelayConstants.</summary>
    private static string ProcessConstants(string html)
    {
        var fields = typeof(ShokoRelayConstants).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Where(f => f.IsLiteral && !f.IsInitOnly);
        foreach (var field in fields)
            html = Regex.Replace(html, $@"\{{\{{\s?{field.Name}\s?\}}}}", field.GetValue(null)?.ToString() ?? "");
        return html;
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
    private sealed record ConfigPropertySchema(string Path, string Type, string? Display, string? Description, object? DefaultValue, object? EnumValues, bool Advanced, bool Rebuild);

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
            string path = string.IsNullOrWhiteSpace(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            var display = prop.GetCustomAttribute<DisplayAttribute>();
            var defaultValue = prop.GetCustomAttribute<DefaultValueAttribute>()?.Value;
            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (propType.IsEnum)
            {
                var values = Enum.GetValues(propType)
                    .Cast<object>()
                    .Select(v =>
                    {
                        int iv = Convert.ToInt32(v);
                        string memberName = Enum.GetName(propType, v) ?? iv.ToString();
                        return new { name = propType.GetField(memberName)?.GetCustomAttribute<DisplayAttribute>()?.Name ?? memberName, value = iv };
                    })
                    .ToArray();
                props.Add(new ConfigPropertySchema(path, "enum", display?.Name, display?.Description, defaultValue, values, isAdvanced, needsRebuild));
            }
            else if (propType == typeof(bool))
                props.Add(new ConfigPropertySchema(path, "bool", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
            else if (propType == typeof(string))
                props.Add(new ConfigPropertySchema(path, "string", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
            else if (propType.IsPrimitive || propType == typeof(decimal))
                props.Add(new ConfigPropertySchema(path, "number", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
            else if (typeof(IDictionary).IsAssignableFrom(propType))
                props.Add(new ConfigPropertySchema(path, "json", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
            else if (propType.IsClass)
                props.AddRange(BuildConfigSchema(propType, path, isAdvanced));
        }
        return props;
    }

    #endregion
}
