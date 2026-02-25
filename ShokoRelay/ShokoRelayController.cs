using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Controllers
{
    [ApiVersionNeutral]
    [ApiController]
    [Route(ShokoRelayInfo.BasePath)]
    public partial class ShokoRelayController : ControllerBase
    {
        private readonly IMetadataService _metadataService;
        private readonly PlexMetadata _mapper;
        private readonly VfsBuilder _vfsBuilder;
        private readonly AnimeThemesGenerator _animeThemesGenerator;
        private readonly AnimeThemesMapping _animeThemesMapping;
        private readonly ConfigProvider _configProvider;
        private readonly PlexAuth _plexAuth;
        private readonly PlexClient _plexLibrary;
        private readonly Services.ICollectionService _collectionService;
        private readonly Services.ICriticRatingService _criticRatingService;
        private readonly SyncToShoko _watchedSyncService;
        private readonly SyncToPlex _syncToPlexService;
        private readonly IUserDataService _userDataService;
        private readonly IUserService _userService;
        private readonly Services.ShokoImportService _shokoImportService;

        private const string ControllerPageFileName = "index.cshtml";

        private string ApiBase => $"{Request.Scheme}://{Request.Host}{ShokoRelayInfo.BasePath}";

        private const string SeasonPrefix = PlexConstants.SeasonPrefix;
        private const string EpisodePrefix = PlexConstants.EpisodePrefix;
        private const string PartPrefix = PlexConstants.PartPrefix;

        public ShokoRelayController(
            IMetadataService metadataService,
            PlexMetadata mapper,
            VfsBuilder vfsBuilder,
            AnimeThemesGenerator animeThemeGenerator,
            AnimeThemesMapping animeThemesMapping,
            ConfigProvider configProvider,
            PlexAuth plexAuth,
            PlexClient plexLibrary,
            Services.ICollectionService collectionService,
            Services.ICriticRatingService criticRatingService,
            SyncToShoko watchedSyncService,
            SyncToPlex syncToPlexService,
            IUserDataService userDataService,
            IUserService userService,
            Services.ShokoImportService shokoImportService
        )
        {
            _metadataService = metadataService;
            _mapper = mapper;
            _vfsBuilder = vfsBuilder;
            _animeThemesGenerator = animeThemeGenerator;
            _animeThemesMapping = animeThemesMapping;
            _configProvider = configProvider;
            _plexAuth = plexAuth;
            _plexLibrary = plexLibrary;
            _collectionService = collectionService;
            _criticRatingService = criticRatingService;
            _watchedSyncService = watchedSyncService;
            _syncToPlexService = syncToPlexService;
            _userDataService = userDataService;
            _userService = userService;
            _shokoImportService = shokoImportService;
        }

        #region Dashboard / Config

        [HttpGet("dashboard/{*path}")]
        public IActionResult GetControllerPage([FromRoute] string? path = null)
        {
            // Serve only from the plugin folder under PluginsPath/…/dashboard
            string dashboardDir = Path.Combine(_configProvider.PluginDirectory, "dashboard");

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

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            // provider handles serialization, augmentation and sanitization
            var payload = _configProvider.GetDashboardConfig();
            return Ok(payload);
        }

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

        [HttpGet("logs/{fileName}")]
        public IActionResult GetLog(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest(new { status = "error", message = "fileName is required" });

            string logsDir = Path.Combine(_configProvider.PluginDirectory, "logs");
            string path = Path.Combine(logsDir, fileName);
            if (!System.IO.File.Exists(path))
                return NotFound(new { status = "error", message = "log not found" });
            return PhysicalFile(path, "text/plain", fileName);
        }

        #endregion

        #region Metadata Provider

        [HttpGet]
        public IActionResult GetMediaProvider()
        {
            var supportedTypes = new[] { PlexConstants.TypeShow, PlexConstants.TypeSeason, PlexConstants.TypeEpisode };

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

        [Route("matches")]
        [HttpPost]
        [HttpGet]
        public IActionResult Match([FromQuery] string? name, [FromBody] PlexMatchBody? body = null)
        {
            int? seriesId = null; // compute below
            string? rawPath = body?.Filename; // the path is always provided in the request body as "filename"

            if (string.IsNullOrWhiteSpace(rawPath))
                if (body?.Manual == 1 && int.TryParse(body.Title, out var manualId))
                    seriesId = manualId;
                else
                    return EmptyMatch();
            else
            {
                // grab first numeric sequence from rawPath as the Shoko series ID
                var m = System.Text.RegularExpressions.Regex.Match(rawPath, "\\d+");
                if (m.Success && int.TryParse(m.Value, out var sid))
                    seriesId = sid;
            }

            if (!seriesId.HasValue)
                return EmptyMatch();

            var series = _metadataService.GetShokoSeriesByID(seriesId.Value);
            if (series == null)
            {
                Logger.Info("Match: no Shoko series found for id {SeriesId}", seriesId.Value);
                return EmptyMatch();
            }

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

                if (partIndex.HasValue && ShokoRelay.Settings.TmdbEpNumbering && episode is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
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

        [HttpGet("metadata/{ratingKey}/images")]
        public IActionResult GetImages(string ratingKey, CancellationToken cancellationToken = default)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null)
                return NotFound();

            object[] images = Array.Empty<object>();
            if (ratingKey.Contains(PlexConstants.EpisodePrefix))
            {
                var epIdPart = ratingKey.Split(PlexConstants.EpisodePrefix)[1];
                int epId = 0;
                int? partIdx = null;
                if (epIdPart.Contains(PlexConstants.PartPrefix))
                {
                    var parts = epIdPart.Split(PlexConstants.PartPrefix);
                    epId = int.Parse(parts[0]);
                    partIdx = int.Parse(parts[1]);
                }
                else
                {
                    epId = int.Parse(epIdPart);
                }
                var episode = ctx.Series.Episodes.FirstOrDefault(e => e.ID == epId);
                if (episode != null)
                {
                    var coords = GetPlexCoordinates(episode);
                    var epMeta = _mapper.MapEpisode(episode, coords, ctx.Series, ctx.Titles, partIdx, null);
                    if (epMeta is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr)
                        images = arr;
                }
            }
            else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(PlexConstants.SeasonPrefix)[1]);
                var seasonMeta = _mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle, cancellationToken);
                if (seasonMeta is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr)
                    images = arr;
            }
            else
            {
                var showMeta = _mapper.MapSeries(ctx.Series, ctx.Titles);
                if (showMeta is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr)
                    images = arr;
            }

            return Ok(
                new
                {
                    MediaContainer = new
                    {
                        offset = 0,
                        totalSize = images.Length,
                        identifier = ShokoRelayInfo.AgentScheme,
                        size = images.Length,
                        Image = images,
                    },
                }
            );
        }

        #endregion

        #region Plex: Authentication

        [HttpGet("plex/auth")]
        public async Task<IActionResult> StartPlexAuth(CancellationToken cancellationToken = default)
        {
            try
            {
                PlexPinResponse pin = await _plexAuth.CreatePinAsync(true, cancellationToken);
                if (string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Code))
                {
                    return StatusCode(502, new { status = "error", message = "Plex pin response missing id/code." });
                }

                string authUrl = _plexAuth.BuildAuthUrl(pin.Code, ShokoRelayInfo.Name);
                return Ok(
                    new
                    {
                        status = "ok",
                        pinId = pin.Id,
                        code = pin.Code,
                        authUrl,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, new { status = "error", message = ex.Message });
            }
        }

        [HttpGet("plex/auth/status")]
        public async Task<IActionResult> GetPlexAuthStatus([FromQuery] string pinId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pinId))
                return BadRequest(new { status = "error", message = "pinId is required" });

            try
            {
                var pin = await _plexAuth.GetPinAsync(pinId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pin.AuthToken))
                    return Ok(new { status = "pending" });

                // persist token and discovery information in the token file
                _configProvider.UpdatePlexTokenInfo(token: pin.AuthToken);

                try
                {
                    // ensure client id exists (write/token file if needed)
                    string clientIdentifier = _configProvider.GetPlexClientIdentifier();

                    try
                    {
                        var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(pin.AuthToken, clientIdentifier, cancellationToken).ConfigureAwait(false);

                        if (discovery.TokenValid && discovery.Servers?.Count > 0)
                        {
                            var servers = discovery
                                .Servers.Select(s => new PlexAvailableServer
                                {
                                    Id = s.Id,
                                    Name = s.Name,
                                    PreferredUri = s.PreferredUri ?? string.Empty,
                                })
                                .ToList();

                            _configProvider.UpdatePlexTokenInfo(servers: servers);
                        }

                        // Deduplicate & map discovered libraries via helper
                        var libs = CollectDiscoveredLibraries(discovery.ShokoLibraries);
                        _configProvider.UpdatePlexTokenInfo(libraries: libs);
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

                return Ok(new { status = "ok", tokenSaved = true });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, new { status = "error", message = ex.Message });
            }
        }

        [HttpPost("plex/auth/unlink")]
        public async Task<IActionResult> UnlinkPlex(CancellationToken cancellationToken = default)
        {
            var token = _configProvider.GetPlexToken();
            if (string.IsNullOrWhiteSpace(token))
                return Ok(new { status = "ok" });

            string clientIdentifier = _configProvider.GetPlexClientIdentifier();
            await _plexAuth.RevokePlexTokenAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);

            // remove persisted data completely
            _configProvider.DeleteTokenFile();

            return Ok(new { status = "ok" });
        }

        [HttpPost("plex/auth/refresh")]
        public async Task<IActionResult> RefreshPlexLibraries(CancellationToken cancellationToken = default)
        {
            var token = _configProvider.GetPlexToken();
            if (string.IsNullOrWhiteSpace(token))
                return Unauthorized(new { status = "error", message = "Plex token is missing." });

            string clientIdentifier = _configProvider.GetPlexClientIdentifier();

            try
            {
                var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);

                if (discovery.TokenValid && discovery.Servers?.Count > 0)
                {
                    var servers = discovery
                        .Servers.Select(s => new PlexAvailableServer
                        {
                            Id = s.Id,
                            Name = s.Name,
                            PreferredUri = s.PreferredUri ?? string.Empty,
                        })
                        .ToList();
                    _configProvider.UpdatePlexTokenInfo(servers: servers);
                }

                // Deduplicate & map discovered libraries via helper
                var libs = CollectDiscoveredLibraries(discovery.ShokoLibraries);
                _configProvider.UpdatePlexTokenInfo(libraries: libs);

                return Ok(new { status = "ok", libraries = libs });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to refresh Plex libraries: {ex.Message}");
                return StatusCode(502, new { status = "error", message = "Failed to refresh Plex libraries." });
            }
        }

        #endregion

        #region Plex: Automation

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

            // Delegate to CollectionService for per-target collection building and poster application
            var managerResult = await _collectionService.BuildCollectionsAsync(seriesList, cancellationToken).ConfigureAwait(false);

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

            // write collection report log
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Collection Build Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Processed: {processed}");
                sb.AppendLine($"Created: {created}");
                sb.AppendLine($"Uploaded: {uploaded}");
                sb.AppendLine($"SeasonPostersUploaded: {seasonPostersUploaded}");
                sb.AppendLine($"Skipped: {skipped}");
                sb.AppendLine($"Errors: {errors}");
                sb.AppendLine($"DeletedEmptyCollections: {deletedEmptyCollections}");
                if (errorsList.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Errors List:");
                    foreach (var e in errorsList)
                        sb.AppendLine(e);
                }
                LogHelper.WriteLog(_configProvider.PluginDirectory, "collection-report.log", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to write collection report log");
            }

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
                    logUrl = $"{ApiBase}/logs/collection-report.log",
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

            var postersResult = await _collectionService.ApplyCollectionPostersAsync(seriesList, cancellationToken).ConfigureAwait(false);

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

        [HttpGet("plex/ratings/apply")]
        public async Task<IActionResult> ApplyAudienceRatings([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
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
            var allowedIds = new HashSet<int>(seriesList.Select(s => s?.ID ?? 0));

            var result = await _criticRatingService.ApplyRatingsAsync(allowedIds, cancellationToken).ConfigureAwait(false);

            // write rating report log
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Audience Rating Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"ProcessedSeries: {result.ProcessedShows}");
                sb.AppendLine($"UpdatedSeries: {result.UpdatedShows}");
                sb.AppendLine($"ProcessedEpisodes: {result.ProcessedEpisodes}");
                sb.AppendLine($"UpdatedEpisodes: {result.UpdatedEpisodes}");
                sb.AppendLine($"Errors: {result.Errors}");
                if (result.ErrorsList?.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Errors List:");
                    foreach (var e in result.ErrorsList)
                        sb.AppendLine(e);
                }
                LogHelper.WriteLog(_configProvider.PluginDirectory, "rating-report.log", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to write rating report log");
            }

            return Ok(
                new
                {
                    status = "ok",
                    processedShows = result.ProcessedShows,
                    updatedShows = result.UpdatedShows,
                    processedEpisodes = result.ProcessedEpisodes,
                    updatedEpisodes = result.UpdatedEpisodes,
                    errors = result.Errors,
                    errorsList = (result.ErrorsList ?? Enumerable.Empty<string>()).Take(200).ToList(),
                    logUrl = $"{ApiBase}/logs/rating-report.log",
                }
            );
        }

        [HttpGet("plex/automation/run")]
        public async Task<IActionResult> RunPlexAutomationNow(CancellationToken cancellationToken = default)
        {
            if (!_plexLibrary.IsEnabled)
            {
                return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });
            }

            if (_collectionService == null && _criticRatingService == null)
                return StatusCode(501, new { status = "error", message = "Automation services not available" });

            try
            {
                var allSeries = _metadataService.GetAllShokoSeries()?.Cast<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>().ToList() ?? new List<Shoko.Abstractions.Metadata.Shoko.IShokoSeries?>();
                if (_collectionService != null)
                    await _collectionService.BuildCollectionsAsync(allSeries, cancellationToken).ConfigureAwait(false);
                if (_criticRatingService != null)
                    await _criticRatingService.ApplyRatingsAsync(null, cancellationToken).ConfigureAwait(false);

                // mark schedule
                ShokoRelay.MarkPlexAutomationRunNow();

                return Ok(new { status = "ok" });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Manual Plex automation run failed");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        #endregion

        #region Plex: Webhook

        // Plugin-level Plex webhook receiver: https://support.plex.tv/articles/115002267687-webhooks/
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
            var adminToken = _configProvider.GetPlexToken();
            if (!string.IsNullOrWhiteSpace(adminToken))
            {
                try
                {
                    var acct = await _plexAuth.GetAccountInfoAsync(adminToken).ConfigureAwait(false);
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
            int? shokoEpisodeId = SyncHelper.TryParseShokoEpisodeIdFromGuid(evt.Metadata.Guid);

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

        #endregion

        #region Virtual File System

        [HttpGet("vfs")]
        public IActionResult BuildVfs([FromQuery] bool clean = true, [FromQuery] bool run = false, [FromQuery] bool cleanOnly = false, [FromQuery] string? filter = null)
        {
            var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
            if (validation != null)
                return validation;

            // Treat a filter value of "0" as a shorthand for clean-only
            bool zeroFilter = (filterIds.Count == 1 && filterIds[0] == 0);
            if (zeroFilter)
            {
                cleanOnly = true;
                filterIds.Clear(); // no actual series filter when performing global clean
            }

            // If caller only wants to perform a clean, ignore the `run` flag and delegate
            // to the dedicated clean API on the builder.
            if (cleanOnly)
            {
                var cleanRes = filterIds.Count > 0 ? _vfsBuilder.Clean(filterIds) : _vfsBuilder.Clean((int?)null);
                return Ok(
                    new
                    {
                        status = "ok",
                        cleaned = true,
                        root = cleanRes.RootPath,
                        errors = cleanRes.Errors,
                        logUrl = $"{ApiBase}/logs/vfs-report.log",
                    }
                );
            }

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
                    logUrl = $"{ApiBase}/logs/vfs-report.log",
                }
            );
        }

        #endregion

        #region Shoko: Automation

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
                        scanned,
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

        [HttpGet("shoko/import/start")]
        public async Task<IActionResult> StartShokoImportNow(CancellationToken cancellationToken = default)
        {
            var settings = _configProvider.GetSettings();
            int freqHours = settings?.ShokoImportFrequencyHours ?? 0;

            if (_shokoImportService == null)
                return StatusCode(501, new { status = "error", message = "ShokoImportService not available" });

            try
            {
                var scanned = await _shokoImportService.TriggerImportAsync(cancellationToken).ConfigureAwait(false);

                // Replace schedule: mark last-run == now so automation schedules next run after configured interval
                ShokoRelay.MarkImportRunNow();

                return Ok(
                    new
                    {
                        status = "ok",
                        triggered = true,
                        scheduled = freqHours > 0,
                        nextRunInHours = freqHours,
                        scanned,
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        #endregion

        #region Sync Watched

        [HttpGet("syncwatched")]
        [HttpPost("syncwatched")]
        public async Task<IActionResult> SyncPlexWatched(
            [FromQuery(Name = "dryRun")] string? dryRun = null,
            [FromQuery(Name = "sinceHours")] int? sinceHours = null,
            [FromQuery(Name = "ratings")] bool? ratings = null,
            [FromQuery(Name = "import")] bool? import = null,
            [FromQuery(Name = "excludeAdmin")] bool? excludeAdmin = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!_plexLibrary.IsEnabled)
            {
                return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });
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

            bool doImport = import.GetValueOrDefault(false);
            bool includeRatings = ratings.GetValueOrDefault(false);

            try
            {
                PlexWatchedSyncResult result;
                string direction;

                if (doImport)
                {
                    if (_syncToPlexService == null)
                        return StatusCode(500, new { status = "error", message = "SyncToPlex service is not available." });

                    direction = "Plex<-Shoko";
                    result = await _syncToPlexService.SyncWatchedAsync(parsedDryRun, sinceHours, includeRatings, excludeAdmin.GetValueOrDefault(false), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (_watchedSyncService == null)
                        return StatusCode(500, new { status = "error", message = "WatchedSyncService is not available." });

                    direction = "Plex->Shoko";
                    result = await _watchedSyncService.SyncWatchedAsync(parsedDryRun, sinceHours, includeRatings, cancellationToken).ConfigureAwait(false);
                }

                // write sync report log if not a dry-run (manual/real sync)
                if (!parsedDryRun)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"Sync Watched Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine($"Direction: {direction}");
                        sb.AppendLine($"Processed: {result.Processed}");
                        sb.AppendLine($"Marked: {result.MarkedWatched}");
                        sb.AppendLine($"Skipped: {result.Skipped}");
                        sb.AppendLine($"Scheduled: {result.ScheduledJobs}");
                        sb.AppendLine($"VotesFound: {result.VotesFound}");
                        sb.AppendLine($"VotesUpdated: {result.VotesUpdated}");
                        sb.AppendLine($"VotesSkipped: {result.VotesSkipped}");
                        sb.AppendLine($"Matched: {result.Matched}");
                        // write a summary of missing mapping ids rather than the List type name
                        var missingList = result.MissingMappings ?? new List<int>();
                        int missingCount = missingList.Count;
                        sb.AppendLine($"MissingMappings: {missingCount}");
                        if (missingCount > 0)
                        {
                            sb.AppendLine("MissingIds: " + string.Join(',', missingList));
                        }
                        if (result.ErrorsList?.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine("Errors:");
                            foreach (var e in result.ErrorsList)
                                sb.AppendLine(e);
                        }
                        LogHelper.WriteLog(_configProvider.PluginDirectory, "sync.log", sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to write sync log");
                    }
                }

                return Ok(
                    new
                    {
                        status = "ok",
                        direction,
                        processed = result.Processed,
                        marked = result.MarkedWatched,
                        skipped = result.Skipped,
                        scheduled = result.ScheduledJobs,
                        votesFound = result.VotesFound,
                        votesUpdated = result.VotesUpdated,
                        votesSkipped = result.VotesSkipped,
                        matched = result.Matched,
                        missingMappings = result.MissingMappings,
                        perUser = result.PerUser,
                        perUserChanges = result.PerUserChanges,
                        errors = result.Errors,
                        errorsList = result.ErrorsList,
                        logUrl = $"{ApiBase}/logs/sync.log",
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SyncPlexWatched failed");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        [HttpGet("syncwatched/start")]
        public async Task<IActionResult> StartWatchedSyncNow(CancellationToken cancellationToken = default)
        {
            var settings = _configProvider.GetSettings();
            int freqHours = settings?.ShokoSyncWatchedFrequencyHours ?? 0;

            if (_watchedSyncService == null)
                return StatusCode(501, new { status = "error", message = "Watched sync service not available" });

            try
            {
                var result = await _watchedSyncService.SyncWatchedAsync(false, freqHours, settings?.ShokoSyncWatchedIncludeRatings ?? false, cancellationToken).ConfigureAwait(false);

                // Replace schedule: mark last-run == now so automation schedules next run after configured interval
                ShokoRelay.MarkSyncRunNow();

                return Ok(
                    new
                    {
                        status = "ok",
                        triggered = true,
                        scheduled = freqHours > 0,
                        nextRunInHours = freqHours,
                        result,
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        #endregion

        #region AnimeThemes

        [HttpGet("animethemes/vfs/build")]
        public async Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? mapPath = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
        {
            var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
            if (validation != null)
                return validation;

            // if the mapping file doesn't exist we return a user-friendly error rather than allowing a 500
            string resolvedMapPath = mapPath ?? Path.Combine(_configProvider.PluginDirectory, AnimeThemesConstants.MapFileName);
            if (!System.IO.File.Exists(resolvedMapPath))
            {
                return BadRequest(new { status = "error", message = "Mapping file not found" });
            }

            var result = await _animeThemesMapping.ApplyMappingAsync(mapPath, filterIds, cancellationToken).ConfigureAwait(false);

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"AnimeThemes: VFS Build Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"LinksCreated: {result.LinksCreated}");
                sb.AppendLine($"Skipped: {result.Skipped}");
                sb.AppendLine($"SeriesMatched: {result.SeriesMatched}");
                if (result.Errors?.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Errors:");
                    foreach (var e in result.Errors)
                        sb.AppendLine(e);
                }
                LogHelper.WriteLog(_configProvider.PluginDirectory, "at-vfs-report.log", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to write animethemes apply log");
            }

            return Ok(
                new
                {
                    result.LinksCreated,
                    result.Skipped,
                    result.SeriesMatched,
                    result.Errors,
                    logUrl = $"{ApiBase}/logs/at-vfs-report.log",
                }
            );
        }

        [HttpGet("animethemes/vfs/map")]
        public async Task<IActionResult> AnimeThemesVfsMap([FromQuery] string? mapPath = null, CancellationToken cancellationToken = default)
        {
            var result = await _animeThemesMapping.BuildMappingFileAsync(mapPath, cancellationToken).ConfigureAwait(false);
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"AnimeThemes: Mapping Build Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"EntriesWritten: {result.EntriesWritten}");
                sb.AppendLine($"Errors: {result.Errors}");
                if (result.Messages?.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Messages:");
                    foreach (var m in result.Messages)
                        sb.AppendLine(m);
                }
                LogHelper.WriteLog(_configProvider.PluginDirectory, "at-mapping.log", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to write animethemes mapping log");
            }
            return Ok(new { result, logUrl = $"{ApiBase}/logs/at-mapping.log" });
        }

        [HttpPost("animethemes/vfs/import")]
        public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
        {
            // use the raw URL directly; avoid the GitHub API which truncates large files
            const string rawUrl = AnimeThemesConstants.RawMapUrl;
            var (count, log) = await _animeThemesMapping.ImportMappingFromUrlAsync(rawUrl, cancellationToken).ConfigureAwait(false);
            return Ok(new { status = "ok", count });
        }

        [HttpGet("animethemes/mp3")]
        public async Task<IActionResult> AnimeThemesMp3([FromQuery] AnimeThemesMp3Query query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query.Path))
                return BadRequest(new { status = "error", message = "path is required" });

            // If user supplied a Plex-style path (how Plex exposes the library), allow it and reverse-map to the configured Shoko path mappings.
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

                // write theme-report log summarizing batch result
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"AnimeThemes: MP3 Batch Report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Processed: {batch.Processed}");
                    sb.AppendLine($"Skipped: {batch.Skipped}");
                    sb.AppendLine($"Errors: {batch.Errors}");
                    foreach (var item in batch.Items)
                    {
                        sb.AppendLine($"{item.Folder} -> {item.Status}{(item.Message != null ? ": " + item.Message : "")} ");
                    }
                    LogHelper.WriteLog(_configProvider.PluginDirectory, "theme-report.log", sb.ToString());
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to write anime themes mp3 report log");
                }

                return Ok(new { batch, logUrl = $"{ApiBase}/logs/theme-report.log" });
            }

            var single = await _animeThemesGenerator.ProcessSingleAsync(query, cancellationToken);
            if (single.Status == "error")
                return BadRequest(single);

            return Ok(single);
        }

        #endregion
    }
}
