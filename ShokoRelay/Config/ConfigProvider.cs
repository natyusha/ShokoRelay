using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shoko.Abstractions.Plugin;
using ShokoRelay.Vfs;

namespace ShokoRelay.Config;

/// <summary>
/// Manages loading, saving, validation and normalization of the plugin configuration and Plex token/secrets file.
/// Watches for external config changes to auto-invalidate the cache.
/// </summary>
public class ConfigProvider
{
    #region Setup & State

    /// <summary>Logger instance for configuration management operations.</summary>
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <summary>Shared JSON serializer options for reading and writing configuration files.</summary>
    private static readonly JsonSerializerOptions s_options = new() { AllowTrailingCommas = true, WriteIndented = true };

    /// <summary>File path for the plugin preferences file.</summary>
    private readonly string _filePath;

    /// <summary>File path for the Plex token secrets file.</summary>
    private readonly string _tokenPath;

    /// <summary>Synchronization lock for thread-safe access to cached configuration state.</summary>
    private readonly Lock _settingsLock = new();

    /// <summary>Cached in-memory instance of the relay configuration.</summary>
    private RelayConfig? _settings;

    /// <summary>Cached list of extra Plex user entries parsed from settings.</summary>
    private List<(string Name, string? Pin)>? _cachedExtraUsers;

    /// <summary>Cached list of discovered Plex servers.</summary>
    private List<PlexAvailableServer>? _cachedServers;

    /// <summary>Cached admin username retrieved from the Plex account.</summary>
    private string? _cachedAdminUsername;

    /// <summary>The absolute path to the plugin's base directory.</summary>
    public string PluginDirectory { get; }

    /// <summary>The absolute path to the plugin's configuration directory.</summary>
    public string ConfigDirectory { get; }

    /// <summary>Service for accessing the current HTTP context, used for URL discovery.</summary>
    public IHttpContextAccessor? HttpContextAccessor { get; set; }

    /// <summary>The externally-reachable base URL of Shoko server. Priority: 1. Advanced.ShokoServerUrl setting, 2. Current HTTP Context, 3. Last discovered value, 4. Localhost fallback.</summary>
    public string ServerBaseUrl
    {
        get
        {
            var settings = GetSettings();
            if (!string.IsNullOrWhiteSpace(settings.Advanced.ShokoServerUrl))
                return settings.Advanced.ShokoServerUrl.Trim().TrimEnd('/');
            if (HttpContextAccessor?.HttpContext is { } ctx)
            {
                var request = ctx.Request;

                // Inspect standard proxy headers to accurately detect HTTPS scheme and external hostname
                var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim() ?? (request.IsHttps ? "https" : request.Scheme);
                var host = request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim() ?? request.Host.ToString();

                var detectedUrl = $"{scheme}://{host}{request.PathBase}".TrimEnd('/');
                if (settings.Advanced.ShokoServerUrlContext != detectedUrl)
                {
                    settings.Advanced.ShokoServerUrlContext = detectedUrl;
                    SaveSettings(settings);
                }
                return detectedUrl;
            }
            return !string.IsNullOrWhiteSpace(settings.Advanced.ShokoServerUrlContext) ? settings.Advanced.ShokoServerUrlContext : "http://localhost:8111";
        }
    }

