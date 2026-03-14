using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Controllers;

/// <summary>Handles the plugin's frontend components, serving static assets and dynamic configuration data.</summary>
[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayInfo.BasePath)]
public class DashboardController(ConfigProvider configProvider, IMetadataService metadataService, PlexClient plexLibrary) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypeProvider = new();

    #region Pages & Assets

    /// <summary>Serves the embedded dashboard UI and its static assets from the plugin folder.</summary>
    /// <param name="path">The relative sub-path within the dashboard directory.</param>
    /// <returns>The requested file or HTML content.</returns>
    [HttpGet("dashboard/{*path}")]
    public IActionResult GetControllerPage([FromRoute] string? path = null)
    {
        string dashboardDir = Path.Combine(_configProvider.PluginDirectory, "dashboard");
        bool isPlayer = "player".Equals(path, StringComparison.OrdinalIgnoreCase);
        string fileName = (string.IsNullOrWhiteSpace(path) || isPlayer) ? (isPlayer ? "player.cshtml" : "dashboard.cshtml") : path;
        string safePath = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, safePath));
        string dashboardRoot = Path.GetFullPath(dashboardDir);

        if (!requested.StartsWith(dashboardRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(requested))
            return NotFound();

        string contentType = GetContentType(requested);
        string ext = Path.GetExtension(requested).ToLowerInvariant();
        if (ext != ".cshtml")
            return PhysicalFile(requested, contentType);

        var html = System.IO.File.ReadAllText(requested);
        if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
        {
            var reqPath = Request.Path.Value ?? "";
            int dashIdx = reqPath.IndexOf("/dashboard", StringComparison.OrdinalIgnoreCase);
            var baseHref = reqPath[..(dashIdx + 10)].TrimEnd('/') + "/";
            var baseTag = $"\n    <base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">";
            html = html.Replace("<head>", "<head>" + baseTag, StringComparison.OrdinalIgnoreCase);
        }
        return Content(html, "text/html");
    }

    #endregion

    #region Config Management

    /// <summary>Returns the current configuration payload used by the dashboard UI.</summary>
    /// <returns>A JSON payload containing settings and VFS overrides.</returns>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var payload = _configProvider.GetDashboardConfig();
        try
        {
            var path = Path.Combine(ShokoRelay.ConfigDirectory, "anidb_vfs_overrides.csv");
            string overrides = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
            return Ok(new { payload, overrides });
        }
        catch
        {
            return Ok(new { payload, overrides = string.Empty });
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
        _configProvider.SaveSettings(config);
        return Ok(new { status = "ok" });
    }

    /// <summary>Builds and returns a JSON schema representation of the configuration properties.</summary>
    /// <returns>A schema list for dynamic form rendering.</returns>
    [HttpGet("config/schema")]
    public IActionResult GetConfigSchema()
    {
        var props = BuildConfigSchema(typeof(RelayConfig), "");
        return Ok(new { properties = props });
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
        string logsDir = Path.Combine(_configProvider.PluginDirectory, "logs");
        string path = Path.Combine(logsDir, fileName);
        return !System.IO.File.Exists(path) ? NotFound(new { status = "error", message = "log not found" }) : PhysicalFile(path, "text/plain", fileName);
    }

    #endregion

    #region Private Helpers

    private string GetContentType(string filePath) =>
        filePath.EndsWith(".cshtml") ? "text/html"
        : ContentTypeProvider.TryGetContentType(filePath, out var contentType) ? contentType
        : "application/octet-stream";

    /// <summary>Schema definition for a configuration property.</summary>
    private sealed record ConfigPropertySchema(string Path, string Type, string? Display, string? Description, object? DefaultValue, object? EnumValues, bool Advanced);

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
                props.Add(new ConfigPropertySchema(path, "enum", display?.Name, display?.Description, defaultValue, values, isAdvanced));
                continue;
            }
            if (propType == typeof(bool))
            {
                props.Add(new ConfigPropertySchema(path, "bool", display?.Name, display?.Description, defaultValue, null, isAdvanced));
                continue;
            }
            if (propType == typeof(string))
            {
                props.Add(new ConfigPropertySchema(path, "string", display?.Name, display?.Description, defaultValue, null, isAdvanced));
                continue;
            }
            if (propType.IsPrimitive || propType == typeof(decimal))
            {
                props.Add(new ConfigPropertySchema(path, "number", display?.Name, display?.Description, defaultValue, null, isAdvanced));
                continue;
            }
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(propType))
            {
                props.Add(new ConfigPropertySchema(path, "json", display?.Name, display?.Description, defaultValue, null, isAdvanced));
                continue;
            }
            if (propType.IsClass && propType != typeof(string))
                props.AddRange(BuildConfigSchema(propType, path, isAdvanced));
        }
        return props;
    }

    #endregion
}
