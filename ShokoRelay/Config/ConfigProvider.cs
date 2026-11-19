using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shoko.Abstractions.Plugin;

namespace ShokoRelay.Config;

/// <summary>Manages loading, saving, validation and normalization of the plugin configuration and Plex token/secrets file.</summary>
/// <remarks>Watches for external config changes to auto-invalidate the cache.</remarks>
public partial class ConfigProvider : IDisposable
{
    #region Setup & State

    /// <summary>Logger instance for configuration management operations.</summary>
    private readonly ILogger<ConfigProvider>? _logger;

    /// <summary>Service for accessing the current HTTP context, used for URL discovery.</summary>
    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>Shared JSON serializer options for reading and writing configuration files.</summary>
    private static readonly JsonSerializerOptions s_options = new() { AllowTrailingCommas = true, WriteIndented = true };

    /// <summary>Pre-cached array of overridable configuration properties to eliminate reflection overhead on requests.</summary>
    private static readonly PropertyInfo[] s_overridableProps =
    [
        .. typeof(RelayConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.PropertyType != typeof(AdvancedConfig) && p.PropertyType != typeof(AutomationConfig) && p.PropertyType != typeof(PlaybackConfig)),
    ];

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

    /// <summary>Cached list of Shoko-to-Plex user mappings parsed from settings.</summary>
    private List<AdvancedUserMappingEntry>? _cachedAdvancedUserMappings;

    /// <summary>Cached list of discovered Plex servers.</summary>
    private List<PlexAvailableServer>? _cachedServers;

    /// <summary>Cached admin username retrieved from the Plex account.</summary>
    private string? _cachedAdminUsername;

    /// <summary>Cached contents of the Plex token file to prevent continuous disk I/O.</summary>
    private TokenFile? _cachedTokenFile;

    /// <summary>File system watcher monitoring changes to preferences.json.</summary>
    private readonly FileSystemWatcher _settingsWatcher;

    /// <summary>File system watcher monitoring changes to plex.token.</summary>
    private readonly FileSystemWatcher _tokenWatcher;

    /// <summary>Monotonic timestamp in milliseconds until which self-initiated settings watcher events are suppressed.</summary>
    private long _suppressSettingsWatcherUntil;

    /// <summary>Monotonic timestamp in milliseconds until which self-initiated token watcher events are suppressed.</summary>
    private long _suppressTokenWatcherUntil;

    /// <summary>The absolute path to the plugin's base directory.</summary>
    public string PluginDirectory { get; }

    /// <summary>The absolute path to the plugin's configuration directory.</summary>
    public string ConfigDirectory { get; }

    /// <summary>The absolute path to the plugin's cache directory.</summary>
    public string CacheDirectory { get; }

    /// <summary>The absolute path to the plugin's task logs directory.</summary>
    public string LogsDirectory => Path.Combine(ConfigDirectory, "logs");

    /// <summary>The externally-reachable base URL of Shoko server. Priority: 1. Advanced.ShokoServerUrl setting, 2. Current HTTP Context, 3. Last discovered value, 4. Localhost fallback.</summary>
    public string ServerBaseUrl
    {
        get
        {
            var settings = GetSettings();
            return !string.IsNullOrWhiteSpace(settings.Advanced.ShokoServerUrl) ? settings.Advanced.ShokoServerUrl.Trim().TrimEnd('/')
                : DetectRequestBaseUrl() is { } detectedUrl ? detectedUrl
                : ReadTokenFile().AutoShokoServerUrl is { Length: > 0 } cachedUrl ? cachedUrl
                : "http://localhost:8111";
        }
    }

