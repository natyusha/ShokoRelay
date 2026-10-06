using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.ScheduledActions.Services;
using ShokoRelay.Actions;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay;

#region Service Registration

/// <summary>Registers plugin services and background workers into the DI container.</summary>
public class ServiceRegistration : IPluginServiceRegistration
{
    /// <summary>The explicitly registered metadata source used to claim ownership of local images uploaded by this plugin.</summary>
    public static MetadataSource RelaySource { get; private set; } = null!;

    /// <summary>The explicitly registered metadata source used to claim ownership of Plex thumbnails uploaded by this plugin.</summary>
    public static MetadataSource RelayPlexSource { get; private set; } = null!;

    /// <summary>Configures all services required by ShokoRelay.</summary>
    /// <param name="serviceCollection">DI collection.</param>
    /// <param name="applicationPaths">Host provided paths.</param>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        RelaySource = MetadataSource.Register("Shoko Relay (Local)", ShokoRelayConstants.RelaySourceId, description: "Local images uploaded and managed by Shoko Relay.");
        RelayPlexSource = MetadataSource.Register("Shoko Relay (Plex)", ShokoRelayConstants.RelayPlexSourceId, description: "Plex thumbnails uploaded and managed by Shoko Relay.");

        serviceCollection.AddHttpContextAccessor();

        string clientName = ShokoRelayConstants.Name.Replace(" ", "");
        serviceCollection
            .AddHttpClient(clientName, client => client.DefaultRequestHeaders.Add("User-Agent", $"{clientName}/{ShokoRelayConstants.Version}"))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    UseCookies = true,
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );

        serviceCollection.AddSingleton(provider => provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
        serviceCollection.AddSingleton(provider => new ConfigProvider(applicationPaths, provider.GetRequiredService<ILogger<ConfigProvider>>()));
        serviceCollection.AddSingleton(provider => new AnimeThemesApi(provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<ILogger<AnimeThemesApi>>()));
        serviceCollection.AddSingleton<AnimeThemesMp3Generator>();
        serviceCollection.AddSingleton<AnimeThemesMapping>();
        serviceCollection.AddSingleton<AnimeThemesWebmDownloader>();
        serviceCollection.AddSingleton<PlexMetadata>();
        serviceCollection.AddSingleton<VfsAssetLinker>();
        serviceCollection.AddSingleton<VfsBuilder>();
        serviceCollection.AddSingleton<VfsWatcher>();
        serviceCollection.AddSingleton<ICollectionService, CollectionService>();
        serviceCollection.AddSingleton<ICriticRatingService, CriticRatingService>();
        serviceCollection.AddSingleton<IImageSyncService, ImageSyncService>();
        serviceCollection.AddSingleton<IShokoImportService, ShokoImportService>();
        serviceCollection.AddSingleton<SourceLinkService>();
        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            return new FfmpegService(cp.PluginDirectory, applicationPaths.ApplicationPath, applicationPaths.DataPath, provider.GetRequiredService<ILogger<FfmpegService>>());
        });
        serviceCollection.AddSingleton<SyncToShoko>();
        serviceCollection.AddSingleton<SyncToPlex>();
        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            var plexAuthConfig = new PlexAuthConfig { ClientIdentifier = cp.GetPlexClientIdentifier() };
            return new PlexAuth(provider.GetRequiredService<HttpClient>(), plexAuthConfig, provider.GetRequiredService<ILogger<PlexAuth>>());
        });
        serviceCollection.AddSingleton<PlexClient>();
        serviceCollection.AddSingleton<PlexCollections>();

        serviceCollection.AddHostedService<ShokoRelay>();
    }
}

#endregion

#region Plugin Descriptor

/// <summary>Plugin entry point and descriptor for Shoko Server.</summary>
public class Plugin : IPlugin
{
    /// <inheritdoc/>
    public Guid ID => new(ShokoRelayConstants.PluginId);

    /// <inheritdoc/>
    public string Name => ShokoRelayConstants.Name;

    /// <inheritdoc/>
    public string? Description => ShokoRelayConstants.Description;

