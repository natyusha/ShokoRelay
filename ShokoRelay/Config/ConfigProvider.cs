using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NLog;
using Shoko.Abstractions.Plugin;

namespace ShokoRelay.Config
{
    public class ConfigProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static RelayConfig CreateDefaultSettings() => new RelayConfig();

        private readonly string _filePath;
        private readonly string _tokenPath;

        // expose plugin directory for convenient access
        public string PluginDirectory { get; }
        private readonly object _settingsLock = new();
        private RelayConfig? _settings;

        private static readonly JsonSerializerOptions Options = new() { AllowTrailingCommas = true, WriteIndented = true };

        public ConfigProvider(IApplicationPaths applicationPaths)
        {
            string pluginDir = ConfigConstants.GetPluginDirectory(applicationPaths);
            PluginDirectory = pluginDir;
            string configDir = Path.Combine(pluginDir, ConfigConstants.ConfigSubfolder);
            Directory.CreateDirectory(pluginDir); // Ensure plugin directory exists
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

        public RelayConfig GetSettings()
        {
            _settings ??= GetSettingsFromFile();
            return _settings;
        }

        public string ConfigDirectory => Path.GetDirectoryName(_filePath)!;

        public void SaveSettings(RelayConfig settings)
        {
            ValidateSettings(settings);

            // Normalize path mappings prior to persisting so users can enter natural paths in the UI
            NormalizePathMappings(settings);

            // Normalize comma-separated fields prior to persisting
            NormalizeCommaSeparatedFields(settings);

            var tokenData = ReadTokenFile();

            // Prune secrets for extra users that are no longer configured.
            // Support entries like "user;1234" where the ";1234" is an optional PIN; tokens are stored keyed by the username only.
            var normExtra = NormalizeCsvString(settings.ExtraPlexUsers);
            var configuredExtraUsernames = ParseExtraPlexUsers(normExtra).Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tokenData.ExtraPlexUserTokens != null)
            {
                var removed = tokenData.ExtraPlexUserTokens.Keys.Where(k => !configuredExtraUsernames.Contains(k)).ToList();
                foreach (var k in removed)
                    tokenData.ExtraPlexUserTokens.Remove(k);
                // persist pruning immediately
                WriteTokenFile(tokenData.Token, tokenData.ClientIdentifier, tokenData.Servers, tokenData.Libraries, tokenData.SelectedLibraryKeys, tokenData.ExtraPlexUserTokens);
            }

            if (string.IsNullOrWhiteSpace(settings.PlexLibrary.Token) && !string.IsNullOrWhiteSpace(tokenData.Token))
                settings.PlexLibrary.Token = tokenData.Token;

            if (string.IsNullOrWhiteSpace(settings.PlexAuth.ClientIdentifier))
                settings.PlexAuth.ClientIdentifier = !string.IsNullOrWhiteSpace(tokenData.ClientIdentifier) ? tokenData.ClientIdentifier : GenerateClientIdentifier();

            settings.PlexLibrary.ClientIdentifier = settings.PlexAuth.ClientIdentifier;

            var sanitized = StripSecrets(settings);

            var json = JsonSerializer.Serialize(sanitized, Options);
            lock (_settingsLock)
            {
                File.WriteAllText(_filePath, json);
                var selectedKeys = settings
                    .PlexLibrary.SelectedLibraries.Select(s =>
                        !string.IsNullOrWhiteSpace(s.Uuid) ? s.Uuid : (!string.IsNullOrWhiteSpace(s.ServerId) ? s.ServerId + "::" + s.SectionId : s.SectionId.ToString())
                    )
                    .ToList();

                WriteTokenFile(settings.PlexLibrary.Token, settings.PlexAuth.ClientIdentifier, settings.PlexLibrary.DiscoveredServers, settings.PlexLibrary.DiscoveredLibraries, selectedKeys);
            }

            _settings = settings;
            Logger.Info("Config saved.");
        }

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

            try
            {
                var contents = File.ReadAllText(_filePath);
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

            ApplySecrets(settings);
            if (EnsureLibraryTargets(settings))
                needsSave = true;

            // Normalize any path mappings so users can enter natural paths (e.g., "M:\\Anime" or "/mnt/plex/anime/")
            if (NormalizePathMappings(settings))
                needsSave = true;

            // Normalize comma-separated fields (TagBlacklist, ExtraPlexUsers)
            if (NormalizeCommaSeparatedFields(settings))
                needsSave = true;

            ValidateSettings(settings);

            if (needsSave)
                SaveSettings(settings);

            return settings;
        }

        // --- helpers for ExtraPlexUsers parsing (shared canonical logic) ---
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

        public List<(string Name, string? Pin)> GetExtraPlexUserEntries()
        {
            var extraRaw = GetSettings().ExtraPlexUsers ?? string.Empty;
            return ParseExtraPlexUsers(extraRaw);
        }