    /// <summary>Creates a new ConfigProvider using the specified paths provided by the host application.</summary>
    /// <param name="applicationPaths">Paths provided by the host application.</param>
    public ConfigProvider(IApplicationPaths applicationPaths)
    {
        PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

        var legacyConfig = Path.Combine(PluginDirectory, ShokoRelayConstants.FolderConfigSubfolder);
        var modernConfig = Path.Combine(applicationPaths.ConfigurationsPath, ShokoRelayConstants.PluginId);

        // Prioritize existing legacy configuration for manual installs. Fallback to the standard Shoko configuration directory for new installs
        ConfigDirectory = Directory.Exists(legacyConfig) ? legacyConfig : modernConfig;
        Directory.CreateDirectory(ConfigDirectory);

        _filePath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FilePreferences);
        _tokenPath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FilePlexToken);

        SetupWatcher(_filePath);
        SetupWatcher(_tokenPath);
    }

    #endregion

    #region Watcher Logic

    /// <summary>Initializes a file system watcher to detect external changes to configuration files.</summary>
    /// <param name="path">The file path to monitor.</param>
    private void SetupWatcher(string path)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        watcher.Changed += (_, _) => InvalidateSettings();
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>Invalidates cached settings and secrets in memory, forcing a reload on next access.</summary>
    private void InvalidateSettings()
    {
        lock (_settingsLock)
        {
            _settings = null;
            _cachedExtraUsers = null;
            _cachedServers = null;
            _cachedAdminUsername = null;
        }
        s_logger.Info("Config: Settings invalidated due to external file change");
    }

    #endregion

    #region Sanitization

    /// <summary>Convert any JsonElement trees within <paramref name="obj"/> into plain CLR values.</summary>
    /// <param name="obj">The JSON element or object to sanitize.</param>
    /// <returns>A sanitized object containing only plain CLR types.</returns>
    public object SanitizeConfigObject(object obj) =>
        obj switch
        {
            JsonElement je => SanitizeConfigElement(je),
            null => null!,
            _ => SanitizeConfigObject(JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(obj, s_options), s_options)!),
        };

    /// <summary>Recursively converts a <see cref="JsonElement"/> into plain primitive or dictionary types.</summary>
    /// <param name="je">The JSON element to convert.</param>
    /// <returns>A converted object containing standard CLR types.</returns>
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

    /// <summary>Return the current settings, loading from disk if not already cached.</summary>
    /// <returns>The current <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetSettings()
    {
        var current = _settings;
        if (current != null)
            return current;

        lock (_settingsLock)
        {
            if (_settings != null)
                return _settings;

            RelayConfig s;
            try
            {
                s = File.Exists(_filePath) ? JsonSerializer.Deserialize<RelayConfig>(File.ReadAllText(_filePath), s_options) ?? new() : new();
            }
            catch (Exception ex)
            {
                s_logger.Warn(ex, "Config: Invalid settings -> Using defaults");
                s = new();
            }
            ApplyDefaultValues(s);
            NormalizeVfsRoots(s);
            NormalizePathMappings(s);
            NormalizeCsvFields(s);
            NormalizeSettings(s);
            return _settings = s;
        }
    }

    /// <summary>Return the current settings, applying any path or query overrides from the current HTTP request.</summary>
    /// <returns>The effective <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetEffectiveSettings()
    {
        var settings = GetSettings();
        var ctx = HttpContextAccessor?.HttpContext;
        if (ctx == null || ctx.Request.Method != HttpMethods.Get)
            return settings;

        if (ctx.Items.TryGetValue("EffectiveRelayConfig", out var cached) && cached is RelayConfig cachedConfig)
            return cachedConfig;

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract from Path Segment (options/{overrides})
        var overridePath = ctx.Request.RouteValues["overrides"]?.ToString();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var pairs = overridePath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2)
                    overrides[kv[0].Trim()] = kv[1].Trim();
            }
        }

        // Extract from Query String (fallback for testing)
        foreach (var q in ctx.Request.Query)
            overrides[q.Key] = q.Value.ToString();

        if (overrides.Count == 0)
            return settings;

        var overridableProps = typeof(RelayConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.PropertyType != typeof(AdvancedConfig) && p.PropertyType != typeof(AutomationConfig) && p.PropertyType != typeof(PlaybackConfig))
            .ToList();

        if (!overridableProps.Any(p => overrides.ContainsKey(p.Name)))
            return settings;

        var cloned = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(settings, s_options), s_options)!;
        foreach (var prop in overridableProps)
        {
            if (overrides.TryGetValue(prop.Name, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                try
                {
                    if (prop.PropertyType == typeof(string))
                        prop.SetValue(cloned, val);
                    else if (prop.PropertyType == typeof(bool))
                        prop.SetValue(cloned, string.Equals(val, "1") || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase));
                    else if (prop.PropertyType.IsEnum)
                        prop.SetValue(cloned, Enum.Parse(prop.PropertyType, val, true));
                }
                catch
                { /* Ignore invalid override values */
                }
            }
        }

        ctx.Items["EffectiveRelayConfig"] = cloned;
        return cloned;
    }

    /// <summary>Construct a sanitized payload of settings plus minimal Plex auth information for the dashboard.</summary>
    /// <returns>A sanitized configuration object for dashboard consumption.</returns>
    public object GetDashboardConfig()
    {
        _ = ServerBaseUrl; // Explicitly access ServerBaseUrl to trigger the HttpContext based auto discovery on dashboard load

        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(GetSettings(), s_options))!;
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

    /// <summary>Validate, normalize and persist the supplied <paramref name="settings"/> to disk.</summary>
    /// <param name="settings">The <see cref="RelayConfig"/> instance to save.</param>
    public void SaveSettings(RelayConfig settings)
    {
        ApplyDefaultValues(settings);
        NormalizeVfsRoots(settings);
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), null, true))
            throw new ArgumentException("Config validation failed.");
        NormalizePathMappings(settings);
        NormalizeCsvFields(settings);
        lock (_settingsLock)
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, s_options));
        _settings = settings;
        _cachedExtraUsers = null; // Clear the cached extra users so they are re-parsed on the next sync after settings changes
    }

    #endregion

    #region Plex Secrets & Tokens

    /// <summary>Data structure representing saved Plex tokens and server discovery details on disk.</summary>
    private sealed class TokenFile
    {
        /// <summary>The saved Plex authentication token.</summary>
        public string? Token { get; set; }

        /// <summary>The unique client identifier for this installation.</summary>
        public string? ClientIdentifier { get; set; }

        /// <summary>The cached username of the Plex account admin.</summary>
        public string? AdminUsername { get; set; }

        /// <summary>List of discovered Plex servers.</summary>
        public List<PlexAvailableServer>? Servers { get; set; }

        /// <summary>List of discovered Plex libraries.</summary>
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
            s_logger.Warn(ex, "Config: Failed to delete token file");
        }
    }

    /// <summary>Reads and deserializes the Plex token secrets file from disk.</summary>
    /// <returns>A populated <see cref="TokenFile"/> instance or a new empty structure on failure.</returns>
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

    /// <summary>Serializes and writes updated Plex token and discovery details to disk.</summary>
    /// <param name="t">Plex token.</param>
    /// <param name="c">Client identifier.</param>
    /// <param name="a">Admin username.</param>
    /// <param name="s">Discovered servers list.</param>
    /// <param name="l">Discovered libraries list.</param>
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
                s_options
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
    public List<PlexAvailableServer> GetPlexDiscoveredServers()
    {
        lock (_settingsLock)
            return _cachedServers ??= ReadTokenFile().Servers ?? [];
    }

    /// <summary>Retrieves the list of discovered Plex libraries from the token file.</summary>
    /// <returns>A list of <see cref="PlexAvailableLibrary"/> instances.</returns>
    public List<PlexAvailableLibrary> GetPlexDiscoveredLibraries() => ReadTokenFile().Libraries ?? [];

    /// <summary>Retrieves the cached Plex admin username.</summary>
    /// <returns>The admin username, or null if not yet discovered.</returns>
    public string? GetAdminUsername()
    {
        lock (_settingsLock)
            return _cachedAdminUsername ??= ReadTokenFile().AdminUsername;
    }

    /// <summary>Refreshes the admin username from the Plex API and updates the local storage.</summary>
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

    /// <summary>Updates specific fields in the Plex token/secrets file.</summary>
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

    /// <summary>Parse a comma-separated string of Plex user entries, optionally containing 4-digit PINs.</summary>
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
    public List<(string Name, string? Pin)> GetExtraPlexUserEntries()
    {
        lock (_settingsLock)
            return _cachedExtraUsers ??= ParseExtraPlexUsers(GetSettings().Automation.ExtraPlexUsers);
    }

    #endregion

    #region Normalize & Validate

    /// <summary>Ensures the standard VFS root and the Movie VFS root do not collide.</summary>
    /// <param name="settings">The relay configuration instance to normalize.</param>
    private static void NormalizeVfsRoots(RelayConfig settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Advanced.VfsRootPath) && string.Equals(settings.Advanced.VfsRootPath.Trim(), settings.Advanced.MovieVfsRootPath?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            settings.Advanced.MovieVfsRootPath = ShokoRelayConstants.FolderMoviesDefault;
            if (string.Equals(settings.Advanced.VfsRootPath.Trim(), settings.Advanced.MovieVfsRootPath, StringComparison.OrdinalIgnoreCase))
                settings.Advanced.MovieVfsRootPath = "!ShokoRelayMovieVFS_Fallback";
        }
    }

    /// <summary>Normalizes path mapping keys and values to ensure consistent cross-platform separator formatting.</summary>
    /// <param name="settings">The relay configuration instance to update.</param>
    /// <returns>True if any path mappings were changed during normalization.</returns>
    private bool NormalizePathMappings(RelayConfig settings)
    {
        if (settings.Advanced.PathMappings.Count == 0)
            return false;
        var norm = settings.Advanced.PathMappings.ToDictionary(
            k =>
            {
                string n = k.Key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
                try
                {
                    return Path.IsPathRooted(n) ? Path.GetFullPath(n).TrimEnd(Path.DirectorySeparatorChar) : n.TrimEnd(Path.DirectorySeparatorChar);
                }
                catch
                {
                    return n;
                }
            },
            v => (TextHelper.NormalizePathForPlex(v.Value.Trim()) is var p && !p.StartsWith('/') && !p.Contains(':') && !p.StartsWith("//", StringComparison.Ordinal)) ? "/" + p : p
        );
        if (JsonSerializer.Serialize(settings.Advanced.PathMappings) == JsonSerializer.Serialize(norm))
            return false;
        settings.Advanced.PathMappings = norm;
        return true;
    }

    /// <summary>Normalizes comma-separated and newline-separated settings fields by trimming and removing duplicates.</summary>
    /// <param name="s">The relay configuration instance to normalize.</param>
    /// <returns>True if any fields were modified during normalization.</returns>
    private bool NormalizeCsvFields(RelayConfig s)
    {
        static string Norm(string? r, char separator) =>
            string.Join(separator + " ", (r ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));

        var (nt, ne) = (Norm(s.TagBlacklist, ','), Norm(s.Automation.ExtraPlexUsers, ','));
        bool c = s.TagBlacklist != nt || s.Automation.ExtraPlexUsers != ne;
        s.TagBlacklist = nt;
        s.Automation.ExtraPlexUsers = ne;

        // Normalize Path Exclusions (Newline separated)
        var nex = string.Join(
            Environment.NewLine,
            (s.Advanced.FolderExclusions ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(VfsShared.NormalizeSeparators).Distinct(VfsShared.PathComparer)
        );

        if (s.Advanced.FolderExclusions != nex)
        {
            s.Advanced.FolderExclusions = nex;
            c = true;
        }
        return c;
    }

    /// <summary>Applies default values to string properties on an object hierarchy where [DefaultValue] attributes exist.</summary>
    /// <param name="obj">The object to apply default values to.</param>
    private static void ApplyDefaultValues(object obj)
    {
        foreach (var p in obj.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            if (p.PropertyType == typeof(string) && string.IsNullOrWhiteSpace(p.GetValue(obj) as string) && p.GetCustomAttribute<DefaultValueAttribute>() is { } d)
                p.SetValue(obj, d.Value);
            else if (p.PropertyType.IsClass && p.PropertyType != typeof(string) && !typeof(IDictionary).IsAssignableFrom(p.PropertyType))
                ApplyDefaultValues(p.GetValue(obj)!);
        }
    }

    /// <summary>Recursively scans an object for properties with [Range] attributes and clamps their values accordingly.</summary>
    /// <param name="obj">The configuration object to normalize.</param>
    private static void NormalizeSettings(object obj)
    {
        if (obj == null)
            return;
        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            // Check for the Range attribute
            var range = prop.GetCustomAttribute<RangeAttribute>();
            if (range != null && prop.PropertyType == typeof(int))
            {
                int currentVal = (int)prop.GetValue(obj)!;
                int min = Convert.ToInt32(range.Minimum);
                int max = Convert.ToInt32(range.Maximum);

                int clampedVal = Math.Clamp(currentVal, min, max);
                if (currentVal != clampedVal)
                {
                    prop.SetValue(obj, clampedVal);
                    s_logger.Trace("Config: Clamped {0} from {1} to {2}", prop.Name, currentVal, clampedVal);
                }
            }
            // Recurse into nested config classes (AutomationConfig, AdvancedConfig, etc.)
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var subObj = prop.GetValue(obj);
                if (subObj != null)
                    NormalizeSettings(subObj);
            }
        }
    }

    #endregion
}
