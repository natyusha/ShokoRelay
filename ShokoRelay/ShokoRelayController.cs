using Microsoft.AspNetCore.Mvc;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Vfs;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Controllers
{
    [ApiVersion("3.0")]
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    public partial class ShokoRelayController : ControllerBase
    {
        private readonly IVideoService _videoService;
        private readonly IMetadataService _metadataService;
        private readonly PlexMetadata _mapper;
        private readonly VfsBuilder _vfsBuilder;
        private readonly AnimeThemesGenerator _animeThemesGenerator;
        private readonly AnimeThemesMapping _animeThemesMapping;
        private readonly ConfigProvider _configProvider;
        private readonly PlexAuth _plexAuth;
        private readonly PlexClient _plexLibrary;
        private readonly Services.IPlexCollectionManager _collectionManager;

        private const string ControllerPageFileName = "ShokoRelayController.cshtml";

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
            ConfigProvider configProvider,
            PlexAuth plexAuth,
            PlexClient plexLibrary,
            Services.IPlexCollectionManager collectionManager
        )
        {
            _videoService = videoService;
            _metadataService = metadataService;
            _mapper = mapper;
            _vfsBuilder = vfsBuilder;
            _animeThemesGenerator = animeThemeGenerator;
            _animeThemesMapping = animeThemesMapping;
            _configProvider = configProvider;
            _plexAuth = plexAuth;
            _plexLibrary = plexLibrary;
            _collectionManager = collectionManager;
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
                                title = series.PreferredTitle,
                                year = series.AirDate?.Year,
                                score = 100,
                                thumb = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                            },
                        },
                    },
                }
            );
        }

        [HttpGet]
        public IActionResult GetMediaProvider()
        {
            // Temporarily advertise only show/season/episode to avoid unsupported metadata type warnings in Plex.
            var supportedTypes = new[]
            {
                PlexConstants.TypeShow,
                PlexConstants.TypeSeason,
                PlexConstants.TypeEpisode,
                //PlexConstants.TypeCollection
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

        [HttpGet("dashboard")]
        public IActionResult GetControllerPage()
        {
            string configDir = _configProvider.ConfigDirectory;
            Directory.CreateDirectory(configDir);
            string pagePath = Path.Combine(configDir, ControllerPageFileName);

            string? template = LoadControllerTemplate();
            if (!System.IO.File.Exists(pagePath))
            {
                if (!string.IsNullOrWhiteSpace(template))
                    System.IO.File.WriteAllText(pagePath, template);
            }
            else if (!string.IsNullOrWhiteSpace(template))
            {
                string existing = System.IO.File.ReadAllText(pagePath);
                if (!string.Equals(existing, template, StringComparison.Ordinal))
                    System.IO.File.WriteAllText(pagePath, template);
            }

            string html = System.IO.File.Exists(pagePath) ? System.IO.File.ReadAllText(pagePath) : "Dashboard template not found.";
            return Content(html, "text/html");
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

                string statusUrl = $"{BaseUrl}/api/v{ShokoRelayInfo.ApiVersion}/ShokoRelay/plexauth/status?pinId={Uri.EscapeDataString(pin.Id)}";
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

                        var collected = new List<PlexAvailableLibrary>();
                        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var (lib, srv) in discovery.ShokoLibraries ?? new List<(PlexLibraryInfo, PlexServerInfo)>())
                        {
                            var key = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : $"{srv.PreferredUri}::{lib.Id}";
                            if (seenKeys.Contains(key))
                                continue;
                            seenKeys.Add(key);

                            var uuidVal = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : key;
                            collected.Add(
                                new PlexAvailableLibrary
                                {
                                    Id = lib.Id,
                                    Title = lib.Title,
                                    Type = lib.Type,
                                    Agent = lib.Agent,
                                    Uuid = uuidVal,
                                    ServerId = srv.Id,
                                    ServerName = srv.Name,
                                    ServerUrl = srv.PreferredUri ?? string.Empty,
                                }
                            );
                        }

                        settings.PlexLibrary.DiscoveredLibraries = collected;
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
            // Delete the token file to remove persisted token and discovered data
            _configProvider.DeleteTokenFile();

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

                var collected = new List<PlexAvailableLibrary>();
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (lib, srv) in discovery.ShokoLibraries ?? new List<(PlexLibraryInfo, PlexServerInfo)>())
                {
                    var key = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : $"{srv.PreferredUri}::{lib.Id}";
                    if (seenKeys.Contains(key))
                        continue;
                    seenKeys.Add(key);

                    var uuidVal = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : key;
                    collected.Add(
                        new PlexAvailableLibrary
                        {
                            Id = lib.Id,
                            Title = lib.Title,
                            Type = lib.Type,
                            Agent = lib.Agent,
                            Uuid = uuidVal,
                            ServerId = srv.Id,
                            ServerName = srv.Name,
                            ServerUrl = srv.PreferredUri ?? string.Empty,
                        }
                    );
                }

                settings.PlexLibrary.DiscoveredLibraries = collected;
                _configProvider.SaveSettings(settings);

                return Ok(new { status = "ok", libraries = collected });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to refresh Plex libraries: {ex.Message}");
                return StatusCode(502, new { status = "error", message = "Failed to refresh Plex libraries." });
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

            var filterErrors = new List<string>();
            var filterIds = ParseFilterIds(filter, out filterErrors);
            if (filterErrors.Count > 0)
            {
                return BadRequest(
                    new
                    {
                        status = "error",
                        message = "Invalid filter values.",
                        errors = filterErrors,
                    }
                );
            }

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
            {
                // No targets configured, nothing to do
                int processedNone = seriesList.Count(s => s != null);
                return Ok(
                    new
                    {
                        status = "ok",
                        processed = processedNone,
                        created = 0,
                        skipped = processedNone,
                        errors = 0,
                        deletedEmptyCollections = 0,
                    }
                );
            }

            // Delegate to PlexCollectionManager for per-target collection building and poster application
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

            var filterErrors = new List<string>();
            var filterIds = ParseFilterIds(filter, out filterErrors);
            if (filterErrors.Count > 0)
            {
                return BadRequest(
                    new
                    {
                        status = "error",
                        message = "Invalid filter values.",
                        errors = filterErrors,
                    }
                );
            }

            if (seriesId.HasValue && filterIds.Count > 0)
            {
                return BadRequest(new { status = "error", message = "Use either seriesId or filter, not both." });
            }

            var seriesList = ResolveSeriesList(seriesId, filterIds);
            var targets = _plexLibrary.GetConfiguredTargets();

            if (targets == null || targets.Count == 0)
            {
                // No targets configured, nothing to do
                int processedNone = seriesList.Count(s => s != null);
                return Ok(
                    new
                    {
                        status = "ok",
                        processed = processedNone,
                        created = 0,
                        skipped = processedNone,
                        errors = 0,
                        deletedEmptyCollections = 0,
                    }
                );
            }

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

            // Response delegated to PlexCollectionManager earlier.
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

            string ext = System.IO.Path.GetExtension(posterPath).ToLowerInvariant();
            string contentType = ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".tbn" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => "application/octet-stream",
            };

            return PhysicalFile(posterPath, contentType);
        }

        [HttpGet("metadata/{ratingKey}")]
        public async Task<IActionResult> GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0, CancellationToken cancellationToken = default)
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
                object? tmdbEpisode = null;

                if (partIndex.HasValue && ShokoRelay.Settings.TMDBEpNumbering && episode is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    var tmdbEps = shokoEp.TmdbEpisodes.OrderBy(te => te.SeasonNumber ?? 0).ThenBy(te => te.EpisodeNumber).ToList();

                    int idx = partIndex.Value - 1;
                    if (idx < tmdbEps.Count)
                    {
                        var tmdbEp = tmdbEps[idx];
                        tmdbEpisode = tmdbEp;
                        if (tmdbEp.SeasonNumber.HasValue)
                            coords = new PlexCoords { Season = tmdbEp.SeasonNumber.Value, Episode = tmdbEp.EpisodeNumber };
                    }
                }

                return WrapInContainer(_mapper.MapEpisode(episode, coords, ctx.Series, ctx.Titles, partIndex, tmdbEpisode));
            }

            // --- SEASON ---
            if (ratingKey.Contains(SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(SeasonPrefix)[1]);
                var seasonMeta = await _mapper.MapSeasonAsync(ctx.Series, sNum, ctx.Titles.DisplayTitle, cancellationToken).ConfigureAwait(false);

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
                var seasonTasks = ctx.FileData.Seasons.Select(s => _mapper.MapSeasonAsync(ctx.Series, s, ctx.Titles.DisplayTitle, cancellationToken));
                var seasons = (await Task.WhenAll(seasonTasks).ConfigureAwait(false)).ToList();

                ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
            }

            return WrapInContainer(showMeta);
        }

        [HttpGet("metadata/{ratingKey}/children")]
        public async Task<IActionResult> GetChildren(string ratingKey, CancellationToken cancellationToken = default)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null)
                return NotFound();

            if (ratingKey.Contains(SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(SeasonPrefix)[1]);
                return WrapInPagedContainer(BuildEpisodeList(ctx, sNum));
            }

            var seasonTasks = ctx.FileData.Seasons.Select(s => _mapper.MapSeasonAsync(ctx.Series, s, ctx.Titles.DisplayTitle, cancellationToken));
            var seasons = (await Task.WhenAll(seasonTasks).ConfigureAwait(false)).ToList();

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

        [HttpGet("animethemes")]
        public async Task<IActionResult> GetAnimeThemes([FromQuery] AnimeThemesQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Mapping)
            {
                string defaultBase = !string.IsNullOrWhiteSpace(ShokoRelay.Settings.AnimeThemesPathMapping) ? ShokoRelay.Settings.AnimeThemesPathMapping : AnimeThemesConstants.BasePath;

                string root = query.TorrentRoot ?? query.Path ?? defaultBase;
                var result = await _animeThemesMapping.BuildMappingFileAsync(root, query.MapPath, cancellationToken);
                return Ok(result);
            }

            if (query.ApplyMapping)
            {
                string defaultBase = !string.IsNullOrWhiteSpace(ShokoRelay.Settings.AnimeThemesPathMapping) ? ShokoRelay.Settings.AnimeThemesPathMapping : AnimeThemesConstants.BasePath;

                string? sourceRoot = query.TorrentRoot ?? query.Path ?? defaultBase;
                var filterErrors = new List<string>();
                var filterIds = ParseFilterIds(query.Filter, out filterErrors);
                if (filterErrors.Count > 0)
                {
                    return BadRequest(
                        new
                        {
                            status = "error",
                            message = "Invalid filter values.",
                            errors = filterErrors,
                        }
                    );
                }

                var result = await _animeThemesMapping.ApplyMappingAsync(query.MapPath, sourceRoot, query.DryRun, filterIds, cancellationToken);
                return Ok(result);
            }

            if (string.IsNullOrWhiteSpace(query.Path))
                return BadRequest(new { status = "error", message = "path is required" });

            if (query.Play && query.Batch)
                return BadRequest(new { status = "error", message = "play is not supported in batch mode" });

            if (query.Batch)
            {
                var batch = await _animeThemesGenerator.ProcessBatchAsync(query, cancellationToken);
                return Ok(batch);
            }

            if (query.Play)
            {
                var preview = await _animeThemesGenerator.PreviewAsync(query, cancellationToken);
                if (preview.Error != null)
                    return BadRequest(preview.Error);

                if (preview.Preview == null)
                    return NotFound(new { status = "error", message = "Preview failed." });

                if (!string.IsNullOrWhiteSpace(preview.Preview.Title))
                    Response.Headers["X-Theme-Title"] = preview.Preview.Title;

                return File(preview.Preview.Stream, preview.Preview.ContentType, preview.Preview.FileName, enableRangeProcessing: true);
            }

            var single = await _animeThemesGenerator.ProcessSingleAsync(query, cancellationToken);
            if (single.Status == "error")
                return BadRequest(single);

            return Ok(single);
        }

        [HttpGet("vfs")]
        public IActionResult BuildVfs([FromQuery] int? seriesId = null, [FromQuery] bool clean = true, [FromQuery] bool dryRun = false, [FromQuery] bool run = false, [FromQuery] string? filter = null)
        {
            var filterErrors = new List<string>();
            var filterIds = ParseFilterIds(filter, out filterErrors);
            if (filterErrors.Count > 0)
            {
                return BadRequest(
                    new
                    {
                        status = "error",
                        message = "Invalid filter values.",
                        errors = filterErrors,
                    }
                );
            }

            if (seriesId.HasValue && filterIds.Count > 0)
            {
                return BadRequest(new { status = "error", message = "Use either seriesId or filter, not both." });
            }

            if (!run && !dryRun)
            {
                return Ok(
                    new
                    {
                        status = "skipped",
                        message = "Set run=true to build the VFS -OR- dryRun=true to simulate without making changes",
                        seriesId,
                        filter = filterIds.Count > 0 ? string.Join(',', filterIds) : null,
                        clean,
                        dryRun,
                    }
                );
            }

            var result = filterIds.Count > 0 ? _vfsBuilder.Build(filterIds, clean, dryRun) : _vfsBuilder.Build(seriesId, clean, dryRun);
            return Ok(
                new
                {
                    status = "ok",
                    root = result.RootPath,
                    seriesProcessed = result.SeriesProcessed,
                    linksCreated = result.CreatedLinks,
                    plannedLinks = result.PlannedLinks,
                    skipped = result.Skipped,
                    dryRun = result.DryRun,
                    reportPath = result.ReportPath,
                    report = result.ReportContent,
                    errors = result.Errors,
                }
            );
        }
    }
}
