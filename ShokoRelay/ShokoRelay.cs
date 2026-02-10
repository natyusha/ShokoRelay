using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Integrations.Shoko;
using ShokoRelay.Plex;
using ShokoRelay.Vfs;

namespace ShokoRelay
{
    public class ServiceRegistration : IPluginServiceRegistration
    {
        public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
        {
            serviceCollection.AddHttpContextAccessor();
            serviceCollection.AddControllers().AddApplicationPart(typeof(ServiceRegistration).Assembly);
            serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
            serviceCollection.AddSingleton(provider => new AnimeThemesGenerator(provider.GetRequiredService<IVideoService>(), applicationPaths));
            serviceCollection.AddSingleton(provider => new AnimeThemesMapping(provider.GetRequiredService<IMetadataService>(), applicationPaths));
            serviceCollection.AddSingleton(provider => new PlexMetadata(provider.GetRequiredService<IMetadataService>(), provider.GetRequiredService<ShokoClient>()));
            serviceCollection.AddSingleton(provider => new PlexAuth(new HttpClient(), provider.GetRequiredService<ConfigProvider>().GetSettings().PlexAuth));
            serviceCollection.AddSingleton(provider => new PlexClient(new HttpClient(), provider.GetRequiredService<ConfigProvider>()));
            serviceCollection.AddSingleton(provider => new PlexCollections(new HttpClient(), provider.GetRequiredService<ConfigProvider>(), provider.GetRequiredService<PlexClient>()));
            serviceCollection.AddSingleton(provider => new ShokoClient(new HttpClient(), provider.GetRequiredService<ConfigProvider>()));
            serviceCollection.AddSingleton<Services.IPlexCollectionManager, Services.PlexCollectionManager>();
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

        public ShokoRelay(IApplicationPaths applicationPaths, IHttpContextAccessor httpContextAccessor, VfsWatcher watcher)
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
