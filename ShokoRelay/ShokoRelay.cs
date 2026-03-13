using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using Shoko.Abstractions.Core;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Plex;
using ShokoRelay.Vfs;

namespace ShokoRelay;

/// <summary>
/// Registers all plugin services, HTTP clients, singletons and the hosted background service into the DI container.
/// </summary>
public class ServiceRegistration : IPluginServiceRegistration
{
    /// <summary>
    /// Configure all services required by the plugin, including helpers, HTTP clients, and hosted services.
    /// </summary>
    /// <param name="serviceCollection">DI service collection.</param>
    /// <param name="applicationPaths">Paths provided by host for configuration and plugin directories.</param>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddControllers().AddApplicationPart(typeof(ServiceRegistration).Assembly);

        // ConfigProvider is required for Secrets (Plex Token) and paths
        serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));

        // These services read settings via ShokoRelay.Settings internally
        serviceCollection.AddSingleton<AnimeThemesMp3Generator>();
        serviceCollection.AddSingleton<AnimeThemesMapping>();
        serviceCollection.AddSingleton<PlexMetadata>();
        serviceCollection.AddSingleton<VfsBuilder>();
        serviceCollection.AddSingleton<VfsWatcher>();
        serviceCollection.AddSingleton<Services.ICollectionService, Services.CollectionService>();
        serviceCollection.AddSingleton<Services.ICriticRatingService, Services.CriticRatingService>();
        serviceCollection.AddSingleton<Services.ShokoImportService>();
        serviceCollection.AddSingleton<Sync.SyncToShoko>();
        serviceCollection.AddSingleton<Sync.SyncToPlex>();

        // Plex Services requiring specific HttpClient configurations
        var handlerWithCookies = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };

        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            var plexAuthConfig = new PlexAuthConfig { ClientIdentifier = cp.GetPlexClientIdentifier() };
            return new PlexAuth(new HttpClient(handlerWithCookies, disposeHandler: false), plexAuthConfig);
        });

        serviceCollection.AddSingleton(provider => new PlexClient(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<ConfigProvider>()));

        serviceCollection.AddSingleton(provider => new PlexCollections(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<PlexClient>()));

        // Main Runtime
        serviceCollection.AddHostedService<ShokoRelay>();
    }
}

/// <summary>
/// Minimal plugin descriptor that exposes the plugin's identity, name, description and embedded thumbnail to the Shoko host.
/// </summary>
public class Plugin : IPlugin
{
    public Guid ID => new("2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a");
    public string Name => ShokoRelayInfo.Name;
    public string? Description => "A Custom Metadata Provider and Automation Tools for Plex in the form of a Shoko Server plugin";
    public string? EmbeddedThumbnailResourceName => "ShokoRelay.dashboard.img.shoko-relay-thumbnail.png";
}