    /// <summary>Extracts the externally-reachable base URL from the current HTTP request headers, if an HTTP context is active.</summary>
    /// <returns>The detected base URL without trailing slash, or <c>null</c> when no HTTP context is active.</returns>
    private string? DetectRequestBaseUrl()
    {
        if (_httpContextAccessor?.HttpContext is not { } ctx)
            return null;

        var request = ctx.Request;

        // Inspect standard proxy headers to accurately detect HTTPS scheme and external hostname
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(scheme) || (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            scheme = request.IsHttps ? "https" : request.Scheme;

        var rawHost = request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = !string.IsNullOrEmpty(rawHost) && TextHelper.ForwardedHostRegex().IsMatch(rawHost) ? rawHost : request.Host.ToString();
        return $"{scheme}://{host}{request.PathBase}".TrimEnd('/');
    }

    /// <summary>Creates a new ConfigProvider using the specified paths provided by the host application.</summary>
    /// <param name="applicationPaths">Shoko application paths.</param>
    /// <param name="pluginPaths">Optional plugin-specific paths resolved by Shoko Server.</param>
    /// <param name="httpContextAccessor">Optional HTTP context accessor for URL discovery.</param>
    /// <param name="logger">Logger instance.</param>
    public ConfigProvider(IApplicationPaths applicationPaths, PluginPaths<Plugin>? pluginPaths = null, IHttpContextAccessor? httpContextAccessor = null, ILogger<ConfigProvider>? logger = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        PluginDirectory = pluginPaths?.InstallPath ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

        ConfigDirectory = pluginPaths?.ConfigurationsPath ?? Path.Combine(applicationPaths.ConfigurationsPath, ShokoRelayConstants.PluginId);
        CacheDirectory = pluginPaths?.CachePath ?? Path.Combine(applicationPaths.CachePath, ShokoRelayConstants.PluginId);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogsDirectory);

        _filePath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FilePreferences);
        _tokenPath = Path.Combine(ConfigDirectory, ShokoRelayConstants.FilePlexToken);

