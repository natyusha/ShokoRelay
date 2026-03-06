using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NLog;
using Shoko.Abstractions.Plugin;

namespace ShokoRelay.Config;

/// <summary>
/// Manages loading, saving, validation and normalization of the plugin configuration and Plex token/secrets file. Watches for external config changes to auto-invalidate the cache.
/// </summary>
public class ConfigProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static RelayConfig CreateDefaultSettings() => new RelayConfig();

    private readonly string _filePath;
    private readonly string _tokenPath;
    private readonly object _settingsLock = new();
    private RelayConfig? _settings;

    private static readonly JsonSerializerOptions Options = new() { AllowTrailingCommas = true, WriteIndented = true };

    // expose plugin directory for convenient access (populated during construction)
    public string PluginDirectory { get; }

    /// <summary>
    /// Accessor for the current HTTP request context, set after DI resolution.
    /// Used by <see cref="ServerBaseUrl"/> to capture the reachable host.
    /// </summary>
    public IHttpContextAccessor? HttpContextAccessor { get; set; }

    private string _serverBaseUrl = "http://localhost:8111";

    /// <summary>
    /// The externally-reachable base URL of the Shoko server (e.g. <c>http://192.168.1.3:8111</c>).
    /// When an explicit <see cref="RelayConfig.ShokoServerUrl"/> is configured that value is always returned.
    /// Otherwise, when an HTTP request context is available the value is automatically refreshed from the incoming host header; background tasks that lack a context receive the last known value.
    /// </summary>
    public string ServerBaseUrl
    {
        get
        {
            // Prefer explicitly configured URL
            var configUrl = GetSettings()?.ShokoServerUrl;
            if (!string.IsNullOrWhiteSpace(configUrl))
                return configUrl!.TrimEnd('/');

            // Auto-detect from current HTTP context
            var ctx = HttpContextAccessor?.HttpContext;
            if (ctx is not null)
                _serverBaseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return _serverBaseUrl;
        }
        set => _serverBaseUrl = value ?? "http://localhost:8111";
    }

    /// <summary>
    /// Creates a new <see cref="ConfigProvider"/> using the specified paths provided by the host application.
    /// </summary>
    /// <param name="applicationPaths">Paths supplied by the environment which include the plugins directory used to determine where configuration files are read and written.</param>
    public ConfigProvider(IApplicationPaths applicationPaths)
    {
        if (applicationPaths is null)
            throw new ArgumentNullException(nameof(applicationPaths));
        PluginDirectory = Path.Combine(applicationPaths.PluginsPath, ConfigConstants.PluginSubfolder);
        string configDir = Path.Combine(PluginDirectory, ConfigConstants.ConfigSubfolder);
        Directory.CreateDirectory(PluginDirectory); // Ensure plugin directory exists
        Directory.CreateDirectory(configDir); // Ensure config directory exists
        _filePath = Path.Combine(configDir, ConfigConstants.ConfigFileName);
        _tokenPath = Path.Combine(configDir, ConfigConstants.SecretsFileName);
        Logger.Info($"Config path: {_filePath}");
        Logger.Info($"Token path: {_tokenPath}");

        // Watch for external changes to the config file
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!, ConfigConstants.ConfigFileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        watcher.Changed += (_, _) => InvalidateSettings();
        watcher.Created += (_, _) => InvalidateSettings();
        watcher.Deleted += (_, _) => InvalidateSettings();
        watcher.Renamed += (_, _) => InvalidateSettings();
        watcher.EnableRaisingEvents = true;

        var tokenWatcher = new FileSystemWatcher(Path.GetDirectoryName(_tokenPath)!, ConfigConstants.SecretsFileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        tokenWatcher.Changed += (_, _) => InvalidateSettings();
        tokenWatcher.Created += (_, _) => InvalidateSettings();
        tokenWatcher.Deleted += (_, _) => InvalidateSettings();
        tokenWatcher.Renamed += (_, _) => InvalidateSettings();
        tokenWatcher.EnableRaisingEvents = true;
    }

    private void InvalidateSettings()
    {
        lock (_settingsLock)
        {
            _settings = null;
        }
        Logger.Info("Config file changed externally, settings invalidated.");
    }

    /// <summary>
    /// Convert any <see cref="JsonElement"/> trees within <paramref name="obj"/> into plain CLR values.
    /// </summary>
    public object SanitizeConfigObject(object obj)
    {
        if (obj is JsonElement je)
            return SanitizeConfigElement(je);
        if (obj == null)
            return obj!;

        try
        {
            var json = JsonSerializer.Serialize(obj, Options);
            var result = JsonSerializer.Deserialize<object>(json, Options) ?? string.Empty;
            return result is JsonElement je2 ? SanitizeConfigElement(je2) : result;
        }
        catch
        {
            return obj;
        }
    }

    private object SanitizeConfigElement(JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.String:
                return je.GetString() ?? string.Empty;
            case JsonValueKind.Number:
                if (je.TryGetInt32(out var i))
                    return i;
                if (je.TryGetInt64(out var l))
                    return l;
                if (je.TryGetDouble(out var d))
                    return d;
                return 0;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object>();
                foreach (var prop in je.EnumerateObject())
                    dict[prop.Name] = SanitizeConfigObject(prop.Value);
                return dict;
            case JsonValueKind.Array:
                var list = new List<object>();
                foreach (var el in je.EnumerateArray())
                    list.Add(SanitizeConfigObject(el));
                return list;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null!;
            default:
                return je.ToString();
        }
    }

    /// <summary>
    /// Return the current settings, loading from disk if not already cached.
    /// </summary>
    /// <returns>The active <see cref="RelayConfig"/> instance.</returns>
    public RelayConfig GetSettings() => _settings ??= GetSettingsFromFile();

    /// <summary>
    /// Construct a sanitized payload of settings plus minimal Plex auth information for consumption by the web dashboard UI.
    /// </summary>
    /// <returns>A deserialized object graph suitable for JSON re-serialization to the dashboard.</returns>
    public object GetDashboardConfig()
    {
        var settings = GetSettings();
        var serOpts = new JsonSerializerOptions { AllowTrailingCommas = true, WriteIndented = true };
        var baseDict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(settings, serOpts)) ?? new Dictionary<string, object>();

        var plexLib = new
        {
            HasToken = !string.IsNullOrWhiteSpace(GetPlexToken()),
            ClientIdentifier = GetPlexClientIdentifier(),
            DiscoveredServers = GetPlexDiscoveredServers(),
            DiscoveredLibraries = GetPlexDiscoveredLibraries(),
        };
        baseDict["PlexLibrary"] = plexLib;
        baseDict["PlexAuth"] = new PlexAuthConfig { ClientIdentifier = plexLib.ClientIdentifier };

        var cleaned = SanitizeConfigObject(baseDict);
        return cleaned is Dictionary<string, object> d ? d : cleaned;
    }

    /// <summary>
    /// Absolute path to the directory containing the configuration file.
    /// </summary>
    public string ConfigDirectory => Path.GetDirectoryName(_filePath)!;

    /// <summary>
    /// Validate, normalize and persist the supplied <paramref name="settings"/> to disk.
    /// </summary>
    public void SaveSettings(RelayConfig settings)
    {
        ApplyDefaultValues(settings);

        ValidateSettings(settings);

        // Normalize path mappings prior to persisting so users can enter natural paths in the UI
        NormalizePathMappings(settings);

        // Normalize comma separated fields prior to persisting
        NormalizeCommaSeparatedFields(settings);

        var json = JsonSerializer.Serialize(settings, Options);
        lock (_settingsLock)
        {
            File.WriteAllText(_filePath, json);
        }

        _settings = settings;
        Logger.Info("Config saved.");
    }

    // Ensure settings object contains sensible defaults for any string property that was left empty/whitespace
    private static void ApplyDefaultValues(RelayConfig settings)
    {
        if (settings == null)
            return;

        var defaults = new RelayConfig();
        foreach (var prop in typeof(RelayConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            if (prop.PropertyType != typeof(string))
                continue;

            var cur = (string?)prop.GetValue(settings);
            if (string.IsNullOrWhiteSpace(cur))
            {
                var def = (string?)prop.GetValue(defaults);
                if (!string.IsNullOrWhiteSpace(def))
                    prop.SetValue(settings, def);
            }
        }
    }

    /// <summary>
    /// Delete the stored Plex token file if it exists; errors are logged but not thrown.
    /// </summary>
    public void DeleteTokenFile()
    {
        try
        {
            if (File.Exists(_tokenPath))
                File.Delete(_tokenPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to delete token file {_tokenPath}: {ex.Message}");
        }
    }

    private RelayConfig GetSettingsFromFile()
    {
        RelayConfig settings;
        var needsSave = false;
        string contents = string.Empty;

        try
        {
            contents = File.ReadAllText(_filePath);
            settings = JsonSerializer.Deserialize<RelayConfig>(contents, Options)!;

            if (settings is null)
            {
                settings = CreateDefaultSettings();
                needsSave = true;
                Logger.Warn("Config file empty or invalid JSON, using defaults.");
            }
            else
            {
                Logger.Info("Config loaded from file.");
            }
        }
        catch (FileNotFoundException)
        {
            settings = CreateDefaultSettings();
            needsSave = true;
            Logger.Info("Config file not found, creating defaults.");
        }
        catch (JsonException ex)
        {
            settings = CreateDefaultSettings();
            needsSave = true;
            Logger.Warn($"Invalid config file, using defaults: {ex.Message}");
        }

        // Normalize any path mappings so users can enter natural paths (e.g., "M:\\Anime" or "/mnt/plex/anime/")
        if (NormalizePathMappings(settings))
            needsSave = true;

        // Normalize comma separated fields (TagBlacklist, ExtraPlexUsers)
        if (NormalizeCommaSeparatedFields(settings))
            needsSave = true;

        ValidateSettings(settings);

        if (needsSave)
            SaveSettings(settings);

        return settings;
    }

    /// <summary>
    /// Parse a comma-separated string of Plex user entries, optionally containing 4-digit PINs after a semicolon.
    /// </summary>
    public static List<(string Name, string? Pin)> ParseExtraPlexUsers(string? extraRaw)
    {
        if (string.IsNullOrWhiteSpace(extraRaw))
            return new List<(string, string?)>();

        return extraRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s =>
            {
                // If the entry contains a semicolon and the part after it is exactly 4 digits, treat that as a PIN.
                // Otherwise treat the full entry as the username (semicolons remain part of the username).
                if (s.Contains(';'))
                {
                    var parts = s.Split(';', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1 && parts[1].Length == 4 && parts[1].All(char.IsDigit))
                        return (Name: parts[0].Trim(), Pin: parts[1].Trim());
                }

                return (Name: s, Pin: (string?)null);
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToList();
    }

    /// <summary>
    /// Return configured extra Plex users as a list of name/PIN tuples.
    /// </summary>
    /// <returns>A list of (Name, Pin) tuples.</returns>
    public List<(string Name, string? Pin)> GetExtraPlexUserEntries()
    {
        var extraRaw = GetSettings().ExtraPlexUsers ?? string.Empty;
        return ParseExtraPlexUsers(extraRaw);
    }

    /// <summary>
    /// Return only the usernames of configured extra Plex users.
    /// </summary>
    /// <returns>A list of username strings (without PINs).</returns>
    public List<string> GetExtraPlexUsernames()
    {
        return GetExtraPlexUserEntries().Select(e => e.Name).ToList();
    }

    private sealed class TokenFile
    {
        public string? Token { get; set; }
        public string? ClientIdentifier { get; set; }
        public List<PlexAvailableServer>? Servers { get; set; }
        public List<PlexAvailableLibrary>? Libraries { get; set; }
    }

    private static TokenFile CreateEmptyTokenFile() => new TokenFile { Servers = new List<PlexAvailableServer>(), Libraries = new List<PlexAvailableLibrary>() };

    private TokenFile ReadTokenFile()
    {
        try
        {
            if (!File.Exists(_tokenPath))
                return CreateEmptyTokenFile();

            var content = File.ReadAllText(_tokenPath);
            if (string.IsNullOrWhiteSpace(content))
                return CreateEmptyTokenFile();

            try
            {
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<TokenFile>(content, jsonOptions);
                if (parsed != null)
                {
                    parsed.Servers ??= new List<PlexAvailableServer>();
                    parsed.Libraries ??= new List<PlexAvailableLibrary>();
                    return parsed;
                }
            }
            catch (JsonException ex)
            {
                // Token file is invalid JSON; return empty token object.
                Logger.Warn($"Invalid token file format at {_tokenPath}: {ex.Message}");
                return CreateEmptyTokenFile();
            }

            // If we reach here the content did not deserialize to a TokenFile — return an empty token object.
            return CreateEmptyTokenFile();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read token file {_tokenPath}: {ex.Message}");
            return CreateEmptyTokenFile();
        }
    }

    private void WriteTokenFile(string? token, string? clientIdentifier, List<PlexAvailableServer>? servers = null, List<PlexAvailableLibrary>? libraries = null)
    {
        var tokenObj = new TokenFile
        {
            Token = token ?? string.Empty,
            ClientIdentifier = clientIdentifier ?? string.Empty,
            Servers = servers ?? new List<PlexAvailableServer>(),
            Libraries = libraries ?? new List<PlexAvailableLibrary>(),
        };

        try
        {
            var json = JsonSerializer.Serialize(tokenObj, Options);
            File.WriteAllText(_tokenPath, json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to write token file {_tokenPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieve the stored Plex token (from plex.token file).
    /// </summary>
    /// <returns>An empty string if no token is stored.</returns>
    public string GetPlexToken() => ReadTokenFile().Token ?? string.Empty;

    /// <summary>
    /// Retrieve the Plex client identifier, generating one if missing.
    /// </summary>
    /// <returns>The existing or newly generated client identifier.</returns>
    public string GetPlexClientIdentifier()
    {
        var tf = ReadTokenFile();
        if (string.IsNullOrWhiteSpace(tf.ClientIdentifier))
        {
            tf.ClientIdentifier = GenerateClientIdentifier();
            WriteTokenFile(tf.Token, tf.ClientIdentifier, tf.Servers, tf.Libraries);
        }
        return tf.ClientIdentifier ?? string.Empty;
    }

    /// <summary>Retrieve the list of Plex servers discovered during authentication.</summary>
    public List<PlexAvailableServer> GetPlexDiscoveredServers() => ReadTokenFile().Servers ?? new List<PlexAvailableServer>();

    /// <summary>Retrieve the list of Plex libraries discovered during authentication.</summary>
    public List<PlexAvailableLibrary> GetPlexDiscoveredLibraries() => ReadTokenFile().Libraries ?? new List<PlexAvailableLibrary>();

    /// <summary>
    /// Update the token file with the provided values. Any null parameter will leave the
    /// existing value in place (except token/clientIdentifier which may be overwritten by empty string).
    /// </summary>
    public void UpdatePlexTokenInfo(string? token = null, string? clientIdentifier = null, List<PlexAvailableServer>? servers = null, List<PlexAvailableLibrary>? libraries = null)
    {
        var existing = ReadTokenFile();
        string newToken = token ?? existing.Token ?? string.Empty;
        string newCid = clientIdentifier ?? existing.ClientIdentifier ?? string.Empty;
        var newServers = servers ?? existing.Servers;
        var newLibs = libraries ?? existing.Libraries;
        WriteTokenFile(newToken, newCid, newServers, newLibs);
    }

    private bool NormalizePathMappings(RelayConfig settings)
    {
        if (settings == null || settings.PathMappings == null || settings.PathMappings.Count == 0)
            return false;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in settings.PathMappings)
        {
            var rawKey = kv.Key ?? string.Empty;
            var rawVal = kv.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(rawVal))
                continue;

            string keyNorm = NormalizeShokoKey(rawKey);
            string valNorm = NormalizePlexBasePath(rawVal);

            if (string.IsNullOrWhiteSpace(keyNorm) || string.IsNullOrWhiteSpace(valNorm))
                continue;

            if (!normalized.ContainsKey(keyNorm))
                normalized[keyNorm] = valNorm;
            else if (normalized[keyNorm] != valNorm)
                Logger.Warn("Duplicate mapping after normalization for {Key}; overriding with {Val}", keyNorm, valNorm);
        }

        // Compare serialized form to detect changes
        var origJson = JsonSerializer.Serialize(settings.PathMappings, Options);
        var normJson = JsonSerializer.Serialize(normalized, Options);
        if (origJson != normJson)
        {
            settings.PathMappings = normalized;
            return true;
        }

        return false;
    }

    private static string NormalizeCsvString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return string.Join(", ", parts);
    }

    private bool NormalizeCommaSeparatedFields(RelayConfig settings)
    {
        if (settings == null)
            return false;

        bool changed = false;

        var tagRaw = settings.TagBlacklist ?? string.Empty;
        var normTag = NormalizeCsvString(tagRaw);
        if (!string.Equals(normTag, settings.TagBlacklist ?? string.Empty, StringComparison.Ordinal))
        {
            settings.TagBlacklist = normTag;
            changed = true;
        }

        var extraRaw = settings.ExtraPlexUsers ?? string.Empty;
        var normExtra = NormalizeCsvString(extraRaw);
        if (!string.Equals(normExtra, settings.ExtraPlexUsers ?? string.Empty, StringComparison.Ordinal))
        {
            settings.ExtraPlexUsers = normExtra;
            changed = true;
        }

        return changed;
    }

    private static string NormalizeShokoKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        // Normalize separators to the local platform and trim
        var sep = Path.DirectorySeparatorChar;
        string normalized = rawKey.Trim();
        normalized = normalized.Replace('/', sep).Replace('\\', sep);

        // Try to produce a full absolute path for rooted inputs
        try
        {
            if (
                normalized.Length >= 2 && normalized[1] == ':' // drive
                || normalized.StartsWith(new string(sep, 2))
                || normalized.StartsWith(sep)
            )
            {
                normalized = Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            else
            {
                normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return normalized;
    }

    private static string NormalizePlexBasePath(string rawVal)
    {
        if (string.IsNullOrWhiteSpace(rawVal))
            return string.Empty;

        string val = rawVal.Trim();
        // Use forward slashes for Plex and remove trailing slashes
        val = val.Replace('\\', '/');
        val = val.TrimEnd('/');

        // If not rooted and not a Windows drive, presume a Unix-style path and prefix '/'
        if (!val.StartsWith("/") && !val.Contains(":") && !val.StartsWith("//"))
            val = "/" + val;

        return val;
    }

    private static string GenerateClientIdentifier() => Guid.NewGuid().ToString("N");

    private static void ValidateSettings(RelayConfig settings)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(settings);

        var isValid = Validator.TryValidateObject(settings, validationContext, validationResults, true);
        if (isValid)
            return;

        foreach (var validationResult in validationResults)
        {
            foreach (var memberName in validationResult.MemberNames)
            {
                Logger.Error($"Error validating settings for property {memberName}: {validationResult.ErrorMessage}");
            }
        }

        throw new ArgumentException("Error in settings validation");
    }
}
