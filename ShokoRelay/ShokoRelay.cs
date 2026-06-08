using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.Video;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
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

        string clientName = ShokoRelayConstants.Name.Replace(" ", "");
        serviceCollection
            .AddHttpClient(clientName, client => client.DefaultRequestHeaders.Add("User-Agent", $"{clientName}/{ShokoRelayConstants.Version}"))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    UseCookies = true,
                    AllowAutoRedirect = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );

        serviceCollection.AddSingleton(provider => provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
        serviceCollection.AddSingleton(new ConfigProvider(applicationPaths));
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
            return new FfmpegService(cp.PluginDirectory, applicationPaths.ApplicationPath, applicationPaths.DataPath);
        });
        serviceCollection.AddSingleton<SyncToShoko>();
        serviceCollection.AddSingleton<SyncToPlex>();
        serviceCollection.AddSingleton<IManagedFolderIgnoreRule, VfsIgnoreRule>();
        serviceCollection.AddSingleton(provider =>
        {
            var cp = provider.GetRequiredService<ConfigProvider>();
            var plexAuthConfig = new PlexAuthConfig { ClientIdentifier = cp.GetPlexClientIdentifier() };
            return new PlexAuth(provider.GetRequiredService<HttpClient>(), plexAuthConfig);
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
    /// <summary>Unique plugin ID.</summary>
    public Guid ID => new(ShokoRelayConstants.PluginId);

    /// <summary>Plugin display name.</summary>
    public string Name => ShokoRelayConstants.Name;

    /// <summary>Plugin description.</summary>
    public string? Description => ShokoRelayConstants.Description;

    /// <summary>Plugin thumbnail resource.</summary>
    public string? EmbeddedThumbnailResourceName => "ShokoRelay.Assets.shoko-relay-logo.png";

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

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static ConfigProvider? s_configProvider;

    /// <summary>Access current plugin settings.</summary>
    public static RelayConfig Settings => s_configProvider?.GetSettings() ?? new RelayConfig();

    /// <summary>Access the Shoko server base URL.</summary>
    public static string ServerBaseUrl => s_configProvider?.ServerBaseUrl ?? "http://localhost:8111";

    /// <summary>Access the plugin config directory.</summary>
    public static string ConfigDirectory => s_configProvider?.ConfigDirectory ?? string.Empty;

    private readonly VfsWatcher _watcher;
    private readonly ISystemService _systemService;
    private readonly SyncToShoko? _watchedSyncService;
    private readonly IShokoImportService? _shokoImportService;
    private readonly ICollectionService? _collectionService;
    private readonly ICriticRatingService? _criticRatingService;
    private readonly IMetadataService _metadataService;
    private readonly IImageSyncService? _imageSyncService;

    private static DateTime? s_lastImportRunUtc;
    private static DateTime? s_lastPlexAutomationUtc;
    private static DateTime? s_lastSyncWatchedUtc;

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
    /// <param name="watchedSyncService">Service for syncing watched states to Shoko.</param>
    /// <param name="shokoImportService">Service for triggering server-side imports.</param>
    /// <param name="collectionService">Service for managing Plex collections.</param>
    /// <param name="criticRatingService">Service for applying audience ratings to Plex.</param>
    /// <param name="imageSyncService">Service for syncing thumbnails from Plex to Shoko.</param>
    public ShokoRelay(
        VfsWatcher watcher,
        ConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        ISystemService systemService,
        IMetadataService metadataService,
        SyncToShoko? watchedSyncService = null,
        IShokoImportService? shokoImportService = null,
        ICollectionService? collectionService = null,
        ICriticRatingService? criticRatingService = null,
        IImageSyncService? imageSyncService = null
    )
    {
        _watcher = watcher;
        s_configProvider = configProvider;
        s_configProvider.HttpContextAccessor = httpContextAccessor;
        _systemService = systemService;
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _watchedSyncService = watchedSyncService;
        _shokoImportService = shokoImportService;
        _collectionService = collectionService;
        _criticRatingService = criticRatingService;
        _imageSyncService = imageSyncService;
        s_logger.Info($"ShokoRelay v{ShokoRelayConstants.Version} initialized");
    }

    #endregion

    #region Background Service

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            s_logger.Info("Relay waiting for Shoko Server to reach 'Started' state...");
            while (!_systemService.IsStarted && !stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                return;
            s_logger.Info("Shoko Server started -> Caching overrides & initializing scheduling anchors...");
            OverrideHelper.Reload(_metadataService); // Warm up the VFS override cache.
            var now = DateTime.UtcNow;
            int offset = Math.Clamp(Settings.Automation.UtcOffsetHours, -12, 14);
            if (Settings.Automation.ShokoImportFrequencyHours > 0)
                s_lastImportRunUtc = ComputeSchedule(now, offset, Settings.Automation.ShokoImportFrequencyHours).LastScheduled;
            if (Settings.Automation.ShokoSyncWatchedFrequencyHours > 0)
                s_lastSyncWatchedUtc = ComputeSchedule(now, offset, Settings.Automation.ShokoSyncWatchedFrequencyHours).LastScheduled;
            if (Settings.Automation.PlexAutomationFrequencyHours > 0)
                s_lastPlexAutomationUtc = ComputeSchedule(now, offset, Settings.Automation.PlexAutomationFrequencyHours).LastScheduled;

            _watcher.Start();
            s_logger.Info("Relay started -> Entering automation loop");
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
        s_logger.Info("Relay stopping...");
        _watcher.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Automation Schedule

    /// <summary>Manually mark import as run.</summary>
    public static void MarkImportRunNow() => s_lastImportRunUtc = DateTime.UtcNow;

    /// <summary>Manually mark automation as run.</summary>
    public static void MarkPlexAutomationRunNow() => s_lastPlexAutomationUtc = DateTime.UtcNow;

    /// <summary>Manually mark sync as run.</summary>
    public static void MarkSyncRunNow() => s_lastSyncWatchedUtc = DateTime.UtcNow;

    /// <summary>Computes the last scheduled time and the next scheduled time for a task based on UTC offsets.</summary>
    /// <param name="now">Current time context.</param>
    /// <param name="offsetHours">The anchor offset from UTC midnight.</param>
    /// <param name="frequencyHours">How often the task should run.</param>
    /// <returns>A tuple containing the LastScheduled and NextRun timestamps.</returns>
    private static (DateTime LastScheduled, DateTime NextRun) ComputeSchedule(DateTime now, int offsetHours, int frequencyHours)
    {
        DateTime anchor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(offsetHours);
        if (anchor > now)
            anchor = anchor.AddDays(-1);
        double periods = Math.Floor((now - anchor).TotalHours / frequencyHours);
        DateTime lastScheduled = anchor.AddHours(Math.Max(0, periods) * frequencyHours);
        return (lastScheduled, lastScheduled.AddHours(frequencyHours));
    }

    /// <summary>The main automation loop that evaluates schedules and triggers background tasks.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the long-running loop.</returns>
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
                    if (s_lastImportRunUtc == null || s_lastImportRunUtc < lastSched)
                    {
                        s_logger.Info("Automation: triggering scheduled Shoko import ({0}h)", importFreq);
                        await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
                        s_lastImportRunUtc = lastSched;
                    }
                }
                int syncFreq = settings.Automation.ShokoSyncWatchedFrequencyHours;
                if (syncFreq > 0 && _watchedSyncService != null)
                {
                    var (lastSched, next) = ComputeSchedule(now, offset, syncFreq);
                    nextRuns.Add(next);
                    if (s_lastSyncWatchedUtc == null || s_lastSyncWatchedUtc < lastSched)
                    {
                        s_logger.Info("Automation: triggering scheduled Plex->Shoko sync ({0}h)", syncFreq);

                        // Background tasks should wait for the lock to become available
                        await SyncHelper.SyncLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await _watchedSyncService.SyncWatchedAsync(false, syncFreq + 1, cancellationToken: ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            SyncHelper.SyncLock.Release();
                        }
                        s_lastSyncWatchedUtc = lastSched;
                    }
                }
                int plexFreq = settings.Automation.PlexAutomationFrequencyHours;
                if (plexFreq > 0 && (_collectionService != null || _criticRatingService != null))
                {
                    var (lastSched, next) = ComputeSchedule(now, offset, plexFreq);
                    nextRuns.Add(next);
                    if (s_lastPlexAutomationUtc == null || s_lastPlexAutomationUtc < lastSched)
                    {
                        s_logger.Info("Automation: triggering scheduled Plex Collection/Rating update ({0}h)", plexFreq);
                        var allSeries = _metadataService.GetAllShokoSeries()?.Cast<IShokoSeries?>().ToList();
                        if (allSeries?.Count > 0)
                        {
                            if (_collectionService != null)
                                await _collectionService.BuildCollectionsAsync(allSeries, cancellationToken: ct).ConfigureAwait(false);
                            if (_criticRatingService != null)
                                await _criticRatingService.ApplyRatingsAsync(null, ct).ConfigureAwait(false);
                            if (settings.Advanced.EnableImageSync && _imageSyncService != null)
                                await _imageSyncService.SyncImagesAsync(cancellationToken: ct).ConfigureAwait(false);
                        }
                        s_lastPlexAutomationUtc = lastSched;
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
                s_logger.Warn(ex, "Automation: loop error");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }
    }

    #endregion
}
