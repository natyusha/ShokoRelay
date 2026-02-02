using Microsoft.Extensions.DependencyInjection;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions;
using NLog;
using ShokoRelay.Config;
using ShokoRelay.Meta;

namespace ShokoRelay
{
    public class ServiceRegistration : IPluginServiceRegistration
    {
        public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
        {
            serviceCollection.AddControllers()
                .AddApplicationPart(typeof(ServiceRegistration).Assembly);
            serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
            serviceCollection.AddSingleton<PlexMatching>();
            serviceCollection.AddSingleton<PlexMetadata>();
        }
    }

    public class ShokoRelay : IPlugin
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string Name => ShokoRelayInfo.Name;

        private static ConfigProvider? _configProvider;
        public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

        private readonly IShokoEventHandler _eventHandler;
        private readonly PlexMatching _plexMatcher;

        public ShokoRelay(IShokoEventHandler eventHandler, PlexMatching plexMatcher, IApplicationPaths applicationPaths)
        {
            _eventHandler = eventHandler;
            _plexMatcher = plexMatcher;
            _configProvider = new ConfigProvider(applicationPaths);

            Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} loaded.");
        }

        public void Load()
        {
            _eventHandler.FileRenamed += (s, e) => OnFileChanged(e.ImportFolder, e.RelativePath, e.PreviousRelativePath);
            _eventHandler.FileMoved += (s, e) => OnFileChanged(e.ImportFolder, e.RelativePath, e.PreviousRelativePath);

            Logger.Info("Event listeners active.");
        }

        private void OnFileChanged(IImportFolder importFolder, string relativePath, string? previousRelativePath)
        {
            string importRoot = importFolder.Path;
            if (string.IsNullOrEmpty(importRoot)) return;

            string newAbsolutePath = Path.Combine(importRoot, relativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? newFolderPath = Path.GetDirectoryName(newAbsolutePath);

            if (!string.IsNullOrEmpty(newFolderPath))
            {
                if (Directory.Exists(newFolderPath))
                {
                    var results = new List<string>();
                    var errors = new List<string>();
                    _plexMatcher.ProcessFolder(newFolderPath, results, errors, cleanup: true);
                    Logger.Info($"Updated .plexmatch in: {newFolderPath}");
                }
                else
                {
                    Logger.Warn($"Directory check failed for new path: {newFolderPath}");
                }
            }

            if (!string.IsNullOrEmpty(previousRelativePath))
            {
                string oldAbsolutePath = Path.Combine(importRoot, previousRelativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string? oldFolderPath = Path.GetDirectoryName(oldAbsolutePath);

                if (!string.IsNullOrEmpty(oldFolderPath) && oldFolderPath != newFolderPath && Directory.Exists(oldFolderPath))
                {
                    Logger.Info($"Cleaning up old directory: {oldFolderPath}");
                    _plexMatcher.ProcessFolder(oldFolderPath, [], [], cleanup: true);
                }
            }
        }

        public void OnSettingsLoaded(IPluginSettings settings) { }
    }
}