    /// <inheritdoc/>
    public string? EmbeddedThumbnailResourceName => "ShokoRelay.Assets.shoko-relay-logo.png";

    /// <inheritdoc/>
    public string? EmbeddedIconResourceName => "ShokoRelay.Assets.shoko-relay-icon.png";

    /// <inheritdoc/>
    public IReadOnlyList<PluginPage> GetPages() =>
        [
            new() { Name = "Dashboard", Url = "/api/plugin/ShokoRelay/dashboard" },
            new() { Name = "VFS Browser", Url = "/api/plugin/ShokoRelay/browser" },
            new() { Name = "AnimeThemes Player", Url = "/api/plugin/ShokoRelay/player" },
        ];
}

#endregion

/// <summary>Hosted service managing the VFS watcher and automation schedules.</summary>
public class ShokoRelay : BackgroundService
{
    #region Setup & State

    private readonly ILogger<ShokoRelay> _logger;
    private static ConfigProvider? s_configProvider;

    /// <summary>Access current plugin settings.</summary>
    public static RelayConfig Settings => s_configProvider?.GetEffectiveSettings() ?? new RelayConfig();

    /// <summary>Access the Shoko server base URL.</summary>
    public static string ServerBaseUrl => s_configProvider?.ServerBaseUrl ?? "http://localhost:8111";

    /// <summary>Access the plugin config directory.</summary>
    public static string ConfigDirectory => s_configProvider?.ConfigDirectory ?? string.Empty;

    private readonly VfsWatcher _watcher;
    private readonly ISystemService _systemService;
    private readonly IMetadataService _metadataService;
    private readonly IScheduledActionService _scheduledActionService;

    /// <summary>Generates ParallelOptions pre-configured with the maximum degree of parallelism (clamped to at least 1) and an optional cancellation token.</summary>
    /// <param name="token">Optional cancellation token.</param>
    /// <returns>A configured ParallelOptions instance.</returns>
    public static ParallelOptions DefaultParallelOptions(CancellationToken token = default) => new() { MaxDegreeOfParallelism = Math.Max(1, Settings.Advanced.Parallelism), CancellationToken = token };

    /// <summary>Returns whether TMDB episode numbering should be used, forced to true if TMDB auto-merging is enabled.</summary>
    public static bool EnforceTmdbNumbering => Settings.Advanced.TmdbEpNumbering || Settings.Advanced.MergeTmdbSeries;

    /// <summary>Initializes the Relay hosted service.</summary>
    /// <param name="watcher">VFS filesystem event watcher.</param>
    /// <param name="configProvider">Configuration and secrets management service.</param>
    /// <param name="httpContextAccessor">Access to the current HTTP request context.</param>
    /// <param name="systemService">Shoko system state service.</param>
    /// <param name="metadataService">Shoko metadata query service.</param>
    /// <param name="scheduledActionService">Shoko scheduled action service.</param>
    /// <param name="logger">Logging service.</param>
    public ShokoRelay(
        VfsWatcher watcher,
        ConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        ISystemService systemService,
        IMetadataService metadataService,
        IScheduledActionService scheduledActionService,
        ILogger<ShokoRelay> logger
    )
    {
        _watcher = watcher;
        s_configProvider = configProvider;
        s_configProvider.HttpContextAccessor = httpContextAccessor;
        _systemService = systemService;
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _scheduledActionService = scheduledActionService;
        _logger = logger;
        _logger.LogInformation("ShokoRelay v{Version} initialized", ShokoRelayConstants.Version);
    }

    #endregion

    #region Background Service

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Relay waiting for Shoko Server to reach 'Started' state...");
            while (!_systemService.IsStarted && !stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                return;

            _logger.LogInformation("Shoko Server started -> Caching overrides & initializing native scheduled actions...");
            OverrideHelper.Reload(_metadataService); // Warm up the VFS override cache.

            // Sync triggers from current configuration
            ActionScheduleHelper.SyncTriggers(Settings, _scheduledActionService);

            _watcher.Start();

            // Keep the BackgroundService alive to hold VfsWatcher
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _watcher.Stop();
            }
            catch { }
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Relay stopping...");
        _watcher.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion
}
