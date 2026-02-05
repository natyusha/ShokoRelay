using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Meta;
using Shoko.Plugin.Abstractions;

namespace ShokoRelay
{
    public class ServiceRegistration : IPluginServiceRegistration
    {
        public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
        {
            serviceCollection.AddHttpContextAccessor();
            serviceCollection.AddControllers()
                .AddApplicationPart(typeof(ServiceRegistration).Assembly);
            serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
            serviceCollection.AddSingleton<PlexMetadata>();
            serviceCollection.AddSingleton<VfsBuilder>();
            serviceCollection.AddSingleton<VfsWatcher>();
        }
    }

    public class ShokoRelay : IPlugin
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string Name => ShokoRelayInfo.Name;

        private static ConfigProvider? _configProvider;
        public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

        private readonly VfsWatcher _watcher;

        public ShokoRelay(
            IApplicationPaths applicationPaths,
            IHttpContextAccessor httpContextAccessor,
            VfsWatcher watcher)
        {
            _configProvider = new ConfigProvider(applicationPaths);

            ImageHelper.HttpContextAccessor = httpContextAccessor;

            _watcher = watcher;

            Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} loaded.");
        }

        public void Load()
        {
            _watcher.Start();
            Logger.Info("Relay loaded with VFS auto-refresh.");
        }

        public void OnSettingsLoaded(IPluginSettings settings) { }
    }
}