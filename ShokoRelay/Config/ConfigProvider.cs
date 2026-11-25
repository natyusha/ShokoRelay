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
            .Where(prop => prop.CanWrite && prop.PropertyType != typeof(AdvancedConfig) && prop.PropertyType != typeof(AutomationConfig) && prop.PropertyType != typeof(PlaybackConfig)),
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

    /// <summary>The absolute path to the plugin's task reports directory.</summary>
    /// <remarks>Does not use <see cref="IApplicationPaths.LogsPath"/> as these are not traditional logs but temporary report files.</remarks>
    public string ReportsDirectory => Path.Combine(CacheDirectory, "reports");

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
        Directory.CreateDirectory(ReportsDirectory);

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
        void HandleEvent(object sender, FileSystemEventArgs eventArgs)
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

    /// <summary>Convert any JsonElement trees within <paramref name="objectValue"/> into plain CLR values.</summary>
    /// <param name="objectValue">The JSON element or object to sanitize.</param>
    /// <returns>A sanitized object containing only plain CLR types.</returns>
    public object SanitizeConfigObject(object objectValue) =>
        objectValue switch
        {
            JsonElement jsonElement => SanitizeConfigElement(jsonElement),
            null => null!,
            _ => SanitizeConfigObject(JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(objectValue, s_options), s_options)!),
        };

    /// <summary>Recursively converts a <see cref="JsonElement"/> into plain primitive or dictionary types.</summary>
    /// <param name="element">The JSON element to convert.</param>
    /// <returns>A converted object containing standard CLR types.</returns>
    private object SanitizeConfigElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.TryGetInt32(out var intValue) ? intValue
            : element.TryGetInt64(out var longValue) ? longValue
            : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => SanitizeConfigObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(item => SanitizeConfigObject(item)).ToList(),
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

            RelayConfig loaded;
            try
            {
                loaded = File.Exists(_filePath) ? JsonSerializer.Deserialize<RelayConfig>(ReadFileWithRetry(_filePath), s_options) ?? new() : new();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Config: Failed to load settings -> Using defaults temporarily");
                var fallback = new RelayConfig();
                ApplyDefaultValues(fallback);
                NormalizeVfsRoots(fallback);
                return fallback;
            }

            ApplyDefaultValues(loaded);
            NormalizeVfsRoots(loaded);
            NormalizePathMappings(loaded);
            NormalizeSubtitleMappings(loaded);
            NormalizeCsvFields(loaded);
            NormalizeSettings(loaded, _logger);
            return _settings = loaded;
        }
    }

    /// <summary>Return the current settings, applying any path or query overrides from the current HTTP request.</summary>
    /// <returns>The effective <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetEffectiveSettings()
    {
        var settings = GetSettings();
        var httpContext = _httpContextAccessor?.HttpContext;
        if (httpContext == null)
            return settings;

        // Synchronize access to HttpContext.Items and Request metadata across parallel worker threads
        lock (httpContext.Items)
        {
            if (httpContext.Items.TryGetValue("EffectiveRelayConfig", out var cached) && cached is RelayConfig cachedConfig)
                return cachedConfig;

            try
            {
                if (httpContext.Request.Method != HttpMethods.Get)
                    return settings;
            }
            catch (ObjectDisposedException)
            {
                return settings;
            }

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Extract from Path Segment (options/{overrides})
            var overridePath = httpContext.Request.RouteValues["overrides"]?.ToString();
            if (!string.IsNullOrWhiteSpace(overridePath))
                foreach (var pair in overridePath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (pair.Split('=', 2) is { Length: 2 } pairTokens)
                        overrides[pairTokens[0].Trim()] = pairTokens[1].Trim();

            // Extract from Query String (fallback for testing)
            foreach (var queryParam in httpContext.Request.Query)
                overrides[queryParam.Key] = queryParam.Value.ToString();

            if (overrides.Count == 0 || !s_overridableProps.Any(prop => overrides.ContainsKey(prop.Name)))
            {
                httpContext.Items["EffectiveRelayConfig"] = settings;
                return settings;
            }

            var cloned = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(settings, s_options), s_options)!;
            foreach (var prop in s_overridableProps)
                if (overrides.TryGetValue(prop.Name, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
                    try
                    {
                        if (prop.PropertyType == typeof(string))
                            prop.SetValue(cloned, rawValue);
                        else if (prop.PropertyType == typeof(bool))
                            prop.SetValue(cloned, string.Equals(rawValue, "1") || string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase));
                        else if (prop.PropertyType.IsEnum)
                            prop.SetValue(cloned, Enum.Parse(prop.PropertyType, rawValue, true));
                    }
                    catch
                    { /* Ignore invalid override values */
                    }

            httpContext.Items["EffectiveRelayConfig"] = cloned;
            return cloned;
        }
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
        settings.Automation ??= new();
        settings.Playback ??= new();
        settings.Advanced ??= new();

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
            throw new ArgumentException($"Config validation failed: {string.Join("; ", validationResults.Select(result => result.ErrorMessage).Where(message => !string.IsNullOrWhiteSpace(message)))}");
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

    /// <summary>Extracts the externally-reachable base URL from the current HTTP request headers, if an HTTP context is active.</summary>
    /// <remarks>
    ///   - Reads <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c> directly because Shoko's <c>Web.TrustedProxies</c> setting defaults to empty and requires a restart
    ///   - Only admin-authenticated dashboard requests persist the detected URL to <c>AutoShokoServerUrl</c>; unauthenticated requests affect only the active response
    /// </remarks>
    /// <returns>The detected base URL without trailing slash, or <c>null</c> when no HTTP context is active.</returns>
    public string? DetectRequestBaseUrl()
    {
        if (_httpContextAccessor?.HttpContext is not { } httpContext)
            return null;

        var request = httpContext.Request;

        // Inspect standard proxy headers to accurately detect HTTPS scheme and external hostname
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(scheme) || (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            scheme = request.IsHttps ? "https" : request.Scheme;

        var rawHost = request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = !string.IsNullOrEmpty(rawHost) && TextHelper.ForwardedHostRegex().IsMatch(rawHost) ? rawHost : request.Host.ToString();
        return $"{scheme}://{host}{request.PathBase}".TrimEnd('/');
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

        var mapped = settings
            .Advanced.PathMappings.Where(mappingPair => !string.IsNullOrWhiteSpace(mappingPair.Key) && !string.IsNullOrWhiteSpace(mappingPair.Value))
            .Select(mappingPair =>
            {
                string normKey = VfsShared.NormalizeSeparators(mappingPair.Key).Trim();
                string key = normKey;
                try
                {
                    string fullKey = Path.IsPathRooted(normKey) ? Path.GetFullPath(normKey) : normKey;
                    string? pathRoot = Path.GetPathRoot(fullKey);
                    key = !string.IsNullOrEmpty(pathRoot) && string.Equals(fullKey, pathRoot, VfsShared.PathComparison) ? fullKey : fullKey.TrimEnd(Path.DirectorySeparatorChar);
                }
                catch { }

                string value =
                    (PlexHelper.NormalizePathForPlex(mappingPair.Value.Trim()) is var plexPath && !plexPath.StartsWith('/') && !plexPath.Contains(':') && !plexPath.StartsWith("//", StringComparison.Ordinal))
                        ? "/" + plexPath
                        : plexPath;
                return (Key: key, Value: value);
            })
            .Where(mapping => !string.IsNullOrEmpty(mapping.Key) && !string.IsNullOrEmpty(mapping.Value));

        // Deduplicate keys before dictionary creation to prevent ArgumentException if duplicate paths were entered
        settings.Advanced.PathMappings = mapped.DistinctBy(mapping => mapping.Key, VfsShared.PathComparer).ToDictionary(mapping => mapping.Key, mapping => mapping.Value, VfsShared.PathComparer);
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
        static string NormalizeList(string? rawInput, char[] separators, string joinWith, StringComparer comparer, Func<string, string>? transform = null) =>
            string.Join(
                joinWith,
                (rawInput ?? "")
                    .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => transform != null ? transform(item) : item)
                    .Where(item => item.Length > 0)
                    .Distinct(comparer)
            );

        settings.Automation.ExtraPlexUsers = NormalizeList(settings.Automation.ExtraPlexUsers, [','], ", ", StringComparer.OrdinalIgnoreCase);
        settings.Automation.AdvancedUserMappings = string.Join(
            "|",
            ParseAdvancedUserMappings(settings.Automation.AdvancedUserMappings)
                .Select(group => string.Join(",", group.PlexUsers.Select(plexEntry => plexEntry.Pin != null ? $"{plexEntry.PlexUser};{plexEntry.Pin}" : plexEntry.PlexUser).Prepend(group.ShokoUser)))
        );

        char[] lineSeparators = ['\r', '\n'];
        settings.Advanced.FolderExclusions = NormalizeList(settings.Advanced.FolderExclusions, lineSeparators, Environment.NewLine, VfsShared.PathComparer, line => line.TrimSlashes());
        settings.Advanced.ManagedFolderExclusions = NormalizeList(settings.Advanced.ManagedFolderExclusions, lineSeparators, Environment.NewLine, StringComparer.OrdinalIgnoreCase);
        settings.Advanced.CollectionExclusions = NormalizeList(settings.Advanced.CollectionExclusions, lineSeparators, Environment.NewLine, StringComparer.OrdinalIgnoreCase);
        settings.TagOverrides = NormalizeList(settings.TagOverrides, lineSeparators, Environment.NewLine, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies default values to string properties on an object hierarchy where [DefaultValue] attributes exist.</summary>
    /// <param name="objectValue">The object to apply default values to.</param>
    private static void ApplyDefaultValues(object objectValue)
    {
        foreach (var prop in objectValue.GetType().GetProperties().Where(prop => prop.CanRead && prop.CanWrite))
        {
            if (prop.PropertyType == typeof(string) && string.IsNullOrWhiteSpace(prop.GetValue(objectValue) as string) && prop.GetCustomAttribute<DefaultValueAttribute>() is { } defaultAttr)
                prop.SetValue(objectValue, defaultAttr.Value);
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !typeof(IDictionary).IsAssignableFrom(prop.PropertyType) && prop.GetValue(objectValue) is { } nestedObject)
                ApplyDefaultValues(nestedObject);
        }
    }

    /// <summary>Recursively scans an object for properties with [Range] attributes and clamps their values accordingly.</summary>
    /// <param name="objectValue">The configuration object to normalize.</param>
    /// <param name="logger">Optional logger instance for trace reporting.</param>
    private static void NormalizeSettings(object objectValue, ILogger? logger = null)
    {
        if (objectValue == null)
            return;
        var type = objectValue.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            // Check for the Range attribute
            var range = prop.GetCustomAttribute<RangeAttribute>();
            if (range != null && prop.PropertyType == typeof(int))
            {
                int currentValue = (int)prop.GetValue(objectValue)!;
                int min = Convert.ToInt32(range.Minimum);
                int max = Convert.ToInt32(range.Maximum);

                int clampedValue = Math.Clamp(currentValue, min, max);
                if (currentValue != clampedValue)
                {
                    prop.SetValue(objectValue, clampedValue);
                    logger?.LogTrace("Config: Clamped {Property} from {Original} to {Clamped}", prop.Name, currentValue, clampedValue);
                }
            }
            // Recurse into nested config classes (AutomationConfig, AdvancedConfig, etc.)
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var nestedObject = prop.GetValue(objectValue);
                if (nestedObject != null)
                    NormalizeSettings(nestedObject, logger);
            }
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Reads a file with a brief retry mechanism to bypass transient file sharing locks.</summary>
    /// <param name="path">The file path to read.</param>
    /// <returns>The file contents.</returns>
    private static string ReadFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
            try
            {
                return VfsShared.ReadAllTextShared(path);
            }
            catch (IOException)
            {
                if (attempt == 2)
                    throw;
                Thread.Sleep(50);
            }
        return string.Empty;
    }

    #endregion
}
