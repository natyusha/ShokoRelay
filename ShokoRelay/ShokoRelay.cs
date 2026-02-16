using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Services;
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
            serviceCollection.AddSingleton(provider => new Services.WatchedSyncService(
                provider.GetRequiredService<PlexClient>(),
                provider.GetRequiredService<IMetadataService>(),
                provider.GetRequiredService<IUserDataService>(),
                provider.GetRequiredService<IUserService>(),
                provider.GetRequiredService<IVideoService>(),
                provider.GetRequiredService<ConfigProvider>(),
                provider.GetRequiredService<PlexAuth>()
            ));

            // Shoko v3 import trigger service (calls /api/v3/ImportFolder and Scan)
            serviceCollection.AddSingleton<Services.ShokoImportService>();
        }
    }

    public class ShokoRelay : IPlugin
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string Name => ShokoRelayInfo.Name;

        private static ConfigProvider? _configProvider;
        public static RelayConfig Settings => _configProvider?.GetSettings() ?? new RelayConfig();

        private readonly VfsWatcher _watcher;
        private readonly Services.WatchedSyncService? _watchedSyncService;
        private readonly Services.ShokoImportService? _shokoImportService;
        private CancellationTokenSource? _automationCts;
        private Task? _automationTask;
        private DateTime? _lastImportRunUtc;
        private DateTime? _lastSyncWatchedUtc;

        public ShokoRelay(
            IApplicationPaths applicationPaths,
            IHttpContextAccessor httpContextAccessor,
            VfsWatcher watcher,
            Services.WatchedSyncService? watchedSyncService = null,
            Services.ShokoImportService? shokoImportService = null
        )
        {
            _configProvider = new ConfigProvider(applicationPaths);

            ImageHelper.HttpContextAccessor = httpContextAccessor;

            _watcher = watcher;
            _watchedSyncService = watchedSyncService;
            _shokoImportService = shokoImportService;

            Logger.Info($"ShokoRelay v{ShokoRelayInfo.Version} loaded.");
        }

        public void Load()
        {
            _watcher.Start();
            Logger.Info("Relay loaded with VFS auto-refresh.");

            // Start automation scheduler loop (reads RelayConfig.ShokoImportFrequencyHours)
            _automationCts = new CancellationTokenSource();
            _automationTask = Task.Run(() => AutomationLoop(_automationCts.Token));
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
                        int freqHours = settings?.ShokoImportFrequencyHours ?? 0;

                        if (freqHours <= 0)
                        {
                            // disabled — reset last-run marker and sleep
                            _lastImportRunUtc = null;
                            await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        if (_lastImportRunUtc == null)
                        {
                            // First-time: set marker so we run after the configured interval (not immediately)
                            _lastImportRunUtc = now;
                        }

                        var nextRun = _lastImportRunUtc.Value.AddHours(freqHours);
                        if (now >= nextRun)
                        {
                            Logger.Info("Automation: triggering scheduled Shoko import (frequency {0}h)", freqHours);

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
                        }

                        // --- Sync watched-state automation ---
                        try
                        {
                            int syncFreq = settings?.ShokoSyncWatchedFrequencyHours ?? 0;
                            bool syncEnabled = settings?.SyncPlexWatched ?? false;

                            if (syncFreq <= 0 || !syncEnabled || _watchedSyncService == null)
                            {
                                // disabled — reset marker
                                _lastSyncWatchedUtc = null;
                            }
                            else
                            {
                                if (_lastSyncWatchedUtc == null)
                                {
                                    // First-time: set marker so we run after the configured interval (not immediately)
                                    _lastSyncWatchedUtc = now;
                                }

                                var nextSync = _lastSyncWatchedUtc.Value.AddHours(syncFreq);
                                if (now >= nextSync)
                                {
                                    Logger.Info("Automation: triggering scheduled Plex->Shoko watched-state sync (frequency {0}h)", syncFreq);
                                    try
                                    {
                                        // Apply lookback equal to the configured frequency (hours) so automation only examines recently-watched items
                                        var syncTask = _watchedSyncService.SyncWatchedAsync(false, syncFreq, ct);
                                        await syncTask.ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Warn(ex, "Automation: Plex watched-state sync failed");
                                    }

                                    _lastSyncWatchedUtc = DateTime.UtcNow;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Automation: error while checking Plex watched-state automation");
                        }

                        // Sleep until next check (wake up sooner if nextRun is near)
                        var delay = (nextRun - DateTime.UtcNow).TotalMilliseconds;
                        if (delay < 0)
                            delay = TimeSpan.FromMinutes(1).TotalMilliseconds; // fallback
                        var wait = Math.Min(delay, TimeSpan.FromMinutes(5).TotalMilliseconds);
                        await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
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

        public void OnSettingsLoaded(IPluginSettings settings) { }
    }
}
