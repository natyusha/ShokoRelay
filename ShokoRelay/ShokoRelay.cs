using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
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
            serviceCollection.AddSingleton(applicationPaths);
            serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
            serviceCollection.AddSingleton(provider => new AnimeThemesGenerator(provider.GetRequiredService<IVideoService>(), applicationPaths));
            serviceCollection.AddSingleton(provider => new AnimeThemesMapping(provider.GetRequiredService<IMetadataService>(), applicationPaths));
            serviceCollection.AddSingleton(provider => new PlexMetadata(provider.GetRequiredService<IMetadataService>()));
            // Use HttpClient instances with CookieContainer so Plex /myplex switch flows that set cookies work correctly.
            var handlerWithCookies = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
            serviceCollection.AddSingleton(provider => new PlexAuth(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<ConfigProvider>().GetSettings().PlexAuth));
            serviceCollection.AddSingleton(provider => new PlexClient(new HttpClient(handlerWithCookies, disposeHandler: false), provider.GetRequiredService<ConfigProvider>()));
            serviceCollection.AddSingleton(provider => new PlexCollections(
                new HttpClient(handlerWithCookies, disposeHandler: false),
                provider.GetRequiredService<ConfigProvider>(),
                provider.GetRequiredService<PlexClient>()
            ));
            serviceCollection.AddSingleton<Services.ICollectionManager, Services.CollectionManager>();
            serviceCollection.AddSingleton<VfsBuilder>();
            serviceCollection.AddSingleton<VfsWatcher>();

            // Watched-state sync service (Plex -> Shoko)
            serviceCollection.AddSingleton(provider => new Sync.SyncToShoko(
                provider.GetRequiredService<PlexClient>(),
                provider.GetRequiredService<IMetadataService>(),
                provider.GetRequiredService<IUserDataService>(),
                provider.GetRequiredService<IUserService>(),
                provider.GetRequiredService<IVideoService>(),
                provider.GetRequiredService<ConfigProvider>(),
                provider.GetRequiredService<PlexAuth>()
            ));

            // Watched-state export service (Plex <- Shoko)
            serviceCollection.AddSingleton(provider => new Sync.SyncToPlex(
                provider.GetRequiredService<PlexClient>(),
                provider.GetRequiredService<IMetadataService>(),
                provider.GetRequiredService<IUserDataService>(),
                provider.GetRequiredService<IUserService>(),
                provider.GetRequiredService<ConfigProvider>()
            ));

            // Shoko v3 import trigger service (calls /api/v3/ImportFolder and Scan)
            serviceCollection.AddSingleton<Services.ShokoImportService>();

            // Run the plugin runtime as a hosted service (starts VFS watcher + automation loop)
            serviceCollection.AddHostedService<ShokoRelay>();
        }
    }

    // Minimal plugin metadata for new abstractions
    public class Plugin : IPlugin
    {
        public Guid ID => new Guid("2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a");
        public string Name => ShokoRelayInfo.Name;
        public string? Description => "A Custom Metadata Provider and Automation Tools for Plex in the form of a Shoko Server plugin";
        public string? EmbeddedThumbnailResourceName => "ShokoRelay.dashboard.img.shoko-relay-thumbnail.png";
    }

    // Hosted service that runs the VFS watcher and automation loop
    public class ShokoRelay : BackgroundService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static ConfigProvider? _configProvider;
        public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

        private readonly VfsWatcher _watcher;
        private readonly Sync.SyncToShoko? _watchedSyncService;
        private readonly Services.ShokoImportService? _shokoImportService;
        private static DateTime? _lastImportRunUtc;
        private static DateTime? _lastSyncWatchedUtc;

        public static void MarkImportRunNow() => _lastImportRunUtc = DateTime.UtcNow;

        public static void MarkSyncRunNow() => _lastSyncWatchedUtc = DateTime.UtcNow;

        public ShokoRelay(
            IApplicationPaths applicationPaths,
            IHttpContextAccessor httpContextAccessor,
            VfsWatcher watcher,
            ConfigProvider configProvider,
            Sync.SyncToShoko? watchedSyncService = null,
            Services.ShokoImportService? shokoImportService = null
        )
        {
            _configProvider = configProvider;

            ImageHelper.HttpContextAccessor = httpContextAccessor;

            _watcher = watcher;
            _watchedSyncService = watchedSyncService;
            _shokoImportService = shokoImportService;

            Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} initialized.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Start watcher immediately
            _watcher.Start();
            Logger.Info("Relay started with VFS auto-refresh.");

            // Run automation loop until cancellation
            await AutomationLoop(stoppingToken).ConfigureAwait(false);

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

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.Info("ShokoRelay stopping...");
            _watcher.Stop();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
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

                        // --- scheduled Shoko import (independent of watched-state automation) ---
                        int importFreqHours = settings?.ShokoImportFrequencyHours ?? 0;
                        DateTime? nextImportRunUtc = null;

                        var now = DateTime.UtcNow;

                        if (importFreqHours <= 0)
                        {
                            // disabled — clear marker but DO NOT short-circuit the loop (allow other automations to run)
                            _lastImportRunUtc = null;
                        }
                        else
                        {
                            if (_lastImportRunUtc == null)
                            {
                                // First-time after startup: do NOT run immediately — start the interval from startup.
                                // Set last-run == now so the first scheduled run occurs after the configured interval.
                                _lastImportRunUtc = now;
                            }

                            nextImportRunUtc = _lastImportRunUtc.Value.AddHours(importFreqHours);
                            if (now >= nextImportRunUtc.Value)
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

                                _lastImportRunUtc = DateTime.UtcNow;
                                nextImportRunUtc = _lastImportRunUtc.Value.AddHours(importFreqHours);
                            }
                        }

                        // --- Sync watched-state automation ---
                        DateTime? nextWatchedSyncRunUtc = null;
                        try
                        {
                            int syncFreq = settings?.ShokoSyncWatchedFrequencyHours ?? 0;

                            // Automatic watched-state sync is controlled *only* by the configured interval.
                            // If interval <= 0 the automation is disabled (same behaviour as Shoko import).
                            if (syncFreq <= 0 || _watchedSyncService == null)
                            {
                                // disabled — reset marker
                                _lastSyncWatchedUtc = null;
                            }
                            else
                            {
                                if (_lastSyncWatchedUtc == null)
                                {
                                    // First-time after startup: do NOT run immediately — start the sync interval from startup.
                                    // Set last-run == now so the first scheduled sync occurs after the configured interval.
                                    _lastSyncWatchedUtc = now;
                                }

                                nextWatchedSyncRunUtc = _lastSyncWatchedUtc.Value.AddHours(syncFreq);
                                if (now >= nextWatchedSyncRunUtc.Value)
                                {
                                    bool includeRatingsForScheduled = settings?.ShokoSyncWatchedIncludeRatings ?? false;
                                    Logger.Info("Automation: triggering scheduled Plex->Shoko watched-state sync (frequency {0}h, ratings={1})", syncFreq, includeRatingsForScheduled);
                                    try
                                    {
                                        // Apply lookback equal to the configured frequency (hours) so automation only examines recently-watched items
                                        var syncTask = _watchedSyncService.SyncWatchedAsync(false, syncFreq, includeRatingsForScheduled, ct);
                                        await syncTask.ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Warn(ex, "Automation: Plex watched-state sync failed");
                                    }

                                    _lastSyncWatchedUtc = DateTime.UtcNow;
                                    nextWatchedSyncRunUtc = _lastSyncWatchedUtc.Value.AddHours(syncFreq);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Automation: error while checking Plex watched-state automation");
                        }

                        // Sleep until next scheduled automation (pick the nearest of import/watch-sync)
                        DateTime? earliestNext = null;
                        if (nextImportRunUtc.HasValue)
                            earliestNext = nextImportRunUtc;
                        if (nextWatchedSyncRunUtc.HasValue && (!earliestNext.HasValue || nextWatchedSyncRunUtc.Value < earliestNext.Value))
                            earliestNext = nextWatchedSyncRunUtc;

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
}