        public List<string> GetExtraPlexUsernames()
        {
            return GetExtraPlexUserEntries().Select(e => e.Name).ToList();
        }

        // --- secrets/token-file helpers for extra Plex user tokens ---
        public string? GetExtraPlexUserToken(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;
            var tf = ReadTokenFile();
            if (tf.ExtraPlexUserTokens == null)
                return null;
            return tf.ExtraPlexUserTokens.TryGetValue(username, out var t) ? t : null;
        }

        public void SetExtraPlexUserToken(string username, string token)
        {
            if (string.IsNullOrWhiteSpace(username))
                return;
            var tf = ReadTokenFile();
            tf.ExtraPlexUserTokens ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            tf.ExtraPlexUserTokens[username] = token ?? string.Empty;
            WriteTokenFile(tf.Token, tf.ClientIdentifier, tf.Servers, tf.Libraries, tf.SelectedLibraryKeys, tf.ExtraPlexUserTokens);
        }

        public void RemoveExtraPlexUserToken(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return;
            var tf = ReadTokenFile();
            if (tf.ExtraPlexUserTokens == null || !tf.ExtraPlexUserTokens.ContainsKey(username))
                return;
            tf.ExtraPlexUserTokens.Remove(username);
            WriteTokenFile(tf.Token, tf.ClientIdentifier, tf.Servers, tf.Libraries, tf.SelectedLibraryKeys, tf.ExtraPlexUserTokens);
        }

