using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Vfs;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Controllers
{
    [ApiVersionNeutral]
    [ApiController]
    [Route("/api/plugin/ShokoRelay")]
    public partial class ShokoRelayController : ControllerBase
    {
        private readonly IVideoService _videoService;
        private readonly IMetadataService _metadataService;
        private readonly PlexMetadata _mapper;
        private readonly VfsBuilder _vfsBuilder;
        private readonly AnimeThemesGenerator _animeThemesGenerator;
        private readonly AnimeThemesMapping _animeThemesMapping;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ConfigProvider _configProvider;
        private readonly PlexAuth _plexAuth;
        private readonly PlexClient _plexLibrary;
        private readonly Services.ICollectionManager _collectionManager;
        private readonly Services.WatchedSyncService _watchedSyncService;
        private readonly IUserDataService _userDataService;
        private readonly IUserService _userService;
        private readonly Services.ShokoImportService _shokoImportService;

        private const string ControllerPageFileName = "index.cshtml";

        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        private const string SeasonPrefix = PlexConstants.SeasonPrefix;
        private const string EpisodePrefix = PlexConstants.EpisodePrefix;
        private const string PartPrefix = PlexConstants.PartPrefix;

        public ShokoRelayController(
            IVideoService videoService,
            IMetadataService metadataService,
            PlexMetadata mapper,
            VfsBuilder vfsBuilder,
            AnimeThemesGenerator animeThemeGenerator,
            AnimeThemesMapping animeThemesMapping,
            IApplicationPaths applicationPaths,
            ConfigProvider configProvider,
            PlexAuth plexAuth,
            PlexClient plexLibrary,
            Services.ICollectionManager collectionManager,
            Services.WatchedSyncService watchedSyncService,
            IUserDataService userDataService,
            IUserService userService,
            Services.ShokoImportService shokoImportService
        )
        {
            _videoService = videoService;
            _metadataService = metadataService;
            _mapper = mapper;
            _vfsBuilder = vfsBuilder;
            _animeThemesGenerator = animeThemeGenerator;
            _animeThemesMapping = animeThemesMapping;
            _applicationPaths = applicationPaths;
            _configProvider = configProvider;
            _plexAuth = plexAuth;
            _plexLibrary = plexLibrary;
            _collectionManager = collectionManager;
            _watchedSyncService = watchedSyncService;
            _userDataService = userDataService;
            _userService = userService;
            _shokoImportService = shokoImportService;
        }

        [HttpGet("dashboard/{*path}")]
        public IActionResult GetControllerPage([FromRoute] string? path = null)
        {
            // Serve only from the plugin folder under PluginsPath/<PluginSubfolder>/dashboard
            string dashboardDir = Path.Combine(_applicationPaths.PluginsPath, ConfigConstants.PluginSubfolder, ConfigConstants.DashboardSubfolder);

            if (!Directory.Exists(dashboardDir))
                return NotFound("Dashboard index not found.");

            // If a subpath is requested, serve only allowed asset types from the plugin dashboard folder.
            if (!string.IsNullOrWhiteSpace(path))
            {
                // Normalize separators and prevent path traversal
                string safePath = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string requested = Path.GetFullPath(Path.Combine(dashboardDir, safePath));
                string dashboardFull = Path.GetFullPath(dashboardDir);
                if (!requested.StartsWith(dashboardFull, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(requested))
                    return NotFound();

                string ext = Path.GetExtension(requested).ToLowerInvariant();
                string? contentType = GetDashboardContentTypeForExtension(ext);

                if (string.IsNullOrEmpty(contentType))
                    return NotFound();

                return PhysicalFile(requested, contentType);
            }

            // No path: serve the dashboard index directly from the plugin dashboard folder.
            string indexPath = Path.Combine(dashboardDir, ControllerPageFileName);
            if (System.IO.File.Exists(indexPath))
            {
                var indexHtml = System.IO.File.ReadAllText(indexPath);
                // Ensure relative asset URLs (fonts/, img/) resolve under the dashboard route by inserting a base href.
                if (indexHtml.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var baseHref = (Request.Path.Value ?? "/").TrimEnd('/') + "/";
                    var baseTag = $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">";
                    indexHtml = indexHtml.Replace("<head>", "<head>\n    " + baseTag, StringComparison.OrdinalIgnoreCase);
                }
                return Content(indexHtml, "text/html");
            }

            return NotFound("Dashboard index not found.");
        }

        [Route("matches")]
        [HttpPost]
        [HttpGet]
        public IActionResult Match([FromQuery] string? name, [FromBody] PlexMatchBody? body = null)
        {
            string? rawPath = name ?? body?.Filename;
            if (string.IsNullOrWhiteSpace(rawPath))
                return EmptyMatch();

            int? fileId = TextHelper.ExtractFileId(rawPath);
            if (!fileId.HasValue)
                return EmptyMatch();

            var video = _videoService.GetVideoByID(fileId.Value);
            var series = video?.Series?.FirstOrDefault();

            if (series == null)
                return EmptyMatch();

            var poster = (series as IWithImages)?.GetImages(ImageEntityType.Poster).FirstOrDefault();

            return Ok(
                new
                {
                    MediaContainer = new
                    {
                        size = 1,
                        identifier = ShokoRelayInfo.AgentScheme,
                        Metadata = new[]
                        {
                            new
                            {
                                guid = _mapper.GetGuid("show", series.ID),
                                title = series.PreferredTitle?.Value,
                                year = series.AirDate?.Year,
                                score = 100,
                                thumb = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                            },
                        },
                    },
                }
            );
        }

        [HttpGet("debug/file/{fileId:int}")]
        public IActionResult DebugFileByFileId([FromRoute] int fileId)
        {
            var video = _videoService.GetVideoByID(fileId);
            if (video == null)
                return NotFound(new { status = "error", message = $"File/video id {fileId} not found." });

            var series = video.Series?.FirstOrDefault();
            if (series == null)
                return NotFound(new { status = "error", message = $"Series for file id {fileId} not found." });

            var debug = MapHelper.GetFileMappingDebug(series, video.ID);
            if (debug == null)
                return NotFound(new { status = "error", message = "No mapping/debug information available for this file." });

            string fileName = System.IO.Path.GetFileName(video.Files.FirstOrDefault()?.Path ?? "");
            return Ok(
                new
                {
                    status = "ok",
                    fileId = video.ID,
                    fileName,
                    data = debug,
                }
            );
        }

        [HttpGet("debug/series/{shokoSeriesId:int}")]
        public IActionResult DebugSeries([FromRoute] int shokoSeriesId)
        {
            var series = _metadataService.GetShokoSeriesByID(shokoSeriesId);
            if (series == null)
                return NotFound(new { status = "error", message = $"Series id {shokoSeriesId} not found." });

            // Gather debug for every unique file that belongs to this series, grouped by episode
            var episodes = series.Episodes.OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.EpisodeNumber).ToList();
            var seenVideoIds = new HashSet<int>();
            var episodeResults = new List<object>();
            var uniqueFiles = new List<object>();

            foreach (var ep in episodes)
            {
                var filesForEpisode = new List<object>();
                if (ep.VideoList != null)
                {
                    foreach (var v in ep.VideoList)
                    {
                        var dbg = MapHelper.GetFileMappingDebug(series, v.ID);
                        if (dbg != null)
                        {
                            filesForEpisode.Add(dbg);

                            if (seenVideoIds.Add(v.ID))
                                uniqueFiles.Add(dbg);
                        }
                    }
                }

                episodeResults.Add(
                    new
                    {
                        episodeId = ep.ID,
                        episodeTitle = ep.PreferredTitle?.Value,
                        files = filesForEpisode,
                    }
                );
            }

            return Ok(
                new
                {
                    status = "ok",
                    seriesId = shokoSeriesId,
                    seriesTitle = series.PreferredTitle?.Value,
                    episodes = episodeResults,
                    files = uniqueFiles,
                }
            );
        }

        [HttpGet]
        public IActionResult GetMediaProvider()
        {
            var supportedTypes = new[]
            {
                PlexConstants.TypeShow,
                PlexConstants.TypeSeason,
                PlexConstants.TypeEpisode,
                //PlexConstants.TypeCollection // Temporarily advertise only show/season/episode to avoid unsupported metadata type warnings in Plex.
            };

            var typePayload = supportedTypes.Select(t => new { type = t, Scheme = new[] { new { scheme = ShokoRelayInfo.AgentScheme } } });

            var featurePayload = new[] { new { type = "metadata", key = "/metadata" }, new { type = "match", key = "/matches" }, new { type = "collection", key = "/collections" } };

            return Ok(
                new
                {
                    MediaProvider = new
                    {
                        identifier = ShokoRelayInfo.AgentScheme,
                        title = ShokoRelayInfo.Name,
                        version = ShokoRelayInfo.Version,
                        Types = typePayload,
                        Feature = featurePayload,
                    },
                }
            );
        }

        [HttpGet("plexauth")]
        public async Task<IActionResult> StartPlexAuth(CancellationToken cancellationToken = default)
        {
            EnsurePlexAuthConfig();

            try
            {
                PlexPinResponse pin = await _plexAuth.CreatePinAsync(true, cancellationToken);
                if (string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Code))
                {
                    return StatusCode(502, new { status = "error", message = "Plex pin response missing id/code." });
                }

                string statusUrl = $"{BaseUrl}/api/plugin/ShokoRelay/plexauth/status?pinId={Uri.EscapeDataString(pin.Id)}";
                string authUrl = _plexAuth.BuildAuthUrl(pin.Code, ShokoRelayInfo.Name, statusUrl);
                return Ok(
                    new
                    {
                        status = "ok",
                        pinId = pin.Id,
                        code = pin.Code,
                        authUrl,
                        statusUrl,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, new { status = "error", message = ex.Message });
            }
        }

        [HttpGet("plexauth/status")]
        public async Task<IActionResult> GetPlexAuthStatus([FromQuery] string pinId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pinId))
                return BadRequest(new { status = "error", message = "pinId is required" });
            try
            {
                var pin = await _plexAuth.GetPinAsync(pinId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pin.AuthToken))
                    return Ok(new { status = "pending" });

                var settings = _configProvider.GetSettings();
                settings.PlexLibrary.Token = pin.AuthToken;

                try
                {
                    // Ensure we have a client identifier and then discover servers/libraries
                    string clientIdentifier = EnsurePlexClientIdentifier(settings);

                    try
                    {
                        var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(settings.PlexLibrary.Token, clientIdentifier, cancellationToken).ConfigureAwait(false);

                        if (discovery.TokenValid && discovery.Servers?.Count > 0)
                        {
                            settings.PlexLibrary.DiscoveredServers = discovery
                                .Servers.Select(s => new PlexAvailableServer
                                {
                                    Id = s.Id,
                                    Name = s.Name,
                                    PreferredUri = s.PreferredUri ?? string.Empty,
                                })
                                .ToList();
                        }

                        // Deduplicate & map discovered libraries via helper
                        settings.PlexLibrary.DiscoveredLibraries = CollectDiscoveredLibraries(discovery.ShokoLibraries);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Plex discovery failed: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Plex discovery failed: {ex.Message}");
                }

                _configProvider.SaveSettings(settings);

                return Ok(new { status = "ok", tokenSaved = true });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, new { status = "error", message = ex.Message });
            }
        }

        [HttpPost("plex/unlink")]
        public async Task<IActionResult> UnlinkPlex(CancellationToken cancellationToken = default)
        {
            var settings = _configProvider.GetSettings();
            if (string.IsNullOrWhiteSpace(settings.PlexLibrary.Token))
                return Ok(new { status = "ok" });

            string clientIdentifier = EnsurePlexClientIdentifier(settings);
            await _plexAuth.RevokePlexTokenAsync(settings.PlexLibrary.Token, clientIdentifier, cancellationToken).ConfigureAwait(false);

            settings.PlexLibrary.Token = string.Empty;
            settings.PlexLibrary.ServerUrl = string.Empty;
            settings.PlexLibrary.ServerIdentifier = string.Empty;
            settings.PlexLibrary.SelectedLibraries.Clear();
            settings.PlexLibrary.LibrarySectionId = 0;
            settings.PlexLibrary.SelectedLibraryName = string.Empty;
            settings.PlexLibrary.SectionUuid = string.Empty;
            _configProvider.SaveSettings(settings);
            _configProvider.DeleteTokenFile(); // Delete the token file to remove persisted token and discovered data

            return Ok(new { status = "ok" });
        }

        [HttpPost("plex/libraries/refresh")]
        public async Task<IActionResult> RefreshPlexLibraries(CancellationToken cancellationToken = default)
        {
            var settings = _configProvider.GetSettings();
            if (string.IsNullOrWhiteSpace(settings.PlexLibrary.Token))
                return Unauthorized(new { status = "error", message = "Plex token is missing." });

            string clientIdentifier = EnsurePlexClientIdentifier(settings);

            try
            {
                var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(settings.PlexLibrary.Token, clientIdentifier, cancellationToken).ConfigureAwait(false);

                if (discovery.TokenValid && discovery.Servers?.Count > 0)
                {
                    settings.PlexLibrary.DiscoveredServers = discovery
                        .Servers.Select(s => new PlexAvailableServer
                        {
                            Id = s.Id,
                            Name = s.Name,
                            PreferredUri = s.PreferredUri ?? string.Empty,
                        })
                        .ToList();
                }

                // Deduplicate & map discovered libraries via helper
                settings.PlexLibrary.DiscoveredLibraries = CollectDiscoveredLibraries(discovery.ShokoLibraries);
                _configProvider.SaveSettings(settings);

                return Ok(new { status = "ok", libraries = settings.PlexLibrary.DiscoveredLibraries });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to refresh Plex libraries: {ex.Message}");
                return StatusCode(502, new { status = "error", message = "Failed to refresh Plex libraries." });
            }
        }

        // Plugin-level Plex webhook receiver (Auto Scrobble)
        // Accepts Plex's form-encoded `payload` (the JSON payload string) or raw JSON body.
        [HttpPost("plex/webhook")]
        public async Task<IActionResult> PluginPlexWebhook()
        {
            var cfg = _configProvider.GetSettings();
            if (!cfg.AutoScrobble)
                return Ok(new { status = "ignored", reason = "auto_scrobble_disabled" });

            string? payloadJson = null;
            if (Request.HasFormContentType && Request.Form.ContainsKey("payload"))
            {
                payloadJson = Request.Form["payload"].ToString();
            }
            else
            {
                using var sr = new System.IO.StreamReader(Request.Body);
                payloadJson = await sr.ReadToEndAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(payloadJson))
                return BadRequest(new { status = "error", message = "missing payload" });

            PlexWebhookPayload? evt;
            try
            {
                evt = System.Text.Json.JsonSerializer.Deserialize<PlexWebhookPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return BadRequest(new { status = "error", message = "invalid payload" });
            }

            if (evt == null || evt.Metadata == null || !string.Equals(evt.Event, "media.scrobble", System.StringComparison.OrdinalIgnoreCase))
                return Ok(); // only care about scrobble events

            // --- FILTER: only accept scrobbles from admin (token owner) or configured ExtraPlexUsers ---
            var plexUserFromPayload = evt.Account?.Title?.Trim();
            if (string.IsNullOrWhiteSpace(plexUserFromPayload))
                return Ok(new { status = "ignored", reason = "no_plex_user" });

            // Reuse centralized ExtraPlexUsers parsing from ConfigProvider
            var extraEntries = _configProvider.GetExtraPlexUserEntries();
            var allowed = new HashSet<string>(extraEntries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            // Attempt to add admin account title/username (if token present)
            if (!string.IsNullOrWhiteSpace(cfg.PlexLibrary.Token))
            {
                try
                {
                    var acct = await _plexAuth.GetAccountInfoAsync(cfg.PlexLibrary.Token).ConfigureAwait(false);
                    if (acct != null)
                    {
                        if (!string.IsNullOrWhiteSpace(acct.Title))
                            allowed.Add(acct.Title);
                        if (!string.IsNullOrWhiteSpace(acct.Username))
                            allowed.Add(acct.Username);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "PluginPlexWebhook: failed to resolve admin Plex account info");
                }
            }

            // If payload user isn't in allowed list, ignore the scrobble
            if (!allowed.Contains(plexUserFromPayload))
            {
                Logger.Info("Ignored Plex scrobble from user '{User}' (not configured in ExtraPlexUsers nor admin)", plexUserFromPayload);
                return Ok(new { status = "ignored", reason = "plex_user_not_allowed" });
            }

            // Try to extract Shoko episode id from GUID (agent GUID format used by this plugin)
            int? shokoEpisodeId = TryParseShokoEpisodeIdFromGuid(evt.Metadata.Guid);

            if (!shokoEpisodeId.HasValue)
                return Ok(new { status = "ignored", reason = "no_shoko_guid" });

            var shokoEpisode = _metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
            if (shokoEpisode == null)
                return Ok(new { status = "ignored", reason = "episode_not_found" });

            var user = _userService.GetUsers().FirstOrDefault();
            if (user == null)
                return Ok(new { status = "ignored", reason = "no_shoko_user" });

            DateTime? watchedAt = null;
            if (evt.Metadata.LastViewedAt.HasValue && evt.Metadata.LastViewedAt.Value > 0)
            {
                try
                {
                    watchedAt = DateTimeOffset.FromUnixTimeSeconds(evt.Metadata.LastViewedAt.Value).UtcDateTime;
                }
                catch { }
            }

            var saved = await _userDataService.SetEpisodeWatchedStatus(shokoEpisode, user, true, watchedAt, true).ConfigureAwait(false);
            bool updated = saved != null;

            return Ok(new { status = "ok", marked = updated });
        }

        private static int? TryParseShokoEpisodeIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            // Expected form: {agentScheme}://episode/e{episodeId}[p{part}]
            var agent = ShokoRelayInfo.AgentScheme + "://episode/" + Plex.PlexConstants.EpisodePrefix;
            int idx = guid.IndexOf(agent, System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            idx += agent.Length; // position at the first digit of episode id
            if (idx >= guid.Length)
                return null;

            int end = idx;
            while (end < guid.Length && char.IsDigit(guid[end]))
                end++;

            var idStr = guid.Substring(idx, end - idx);
            if (int.TryParse(idStr, out int id))
                return id;

            return null;
        }

        private class PlexWebhookPayload
        {
            [System.Text.Json.Serialization.JsonPropertyName("event")]
            public string? Event { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("Account")]
            public PlexAccount? Account { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("Metadata")]
            public PlexMetadata? Metadata { get; set; }

            public class PlexAccount
            {
                [System.Text.Json.Serialization.JsonPropertyName("title")]
                public string? Title { get; set; }
            }

            public class PlexMetadata
            {
                [System.Text.Json.Serialization.JsonPropertyName("guid")]
                public string? Guid { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("index")]
                public int? Index { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("lastViewedAt")]
                public long? LastViewedAt { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("librarySectionId")]
                public int? LibrarySectionId { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("type")]
                public string? Type { get; set; }
            }
        }

        [HttpGet("config")]
        public IActionResult GetConfig() => Ok(_configProvider.GetSettings());

        [HttpPost("config")]
        public IActionResult SaveConfig([FromBody] RelayConfig config)
        {
            if (config == null)
                return BadRequest(new { status = "error", message = "Config payload is required." });

            _configProvider.SaveSettings(config);
            return Ok(new { status = "ok" });
        }

        [HttpGet("config/schema")]
        public IActionResult GetConfigSchema()
        {
            var props = BuildConfigSchema(typeof(RelayConfig), "");
            return Ok(new { properties = props });
        }

        [HttpGet("plex/collections/build")]
        public async Task<IActionResult> BuildPlexCollections([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
        {
            if (!_plexLibrary.IsEnabled)
            {
                return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });
            }

            var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
            if (validation != null)
                return validation;

            if (seriesId.HasValue && filterIds.Count > 0)
            {
                return BadRequest(new { status = "error", message = "Use either seriesId or filter, not both." });
            }

            var seriesList = ResolveSeriesList(seriesId, filterIds);
            int processed;
            int skipped;
            int created;
            int uploaded;
            int seasonPostersUploaded;
            int errors;
            int deletedEmptyCollections;

            var addedSeries = new HashSet<int>();
            var targets = _plexLibrary.GetConfiguredTargets();

            var createdCollections = new List<object>();
            var errorsList = new List<string>();

            if (targets == null || targets.Count == 0)
                return NoPlexTargetsResponse(seriesList);

            // Delegate to CollectionManager for per-target collection building and poster application
            var managerResult = await _collectionManager.BuildCollectionsAsync(seriesList, cancellationToken).ConfigureAwait(false);

            // Map service result back into controller response variables
            processed = managerResult.Processed;
            created = managerResult.Created;
            uploaded = managerResult.Uploaded;
            seasonPostersUploaded = managerResult.SeasonPostersUploaded;
            errors = managerResult.Errors;
            deletedEmptyCollections = managerResult.DeletedEmptyCollections;
            createdCollections = managerResult.CreatedCollections;
            errorsList.AddRange(managerResult.ErrorsList);

            // Use manager-provided skipped count
            skipped = managerResult.Skipped;

            return Ok(
                new
                {
                    status = "ok",
                    processed,
                    created,
                    uploaded,
                    seasonPostersUploaded,
                    skipped,
                    errors,
                    deletedEmptyCollections,
                    createdCollections,
                    errorsList = errorsList.Take(200).ToList(), // limit output
                }
            );
        }

        [HttpGet("plex/collections/posters")]
        public async Task<IActionResult> ApplyCollectionPosters([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
        {
            if (!_plexLibrary.IsEnabled)
            {
                return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });
            }

            var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
            if (validation != null)
                return validation;

            if (seriesId.HasValue && filterIds.Count > 0)
            {
                return BadRequest(new { status = "error", message = "Use either seriesId or filter, not both." });
            }

            var seriesList = ResolveSeriesList(seriesId, filterIds);
            var targets = _plexLibrary.GetConfiguredTargets();

            if (targets == null || targets.Count == 0)
                return NoPlexTargetsResponse(seriesList);

            var postersResult = await _collectionManager.ApplyCollectionPostersAsync(seriesList, cancellationToken).ConfigureAwait(false);

            return Ok(
                new
                {
                    status = "ok",
                    processed = postersResult.Processed,
                    uploaded = postersResult.Uploaded,
                    skipped = postersResult.Skipped,
                    errors = postersResult.Errors,
                    errorsList = postersResult.ErrorsList.Take(200).ToList(),
                }
            );

            // Response delegated to CollectionManager earlier.
        }

        [HttpGet("collections/{groupId}")]
        public IActionResult GetCollection(int groupId)
        {
            var group = _metadataService.GetShokoGroupByID(groupId);
            if (group == null)
                return NotFound();

            var primarySeries = group.MainSeries ?? group.Series?.FirstOrDefault();
            if (primarySeries == null)
                return NotFound();

            var meta = _mapper.MapCollection(group, primarySeries);

            return WrapInContainer(meta);
        }

        [HttpGet("collections/user/{groupId}")]
        public IActionResult GetCollectionPoster(int groupId)
        {
            var group = _metadataService.GetShokoGroupByID(groupId);
            if (group == null)
                return NotFound();

            var primarySeries = group.MainSeries ?? group.Series?.FirstOrDefault();
            if (primarySeries == null)
                return NotFound();

            // Try to find a collection poster by group id inside any known import root's collection posters folder.
            // Accept files named by id ("25932.png") or by group title ("My Group.png"). First try exact name, then try a name stripped of invalid Windows filename characters.
            var posterPath = PlexHelpers.FindCollectionPosterPathByGroup(primarySeries, groupId);
            if (string.IsNullOrWhiteSpace(posterPath) || !System.IO.File.Exists(posterPath))
                return NotFound();

            string ext = Path.GetExtension(posterPath).ToLowerInvariant();
            string contentType = GetCollectionContentTypeForExtension(ext) ?? "application/octet-stream";

            return PhysicalFile(posterPath, contentType);
        }

        [HttpGet("metadata/{ratingKey}")]
        public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0, CancellationToken cancellationToken = default)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null)
                return NotFound();

            // --- EPISODE ---
            if (ratingKey.StartsWith(EpisodePrefix))
            {
                var epPart = ratingKey.Substring(EpisodePrefix.Length);
                int? partIndex = null;

                if (epPart.Contains(PartPrefix))
                {
                    var parts = epPart.Split(PartPrefix);
                    epPart = parts[0];
                    partIndex = int.Parse(parts[1]);
                }

                var episode = _metadataService.GetShokoEpisodeByID(int.Parse(epPart));
                if (episode == null)
                    return NotFound();

                var coords = GetPlexCoordinates(episode);
                // If this episode's file mapping was reassigned (Other -> S01/S00) use the mapping's effective season so metadata reflects VFS placement.
                var mappingForEpisode = ctx.FileData.Mappings.FirstOrDefault(m => m.Episodes.Any(ep => ep.ID == episode.ID));
                if (mappingForEpisode != null && coords.Season == PlexConstants.SeasonOther && mappingForEpisode.Coords.Season != PlexConstants.SeasonOther)
                {
                    coords = new PlexCoords
                    {
                        Season = mappingForEpisode.Coords.Season,
                        Episode = coords.Episode,
                        EndEpisode = coords.EndEpisode,
                    };
                }

                object? tmdbEpisode = null;

                if (partIndex.HasValue && ShokoRelay.Settings.TMDBEpNumbering && episode is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    string? showPrefId = (ctx.Series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.PreferredOrdering?.OrderingID;
                    var tmdbEps = SelectPreferredTmdbOrdering(shokoEp.TmdbEpisodes, showPrefId);

                    int idx = partIndex.Value - 1;
                    if (idx < tmdbEps.Count)
                    {
                        var tmdbEp = tmdbEps[idx];
                        tmdbEpisode = tmdbEp;
                        var ord = Plex.PlexMapping.GetOrderingCoords(tmdbEp, showPrefId);
                        if (ord.Season.HasValue)
                            coords = new PlexCoords { Season = ord.Season.Value, Episode = ord.Episode };
                    }
                }

                return WrapInContainer(_mapper.MapEpisode(episode, coords, ctx.Series, ctx.Titles, partIndex, tmdbEpisode));
            }

            // --- SEASON ---
            if (ratingKey.Contains(SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(SeasonPrefix)[1]);
                var seasonMeta = _mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle, cancellationToken);

                if (includeChildren == 1)
                {
                    var episodes = BuildEpisodeList(ctx, sNum);
                    ((IDictionary<string, object?>)seasonMeta)["Children"] = new { size = episodes.Count, Metadata = episodes };
                }

                return WrapInContainer(seasonMeta);
            }

            // --- SERIES ---
            var showMeta = _mapper.MapSeries(ctx.Series, ctx.Titles);

            if (includeChildren == 1)
            {
                var seasons = ctx.FileData.Seasons.Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle, cancellationToken)).ToList();

                ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
            }

            return WrapInContainer(showMeta);
        }

        [HttpGet("metadata/{ratingKey}/children")]
        public IActionResult GetChildren(string ratingKey, CancellationToken cancellationToken = default)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null)
                return NotFound();

            if (ratingKey.Contains(SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(SeasonPrefix)[1]);
                return WrapInPagedContainer(BuildEpisodeList(ctx, sNum));
            }

            var seasons = ctx.FileData.Seasons.Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle, cancellationToken)).ToList();

            return WrapInPagedContainer(seasons);
        }

        [HttpGet("metadata/{ratingKey}/grandchildren")]
        public IActionResult GetGrandchildren(string ratingKey)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null)
                return NotFound();

            var allEpisodes = ctx
                .FileData.Mappings.OrderBy(m => m.Coords.Season)
                .ThenBy(m => m.Coords.Episode)
                .Select(m => _mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode))
                .ToList();

            return WrapInPagedContainer(allEpisodes);
        }

        [HttpGet("animethemes/mp3")]
        public async Task<IActionResult> AnimeThemesMp3([FromQuery] AnimeThemesMp3Query query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query.Path))
                return BadRequest(new { status = "error", message = "path is required" });

            // If user supplied a Plex-style path (how Plex exposes the library), allow it and reverse-map to the configured Shoko path mappings.
            // This helps when dashboard users paste paths copied from Plex instead of server-local Shoko paths.
            if (!string.IsNullOrWhiteSpace(query.Path))
            {
                string reverse = _plexLibrary.MapPlexPathToShokoPath(query.Path);
                if (!string.Equals(reverse, query.Path, StringComparison.Ordinal))
                {
                    query = query with { Path = reverse };
                }
            }

            if (query.Batch)
            {
                var batch = await _animeThemesGenerator.ProcessBatchAsync(query, cancellationToken);
                return Ok(batch);
            }

            var single = await _animeThemesGenerator.ProcessSingleAsync(query, cancellationToken);
            if (single.Status == "error")
                return BadRequest(single);

            return Ok(single);
        }

        [HttpGet("animethemes/vfs")]
        public async Task<IActionResult> AnimeThemesVfs([FromQuery] AnimeThemesVfsQuery query, CancellationToken cancellationToken = default)
        {
            // `path` is intentionally NOT supported for the vfs endpoint — reject if provided.
            if (Request.Query.ContainsKey("path"))
                return BadRequest(
                    new { status = "error", message = "The 'path' query parameter is not supported on /animethemes/vfs. Use 'torrentRoot' or the configured AnimeThemesPathMapping instead." }
                );

            if (query.Mapping)
            {
                string defaultBase = !string.IsNullOrWhiteSpace(ShokoRelay.Settings.AnimeThemesPathMapping) ? ShokoRelay.Settings.AnimeThemesPathMapping : AnimeThemesConstants.BasePath;

                string root = query.TorrentRoot ?? defaultBase;
                var result = await _animeThemesMapping.BuildMappingFileAsync(root, query.MapPath, cancellationToken);
                return Ok(result);
            }

            if (query.ApplyMapping)
            {
                string defaultBase = !string.IsNullOrWhiteSpace(ShokoRelay.Settings.AnimeThemesPathMapping) ? ShokoRelay.Settings.AnimeThemesPathMapping : AnimeThemesConstants.BasePath;

                string? sourceRoot = query.TorrentRoot ?? defaultBase;
                var validation = ValidateFilterOrBadRequest(query.Filter, out var filterIds);
                if (validation != null)
                    return validation;

                var result = await _animeThemesMapping.ApplyMappingAsync(query.MapPath, sourceRoot, filterIds, cancellationToken);

                // Return sanitized response (omit 'Planned' to reduce log noise)
                return Ok(
                    new
                    {
                        LinksCreated = result.LinksCreated,
                        Skipped = result.Skipped,
                        SeriesMatched = result.SeriesMatched,
                        Errors = result.Errors,
                    }
                );
            }

            return BadRequest(new { status = "error", message = "missing operation (use mapping/applyMapping on /animethemes/vfs or use /animethemes/mp3 for single/batch)" });
        }

        [HttpGet("vfs")]
        public IActionResult BuildVfs([FromQuery] bool clean = true, [FromQuery] bool run = false, [FromQuery] string? filter = null)
        {
            var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
            if (validation != null)
                return validation;

            if (!run)
            {
                return Ok(
                    new
                    {
                        status = "skipped",
                        message = "Set run=true to build the VFS",
                        filter = filterIds.Count > 0 ? string.Join(',', filterIds) : null,
                        clean,
                    }
                );
            }

            var result = filterIds.Count > 0 ? _vfsBuilder.Build(filterIds, clean) : _vfsBuilder.Build((int?)null, clean);

            // If this is a manual run and the Plex client is configured to scan on VFS refresh,
            // trigger a background Plex refresh for the filtered series (or the single series) so the new links are picked up.
            if (run && _plexLibrary.IsEnabled && _plexLibrary.ScanOnVfsRefresh && filterIds.Count > 0)
            {
                Logger.Info("Manual VFS build completed (run=true) — triggering Plex scans for filtered series.");
                var toProcess = ResolveSeriesList(null, filterIds).Where(s => s != null).Cast<IShokoSeries>().ToList();
                _ = SchedulePlexRefreshForSeriesAsync(toProcess);
            }

            return Ok(
                new
                {
                    status = "ok",
                    root = result.RootPath,
                    seriesProcessed = result.SeriesProcessed,
                    linksCreated = result.CreatedLinks,
                    plannedLinks = result.PlannedLinks,
                    skipped = result.Skipped,
                    errors = result.Errors,
                }
            );
        }

        // POST /shoko/import -> trigger server import via Shoko v3 API using configured ShokoApiKey
        [HttpPost("shoko/import")]
        public async Task<IActionResult> RunShokoImport([FromQuery] bool onlyUnrecognized = true)
        {
            try
            {
                if (_shokoImportService == null)
                    return StatusCode(500, new { status = "error", message = "Import service not available." });

                var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);
                return Ok(
                    new
                    {
                        status = "ok",
                        scanned = scanned,
                        scannedCount = scanned?.Count ?? 0,
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RunShokoImport failed");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        // POST /shoko/remove-missing -> call Shoko v3 Action/RemoveMissingFiles
        [HttpPost("shoko/remove-missing")]
        public async Task<IActionResult> RemoveMissingFiles([FromQuery] bool removeFromMyList = true)
        {
            try
            {
                if (_shokoImportService == null)
                    return StatusCode(500, new { status = "error", message = "Import service not available." });

                var body = await _shokoImportService.RemoveMissingFilesAsync(removeFromMyList).ConfigureAwait(false);
                return Ok(new { status = "ok", response = body });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RemoveMissingFiles failed");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        // GET/POST /plex/syncwatched -> sync watched-state from configured Plex libraries into Shoko
        [HttpGet("plex/syncwatched")]
        [HttpPost("plex/syncwatched")]
        public async Task<IActionResult> SyncPlexWatched(
            [FromQuery(Name = "dryRun")] string? dryRun = null,
            [FromQuery(Name = "sinceHours")] int? sinceHours = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!_plexLibrary.IsEnabled)
            {
                return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });
            }

            if (_watchedSyncService == null)
            {
                return StatusCode(500, new { status = "error", message = "WatchedSyncService is not available." });
            }

            // default to safe dry-run when the query param is omitted; real writes require explicit dryRun=false
            bool parsedDryRun = true;
            if (!string.IsNullOrWhiteSpace(dryRun))
            {
                var v = dryRun.Trim();
                if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                {
                    parsedDryRun = true;
                }
                else if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
                {
                    parsedDryRun = false;
                }
                else
                {
                    return BadRequest(new { status = "error", message = "Invalid value for dryRun — expected true or false." });
                }
            }

            try
            {
                // Allow optional lookback flag for manual invocation: sinceHours (hours)
                var result = await _watchedSyncService.SyncWatchedAsync(parsedDryRun, sinceHours, cancellationToken).ConfigureAwait(false);
                return Ok(
                    new
                    {
                        status = "ok",
                        processed = result.Processed,
                        marked = result.MarkedWatched,
                        skipped = result.Skipped,
                        scheduled = result.ScheduledJobs,
                        perUser = result.PerUser,
                        perUserChanges = result.PerUserChanges,
                        errors = result.Errors,
                        errorsList = result.ErrorsList,
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SyncPlexWatched failed");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }
    }
}