/// <summary>
/// Hosted background service that starts the VFS file-system watcher and runs the periodic automation loop (imports, Plex collections, watched-state sync).
/// </summary>
public class ShokoRelay : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static ConfigProvider? _configProvider;

    /// <summary>
    /// Shortcut to the current plugin settings; returns a default instance if the provider is not yet initialized.
    /// </summary>
    public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

    /// <summary>
    /// The externally-reachable base URL of the Shoko server, auto-refreshed from the current HTTP context. Falls back to <c>http://localhost:8111</c> when the provider is not yet initialized.
    /// </summary>
    public static string ServerBaseUrl => _configProvider?.ServerBaseUrl ?? "http://localhost:8111";

    /// <summary>
    /// Convenience accessor for the configuration directory path. Returns an empty string if the provider is not yet available.
    /// </summary>
    public static string ConfigDirectory => _configProvider?.ConfigDirectory ?? string.Empty;

    private readonly VfsWatcher _watcher;
    private readonly ISystemService _systemService;
    private readonly Sync.SyncToShoko? _watchedSyncService;
    private readonly Services.ShokoImportService? _shokoImportService;
    private readonly Services.ICollectionService? _collectionService;
    private readonly Services.ICriticRatingService? _criticRatingService;
    private readonly IMetadataService _metadataService;

    private static DateTime? _lastImportRunUtc;
    private static DateTime? _lastPlexAutomationUtc;
    private static DateTime? _lastSyncWatchedUtc;

    /// <summary>
    /// Records automation execution timestamps.
    /// </summary>
    public static void MarkImportRunNow() => _lastImportRunUtc = DateTime.UtcNow;

    public static void MarkPlexAutomationRunNow() => _lastPlexAutomationUtc = DateTime.UtcNow;

    public static void MarkSyncRunNow() => _lastSyncWatchedUtc = DateTime.UtcNow;

    /// <summary>
    /// Construct the hosted plugin service, wiring in required dependencies.
    /// </summary>
    public ShokoRelay(
        VfsWatcher watcher,
        ConfigProvider configProvider,
        ISystemService systemService,
        IMetadataService metadataService,
        IHttpContextAccessor httpContextAccessor,
        Sync.SyncToShoko? watchedSyncService = null,
        Services.ShokoImportService? shokoImportService = null,
        Services.ICollectionService? collectionService = null,
        Services.ICriticRatingService? criticRatingService = null
    )
    {
        _configProvider = configProvider;
        _configProvider.HttpContextAccessor = httpContextAccessor;
        _watcher = watcher;
        _systemService = systemService;
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _watchedSyncService = watchedSyncService;
        _shokoImportService = shokoImportService;
        _collectionService = collectionService;
        _criticRatingService = criticRatingService;

        Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} initialized.");
    }

    /// <summary>
    /// Main execution loop for the hosted service. Waits for Shoko Server readiness, initializes scheduling anchors to prevent redundant startup tasks, and starts the loop.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for Shoko Server to signal that all core services are usable
            Logger.Info("Relay waiting for Shoko Server to reach 'Started' state...");
            while (!_systemService.IsStarted && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested)
                return;
            Logger.Info("Shoko Server started. Initializing Relay scheduling anchors...");

            // Initialize automation anchors on startup to prevent redundant catch-up runs for slots that have already passed.
            var now = DateTime.UtcNow;
            var settings = Settings;
            int offset = Math.Clamp(settings.Automation.UtcOffsetHours, -12, 14);

            if (settings.Automation.ShokoImportFrequencyHours > 0)
                _lastImportRunUtc = ComputeSchedule(now, offset, settings.Automation.ShokoImportFrequencyHours).LastScheduled;

            if (settings.Automation.ShokoSyncWatchedFrequencyHours > 0)
                _lastSyncWatchedUtc = ComputeSchedule(now, offset, settings.Automation.ShokoSyncWatchedFrequencyHours).LastScheduled;

            if (settings.Automation.PlexAutomationFrequencyHours > 0)
                _lastPlexAutomationUtc = ComputeSchedule(now, offset, settings.Automation.PlexAutomationFrequencyHours).LastScheduled;

            // Start VFS Watcher and enter the loop
            _watcher.Start();
            Logger.Info("Relay started with VFS auto-refresh. Automation anchors synchronized to current UTC slots.");

            await AutomationLoop(stoppingToken).ConfigureAwait(false);
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

    /// <summary>
    /// Cleanly stop the hosted service.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Info("ShokoRelay stopping...");
        _watcher.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compute the last scheduled and next scheduled run times based on the current time and UTC offset.
    /// </summary>
    private static (DateTime LastScheduled, DateTime NextRun) ComputeSchedule(DateTime now, int offsetHours, int frequencyHours)
    {
        DateTime anchor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(offsetHours);
        if (anchor > now)
            anchor = anchor.AddDays(-1);
        double periods = Math.Floor((now - anchor).TotalHours / frequencyHours);
        DateTime lastScheduled = anchor.AddHours(Math.Max(0, periods) * frequencyHours);
        return (lastScheduled, lastScheduled.AddHours(frequencyHours));
    }

    private async Task AutomationLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = Settings;
                var now = DateTime.UtcNow;
                int offset = Math.Clamp(settings.Automation.UtcOffsetHours, -12, 14);
                List<DateTime> nextRuns = [];

                // Scheduled Shoko import
                int importFreq = settings.Automation.ShokoImportFrequencyHours;
                if (importFreq > 0 && _shokoImportService != null)
                {
                    var (lastSched, next) = ComputeSchedule(now, offset, importFreq);
                    nextRuns.Add(next);
                    if (_lastImportRunUtc == null || _lastImportRunUtc < lastSched)
                    {
                        Logger.Info("Automation: triggering scheduled Shoko import ({0}h)", importFreq);
                        await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
                        _lastImportRunUtc = lastSched;
                    }
                }

                // Watched-state sync
                int syncFreq = settings.Automation.ShokoSyncWatchedFrequencyHours;
                if (syncFreq > 0 && _watchedSyncService != null)
                {
                    var (lastSched, next) = ComputeSchedule(now, offset, syncFreq);
                    nextRuns.Add(next);
                    if (_lastSyncWatchedUtc == null || _lastSyncWatchedUtc < lastSched)
                    {
                        Logger.Info("Automation: triggering scheduled Plex->Shoko sync ({0}h)", syncFreq);
                        await _watchedSyncService.SyncWatchedAsync(false, syncFreq + 1, cancellationToken: ct).ConfigureAwait(false);
                        _lastSyncWatchedUtc = lastSched;
                    }
                }

                // Plex Automation (collections + ratings)
                int plexFreq = settings.Automation.PlexAutomationFrequencyHours;
                if (plexFreq > 0 && (_collectionService != null || _criticRatingService != null))
                {
                    var (lastSched, next) = ComputeSchedule(now, offset, plexFreq);
                    nextRuns.Add(next);
                    if (_lastPlexAutomationUtc == null || _lastPlexAutomationUtc < lastSched)
                    {
                        Logger.Info("Automation: triggering scheduled Plex automation ({0}h)", plexFreq);
                        var allSeries = _metadataService.GetAllShokoSeries()?.Cast<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>().ToList();
                        if (allSeries?.Count > 0)
                        {
                            if (_collectionService != null)
                                await _collectionService.BuildCollectionsAsync(allSeries, ct).ConfigureAwait(false);
                            if (_criticRatingService != null)
                                await _criticRatingService.ApplyRatingsAsync(null, ct).ConfigureAwait(false);
                        }
                        _lastPlexAutomationUtc = lastSched;
                    }
                }

                // Sleep until next scheduled task or fallback to 1 minute
                double delayMs = nextRuns.Any() ? (nextRuns.Min() - DateTime.UtcNow).TotalMilliseconds : 60000;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 1000, 300000)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Automation loop error");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }
    }
}
