using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        string dashboardRoot = Path.GetFullPath(dashboardDir);

        return !requested.StartsWith(dashboardRoot, StringComparison.OrdinalIgnoreCase) || !IoFile.Exists(requested) || requested.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
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

        try
        {
            var path = Path.Combine(ConfigDirectory, ShokoRelayConstants.FileVfsOverrides);
            string overrides = IoFile.Exists(path) ? IoFile.ReadAllText(path) : string.Empty;
            return Ok(
                new
                {
                    payload,
                    overrides,
                    themes,
                }
            );
        }
        catch
        {
            return Ok(
                new
                {
                    payload,
                    overrides = string.Empty,
                    themes,
                }
            );
        }
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
        var settings = ConfigProvider.GetSettings();
        string themeId = settings.Advanced.SelectedTheme;

        if (string.IsNullOrWhiteSpace(themeId) || themeId.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Content(string.Empty, "text/css");

        if (string.Equals(themeId, "shoko-gray", StringComparison.OrdinalIgnoreCase))
        {
            var shokoGrayBuilder = new StringBuilder();
            shokoGrayBuilder.AppendLine("/* Shoko Gray Theme Variables */");
            shokoGrayBuilder.AppendLine(":root {");
            shokoGrayBuilder.AppendLine("  --bg-color: #282e38;");
            shokoGrayBuilder.AppendLine("  --panel-color: #2c333e;");
            shokoGrayBuilder.AppendLine("  --border-color: #21242b;");
            shokoGrayBuilder.AppendLine("  --inset-color: #1e2027;");
            shokoGrayBuilder.AppendLine("  --text-color: #cfd8e3;");
            shokoGrayBuilder.AppendLine("  --highlight-color: #44a3ff;");
            shokoGrayBuilder.AppendLine("  --button-color: #44a3ff;");
            shokoGrayBuilder.AppendLine("  --hover-color: #64b3ff;");
            shokoGrayBuilder.AppendLine("  --danger-color: #ff6c6c;");
            shokoGrayBuilder.AppendLine("  --warning-color: #f9c851;");
            shokoGrayBuilder.AppendLine("  --ok-color: #10c469;");
            shokoGrayBuilder.AppendLine("  --logo-outline: #000;");
            shokoGrayBuilder.AppendLine("  --logo-skin: #fdf5e8;");
            shokoGrayBuilder.AppendLine("  --logo-face-shadow: #fe514d;");
            shokoGrayBuilder.AppendLine("  --logo-eye-ref1: #e3e4d6;");
            shokoGrayBuilder.AppendLine("  --logo-eye-ref2: #e8c8bb;");
            shokoGrayBuilder.AppendLine("  --logo-eye-ref3: #ffc2b2;");
            shokoGrayBuilder.AppendLine("  --logo-eye-gradient1: #ae303b;");
            shokoGrayBuilder.AppendLine("  --logo-eye-gradient2: #ec4050;");
            shokoGrayBuilder.AppendLine("  --logo-eye-gradient3: #fd877d;");
            shokoGrayBuilder.AppendLine("  --logo-hair-gradient1: #c33144;");
            shokoGrayBuilder.AppendLine("  --logo-hair-gradient2: #6b8cdb;");
            shokoGrayBuilder.AppendLine("  --logo-hair-gradient3: #79f0f8;");
            shokoGrayBuilder.AppendLine("}");

            return Content(shokoGrayBuilder.ToString(), "text/css");
        }

        try
        {
            string cssPath = Path.Combine(applicationPaths.ThemesPath, $"{themeId}.css");

            if (!IoFile.Exists(cssPath))
                return Content(string.Empty, "text/css");

            string rawCss = IoFile.ReadAllText(cssPath);

            var mappingBuilder = new StringBuilder();
            mappingBuilder.AppendLine().AppendLine("/* Shoko Relay Theme Variable Mapping */").AppendLine(":root {");
            mappingBuilder.AppendLine("  --bg-color: var(--panel-background-alt, #0f0f0f);");
            mappingBuilder.AppendLine("  --panel-color: var(--panel-background, #1c1c1c);");
            mappingBuilder.AppendLine("  --border-color: var(--panel-border, #0f0f0f);");
            mappingBuilder.AppendLine("  --inset-color: var(--panel-input, #151515);");
            mappingBuilder.AppendLine("  --text-color: var(--panel-text, #ddd);");
            mappingBuilder.AppendLine("  --highlight-color: var(--panel-icon-action, #94aad1);");
            mappingBuilder.AppendLine("  --button-color: var(--button-primary, #a497b0);");
            mappingBuilder.AppendLine("  --hover-color: var(--button-primary-hover, #ea005e);");
            mappingBuilder.AppendLine("  --danger-color: var(--panel-icon-danger, #cc0000);");
            mappingBuilder.AppendLine("  --warning-color: var(--panel-icon-warning, #f9c851);");
            mappingBuilder.AppendLine("  --ok-color: var(--panel-text-important, #00ab3f);");
            mappingBuilder.AppendLine("}");

            return Content(rawCss + mappingBuilder.ToString(), "text/css");
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

    /// <summary>Serves report files from the plugin's logs directory.</summary>
    /// <param name="fileName">The log filename.</param>
    /// <returns>The log content as text/plain.</returns>
    [HttpGet("logs/{fileName}")]
    public IActionResult GetLog(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { status = "error", message = "fileName is required" });
        string logsDir = Path.Combine(ConfigProvider.PluginDirectory, "logs");
        string path = Path.Combine(logsDir, fileName);
        return !IoFile.Exists(path) ? NotFound(new { status = "error", message = "log not found" }) : PhysicalFile(path, "text/plain");
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
        string dashboardRoot = Path.GetFullPath(dashboardDir);

        if (!requested.StartsWith(dashboardRoot, StringComparison.OrdinalIgnoreCase) || !IoFile.Exists(requested))
            return NotFound();

        var html = IoFile.ReadAllText(requested);
        html = ProcessConstants(html); // Process C# Constants into HTML

        if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
        {
            var baseHref = $"{Request.PathBase}{ShokoRelayConstants.BasePath}/dashboard/";
            var baseTag = $"\n    <base href=\"{WebUtility.HtmlEncode(baseHref)}\">";
            html = html.Replace("<head>", "<head>" + baseTag, StringComparison.OrdinalIgnoreCase);
        }
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
        {
            string value = field.GetValue(null)?.ToString() ?? "";
            string pattern = $@"\{{\{{\s?{field.Name}\s?\}}}}"; // Account for prettier adding a space
            html = Regex.Replace(html, pattern, value);
        }
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
                        var field = propType.GetField(memberName);
                        return new { name = field?.GetCustomAttribute<DisplayAttribute>()?.Name ?? memberName, value = iv };
                    })
                    .ToArray();
                props.Add(new ConfigPropertySchema(path, "enum", display?.Name, display?.Description, defaultValue, values, isAdvanced, needsRebuild));
                continue;
            }
            if (propType == typeof(bool))
            {
                props.Add(new ConfigPropertySchema(path, "bool", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
                continue;
            }
            if (propType == typeof(string))
            {
                props.Add(new ConfigPropertySchema(path, "string", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
                continue;
            }
            if (propType.IsPrimitive || propType == typeof(decimal))
            {
                props.Add(new ConfigPropertySchema(path, "number", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
                continue;
            }
            if (typeof(IDictionary).IsAssignableFrom(propType))
            {
                props.Add(new ConfigPropertySchema(path, "json", display?.Name, display?.Description, defaultValue, null, isAdvanced, needsRebuild));
                continue;
            }
            if (propType.IsClass && propType != typeof(string))
                props.AddRange(BuildConfigSchema(propType, path, isAdvanced));
        }
        return props;
    }

    #endregion
}