        _settingsWatcher = SetupWatcher(_filePath, () => Environment.TickCount64 > Volatile.Read(ref _suppressSettingsWatcherUntil), InvalidateSettings);
        _tokenWatcher = SetupWatcher(_tokenPath, () => Environment.TickCount64 > Volatile.Read(ref _suppressTokenWatcherUntil), InvalidateTokenInfo);
    }

    #endregion

    #region Watcher Logic

    /// <summary>Initializes a file system watcher to detect external changes to configuration files.</summary>
    /// <param name="path">The file path to monitor.</param>
    /// <param name="shouldHandle">Predicate evaluated before invalidating to skip self-initiated writes.</param>
    /// <param name="onChanged">Callback invoked when an external file modification is detected.</param>
    /// <returns>The configured <see cref="FileSystemWatcher"/> instance.</returns>
    private static FileSystemWatcher SetupWatcher(string path, Func<bool> shouldHandle, Action onChanged)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
        void HandleEvent(object _, FileSystemEventArgs __)
        {
            if (shouldHandle())
                onChanged();
        }
        watcher.Changed += HandleEvent;
        watcher.Created += HandleEvent;
        watcher.Deleted += HandleEvent;
        watcher.Renamed += HandleEvent;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Invalidates cached settings in memory, forcing a reload on next access.</summary>
    private void InvalidateSettings()
    {
        lock (_settingsLock)
        {
            _settings = null;
            _cachedExtraUsers = null;
            _cachedAdvancedUserMappings = null;
        }
        _logger?.LogInformation("Config: Settings invalidated due to external file change");
    }

    /// <summary>Invalidates cached Plex token and discovery details in memory, forcing a reload on next access.</summary>
    private void InvalidateTokenInfo()
    {
        lock (_settingsLock)
        {
            _cachedServers = null;
            _cachedAdminUsername = null;
            _cachedTokenFile = null;
        }
        _logger?.LogInformation("Config: Plex token cache invalidated due to external file change");
    }

    /// <summary>Disposes the file system watchers monitoring configuration files.</summary>
    public void Dispose()
    {
        _settingsWatcher.Dispose();
        _tokenWatcher.Dispose();
        GC.SuppressFinalize(this);
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
    /// <param name="element">The JSON element to convert.</param>
    /// <returns>A converted object containing standard CLR types.</returns>
    private object SanitizeConfigElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i
            : element.TryGetInt64(out var l) ? l
            : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => SanitizeConfigObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(x => SanitizeConfigObject(x)).ToList(),
            _ => null!,
        };

    #endregion

    #region Settings Management

    /// <summary>Reads a file with a brief retry mechanism to bypass transient file sharing locks.</summary>
    /// <param name="path">The file path to read.</param>
    /// <returns>The file contents.</returns>
    private static string ReadFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                if (attempt == 2)
                    throw;
                Thread.Sleep(50);
            }
        }
        return string.Empty;
    }

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
                s = File.Exists(_filePath) ? JsonSerializer.Deserialize<RelayConfig>(ReadFileWithRetry(_filePath), s_options) ?? new() : new();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Config: Failed to load settings -> Using defaults temporarily");
                var fallback = new RelayConfig();
                ApplyDefaultValues(fallback);
                NormalizeVfsRoots(fallback);
                return fallback;
            }

            ApplyDefaultValues(s);
            NormalizeVfsRoots(s);
            NormalizePathMappings(s);
            NormalizeSubtitleMappings(s);
            NormalizeCsvFields(s);
            NormalizeSettings(s, _logger);
            return _settings = s;
        }
    }

    /// <summary>Return the current settings, applying any path or query overrides from the current HTTP request.</summary>
    /// <returns>The effective <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetEffectiveSettings()
    {
        var settings = GetSettings();
        var ctx = _httpContextAccessor?.HttpContext;
        if (ctx == null || ctx.Request.Method != HttpMethods.Get)
            return settings;

        if (ctx.Items.TryGetValue("EffectiveRelayConfig", out var cached) && cached is RelayConfig cachedConfig)
            return cachedConfig;

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract from Path Segment (options/{overrides})
        var overridePath = ctx.Request.RouteValues["overrides"]?.ToString();
        if (!string.IsNullOrWhiteSpace(overridePath))
            foreach (var pair in overridePath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (pair.Split('=', 2) is { Length: 2 } kv)
                    overrides[kv[0].Trim()] = kv[1].Trim();

        // Extract from Query String (fallback for testing)
        foreach (var queryParam in ctx.Request.Query)
            overrides[queryParam.Key] = queryParam.Value.ToString();

        if (overrides.Count == 0 || !s_overridableProps.Any(p => overrides.ContainsKey(p.Name)))
            return settings;

        var cloned = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(settings, s_options), s_options)!;
        foreach (var prop in s_overridableProps)
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
        var settings = GetSettings();
        if (string.IsNullOrWhiteSpace(settings.Advanced.ShokoServerUrl) && DetectRequestBaseUrl() is { } detectedUrl)
        {
            var tokenFile = ReadTokenFile();
            if (!string.Equals(tokenFile.AutoShokoServerUrl, detectedUrl, StringComparison.OrdinalIgnoreCase))
                UpdatePlexTokenInfo(autoUrl: detectedUrl);
        }

        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(settings, s_options))!;
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

        // Sanitize server URLs before validation to prevent trailing slashes from failing the regex
        if (!string.IsNullOrWhiteSpace(settings.Advanced.ShokoServerUrl))
            settings.Advanced.ShokoServerUrl = settings.Advanced.ShokoServerUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(settings.Advanced.PlexServerUrl))
            settings.Advanced.PlexServerUrl = settings.Advanced.PlexServerUrl.Trim().TrimEnd('/');

        var validationResults = new List<ValidationResult>();
        bool isValid =
            Validator.TryValidateObject(settings, new ValidationContext(settings), validationResults, true)
            && Validator.TryValidateObject(settings.Automation, new ValidationContext(settings.Automation), validationResults, true)
            && Validator.TryValidateObject(settings.Playback, new ValidationContext(settings.Playback), validationResults, true)
            && Validator.TryValidateObject(settings.Advanced, new ValidationContext(settings.Advanced), validationResults, true);
        if (!isValid)
            throw new ArgumentException($"Config validation failed: {string.Join("; ", validationResults.Select(r => r.ErrorMessage).Where(m => !string.IsNullOrWhiteSpace(m)))}");
        NormalizePathMappings(settings);
        NormalizeSubtitleMappings(settings);
        NormalizeCsvFields(settings);
        lock (_settingsLock)
        {
            Volatile.Write(ref _suppressSettingsWatcherUntil, Environment.TickCount64 + 500);
            VfsShared.WriteTextAtomic(_filePath, JsonSerializer.Serialize(settings, s_options));
            _settings = settings;
            _cachedExtraUsers = null; // Clear the cached extra users so they are re-parsed on the next sync after settings changes
            _cachedAdvancedUserMappings = null;
        }
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

        /// <summary>The auto-detected base URL of the Shoko server.</summary>
        public string? AutoShokoServerUrl { get; set; }
    }

    /// <summary>Deletes the Plex token/secrets file from disk.</summary>
    public void DeleteTokenFile()
    {
        try
        {
            lock (_settingsLock)
            {
                Volatile.Write(ref _suppressTokenWatcherUntil, Environment.TickCount64 + 500);
                if (File.Exists(_tokenPath))
                    File.Delete(_tokenPath);
                _cachedTokenFile = null;
                _cachedServers = null;
                _cachedAdminUsername = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Config: Failed to delete token file");
        }
    }

    /// <summary>Reads and deserializes the Plex token secrets file from disk.</summary>
    /// <returns>A populated <see cref="TokenFile"/> instance or a new empty structure on failure.</returns>
    private TokenFile ReadTokenFile()
    {
        lock (_settingsLock)
        {
            if (_cachedTokenFile != null)
                return _cachedTokenFile;
            try
            {
                return _cachedTokenFile = File.Exists(_tokenPath)
                    ? JsonSerializer.Deserialize<TokenFile>(ReadFileWithRetry(_tokenPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                    : new();
            }
            catch
            {
                return new();
            }
        }
    }

    /// <summary>Serializes and writes updated Plex token and discovery details to disk.</summary>
    /// <param name="token">Plex token.</param>
    /// <param name="clientIdentifier">Client identifier.</param>
    /// <param name="adminUsername">Admin username.</param>
    /// <param name="servers">Discovered servers list.</param>
    /// <param name="libraries">Discovered libraries list.</param>
    /// <param name="autoUrl">Auto-detected Shoko Server URL.</param>
    private void WriteTokenFile(string? token, string? clientIdentifier, string? adminUsername, List<PlexAvailableServer>? servers = null, List<PlexAvailableLibrary>? libraries = null, string? autoUrl = null)
    {
        var tf = new TokenFile
        {
            Token = token ?? "",
            ClientIdentifier = clientIdentifier ?? "",
            AdminUsername = adminUsername ?? "",
            Servers = servers ?? [],
            Libraries = libraries ?? [],
            AutoShokoServerUrl = autoUrl ?? "",
        };
        lock (_settingsLock)
        {
            Volatile.Write(ref _suppressTokenWatcherUntil, Environment.TickCount64 + 500);
            VfsShared.WriteTextAtomic(_tokenPath, JsonSerializer.Serialize(tf, s_options));
            _cachedTokenFile = tf;
            _cachedServers = tf.Servers;
            _cachedAdminUsername = tf.AdminUsername;
        }
    }

    /// <summary>Retrieves the saved Plex authentication token.</summary>
    /// <returns>The Plex authentication token string.</returns>
    public string GetPlexToken() => ReadTokenFile().Token ?? "";

    /// <summary>Retrieves or generates the unique Plex client identifier.</summary>
    /// <returns>The Plex client identifier string.</returns>
    public string GetPlexClientIdentifier()
    {
        lock (_settingsLock)
        {
            var tokenFile = ReadTokenFile();
            if (string.IsNullOrWhiteSpace(tokenFile.ClientIdentifier))
            {
                tokenFile.ClientIdentifier = Guid.NewGuid().ToString("N");
                WriteTokenFile(tokenFile.Token, tokenFile.ClientIdentifier, tokenFile.AdminUsername, tokenFile.Servers, tokenFile.Libraries, tokenFile.AutoShokoServerUrl);
            }
            return tokenFile.ClientIdentifier;
        }
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
    /// <param name="auth">Plex authentication service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the refresh operation.</returns>
    public async Task RefreshAdminUsername(PlexAuth auth, CancellationToken cancellationToken)
    {
        if (await auth.GetAccountInfoAsync(GetPlexToken(), cancellationToken) is { } info)
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
    /// <param name="autoUrl">Optional auto-detected Shoko Server URL.</param>
    public void UpdatePlexTokenInfo(
        string? token = null,
        string? clientIdentifier = null,
        string? adminName = null,
        List<PlexAvailableServer>? servers = null,
        List<PlexAvailableLibrary>? libraries = null,
        string? autoUrl = null
    )
    {
        lock (_settingsLock)
        {
            var existing = ReadTokenFile();
            WriteTokenFile(
                token ?? existing.Token,
                clientIdentifier ?? existing.ClientIdentifier,
                adminName ?? existing.AdminUsername,
                servers ?? existing.Servers,
                libraries ?? existing.Libraries,
                autoUrl ?? existing.AutoShokoServerUrl
            );
        }
    }

    /// <summary>Checks if the provided server UUID matches a managed server known to the provider.</summary>
    /// <param name="uuid">The server UUID to check.</param>
    /// <returns>True if the server is in the discovered list.</returns>
    public bool IsManagedServer(string? uuid) => !string.IsNullOrWhiteSpace(uuid) && GetPlexDiscoveredServers().Any(s => string.Equals(s.Id, uuid, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region User Management

    /// <summary>Represents a configured mapping between a Shoko username and one or more Plex users.</summary>
    /// <param name="ShokoUser">The target Shoko username.</param>
    /// <param name="PlexUsers">The Plex usernames and optional 4-digit PINs assigned to this Shoko user.</param>
    public sealed record AdvancedUserMappingEntry(string ShokoUser, List<(string PlexUser, string? Pin)> PlexUsers);

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

    /// <summary>Parses a pipe-delimited string of Shoko-to-Plex user mappings (<c>ShokoUser,PlexUser1;PIN,PlexUser2|ShokoUser2,PlexUser3</c>).</summary>
    /// <param name="raw">The raw configuration string to parse.</param>
    /// <returns>A list of parsed <see cref="AdvancedUserMappingEntry"/> records.</returns>
    public static List<AdvancedUserMappingEntry> ParseAdvancedUserMappings(string? raw)
    {
        var entries = new List<AdvancedUserMappingEntry>();
        var seenPlex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in (raw ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2 || string.IsNullOrWhiteSpace(tokens[0]))
                continue;

            string shokoUser = tokens[0].Trim();
            var plexUsers = new List<(string PlexUser, string? Pin)>();
            for (int tokenIdx = 1; tokenIdx < tokens.Length; tokenIdx++)
            {
                var parts = tokens[tokenIdx].Split(';', 2);
                string plexUser = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(plexUser) || !seenPlex.Add(plexUser))
                    continue;

                string? pin = (parts.Length > 1 && parts[1].Trim().Length == 4 && parts[1].Trim().All(char.IsDigit)) ? parts[1].Trim() : null;
                plexUsers.Add((plexUser, pin));
            }

            if (plexUsers.Count > 0)
                entries.Add(new AdvancedUserMappingEntry(shokoUser, plexUsers));
        }

        return entries;
    }

    /// <summary>Returns the parsed and cached list of extra Plex users configured in settings.</summary>
    /// <returns>A list of extra user name and PIN tuples.</returns>
    public List<(string Name, string? Pin)> GetExtraPlexUserEntries()
    {
        lock (_settingsLock)
            return _cachedExtraUsers ??= ParseExtraPlexUsers(GetSettings().Automation.ExtraPlexUsers);
    }

    /// <summary>Returns the parsed and cached list of Shoko-to-Plex user mappings configured in settings.</summary>
    /// <returns>A list of <see cref="AdvancedUserMappingEntry"/> records.</returns>
    public List<AdvancedUserMappingEntry> GetAdvancedUserMappingEntries()
    {
        lock (_settingsLock)
            return _cachedAdvancedUserMappings ??= ParseAdvancedUserMappings(GetSettings().Automation.AdvancedUserMappings);
    }

    /// <summary>Resolves the mapped Shoko username for a given Plex user, checking both literal names and the Plex admin alias.</summary>
    /// <param name="plexUser">The Plex username or <c>admin</c>.</param>
    /// <param name="adminUsername">Optional resolved Plex admin username.</param>
    /// <param name="isOwner">Whether the caller represents the Plex server owner/admin.</param>
    /// <returns>The mapped Shoko username, or <c>null</c> if unmapped.</returns>
    public string? ResolveMappedShokoUsername(string plexUser, string? adminUsername = null, bool isOwner = false)
    {
        foreach (var group in GetAdvancedUserMappingEntries())
        foreach (var (mappedPlex, _) in group.PlexUsers)
        {
            if ((isOwner || !string.Equals(mappedPlex, "admin", StringComparison.OrdinalIgnoreCase)) && string.Equals(mappedPlex, plexUser, StringComparison.OrdinalIgnoreCase))
                return group.ShokoUser;
            if (
                isOwner
                && (
                    string.Equals(mappedPlex, "admin", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(adminUsername) && string.Equals(mappedPlex, adminUsername, StringComparison.OrdinalIgnoreCase))
                )
            )
                return group.ShokoUser;
        }
        return null;
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
    private static void NormalizePathMappings(RelayConfig settings)
    {
        if (settings.Advanced.PathMappings.Count == 0)
            return;

        var mapped = settings.Advanced.PathMappings.Select(kvp =>
        {
            string normKey = VfsShared.NormalizeSeparators(kvp.Key).Trim();
            string key = normKey;
            try
            {
                key = Path.IsPathRooted(normKey) ? Path.GetFullPath(normKey).TrimEnd(Path.DirectorySeparatorChar) : normKey.TrimEnd(Path.DirectorySeparatorChar);
            }
            catch { }

            string val =
                (PlexHelper.NormalizePathForPlex(kvp.Value.Trim()) is var plexPath && !plexPath.StartsWith('/') && !plexPath.Contains(':') && !plexPath.StartsWith("//", StringComparison.Ordinal))
                    ? "/" + plexPath
                    : plexPath;
            return (Key: key, Value: val);
        });

        // Deduplicate keys before dictionary creation to prevent ArgumentException if duplicate paths were entered
        settings.Advanced.PathMappings = mapped.DistinctBy(x => x.Key, VfsShared.PathComparer).ToDictionary(x => x.Key, x => x.Value, VfsShared.PathComparer);
    }

    /// <summary>Normalizes the subtitle language mappings to ensure they are case-insensitive while preserving priority order.</summary>
    /// <param name="settings">The relay configuration instance to update.</param>
    private static void NormalizeSubtitleMappings(RelayConfig settings)
    {
        if (settings.Advanced.SubtitleLanguageMappings.Count == 0)
            return;

        var dict = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.Advanced.SubtitleLanguageMappings)
            if (!dict.ContainsKey(entry.Key))
                dict.Add(entry.Key, entry.Value);

        settings.Advanced.SubtitleLanguageMappings = dict;
    }

    /// <summary>Normalizes comma-separated and newline-separated settings fields by trimming and removing duplicates.</summary>
    /// <param name="settings">The relay configuration instance to normalize.</param>
    private static void NormalizeCsvFields(RelayConfig settings)
    {
        static string Norm(string? r, char separator) =>
            string.Join(separator + " ", (r ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));

        settings.Automation.ExtraPlexUsers = Norm(settings.Automation.ExtraPlexUsers, ',');
        settings.Automation.AdvancedUserMappings = string.Join(
            "|",
            ParseAdvancedUserMappings(settings.Automation.AdvancedUserMappings).Select(g => string.Join(",", g.PlexUsers.Select(p => p.Pin != null ? $"{p.PlexUser};{p.Pin}" : p.PlexUser).Prepend(g.ShokoUser)))
        );

        settings.Advanced.FolderExclusions = string.Join(
            Environment.NewLine,
            (settings.Advanced.FolderExclusions ?? "")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimSlashes())
                .Where(x => x.Length > 0)
                .Distinct(VfsShared.PathComparer)
        );

        settings.Advanced.ManagedFolderExclusions = string.Join(
            Environment.NewLine,
            (settings.Advanced.ManagedFolderExclusions ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)
        );

        settings.Advanced.CollectionExclusions = string.Join(
            Environment.NewLine,
            (settings.Advanced.CollectionExclusions ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)
        );

        settings.TagOverrides = string.Join(
            Environment.NewLine,
            (settings.TagOverrides ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)
        );
    }

    /// <summary>Applies default values to string properties on an object hierarchy where [DefaultValue] attributes exist.</summary>
    /// <param name="obj">The object to apply default values to.</param>
    private static void ApplyDefaultValues(object obj)
    {
        foreach (var prop in obj.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            if (prop.PropertyType == typeof(string) && string.IsNullOrWhiteSpace(prop.GetValue(obj) as string) && prop.GetCustomAttribute<DefaultValueAttribute>() is { } d)
                prop.SetValue(obj, d.Value);
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !typeof(IDictionary).IsAssignableFrom(prop.PropertyType))
                ApplyDefaultValues(prop.GetValue(obj)!);
        }
    }

    /// <summary>Recursively scans an object for properties with [Range] attributes and clamps their values accordingly.</summary>
    /// <param name="obj">The configuration object to normalize.</param>
    /// <param name="logger">Optional logger instance for trace reporting.</param>
    private static void NormalizeSettings(object obj, ILogger? logger = null)
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
                    logger?.LogTrace("Config: Clamped {Property} from {Original} to {Clamped}", prop.Name, currentVal, clampedVal);
                }
            }
            // Recurse into nested config classes (AutomationConfig, AdvancedConfig, etc.)
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var subObj = prop.GetValue(obj);
                if (subObj != null)
                    NormalizeSettings(subObj, logger);
            }
        }
    }

    #endregion
}
