using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NLog;
using Shoko.Plugin.Abstractions;

namespace ShokoRelay.Config
{
    public class ConfigProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static RelayConfig CreateDefaultSettings() => new RelayConfig();

        private readonly string _filePath;
        private readonly string _tokenPath;
        private readonly object _settingsLock = new();
        private RelayConfig? _settings;

        private static readonly JsonSerializerOptions Options = new() { AllowTrailingCommas = true, WriteIndented = true };

        public ConfigProvider(IApplicationPaths applicationPaths)
        {
            string pluginDir = Path.Combine(applicationPaths.PluginsPath, ConfigConstants.PluginSubfolder);
            Directory.CreateDirectory(pluginDir); // Ensure directory exists
            _filePath = Path.Combine(pluginDir, ConfigConstants.ConfigFileName);
            _tokenPath = Path.Combine(pluginDir, ConfigConstants.SecretsFileName);
            Logger.Info($"Config path: {_filePath}");
            Logger.Info($"Token path: {_tokenPath}");

            // Watch for external changes to the config file
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!, ConfigConstants.ConfigFileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            watcher.Changed += (_, _) => InvalidateSettings();
            watcher.Created += (_, _) => InvalidateSettings();
            watcher.Deleted += (_, _) => InvalidateSettings();
            watcher.Renamed += (_, _) => InvalidateSettings();
            watcher.EnableRaisingEvents = true;

            var tokenWatcher = new FileSystemWatcher(pluginDir, ConfigConstants.SecretsFileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
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

            var tokenData = ReadTokenFile();
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

            ValidateSettings(settings);

            if (needsSave)
                SaveSettings(settings);

            return settings;
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
                    };

                var content = File.ReadAllText(_tokenPath);
                if (string.IsNullOrWhiteSpace(content))
                    return new TokenFile
                    {
                        Servers = new List<PlexAvailableServer>(),
                        Libraries = new List<PlexAvailableLibrary>(),
                        SelectedLibraryKeys = new List<string>(),
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
                };
            }
        }

        private void WriteTokenFile(
            string? token,
            string? clientIdentifier,
            List<PlexAvailableServer>? servers = null,
            List<PlexAvailableLibrary>? libraries = null,
            List<string>? selectedLibraryKeys = null
        )
        {
            var tokenObj = new TokenFile
            {
                Token = token ?? string.Empty,
                ClientIdentifier = clientIdentifier ?? string.Empty,
                Servers = servers ?? new List<PlexAvailableServer>(),
                Libraries = libraries ?? new List<PlexAvailableLibrary>(),
                SelectedLibraryKeys = selectedLibraryKeys ?? new List<string>(),
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
