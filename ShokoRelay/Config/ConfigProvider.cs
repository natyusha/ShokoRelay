using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NLog;
using Shoko.Abstractions.Plugin;
using ShokoRelay.Plex;

namespace ShokoRelay.Config;

/// <summary>
/// Manages loading, saving, validation and normalization of the plugin configuration and Plex token/secrets file.
/// Watches for external config changes to auto-invalidate the cache.
/// </summary>
public class ConfigProvider
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions Options = new() { AllowTrailingCommas = true, WriteIndented = true };

    private readonly string _filePath,
        _tokenPath;
    private readonly Lock _settingsLock = new();
    private RelayConfig? _settings;
    private List<(string Name, string? Pin)>? _cachedExtraUsers;
    private List<PlexAvailableServer>? _cachedServers;
    private string? _cachedAdminUsername;

    /// <summary>The absolute path to the plugin's base directory.</summary>
    public string PluginDirectory { get; }

    /// <summary>Service for accessing the current HTTP context, used for URL discovery.</summary>
    public IHttpContextAccessor? HttpContextAccessor { get; set; }

    /// <summary>
    /// The externally-reachable base URL of the Shoko server. Priority: 1. Advanced.ShokoServerUrl setting, 2. Current HTTP Context, 3. Last known good value.
    /// </summary>
    public string ServerBaseUrl
    {
        get
        {
            var configUrl = GetSettings()?.Advanced.ShokoServerUrl;
            if (!string.IsNullOrWhiteSpace(configUrl))
                return configUrl.Trim().TrimEnd('/');
            if (HttpContextAccessor?.HttpContext is { } ctx)
                field = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return field;
        }
    } = "http://localhost:8111";

    /// <summary>
    /// Creates a new ConfigProvider using the specified paths provided by the host application.
    /// </summary>
    /// <param name="applicationPaths">Paths provided by the host application.</param>
    public ConfigProvider(IApplicationPaths applicationPaths)
    {
        PluginDirectory = Path.Combine(applicationPaths.PluginsPath, ConfigConstants.PluginSubfolder);
        string configDir = Path.Combine(PluginDirectory, ConfigConstants.ConfigSubfolder);
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, ConfigConstants.ConfigFileName);
        _tokenPath = Path.Combine(configDir, ConfigConstants.SecretsFileName);

        SetupWatcher(_filePath);
        SetupWatcher(_tokenPath);
    }

    #endregion

    #region Watcher Logic

    private void SetupWatcher(string path)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        watcher.Changed += (_, _) => InvalidateSettings();
        watcher.EnableRaisingEvents = true;
    }

    private void InvalidateSettings()
    {
        lock (_settingsLock)
        {
            _settings = null;
            _cachedExtraUsers = null;
            _cachedServers = null;
            _cachedAdminUsername = null;
        }
        Logger.Info("Settings invalidated due to external file change.");
    }

    #endregion

    #region Sanitization

    /// <summary>
    /// Convert any JsonElement trees within <paramref name="obj"/> into plain CLR values.
    /// </summary>
    /// <param name="obj">The JSON element or object to sanitize.</param>
    /// <returns>A sanitized object containing only plain CLR types.</returns>
    public object SanitizeConfigObject(object obj) =>
        obj switch
        {
            JsonElement je => SanitizeConfigElement(je),
            null => null!,
            _ => SanitizeConfigObject(JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(obj, Options), Options)!),
        };

    private object SanitizeConfigElement(JsonElement je) =>
        je.ValueKind switch
        {
            JsonValueKind.String => je.GetString()!,
            JsonValueKind.Number => je.TryGetInt32(out var i) ? i
            : je.TryGetInt64(out var l) ? l
            : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => SanitizeConfigObject(p.Value)),
            JsonValueKind.Array => je.EnumerateArray().Select(x => SanitizeConfigObject(x)).ToList(),
            _ => null!,
        };

    #endregion

    #region Settings Management

    /// <summary>
    /// Return the current settings, loading from disk if not already cached.
    /// </summary>
    /// <returns>The current <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetSettings() => _settings ??= GetSettingsFromFile();

    /// <summary>
    /// Construct a sanitized payload of settings plus minimal Plex auth information for the dashboard.
    /// </summary>
    /// <returns>A sanitized configuration object for dashboard consumption.</returns>
    public object GetDashboardConfig()
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(GetSettings(), Options))!;
        dict["PlexLibrary"] = new
        {
            HasToken = !string.IsNullOrWhiteSpace(GetPlexToken()),
            ClientIdentifier = GetPlexClientIdentifier(),
            DiscoveredServers = GetPlexDiscoveredServers(),
            DiscoveredLibraries = GetPlexDiscoveredLibraries(),
        };
        dict["PlexAuth"] = new { ClientIdentifier = GetPlexClientIdentifier() };
        return SanitizeConfigObject(dict);
    }

    /// <summary>The absolute path to the plugin's configuration directory.</summary>
    public string ConfigDirectory => Path.GetDirectoryName(_filePath)!;

    /// <summary>
    /// Validate, normalize and persist the supplied <paramref name="settings"/> to disk.
    /// </summary>
    /// <param name="settings">The <see cref="RelayConfig"/> instance to save.</param>
    public void SaveSettings(RelayConfig settings)
    {
        ApplyDefaultValues(settings);
        ValidateSettings(settings);
        NormalizePathMappings(settings);
        NormalizeCsvFields(settings);
        lock (_settingsLock)
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, Options));
        _settings = settings;
    }

    private RelayConfig GetSettingsFromFile()
    {
        RelayConfig s;
        try
        {
            s = File.Exists(_filePath) ? JsonSerializer.Deserialize<RelayConfig>(File.ReadAllText(_filePath), Options) ?? new() : new();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Invalid config, using defaults.");
            s = new();
        }
        ApplyDefaultValues(s);
        NormalizePathMappings(s);
        NormalizeCsvFields(s);
        return s;
    }

    #endregion

    #region Plex Secrets and Token Management

    private sealed class TokenFile
    {
        public string? Token { get; set; }
        public string? ClientIdentifier { get; set; }
        public string? AdminUsername { get; set; }
        public List<PlexAvailableServer>? Servers { get; set; }
        public List<PlexAvailableLibrary>? Libraries { get; set; }
    }

    /// <summary>Deletes the Plex token/secrets file from disk.</summary>
    public void DeleteTokenFile()
    {
        try
        {
            if (File.Exists(_tokenPath))
                File.Delete(_tokenPath);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to delete token file.");
        }
    }

    private TokenFile ReadTokenFile()
    {
        try
        {
            return File.Exists(_tokenPath) ? JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(_tokenPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private void WriteTokenFile(string? t, string? c, string? a, List<PlexAvailableServer>? s = null, List<PlexAvailableLibrary>? l = null) =>
        File.WriteAllText(
            _tokenPath,
            JsonSerializer.Serialize(
                new TokenFile
                {
                    Token = t ?? "",
                    ClientIdentifier = c ?? "",
                    AdminUsername = a ?? "",
                    Servers = s ?? [],
                    Libraries = l ?? [],
                },
                Options
            )
        );

    /// <summary>Retrieves the saved Plex authentication token.</summary>
    /// <returns>The Plex authentication token string.</returns>
    public string GetPlexToken() => ReadTokenFile().Token ?? "";

    /// <summary>Retrieves or generates the unique Plex client identifier.</summary>
    /// <returns>The Plex client identifier string.</returns>
    public string GetPlexClientIdentifier()
    {
        var tf = ReadTokenFile();
        if (string.IsNullOrWhiteSpace(tf.ClientIdentifier))
        {
            tf.ClientIdentifier = Guid.NewGuid().ToString("N");
            WriteTokenFile(tf.Token, tf.ClientIdentifier, tf.AdminUsername, tf.Servers, tf.Libraries);
        }
        return tf.ClientIdentifier;
    }

    /// <summary>Retrieves the list of discovered Plex servers from the token file.</summary>
    /// <returns>A list of <see cref="PlexAvailableServer"/> instances.</returns>
    public List<PlexAvailableServer> GetPlexDiscoveredServers() => _cachedServers ??= ReadTokenFile().Servers ?? [];

    /// <summary>Retrieves the list of discovered Plex libraries from the token file.</summary>
    /// <returns>A list of <see cref="PlexAvailableLibrary"/> instances.</returns>
    public List<PlexAvailableLibrary> GetPlexDiscoveredLibraries() => ReadTokenFile().Libraries ?? [];

    /// <summary>Retrieves the cached Plex admin username.</summary>
    /// <returns>The admin username, or null if not yet discovered.</returns>
    public string? GetAdminUsername() => _cachedAdminUsername ??= ReadTokenFile().AdminUsername;

    /// <summary>
    /// Refreshes the admin username from the Plex API and updates the local storage.
    /// </summary>
    /// <param name="auth">The <see cref="PlexAuth"/> service to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the refresh operation.</returns>
    public async Task RefreshAdminUsername(PlexAuth auth, CancellationToken ct)
    {
        if (await auth.GetAccountInfoAsync(GetPlexToken(), ct) is { } info)
        {
            _cachedAdminUsername = info.Title ?? info.Username;
            UpdatePlexTokenInfo(adminName: _cachedAdminUsername);
        }
    }

    /// <summary>
    /// Updates specific fields in the Plex token/secrets file.
    /// </summary>
    /// <param name="token">Optional new Plex token.</param>
    /// <param name="clientIdentifier">Optional new client identifier.</param>
    /// <param name="adminName">Optional new admin username.</param>
    /// <param name="servers">Optional list of discovered servers.</param>
    /// <param name="libraries">Optional list of discovered libraries.</param>
    public void UpdatePlexTokenInfo(string? token = null, string? clientIdentifier = null, string? adminName = null, List<PlexAvailableServer>? servers = null, List<PlexAvailableLibrary>? libraries = null)
    {
        var e = ReadTokenFile();
        WriteTokenFile(token ?? e.Token, clientIdentifier ?? e.ClientIdentifier, adminName ?? e.AdminUsername, servers ?? e.Servers, libraries ?? e.Libraries);
    }

    /// <summary>Checks if the provided server UUID matches a managed server known to the provider.</summary>
    /// <param name="uuid">The server UUID to check.</param>
    /// <returns>True if the server is in the discovered list.</returns>
    public bool IsManagedServer(string? uuid) => !string.IsNullOrWhiteSpace(uuid) && GetPlexDiscoveredServers().Any(s => string.Equals(s.Id, uuid, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region User Management

    /// <summary>
    /// Parse a comma-separated string of Plex user entries, optionally containing 4-digit PINs.
    /// </summary>
    /// <param name="raw">The raw configuration string to parse.</param>
    /// <returns>A list of tuples containing the username and optional PIN.</returns>
    public static List<(string Name, string? Pin)> ParseExtraPlexUsers(string? raw) =>
        [
            .. (raw ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Split(';', 2))
                .Where(p => p.Length > 0 && !string.IsNullOrWhiteSpace(p[0]))
                .Select(p => (Name: p[0].Trim(), Pin: (p.Length > 1 && p[1].Trim().Length == 4 && p[1].Trim().All(char.IsDigit)) ? p[1].Trim() : null)),
        ];

    /// <summary>Returns the parsed and cached list of extra Plex users configured in settings.</summary>
    /// <returns>A list of extra user name and PIN tuples.</returns>
    public List<(string Name, string? Pin)> GetExtraPlexUserEntries() => _cachedExtraUsers ??= ParseExtraPlexUsers(GetSettings().Automation.ExtraPlexUsers);

    #endregion

    #region Normalization and Validation

    private bool NormalizePathMappings(RelayConfig settings)
    {
        if (settings.Advanced.PathMappings.Count == 0)
            return false;
        var norm = settings.Advanced.PathMappings.ToDictionary(k => NormalizeShokoKey(k.Key), v => NormalizePlexBasePath(v.Value));
        if (JsonSerializer.Serialize(settings.Advanced.PathMappings) == JsonSerializer.Serialize(norm))
            return false;
        settings.Advanced.PathMappings = norm;
        return true;
    }

    private bool NormalizeCsvFields(RelayConfig s)
    {
        static string Norm(string? r) => string.Join(", ", (r ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        var (nt, ne) = (Norm(s.TagBlacklist), Norm(s.Automation.ExtraPlexUsers));
        bool c = s.TagBlacklist != nt || s.Automation.ExtraPlexUsers != ne;
        s.TagBlacklist = nt;
        s.Automation.ExtraPlexUsers = ne;
        return c;
    }

    private static string NormalizeShokoKey(string k)
    {
        string n = k.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
        try
        {
            return Path.IsPathRooted(n) ? Path.GetFullPath(n).TrimEnd(Path.DirectorySeparatorChar) : n.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return n;
        }
    }

    private static string NormalizePlexBasePath(string v) => (v.Trim().Replace('\\', '/').TrimEnd('/') is var p && !p.StartsWith("/") && !p.Contains(":") && !p.StartsWith("//")) ? "/" + p : p;

    private static void ApplyDefaultValues(object obj)
    {
        foreach (var p in obj.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            if (p.PropertyType == typeof(string) && string.IsNullOrWhiteSpace(p.GetValue(obj) as string) && p.GetCustomAttribute<DefaultValueAttribute>() is { } d)
                p.SetValue(obj, d.Value);
            else if (p.PropertyType.IsClass && p.PropertyType != typeof(string) && !typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType))
                ApplyDefaultValues(p.GetValue(obj)!);
        }
    }

    private static void ValidateSettings(RelayConfig s)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(s, new ValidationContext(s), results, true))
            throw new ArgumentException("Config validation failed.");
    }

    #endregion
}
