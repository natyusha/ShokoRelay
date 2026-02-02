using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NLog;
using Shoko.Plugin.Abstractions;

namespace ShokoRelay.Config
{
    public class ConfigProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _filePath;
        private readonly object _settingsLock = new();
        private RelayConfig? _settings;

        private static readonly JsonSerializerOptions Options = new()
        {
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        public ConfigProvider(IApplicationPaths applicationPaths)
        {
            _filePath = Path.Combine(applicationPaths.ProgramDataPath, "ShokoRelayConfig.json");
            Logger.Info($"Config path: {_filePath}");
        }

        public RelayConfig GetSettings()
        {
            _settings ??= GetSettingsFromFile();
            return _settings;
        }

        public void SaveSettings(RelayConfig settings)
        {
            ValidateSettings(settings);

            var json = JsonSerializer.Serialize(settings, Options);
            lock (_settingsLock)
            {
                using FileStream stream = new(_filePath, FileMode.Create);
                using StreamWriter writer = new(stream);
                writer.Write(json);
            }

            _settings = settings;
            Logger.Info("Config saved.");
        }

        private RelayConfig GetSettingsFromFile()
        {
            RelayConfig settings;
            try
            {
                var contents = File.ReadAllText(_filePath);
                settings = JsonSerializer.Deserialize<RelayConfig>(contents, Options) ?? new RelayConfig();
                Logger.Info("Config loaded from file.");
            }
            catch (FileNotFoundException)
            {
                settings = new RelayConfig();
                Logger.Info("Config file not found, creating defaults.");
            }
            catch (JsonException ex)
            {
                Logger.Warn($"Invalid config file, using defaults: {ex.Message}");
                settings = new RelayConfig();
            }

            ValidateSettings(settings);
            SaveSettings(settings);
            return settings;
        }

        private static void ValidateSettings(RelayConfig settings)
        {
            List<ValidationResult> validationResults = [];
            ValidationContext validationContext = new(settings);

            var isValid = Validator.TryValidateObject(settings, validationContext, validationResults, true);

            if (!isValid)
            {
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
}