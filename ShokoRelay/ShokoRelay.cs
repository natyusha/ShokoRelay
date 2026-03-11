using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
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
    public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
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

        // Plex Services still need ConfigProvider for the Token (Secrets)
        var handlerWithCookies = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };

        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            var plexAuthConfig = new PlexAuthConfig { ClientIdentifier = cp.GetPlexClientIdentifier() };
            return new PlexAuth(new HttpClient(handlerWithCookies, disposeHandler: false), plexAuthConfig);
        });

        serviceCollection.AddSingleton(provider => new PlexClient(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<ConfigProvider>()));

        serviceCollection.AddSingleton(provider => new PlexCollections(
            new HttpClient(handlerWithCookies, disposeHandler: false),
            provider.GetRequiredService<ConfigProvider>(),
            provider.GetRequiredService<PlexClient>()
        ));

        // Sync Services
        serviceCollection.AddSingleton<Sync.SyncToShoko>();
        serviceCollection.AddSingleton<Sync.SyncToPlex>();

        // Main Runtime
        serviceCollection.AddHostedService<ShokoRelay>();
    }
}

/// <summary>
/// Minimal plugin descriptor that exposes the plugin's identity, name, description and embedded thumbnail to the Shoko host.
/// </summary>
public class Plugin : IPlugin
{
    public Guid ID => new Guid("2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a");
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
    /// The externally-reachable base URL of the Shoko server, auto-refreshed from the current HTTP context.
    /// Falls back to <c>http://localhost:8111</c> when the provider is not yet initialized.
    /// </summary>
    public static string ServerBaseUrl => _configProvider?.ServerBaseUrl ?? "http://localhost:8111";

    /// <summary>
    /// Convenience accessor for the configuration directory path (same location as overrides, logs, etc.).
    /// Returns an empty string if the provider is not yet available.
    /// </summary>
    public static string ConfigDirectory => _configProvider?.ConfigDirectory ?? string.Empty;

    private readonly VfsWatcher _watcher;
    private readonly Sync.SyncToShoko? _watchedSyncService;
    private readonly Services.ShokoImportService? _shokoImportService;
    private readonly Services.ICollectionService? _collectionService;
    private readonly Services.ICriticRatingService? _criticRatingService;
    private readonly IMetadataService _metadataService;
    private static DateTime? _lastImportRunUtc;
    private static DateTime? _lastPlexAutomationUtc;
    private static DateTime? _lastSyncWatchedUtc;

    /// <summary>
    /// Record that a Shoko import operation was triggered at the current time.
    /// </summary>
    public static void MarkImportRunNow() => _lastImportRunUtc = DateTime.UtcNow;

    /// <summary>
    /// Record that the Plex automation loop ran at the current time.
    /// </summary>
    public static void MarkPlexAutomationRunNow() => _lastPlexAutomationUtc = DateTime.UtcNow;

    /// <summary>
    /// Record that a watched-state sync cycle was executed just now.
    /// </summary>
    public static void MarkSyncRunNow() => _lastSyncWatchedUtc = DateTime.UtcNow;