        private void ApplySecrets(RelayConfig settings)
        {
            var tokenData = ReadTokenFile();

            if (!string.IsNullOrWhiteSpace(tokenData.Token))
                settings.PlexLibrary.Token = tokenData.Token;

            string clientId = tokenData.ClientIdentifier ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = !string.IsNullOrWhiteSpace(settings.PlexAuth.ClientIdentifier) ? settings.PlexAuth.ClientIdentifier : GenerateClientIdentifier();

            settings.PlexAuth.ClientIdentifier = clientId;
            settings.PlexLibrary.ClientIdentifier = clientId;

            // Apply discovered servers and libraries from token file if present
            if (tokenData.Servers != null && tokenData.Servers.Count > 0)
            {
                settings.PlexLibrary.DiscoveredServers = tokenData.Servers;
            }

            if (tokenData.Libraries != null && tokenData.Libraries.Count > 0)
            {
                settings.PlexLibrary.DiscoveredLibraries = tokenData.Libraries;
            }

            // Apply selected library keys from token file (uuid or serverId::sectionId)
            if (tokenData.SelectedLibraryKeys != null && tokenData.SelectedLibraryKeys.Count > 0)
            {
                var selected = new List<PlexLibraryTarget>();
                foreach (var key in tokenData.SelectedLibraryKeys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    PlexAvailableLibrary? found = null;

                    // Try match by UUID
                    found = settings.PlexLibrary.DiscoveredLibraries.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Uuid) && string.Equals(l.Uuid, key, StringComparison.OrdinalIgnoreCase));

                    // Try serverId::id
                    if (found == null && key.Contains("::"))
                    {
                        var parts = key.Split(new[] { "::" }, StringSplitOptions.None);
                        if (parts.Length >= 2)
                        {
                            var serverId = parts[0];
                            if (int.TryParse(parts[1], out int sid))
                                found = settings.PlexLibrary.DiscoveredLibraries.FirstOrDefault(l => l.Id == sid && string.Equals(l.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    // Try plain numeric id fallback
                    if (found == null && int.TryParse(key, out int nid))
                    {
                        found = settings.PlexLibrary.DiscoveredLibraries.FirstOrDefault(l => l.Id == nid);
                    }

                    if (found != null)
                    {
                        selected.Add(
                            new PlexLibraryTarget
                            {
                                SectionId = found.Id,
                                Title = found.Title,
                                Type = found.Type,
                                Uuid = found.Uuid,
                                ServerId = found.ServerId,
                                ServerName = found.ServerName,
                                ServerUrl = found.ServerUrl,
                                LibraryType = (found.Type ?? string.Empty).Trim().ToLowerInvariant() switch
                                {
                                    "movie" => PlexLibraryType.Movie,
                                    "show" => PlexLibraryType.Show,
                                    "artist" => PlexLibraryType.Music,
                                    "photo" => PlexLibraryType.Photo,
                                    _ => PlexLibraryType.Show,
                                },
                            }
                        );
                    }
                }

                if (selected.Count > 0)
                    settings.PlexLibrary.SelectedLibraries = selected;
            }

            // Ensure we write back a normalized token file
            var selectedKeys = settings
                .PlexLibrary.SelectedLibraries.Select(s =>
                    !string.IsNullOrWhiteSpace(s.Uuid) ? s.Uuid : (!string.IsNullOrWhiteSpace(s.ServerId) ? s.ServerId + "::" + s.SectionId : s.SectionId.ToString())
                )
                .ToList();
            WriteTokenFile(settings.PlexLibrary.Token, clientId, settings.PlexLibrary.DiscoveredServers, settings.PlexLibrary.DiscoveredLibraries, selectedKeys);
        }

        private sealed class TokenFile
        {
            public string? Token { get; set; }
            public string? ClientIdentifier { get; set; }
            public List<PlexAvailableServer>? Servers { get; set; }
            public List<PlexAvailableLibrary>? Libraries { get; set; }
            public List<string>? SelectedLibraryKeys { get; set; }
            public Dictionary<string, string>? ExtraPlexUserTokens { get; set; }
        }

        private TokenFile ReadTokenFile()
        {
            try
            {
                if (!File.Exists(_tokenPath))
                    return new TokenFile
                    {
                        Servers = new List<PlexAvailableServer>(),
                        Libraries = new List<PlexAvailableLibrary>(),
                        SelectedLibraryKeys = new List<string>(),
                        ExtraPlexUserTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    };

                var content = File.ReadAllText(_tokenPath);
                if (string.IsNullOrWhiteSpace(content))
                    return new TokenFile
                    {
                        Servers = new List<PlexAvailableServer>(),
                        Libraries = new List<PlexAvailableLibrary>(),
                        SelectedLibraryKeys = new List<string>(),
                        ExtraPlexUserTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    };

                try
                {
                    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<TokenFile>(content, jsonOptions);
                    if (parsed != null)
                    {
                        parsed.Servers ??= new List<PlexAvailableServer>();
                        parsed.Libraries ??= new List<PlexAvailableLibrary>();
                        parsed.SelectedLibraryKeys ??= new List<string>();
                        parsed.ExtraPlexUserTokens ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        return parsed;
                    }
                }
                catch (JsonException ex)
                {
                    // Token file is invalid JSON — refuse to parse legacy two-line format and return an empty token object.
                    Logger.Warn($"Invalid token file format at {_tokenPath}: {ex.Message}");
                    return new TokenFile
                    {
                        Token = null,
                        ClientIdentifier = null,
                        Servers = new List<PlexAvailableServer>(),
                        Libraries = new List<PlexAvailableLibrary>(),
                        SelectedLibraryKeys = new List<string>(),
                        ExtraPlexUserTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    };
                }

                // If we reach here the content did not deserialize to a TokenFile — return an empty token object.
                return new TokenFile
                {
                    Token = null,
                    ClientIdentifier = null,
                    Servers = new List<PlexAvailableServer>(),
                    Libraries = new List<PlexAvailableLibrary>(),
                    SelectedLibraryKeys = new List<string>(),
                    ExtraPlexUserTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to read token file {_tokenPath}: {ex.Message}");
                return new TokenFile
                {
                    Servers = new List<PlexAvailableServer>(),
                    Libraries = new List<PlexAvailableLibrary>(),
                    SelectedLibraryKeys = new List<string>(),
                    ExtraPlexUserTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                };
            }
        }

        private void WriteTokenFile(
            string? token,
            string? clientIdentifier,
            List<PlexAvailableServer>? servers = null,
            List<PlexAvailableLibrary>? libraries = null,
            List<string>? selectedLibraryKeys = null,
            Dictionary<string, string>? extraUserTokens = null
        )
        {
            var tokenObj = new TokenFile
            {
                Token = token ?? string.Empty,
                ClientIdentifier = clientIdentifier ?? string.Empty,
                Servers = servers ?? new List<PlexAvailableServer>(),
                Libraries = libraries ?? new List<PlexAvailableLibrary>(),
                SelectedLibraryKeys = selectedLibraryKeys ?? new List<string>(),
                ExtraPlexUserTokens = extraUserTokens ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
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
                    normalized.Length >= 2 && normalized[1] == ':' /* drive */
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

        private static RelayConfig StripSecrets(RelayConfig settings)
        {
            var cloned = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(settings, Options), Options) ?? new RelayConfig();
            cloned.PlexAuth.ClientIdentifier = "";
            cloned.PlexLibrary.Token = "";
            cloned.PlexLibrary.ClientIdentifier = "";
            return cloned;
        }

        private static bool EnsureLibraryTargets(RelayConfig settings)
        {
            if (settings.PlexLibrary.SelectedLibraries.Count > 0)
                return false;

            if (settings.PlexLibrary.LibrarySectionId <= 0)
                return false;

            settings.PlexLibrary.SelectedLibraries.Add(
                new PlexLibraryTarget
                {
                    SectionId = settings.PlexLibrary.LibrarySectionId,
                    Title = settings.PlexLibrary.SelectedLibraryName,
                    Type = settings.PlexLibrary.LibraryType.ToString(),
                    Uuid = settings.PlexLibrary.SectionUuid,
                    LibraryType = settings.PlexLibrary.LibraryType,
                }
            );

            return true;
        }

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
}
