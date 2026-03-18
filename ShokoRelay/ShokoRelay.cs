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

#region Service Registration

/// <summary>Registers plugin services and background workers into the DI container.</summary>
public class ServiceRegistration : IPluginServiceRegistration
{
    /// <summary>Configures all services required by ShokoRelay.</summary>
    /// <param name="serviceCollection">DI collection.</param>
    /// <param name="applicationPaths">Host provided paths.</param>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddControllers().AddApplicationPart(typeof(ServiceRegistration).Assembly);

        serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
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

        var handlerWithCookies = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            var plexAuthConfig = new PlexAuthConfig { ClientIdentifier = cp.GetPlexClientIdentifier() };
            return new PlexAuth(new HttpClient(handlerWithCookies, disposeHandler: false), plexAuthConfig);
        });
        serviceCollection.AddSingleton(provider => new PlexClient(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<ConfigProvider>()));
        serviceCollection.AddSingleton(provider => new PlexCollections(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<PlexClient>()));

        serviceCollection.AddHostedService<ShokoRelay>();
    }
}

#endregion

#region Plugin Descriptor

/// <summary>Plugin entry point and descriptor for Shoko Server.</summary>
public class Plugin : IPlugin
{
    /// <summary>Unique plugin ID.</summary>
    public Guid ID => new("2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a");

    /// <summary>Plugin display name.</summary>
    public string Name => ShokoRelayConstants.Name;

    /// <summary>Plugin description.</summary>
    public string? Description => "A Custom Metadata Provider and Automation Tools for Plex and AnimeThemes in the form of a Shoko Server plugin";

    /// <summary>Plugin thumbnail resource.</summary>
    public string? EmbeddedThumbnailResourceName => "ShokoRelay.dashboard.img.shoko-relay-thumbnail.png";
}

#endregion

/// <summary>Hosted service managing the VFS watcher and automation schedules.</summary>
public class ShokoRelay : BackgroundService
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static ConfigProvider? _configProvider;

    /// <summary>Access current plugin settings.</summary>
    public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

    /// <summary>Access the Shoko server base URL.</summary>
    public static string ServerBaseUrl => _configProvider?.ServerBaseUrl ?? "http://localhost:8111";

    /// <summary>Access the plugin config directory.</summary>
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

    /// <summary>Initializes the Relay hosted service.</summary>
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
        Logger.Info($"ShokoRelay v{ShokoRelayConstants.Version} initialized.");
    }

    #endregion

    #region Background Service

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Logger.Info("Relay waiting for Shoko Server to reach 'Started' state...");
            while (!_systemService.IsStarted && !stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                return;

            Logger.Info("Shoko Server started. Initializing Relay scheduling anchors...");
            var now = DateTime.UtcNow;
            var settings = Settings;
            int offset = Math.Clamp(settings.Automation.UtcOffsetHours, -12, 14);
            if (settings.Automation.ShokoImportFrequencyHours > 0)
                _lastImportRunUtc = ComputeSchedule(now, offset, settings.Automation.ShokoImportFrequencyHours).LastScheduled;
            if (settings.Automation.ShokoSyncWatchedFrequencyHours > 0)
                _lastSyncWatchedUtc = ComputeSchedule(now, offset, settings.Automation.ShokoSyncWatchedFrequencyHours).LastScheduled;
            if (settings.Automation.PlexAutomationFrequencyHours > 0)
                _lastPlexAutomationUtc = ComputeSchedule(now, offset, settings.Automation.PlexAutomationFrequencyHours).LastScheduled;

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

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Info("ShokoRelay stopping...");
        _watcher.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Automation Schedule

    /// <summary>Manually mark import as run.</summary>
    public static void MarkImportRunNow() => _lastImportRunUtc = DateTime.UtcNow;

    /// <summary>Manually mark automation as run.</summary>
    public static void MarkPlexAutomationRunNow() => _lastPlexAutomationUtc = DateTime.UtcNow;

    /// <summary>Manually mark sync as run.</summary>
    public static void MarkSyncRunNow() => _lastSyncWatchedUtc = DateTime.UtcNow;

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

    #endregion
}