    /// <summary>
    /// Construct the hosted plugin service, wiring in required dependencies such as the VFS watcher, sync services, and configuration provider.
    /// </summary>
    /// <param name="applicationPaths">Host-provided paths (used for plugin directory).</param>
    /// <param name="httpContextAccessor">Accessor used by <see cref="ConfigProvider"/> for base URL resolution.</param>
    /// <param name="watcher">VFS watcher service.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="watchedSyncService">Optional watched-sync service (Plex→Shoko).</param>
    /// <param name="shokoImportService">Optional service to trigger Shoko imports.</param>
    /// <param name="collectionService">Optional collection management service.</param>
    /// <param name="criticRatingService">Optional critic-rating service.</param>
    /// <param name="metadataService">Mandatory metadata service; throws if null.</param>
    public ShokoRelay(
        IApplicationPaths applicationPaths,
        IHttpContextAccessor httpContextAccessor,
        VfsWatcher watcher,
        ConfigProvider configProvider,
        Sync.SyncToShoko? watchedSyncService = null,
        Services.ShokoImportService? shokoImportService = null,
        Services.ICollectionService? collectionService = null,
        Services.ICriticRatingService? criticRatingService = null,
        IMetadataService metadataService = null!
    )
    {
        if (metadataService == null)
            throw new ArgumentNullException(nameof(metadataService));
        _configProvider = configProvider;

        configProvider.HttpContextAccessor = httpContextAccessor;

        _watcher = watcher;
        _watchedSyncService = watchedSyncService;
        _shokoImportService = shokoImportService;
        _collectionService = collectionService;
        _criticRatingService = criticRatingService;
        _metadataService = metadataService;

        Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} initialized.");
    }

    /// <summary>
    /// Main execution loop for the hosted service. Starts the VFS watcher,
    /// initializes scheduling anchors to prevent redundant startup tasks,
    /// and then enters the automation loop until cancellation is requested.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Initialize automation anchors on startup. Set the last run time to the "LastScheduled" slot of the current time, preventing catch-up runs for slots that have already passed.
            var now = DateTime.UtcNow;
            var settings = Settings;
            int offset = Math.Clamp(settings?.Automation.UtcOffsetHours ?? 0, -12, 14);

            if (settings?.Automation.ShokoImportFrequencyHours > 0)
            {
                _lastImportRunUtc = ComputeSchedule(now, offset, settings.Automation.ShokoImportFrequencyHours).LastScheduled;
            }

            if (settings?.Automation.ShokoSyncWatchedFrequencyHours > 0)
            {
                _lastSyncWatchedUtc = ComputeSchedule(now, offset, settings.Automation.ShokoSyncWatchedFrequencyHours).LastScheduled;
            }

            if (settings?.Automation.PlexAutomationFrequencyHours > 0)
            {
                _lastPlexAutomationUtc = ComputeSchedule(now, offset, settings.Automation.PlexAutomationFrequencyHours).LastScheduled;
            }

            // Start watcher immediately
            _watcher.Start();
            Logger.Info("Relay started with VFS auto-refresh. Automation anchors initialized to current UTC slots.");

            // Run automation loop until cancellation
            await AutomationLoop(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Shutdown watcher when stopping
            try
            {
                _watcher.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error while stopping VFS watcher");
            }
        }
    }

    /// <summary>
    /// Cleanly stop the hosted service by stopping the watcher and then performing base shutdown logic.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Info("ShokoRelay stopping...");
        _watcher.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compute the last scheduled and next scheduled run times based on the current time, UTC offset and frequency.
    /// </summary>
    /// <param name="now">Current UTC time used as the reference point.</param>
    /// <param name="offsetHours">UTC offset (hours) that anchors the daily schedule grid.</param>
    /// <param name="frequencyHours">Interval in hours between successive runs.</param>
    /// <returns>A tuple of the most recent scheduled time and the next upcoming run time.</returns>
    private static (DateTime LastScheduled, DateTime NextRun) ComputeSchedule(DateTime now, int offsetHours, int frequencyHours)
    {
        DateTime anchor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromHours(offsetHours);
        if (anchor > now)
            anchor = anchor.AddDays(-1);
        double periods = Math.Floor((now - anchor).TotalHours / frequencyHours);
        if (periods < 0)
            periods = 0;
        DateTime lastScheduled = anchor.AddHours(periods * frequencyHours);
        return (lastScheduled, lastScheduled.AddHours(frequencyHours));
    }

    private async Task AutomationLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var settings = Settings; // RelayConfig

                    // Scheduled Shoko import (independent of watched-state automation)
                    int importFreqHours = settings?.Automation.ShokoImportFrequencyHours ?? 0;
                    DateTime? nextImportRunUtc = null;

                    var now = DateTime.UtcNow;
                    int offset = Math.Clamp(settings?.Automation.UtcOffsetHours ?? 0, -12, 14);

                    if (importFreqHours <= 0)
                    {
                        _lastImportRunUtc = null;
                    }
                    else
                    {
                        var (lastScheduled, nextRun) = ComputeSchedule(now, offset, importFreqHours);
                        nextImportRunUtc = nextRun;

                        if (_lastImportRunUtc == null || _lastImportRunUtc < lastScheduled)
                        {
                            Logger.Info("Automation: triggering scheduled Shoko import (frequency {0}h)", importFreqHours);
                            if (_shokoImportService != null)
                            {
                                try
                                {
                                    var scanned = await _shokoImportService.TriggerImportAsync(ct).ConfigureAwait(false);
                                    if (scanned?.Count > 0)
                                        Logger.Info("Automation: triggered import scans for {Count} folders: {Folders}", scanned.Count, string.Join(", ", scanned));
                                    else
                                        Logger.Info("Automation: import scan executed but no source-type import folders were scanned.");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Automation: Shoko import trigger failed");
                                }
                            }
                            else
                            {
                                Logger.Warn("Automation: ShokoImportService not available; cannot trigger imports via v3 API");
                            }

                            _lastImportRunUtc = lastScheduled;
                        }
                    }

                    // Watched-state sync automation
                    DateTime? nextWatchedSyncRunUtc = null;
                    try
                    {
                        int syncFreq = settings?.Automation.ShokoSyncWatchedFrequencyHours ?? 0;

                        // Automatic watched-state sync is controlled *only* by the configured interval.
                        // If interval <= 0 the automation is disabled (same behaviour as Shoko import).
                        if (syncFreq <= 0 || _watchedSyncService == null)
                        {
                            _lastSyncWatchedUtc = null;
                        }
                        else
                        {
                            var (lastScheduled, nextRun) = ComputeSchedule(now, offset, syncFreq);
                            nextWatchedSyncRunUtc = nextRun;

                            if (_lastSyncWatchedUtc == null || _lastSyncWatchedUtc < lastScheduled)
                            {
                                bool includeRatingsForScheduled = settings?.Automation.ShokoSyncWatchedIncludeRatings ?? false;
                                bool excludeAdminForScheduled = settings?.Automation.ShokoSyncWatchedExcludeAdmin ?? false;
                                Logger.Info(
                                    "Automation: triggering scheduled Plex->Shoko watched-state sync (frequency {0}h, ratings={1}, excludeAdmin={2})",
                                    syncFreq,
                                    includeRatingsForScheduled,
                                    excludeAdminForScheduled
                                );
                                try
                                {
                                    var syncTask = _watchedSyncService.SyncWatchedAsync(false, syncFreq + 1, ct);
                                    await syncTask.ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Automation: Plex watched-state sync failed");
                                }

                                _lastSyncWatchedUtc = lastScheduled;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Automation: error while checking Plex watched-state automation");
                    }

                    // Plex Automation (collections + ratings)
                    DateTime? nextPlexRunUtc = null;
                    try
                    {
                        int plexFreq = settings?.Automation.PlexAutomationFrequencyHours ?? 0;
                        if (plexFreq <= 0 || (_collectionService == null && _criticRatingService == null))
                        {
                            _lastPlexAutomationUtc = null;
                        }
                        else
                        {
                            var (lastScheduled, nextRun) = ComputeSchedule(now, offset, plexFreq);
                            nextPlexRunUtc = nextRun;

                            if (_lastPlexAutomationUtc == null || _lastPlexAutomationUtc < lastScheduled)
                            {
                                Logger.Info("Automation: triggering scheduled Plex automation (frequency {0}h)", plexFreq);
                                try
                                {
                                    // attempt to enumerate series; if this throws or yields no results, the server likely isn't ready yet.
                                    List<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?> allSeries;
                                    try
                                    {
                                        allSeries =
                                            _metadataService.GetAllShokoSeries()?.Cast<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>().ToList() ?? new List<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>();
                                    }
                                    catch (Exception innerEx)
                                    {
                                        Logger.Warn(innerEx, "Automation: metadata service not ready, skipping Plex automation");
                                        allSeries = new List<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>();
                                    }

                                    if (allSeries.Count == 0)
                                    {
                                        Logger.Info("Automation: no series returned, deferring Plex automation");
                                    }
                                    else
                                    {
                                        if (_collectionService != null)
                                        {
                                            await _collectionService.BuildCollectionsAsync(allSeries, ct).ConfigureAwait(false);
                                        }
                                        if (_criticRatingService != null)
                                        {
                                            await _criticRatingService.ApplyRatingsAsync(null, ct).ConfigureAwait(false);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Automation: Plex automation run failed");
                                }

                                _lastPlexAutomationUtc = lastScheduled;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Automation: error while checking Plex automation");
                    }

                    // Sleep until next scheduled automation (pick the nearest of import/watch-sync/plex)
                    DateTime? earliestNext = null;
                    if (nextImportRunUtc.HasValue)
                        earliestNext = nextImportRunUtc;
                    if (nextWatchedSyncRunUtc.HasValue && (!earliestNext.HasValue || nextWatchedSyncRunUtc.Value < earliestNext.Value))
                        earliestNext = nextWatchedSyncRunUtc;
                    if (nextPlexRunUtc.HasValue && (!earliestNext.HasValue || nextPlexRunUtc.Value < earliestNext.Value))
                        earliestNext = nextPlexRunUtc;

                    double delayMs;
                    if (earliestNext.HasValue)
                    {
                        delayMs = (earliestNext.Value - DateTime.UtcNow).TotalMilliseconds;
                        if (delayMs < 0)
                            delayMs = TimeSpan.FromMinutes(1).TotalMilliseconds; // fallback
                    }
                    else
                    {
                        // No scheduled jobs — poll in 1 minute
                        delayMs = TimeSpan.FromMinutes(1).TotalMilliseconds;
                    }

                    var waitMs = Math.Min(delayMs, TimeSpan.FromMinutes(5).TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
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
        finally
        {
            Logger.Info("Automation scheduler stopped.");
        }
    }
}
