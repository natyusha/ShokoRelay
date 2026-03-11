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

namespace ShokoRelay.Controllers;

[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayInfo.BasePath)]
public partial class ShokoRelayController : ControllerBase
{
    private readonly IMetadataService _metadataService;
    private readonly PlexMetadata _mapper;
    private readonly VfsBuilder _vfsBuilder;
    private readonly AnimeThemesMp3Generator _animeThemesMp3Generator;
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

    private string ApiBase => $"{Request.Scheme}://{Request.Host}{ShokoRelayInfo.BasePath}";

    private const string SeasonPrefix = PlexConstants.SeasonPrefix;
    private const string EpisodePrefix = PlexConstants.EpisodePrefix;
    private const string PartPrefix = PlexConstants.PartPrefix;

    /// <summary>
    /// Constructs the controller with required services and helpers injected by dependency injection.
    /// </summary>
    /// <param name="metadataService">Service for accessing Shoko metadata.</param>
    /// <param name="mapper">Helper that maps Shoko metadata to Plex structures.</param>
    /// <param name="vfsBuilder">VFS builder used for virtual filesystem endpoints.</param>
    /// <param name="animeThemeGenerator">Generator for anime theme VFS data.</param>
    /// <param name="animeThemesMapping">Mapping helper for anime themes.</param>
    /// <param name="configProvider">Configuration provider implementation.</param>
    /// <param name="plexAuth">Plex authentication helper.</param>
    /// <param name="plexLibrary">Plex client for library operations.</param>
    /// <param name="collectionService">Service for collection management.</param>
    /// <param name="criticRatingService">Service for critic ratings.</param>
    /// <param name="watchedSyncService">Service syncing watched state from Plex.</param>
    /// <param name="syncToPlexService">Service syncing watched state to Plex.</param>
    /// <param name="userDataService">User data service.</param>
    /// <param name="userService">User service.</param>
    /// <param name="shokoImportService">Service triggering Shoko imports.</param>
    public ShokoRelayController(
        IMetadataService metadataService,
        PlexMetadata mapper,
        VfsBuilder vfsBuilder,
        AnimeThemesMp3Generator animeThemeGenerator,
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
        _animeThemesMp3Generator = animeThemeGenerator;
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
    /// <summary>
    /// Serves the embedded dashboard UI and its static assets from the plugin folder. An optional <paramref name="path"/> selects a specific asset within the dashboard directory.
    /// </summary>
    /// <param name="path">Subpath within the dashboard folder (optional).</param>
    public IActionResult GetControllerPage([FromRoute] string? path = null)
    {
        string dashboardDir = Path.Combine(_configProvider.PluginDirectory, "dashboard");

        // Select the file: default to index, check for player, otherwise use path
        bool isPlayer = "player".Equals(path, StringComparison.OrdinalIgnoreCase);
        string fileName = (string.IsNullOrWhiteSpace(path) || isPlayer) ? (isPlayer ? "player.cshtml" : "index.cshtml") : path;

        // Security & Path Resolution
        string safePath = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string requested = Path.GetFullPath(Path.Combine(dashboardDir, safePath));
        string dashboardRoot = Path.GetFullPath(dashboardDir);

        if (!requested.StartsWith(dashboardRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(requested))
            return NotFound();

        // Serve Static Assets
        string ext = Path.GetExtension(requested).ToLowerInvariant();
        if (ext != ".cshtml")
        {
            string? contentType = GetDashboardContentTypeForExtension(ext);
            return contentType != null ? PhysicalFile(requested, contentType) : NotFound();
        }

        // Serve HTML Pages (index/player) with dynamic <base> tag
        var html = System.IO.File.ReadAllText(requested);
        if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
        {
            // Find the index of "/dashboard" in the URL and cut there to ensure the base is always the dashboard root, regardless of the sub-page.
            var reqPath = Request.Path.Value ?? "";
            int dashIdx = reqPath.IndexOf("/dashboard", StringComparison.OrdinalIgnoreCase);
            var baseHref = reqPath.Substring(0, dashIdx + 10).TrimEnd('/') + "/";

            var baseTag = $"\n    <base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">";
            html = html.Replace("<head>", "<head>" + baseTag, StringComparison.OrdinalIgnoreCase);
        }

        return Content(html, "text/html");
    }

    [HttpGet("config")]
    /// <summary>
    /// Returns the current configuration payload used by the dashboard UI. The result is sanitized and augmented by <see cref="ConfigProvider"/>.
    /// This also includes the current contents of the "anidb_vfs_overrides.csv" file for convenient editing within the dashboard, if the file exists.
    /// If the file is not present or cannot be read, an empty string is returned for the overrides content. The payload is never null.
    /// </summary>
    public IActionResult GetConfig()
    {
        // provider handles serialization, augmentation and sanitization
        var payload = _configProvider.GetDashboardConfig();
        try
        {
            var path = Path.Combine(ShokoRelay.ConfigDirectory, "anidb_vfs_overrides.csv");
            string overrides = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
            // payload is an anonymous object; create a new object merging the two
            return Ok(new { payload, overrides });
        }
        catch
        {
            return Ok(new { payload, overrides = string.Empty });
        }
    }

    [HttpPost("config")]
    /// <summary>
    /// Accepts a new dashboard configuration from the client and persists it via the <see cref="ConfigProvider"/>. The payload must not be null.
    /// </summary>
    /// <param name="config">New configuration settings.</param>
    public IActionResult SaveConfig([FromBody] RelayConfig config)
    {
        if (config == null)
            return BadRequest(new { status = "error", message = "Config payload is required." });

        _configProvider.SaveSettings(config);
        return Ok(new { status = "ok" });
    }

    [HttpGet("config/schema")]
    /// <summary>
    /// Builds and returns a JSON schema describing <see cref="RelayConfig"/> properties; used by the dashboard to render dynamic forms.
    /// </summary>
    public IActionResult GetConfigSchema()
    {
        var props = BuildConfigSchema(typeof(RelayConfig), "");
        return Ok(new { properties = props });
    }

    [HttpGet("logs/{fileName}")]
    /// <summary>
    /// Returns the contents of a log file stored under the plugin's "logs" directory.
    /// The <paramref name="fileName"/> must be provided and refer to an existing file, otherwise a 404/400 is returned.
    /// </summary>
    /// <param name="fileName">Name of the log file to retrieve.</param>
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
    /// <summary>
    /// Announces the media provider capabilities to Plex. This endpoint is called by Plex agents to discover supported types and features.
    /// </summary>
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
    /// <summary>
    /// Attempts to match a Plex media request to a Shoko series.
    /// The search may use the <paramref name="name"/> query parameter, the <see cref="PlexMatchBody"/> body, or even a manual override contained therein.
    /// </summary>
    /// <param name="name">Optional query parameter containing a lookup name.</param>
    /// <param name="body">Optional JSON body with filename/title/manual fields.</param>
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
            seriesId = TextHelper.ExtractSeriesId(rawPath);
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
    /// <summary>
    /// Retrieve metadata for a Plex collection representing the Shoko group identified by <paramref name="groupId"/>.
    /// </summary>
    /// <param name="groupId">Shoko group identifier.</param>
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
    /// <summary>
    /// Fetch the poster image file for a collection, if one exists on disk.
    /// </summary>
    /// <param name="groupId">ID of the Shoko group whose collection poster is requested.</param>
    public IActionResult GetCollectionPoster(int groupId)
    {
        var group = _metadataService.GetShokoGroupByID(groupId);
        if (group == null)
            return NotFound();

        var primarySeries = group.MainSeries ?? group.Series?.FirstOrDefault();
        if (primarySeries == null)
            return NotFound();

        var posterPath = PlexHelper.FindCollectionPosterPathByGroup(primarySeries, groupId);
        if (string.IsNullOrWhiteSpace(posterPath) || !System.IO.File.Exists(posterPath))
            return NotFound();

        string ext = Path.GetExtension(posterPath).ToLowerInvariant();
        string contentType = GetCollectionContentTypeForExtension(ext) ?? "application/octet-stream";

        return PhysicalFile(posterPath, contentType);
    }

    [HttpGet("metadata/{ratingKey}")]
    /// <summary>
    /// Return Plex-formatted metadata for the specified <paramref name="ratingKey"/>, which can represent a show, season, or episode.
    /// The <paramref name="includeChildren"/> flag controls whether child items are embedded.
    /// </summary>
    /// <param name="ratingKey">Plex-style rating key (show/season/episode).</param>
    /// <param name="includeChildren">Nonzero value requests child metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0, CancellationToken cancellationToken = default)
    {
        var ctx = GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

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

        var showMeta = _mapper.MapSeries(ctx.Series, ctx.Titles);

        if (includeChildren == 1)
        {
            var seasons = ctx.FileData.Seasons.Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle, cancellationToken)).ToList();

            ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
        }

        return WrapInContainer(showMeta);
    }

    [HttpGet("metadata/{ratingKey}/children")]
    /// <summary>
    /// List immediate child items (seasons or episodes) of the provided <paramref name="ratingKey"/>.
    /// </summary>
    /// <param name="ratingKey">Parent rating key.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
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
    /// <summary>
    /// Retrieve the grandchildren of a given rating key (e.g. episodes when the key represents a show).
    /// </summary>
    /// <param name="ratingKey">Parent rating key for which to fetch grandchildren.</param>
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
    /// <summary>
    /// Fetch image metadata (posters, thumbnails, etc.) associated with a particular rating key.
    /// </summary>
    /// <param name="ratingKey">Rating key whose images are requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IActionResult GetImages(string ratingKey, CancellationToken cancellationToken = default)
    {
        var ctx = GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        object[] images;
        if (ratingKey.Contains(PlexConstants.EpisodePrefix))
        {
            var epIdPart = ratingKey.Split(PlexConstants.EpisodePrefix)[1];
            int epId;
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
            images = episode != null ? ExtractImages(_mapper.MapEpisode(episode, GetPlexCoordinates(episode), ctx.Series, ctx.Titles, partIdx, null)) : Array.Empty<object>();
        }
        else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            int sNum = int.Parse(ratingKey.Split(PlexConstants.SeasonPrefix)[1]);
            images = ExtractImages(_mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle, cancellationToken));
        }
        else
        {
            images = ExtractImages(_mapper.MapSeries(ctx.Series, ctx.Titles));
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

    /// <summary>
    /// Initiates the PIN‑based Plex authentication flow and returns both the PIN identifier and authorization URL required by the user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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
    /// <summary>
    /// Polls the Plex authentication status for a previously created PIN; if completed the Plex token is persisted in the configuration file.
    /// </summary>
    /// <param name="pinId">Identifier returned by <see cref="StartPlexAuth"/>.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
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
                await _configProvider.RefreshAdminUsername(_plexAuth, cancellationToken); // Fetch and save the Admin Username immediately after linking
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to fetch admin name during auth: {ex.Message}");
            }

            try
            {
                string clientIdentifier = _configProvider.GetPlexClientIdentifier();
                var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(pin.AuthToken, clientIdentifier, cancellationToken).ConfigureAwait(false);
                PersistDiscoveryResults(discovery);
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

    /// <summary>
    /// Revokes the stored Plex token and removes all persisted Plex discovery data (servers, libraries) from the configuration file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Re-discovers Plex servers and libraries using the stored token and updates the persisted discovery data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("plex/auth/refresh")]
    public async Task<IActionResult> RefreshPlexLibraries(CancellationToken cancellationToken = default)
    {
        var token = _configProvider.GetPlexToken();
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { status = "error", message = "Plex token is missing." });

        await _configProvider.RefreshAdminUsername(_plexAuth, cancellationToken);
        string clientIdentifier = _configProvider.GetPlexClientIdentifier();

        try
        {
            var discovery = await _plexAuth.DiscoverShokoLibrariesAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);
            PersistDiscoveryResults(discovery);

            var libs = CollectDiscoveredLibraries(discovery.ShokoLibraries);
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
    /// <summary>
    /// Triggers the generation of Plex collections for a given set of series; supply either a single <paramref name="seriesId"/> or a <paramref name="filter"/>, not both.
    /// </summary>
    /// <param name="seriesId">Optional Shoko series ID to build collections for.</param>
    /// <param name="filter">Optional comma‑separated list of series IDs or search
    /// filter expression.</param>
    /// <param name="cancellationToken">Token to observe for request cancellation.</param>
    public async Task<IActionResult> BuildPlexCollections([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var targets = _plexLibrary.GetConfiguredTargets();
        if (targets == null || targets.Count == 0)
            return NoPlexTargetsResponse(seriesList);

        var r = await _collectionService.BuildCollectionsAsync(seriesList, cancellationToken).ConfigureAwait(false);

        WriteReportLog("collections-report.log", sb => LogHelper.BuildCollectionsReport(sb, r));

        return Ok(
            new
            {
                status = "ok",
                processed = r.Processed,
                created = r.Created,
                uploaded = r.Uploaded,
                seasonPostersUploaded = r.SeasonPostersUploaded,
                skipped = r.Skipped,
                errors = r.Errors,
                deletedEmptyCollections = r.DeletedEmptyCollections,
                createdCollections = r.CreatedCollections,
                errorsList = r.ErrorsList.Take(200).ToList(),
                logUrl = $"{ApiBase}/logs/collections-report.log",
            }
        );
    }

    [HttpGet("plex/collections/posters")]
    /// <summary>
    /// Applies poster artwork to Plex collections for the given series set; specify either <paramref name="seriesId"/> or <paramref name="filter"/>, not both.
    /// </summary>
    /// <param name="seriesId">Optional single series ID to operate on.</param>
    /// <param name="filter">Optional filter expression or comma separated list of IDs.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    public async Task<IActionResult> ApplyCollectionPosters([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var targets = _plexLibrary.GetConfiguredTargets();
        if (targets == null || targets.Count == 0)
            return NoPlexTargetsResponse(seriesList);

        var r = await _collectionService.ApplyCollectionPostersAsync(seriesList, cancellationToken).ConfigureAwait(false);

        return Ok(
            new
            {
                status = "ok",
                processed = r.Processed,
                uploaded = r.Uploaded,
                skipped = r.Skipped,
                errors = r.Errors,
                errorsList = r.ErrorsList.Take(200).ToList(),
            }
        );
    }

    /// <summary>
    /// Applies critic/audience ratings from Shoko metadata to the corresponding Plex library items; supply either <paramref name="seriesId"/> or <paramref name="filter"/>, not both.
    /// </summary>
    /// <param name="seriesId">Optional single series to update.</param>
    /// <param name="filter">Optional comma-separated list of series IDs.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    [HttpGet("plex/ratings/apply")]
    public async Task<IActionResult> ApplyAudienceRatings([FromQuery] int? seriesId = null, [FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var guard = ValidatePlexFilterRequest(seriesId, filter, out var seriesList, out _);
        if (guard != null)
            return guard;

        var allowedIds = new HashSet<int>(seriesList.Select(s => s?.ID ?? 0));
        var result = await _criticRatingService.ApplyRatingsAsync(allowedIds, cancellationToken).ConfigureAwait(false);

        WriteReportLog("ratings-report.log", sb => LogHelper.BuildRatingsReport(sb, result));

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
                logUrl = $"{ApiBase}/logs/ratings-report.log",
            }
        );
    }

    /// <summary>
    /// Manually triggers both collection building and critic-rating application for all series, then marks the automation schedule as run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Receives webhook events sent by Plex to the relay plugin and processes them if auto-scrobble is enabled.
    /// Distinguishes between the actual Admin account and managed users.
    /// Info: https://support.plex.tv/articles/115002267687-webhooks/
    /// </summary>
    [HttpPost("plex/webhook")]
    public async Task<IActionResult> PluginPlexWebhook()
    {
        var cfg = _configProvider.GetSettings();
        if (!cfg.AutoScrobble)
            return Ok(new { status = "ignored", reason = "auto_scrobble_disabled" });

        // Safely extract and deserialize the payload from the Request (form or body)
        var evt = await ExtractPlexWebhookPayloadAsync().ConfigureAwait(false);

        if (evt == null || evt.Metadata == null)
            return BadRequest(new { status = "error", message = "missing or invalid payload" });

        // Determine if payload is a scrobble or rating event
        bool isScrobble = string.Equals(evt.Event, "media.scrobble", StringComparison.OrdinalIgnoreCase);
        bool isRate = string.Equals(evt.Event, "media.rate", StringComparison.OrdinalIgnoreCase);

        // Exit early if the event type isn't supported or ratings sync is disabled
        if (!(isScrobble || (isRate && cfg.ShokoSyncWatchedIncludeRatings)))
            return Ok(new { status = "ignored", reason = "unsupported_event_type" });

        // Strict Validation: Server UUID + Identity Check (Managed User vs Admin)
        var (allowed, reason) = await ValidateWebhookSource(evt, cfg, HttpContext.RequestAborted);
        if (!allowed)
        {
            Logger.Info("Plex Webhook Ignored: {Reason} | User: {User} | Event: {Event}", reason, evt.Account?.Title, evt.Event);
            return Ok(new { status = "ignored", reason });
        }

        // Try to extract Shoko episode id from the Plex GUID
        int? shokoEpisodeId = SyncHelper.TryParseShokoEpisodeIdFromGuid(evt.Metadata.Guid);
        if (!shokoEpisodeId.HasValue)
            return Ok(new { status = "ignored", reason = "no_shoko_guid" });

        var shokoEpisode = _metadataService.GetShokoEpisodeByID(shokoEpisodeId.Value);
        if (shokoEpisode == null)
            return Ok(new { status = "ignored", reason = "episode_not_found" });

        // Resolve the primary Shoko user to apply the sync to
        Shoko.Abstractions.User.IUser? user = null;
        try
        {
            // Plugin defaults to first Shoko user
            user = _userService.GetUsers().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Webhook: error enumerating Shoko users");
        }

        if (user == null)
            return Ok(new { status = "ignored", reason = "no_shoko_user" });

        // Prepare metadata for logging (Series Name - S01E05)
        string seriesName = evt.Metadata.GrandparentTitle ?? shokoEpisode.Series?.PreferredTitle?.Value ?? "Unknown Series";
        string seasonEp = $"S{(evt.Metadata.ParentIndex ?? 0):D2}E{(evt.Metadata.Index ?? 0):D2}";

        // Handle Rating Events
        if (isRate)
        {
            double? ratingValue = evt.Metadata.UserRating;
            if (!ratingValue.HasValue)
                return Ok(new { status = "ignored", reason = "no_rating" });

            await _userDataService.RateEpisode(shokoEpisode, user, ratingValue.Value).ConfigureAwait(false);
            Logger.Info("Plex rating applied: user='{User}', series='{Series}', episode='{SeasonEp}', rating={Rating}", evt.Account?.Title, seriesName, seasonEp, ratingValue);

            return Ok(
                new
                {
                    status = "ok",
                    rated = true,
                    rating = ratingValue,
                }
            );
        }

        // Handle Scrobble (Watched Status) Events
        DateTime? watchedAt = null;
        if (evt.Metadata.LastViewedAt.HasValue && evt.Metadata.LastViewedAt.Value > 0)
        {
            try
            {
                watchedAt = DateTimeOffset.FromUnixTimeSeconds(evt.Metadata.LastViewedAt.Value).UtcDateTime;
            }
            catch
            {
                /* Fallback to server time implicitly if timestamp is invalid */
            }
        }

        var saved = await _userDataService.SetEpisodeWatchedStatus(shokoEpisode, user, true, watchedAt).ConfigureAwait(false);
        bool updated = saved != null;

        if (updated)
        {
            Logger.Info("Plex scrobble applied: user='{User}', series='{Series}', episode='{SeasonEp}'", evt.Account?.Title, seriesName, seasonEp);
        }

        return Ok(new { status = "ok", marked = updated });
    }

    #endregion

    #region Virtual File System

    /// <summary>
    /// Builds (or previews) the VFS symlink tree for the configured import folders. Set <paramref name="run"/> to <c>true</c> to actually create links; optionally restrict to specific series via <paramref name="filter"/>.
    /// </summary>
    /// <param name="clean">If true, removes stale links before building.</param>
    /// <param name="run">Must be true to execute; otherwise returns a dry-run summary.</param>
    /// <param name="filter">Optional comma-separated series IDs to restrict the build.</param>
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
                logUrl = $"{ApiBase}/logs/vfs-report.log",
            }
        );
    }

    [HttpPost("vfs/overrides")]
    /// <summary>
    /// Persist the contents of an <c>anidb_vfs_overrides.csv</c> file supplied by the client.
    /// The endpoint writes the raw text body to the config directory and reloads the groups so that subsequent VFS/metadata operations immediately honour any changes.
    /// </summary>
    /// <param name="content">Entire CSV payload (may be empty to clear the file).</param>
    /// <returns>
    /// <c>200 OK</c> with <c>{status:"ok"}</c> on success, or <c>400 BadRequest</c> with an error message if the write failed.
    /// </returns>
    public IActionResult SaveVfsOverrides([FromBody] string content)
    {
        try
        {
            string path = Path.Combine(ShokoRelay.ConfigDirectory, "anidb_vfs_overrides.csv");
            System.IO.File.WriteAllText(path, content ?? string.Empty);
            OverrideHelper.EnsureLoaded();
            return Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    #endregion

    #region Shoko: Automation

    [HttpGet("shoko/remove-missing")]
    [HttpPost("shoko/remove-missing")]
    /// <summary>
    /// Removes missing video files from the Shoko database; when <paramref name="dryRun"/> is true, no changes are made and a report is returned.
    /// </summary>
    /// <param name="dryRun">If true skips deletion and returns a summary.</param>
    public async Task<IActionResult> RemoveMissingFiles([FromQuery] bool? dryRun = null)
    {
        var (data, error) = await PerformRemoveMissingFilesAsync(dryRun);
        return error != null ? StatusCode(500, new { status = "error", message = error }) : Ok(data);
    }

    /// <summary>
    /// Initiates a Shoko import scan for source-type import folders. Only unrecognized files are scanned by default.
    /// </summary>
    /// <param name="onlyUnrecognized">If true only unrecognized files are imported.</param>
    [HttpPost("shoko/import")]
    public async Task<IActionResult> RunShokoImport([FromQuery] bool onlyUnrecognized = true)
    {
        var (data, error) = await PerformShokoImportAsync(false);
        return error != null ? StatusCode(500, new { status = "error", message = error }) : Ok(data);
    }

    /// <summary>
    /// Triggers an immediate Shoko import and resets the automation schedule so the next run occurs after the configured interval.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("shoko/import/start")]
    public async Task<IActionResult> StartShokoImportNow(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.GetSettings();
        var (data, error) = await PerformShokoImportAsync(true);

        if (error != null)
            return StatusCode(500, new { status = "error", message = error });

        // Add the extra scheduling info required by this specific endpoint
        var freqHours = settings?.ShokoImportFrequencyHours ?? 0;
        return Ok(
            new
            {
                status = "ok",
                triggered = true,
                scheduled = freqHours > 0,
                nextRunInHours = freqHours,
                scanned = ((dynamic)data!).scanned,
            }
        );
    }

    #endregion

    #region Sync Watched

    [HttpGet("sync-watched")]
    [HttpPost("sync-watched")]
    /// <summary>
    /// Synchronizes watched status between Plex and Shoko with options to control scope, ratings sync and imports.
    /// </summary>
    /// <param name="dryRun">If non‑null the string "true" causes no updates.</param>
    /// <param name="sinceHours">Only consider activity since this many hours ago.</param>
    /// <param name="ratings">Whether to sync rating changes as well.</param>
    /// <param name="import">True to import new episodes during sync.</param>
    /// <param name="excludeAdmin">If true, ignore admin users' plex activity.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
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

        var (parsedDryRun, dryRunError) = ParseDryRunParam(dryRun);
        if (dryRunError != null)
            return dryRunError;

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
                // pass excludeAdmin through to the Shoko sync service; note scheduling also uses the new config property
                result = await _watchedSyncService.SyncWatchedAsync(parsedDryRun, sinceHours, includeRatings, excludeAdmin.GetValueOrDefault(false), cancellationToken).ConfigureAwait(false);
            }

            WriteReportLog("sync-watched-report.log", sb => LogHelper.BuildSyncWatchedReport(sb, result, direction, parsedDryRun, includeRatings));

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
                    logUrl = $"{ApiBase}/logs/sync-watched-report.log",
                }
            );
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "SyncPlexWatched failed");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    /// <summary>
    /// Triggers an immediate Plex→Shoko watched-state sync and resets the automation schedule so the next run occurs after the configured interval.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("sync-watched/start")]
    public async Task<IActionResult> StartWatchedSyncNow(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.GetSettings();
        int freqHours = settings?.ShokoSyncWatchedFrequencyHours ?? 0;

        if (_watchedSyncService == null)
            return StatusCode(501, new { status = "error", message = "Watched sync service not available" });

        try
        {
            var result = await _watchedSyncService
                .SyncWatchedAsync(false, freqHours, settings?.ShokoSyncWatchedIncludeRatings ?? false, settings?.ShokoSyncWatchedExcludeAdmin ?? false, cancellationToken)
                .ConfigureAwait(false);

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
    /// <summary>
    /// Apply the anime‑themes mapping file to the directory structure, optionally restricting to a subset of series via <paramref name="filter"/>.
    /// </summary>
    /// <param name="filter">Comma‑separated list of Shoko series IDs to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
        if (validation != null)
            return validation;

        string resolvedMapPath = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtMapFileName);
        if (!System.IO.File.Exists(resolvedMapPath))
            return BadRequest(new { status = "error", message = "Mapping file not found" });

        var result = await _animeThemesMapping.ApplyMappingAsync(filterIds, cancellationToken).ConfigureAwait(false);

        // Only save the full cache during UNFILTERED builds
        if (filterIds == null || filterIds.Count == 0)
        {
            try
            {
                // Resolve the root folder name once (e.g., "!AnimeThemes")
                string themeRoot = VfsShared.ResolveAnimeThemesFolderName();

                // Load xrefs: Key is the filepath from the CSV, Value is the VideoId
                var xrefs = System
                    .IO.File.ReadAllLines(resolvedMapPath)
                    .Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l))
                    .Select(l => TextHelper.SplitCsvLine(l))
                    .Where(f => f.Length > 1)
                    .ToDictionary(f => f[0], f => f[1], StringComparer.OrdinalIgnoreCase);

                var cacheLines = new List<string>();
                foreach (var plan in result.Planned)
                {
                    // plan looks like: "VfsPath <- ../../../!AnimeThemes/subdir/file.webm"
                    var parts = plan.Split(" <- ");
                    if (parts.Length < 2)
                        continue;

                    string vfsPath = parts[0];
                    string symlinkTarget = parts[1].Replace('\\', '/'); // Standardize slashes

                    // Extract the relative path from the symlink target. Look for the position of the root folder name and take everything after it.
                    string lookupKey = string.Empty;
                    int rootIdx = symlinkTarget.IndexOf("/" + themeRoot + "/", StringComparison.OrdinalIgnoreCase);
                    if (rootIdx != -1)
                        lookupKey = symlinkTarget.Substring(rootIdx + themeRoot.Length + 1); // Skip /!AnimeThemes
                    else if (symlinkTarget.Contains(themeRoot + "/"))
                        lookupKey = symlinkTarget.Substring(symlinkTarget.IndexOf(themeRoot + "/") + themeRoot.Length);

                    // Ensure lookupKey starts with / to match CSV format
                    if (!lookupKey.StartsWith("/"))
                        lookupKey = "/" + lookupKey;

                    if (xrefs.TryGetValue(lookupKey, out var vid))
                        cacheLines.Add($"{vfsPath}|{vid}");
                    else
                        cacheLines.Add($"{vfsPath}|0");
                }

                System.IO.File.WriteAllLines(Path.Combine(_configProvider.ConfigDirectory, "webm_animethemes.cache"), cacheLines);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to save webm cache with IDs");
            }
        }

        WriteReportLog("at-vfs-build-report.log", sb => LogHelper.BuildAtVfsBuildReport(sb, result, filterIds ?? new List<int>()));

        return Ok(
            new
            {
                result.LinksCreated,
                result.Skipped,
                result.SeriesMatched,
                result.Errors,
                elapsed = result.Elapsed.ToString(),
                logUrl = $"{ApiBase}/logs/at-vfs-build-report.log",
            }
        );
    }

    [HttpGet("animethemes/vfs/map")]
    /// <summary>
    /// Generate the anime‑themes mapping CSV file by scanning the configured directory structure, or test a single filename.
    /// When `testPath` is provided, tests that single filename instead of building the full mapping.
    /// </summary>
    /// <param name="testPath">Optional webm filename to test (e.g., "OP1.webm"). When provided, returns metadata for that file instead of building the full CSV.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> AnimeThemesVfsMap(string? testPath = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(testPath))
        {
            // Test mode: process a single filename
            var (entry, error, generatedFilename) = await _animeThemesMapping.TestMappingEntryAsync(testPath, cancellationToken).ConfigureAwait(false);
            if (error != null)
                return Ok(
                    new
                    {
                        status = "error",
                        testPath,
                        error,
                    }
                );

            return Ok(
                new
                {
                    status = "ok",
                    testPath,
                    generatedFilename,
                    csvLine = entry != null ? AnimeThemesMapping.SerializeMappingEntry(entry) : null,
                    entry = new
                    {
                        videoId = entry?.VideoId,
                        anidbId = entry?.AniDbId,
                        nc = entry?.NC,
                        slug = entry?.Slug,
                        version = entry?.Version,
                        artistName = entry?.ArtistName,
                        songTitle = entry?.SongTitle,
                        lyrics = entry?.Lyrics,
                        subbed = entry?.Subbed,
                        uncen = entry?.Uncen,
                        nsfw = entry?.NSFW,
                        spoiler = entry?.Spoiler,
                        source = entry?.Source,
                        resolution = entry?.Resolution,
                        episodes = entry?.Episodes,
                    },
                }
            );
        }

        // Normal mode: build full CSV
        var result = await _animeThemesMapping.BuildMappingFileAsync(cancellationToken).ConfigureAwait(false);
        WriteReportLog("at-vfs-map-report.log", sb => LogHelper.BuildAtVfsMapReport(sb, result));
        return Ok(new { result, logUrl = $"{ApiBase}/logs/at-vfs-map-report.log" });
    }

    [HttpPost("animethemes/vfs/import")]
    /// <summary>
    /// Download and import the latest anime‑themes mapping file from the official repository URL, overwriting the local map if successful.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
    {
        // use the raw URL directly; avoid the GitHub API which truncates large files
        const string rawUrl = AnimeThemesHelper.AtRawMapUrl + AnimeThemesHelper.AtMapFileName;
        var (count, log) = await _animeThemesMapping.ImportMappingFromUrlAsync(rawUrl, cancellationToken).ConfigureAwait(false);
        return Ok(new { status = "ok", count });
    }

    #endregion

    #region AnimeThemes: MP3

    [HttpGet("animethemes/mp3")]
    /// <summary>
    /// Generates or previews MP3 audio for anime themes based on the supplied <paramref name="query"/>, which encodes path and selection criteria; setting Batch processes multiple folders.
    /// </summary>
    /// <param name="query">Details including path, slug, offset, batch and force flags.</param>
    /// <param name="cancellationToken">Token to cancel processing.</param>
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
            var batch = await _animeThemesMp3Generator.ProcessBatchAsync(query, cancellationToken);
            foreach (var item in batch.Items.Where(i => i.Status == "ok" && !string.IsNullOrWhiteSpace(i.Folder)))
                _animeThemesMp3Generator.AddToThemeMp3Cache(item.Folder);

            WriteReportLog("at-mp3-report.log", sb => LogHelper.BuildAtMp3Report(sb, batch));

            return Ok(new { batch, logUrl = $"{ApiBase}/logs/at-mp3-report.log" });
        }

        var single = await _animeThemesMp3Generator.ProcessSingleAsync(query, cancellationToken);
        if (single.Status == "error")
            return BadRequest(single);

        if (single.Status == "ok" && !string.IsNullOrWhiteSpace(single.Folder))
            _animeThemesMp3Generator.AddToThemeMp3Cache(single.Folder);
        return Ok(single);
    }

    /// <summary>
    /// Returns the folder path of a random Theme.mp3 from the startup cache. Optionally pass <c>refresh=true</c> to re-scan.
    /// </summary>
    [HttpGet("animethemes/mp3/random")]
    public IActionResult AnimeThemesMp3Random([FromQuery] bool refresh = false)
    {
        if (refresh)
            _animeThemesMp3Generator.RefreshThemeMp3Cache();

        var folders = _animeThemesMp3Generator.GetCachedThemeMp3Folders();
        if (folders.Count == 0)
            return NotFound(new { status = "error", message = "No Theme.mp3 files found in any import folder." });

        var picked = folders[Random.Shared.Next(folders.Count)];
        return Ok(new { status = "ok", path = picked });
    }

    [HttpGet("animethemes/mp3/stream")]
    [HttpHead("animethemes/mp3/stream")]
    /// <summary>
    /// Streams an existing Theme.mp3 file from the specified path for in-browser playback.
    /// </summary>
    /// <param name="path">The folder path containing the Theme.mp3 file.</param>
    public IActionResult AnimeThemesMp3Stream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { status = "error", message = "path is required" });

        // Allow Plex-style path reverse mapping
        string resolved = _plexLibrary.MapPlexPathToShokoPath(path);
        string themePath = Path.Combine(Path.GetFullPath(resolved), "Theme.mp3");

        if (!System.IO.File.Exists(themePath))
            return NotFound(new { status = "error", message = "Theme.mp3 not found at the specified path." });

        // Embed ID3 tags as response headers so the dashboard can display "Now Playing" info
        try
        {
            var tags = ReadId3v2Tags(themePath);
            if (tags.TryGetValue("TIT2", out var title) && !string.IsNullOrWhiteSpace(title))
                Response.Headers["X-Theme-Title"] = title;
            if (tags.TryGetValue("TIT3", out var slug) && !string.IsNullOrWhiteSpace(slug))
                Response.Headers["X-Theme-Slug"] = slug;
            if (tags.TryGetValue("TPE1", out var artist) && !string.IsNullOrWhiteSpace(artist))
                Response.Headers["X-Theme-Artist"] = artist;
            if (tags.TryGetValue("TALB", out var album) && !string.IsNullOrWhiteSpace(album))
                Response.Headers["X-Theme-Album"] = album;
            Response.Headers["Access-Control-Expose-Headers"] = "X-Theme-Title, X-Theme-Slug, X-Theme-Artist, X-Theme-Album";
        }
        catch
        { // tag reading is best-effort
        }

        // Stream via FileStream so the length is captured at open time.
        // Avoids ArgumentOutOfRangeException when the file is being regenerated concurrently and its size changes between stat and send.
        var stream = new FileStream(themePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/mpeg", enableRangeProcessing: true);
    }

    #endregion

    #region AnimeThemes: WebM

    [HttpGet("animethemes/webm/tree")]
    /// <summary>
    /// Returns the webm VFS cache as a tree of groups, series and files suitable for the dashboard video player modal.
    /// Each series ID in the cache path is resolved to a display title and parent group name.
    /// </summary>
    public IActionResult AnimeThemesWebmTree()
    {
        string cachePath = Path.Combine(_configProvider.ConfigDirectory, "webm_animethemes.cache");
        if (!System.IO.File.Exists(cachePath))
            return Ok(new { status = "empty" });

        string[] lines;
        try
        {
            lines = System.IO.File.ReadAllLines(cachePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }
        catch
        {
            return Ok(new { status = "empty" });
        }

        var items = new List<object>();
        var seriesTitleCache = new Dictionary<int, (string GroupTitle, string SeriesTitle)>();

        foreach (var line in lines)
        {
            // Split by the pipe character added in the generation step
            var pipeParts = line.Split('|');
            string pathRaw = pipeParts[0];
            int videoId = (pipeParts.Length > 1 && int.TryParse(pipeParts[1], out var vid)) ? vid : 0;

            string normalized = pathRaw.Replace('\\', '/').Trim();
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            int? seriesId = null;
            string? fileName = null;

            for (int i = 0; i < segments.Length; i++)
            {
                if (int.TryParse(segments[i], out int sid))
                {
                    seriesId = sid;
                    fileName = segments[^1];
                    break;
                }
            }

            if (!seriesId.HasValue || fileName == null)
                continue;

            if (!seriesTitleCache.TryGetValue(seriesId.Value, out var titles))
            {
                var series = _metadataService.GetShokoSeriesByID(seriesId.Value);
                if (series != null)
                {
                    var resolved = TextHelper.ResolveFullSeriesTitles(series);
                    var group = _metadataService.GetShokoGroupByID(series.TopLevelGroupID);
                    titles = (group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle!.Value : resolved.DisplayTitle, resolved.DisplayTitle);
                }
                else
                    titles = ($"Series {seriesId.Value}", $"Series {seriesId.Value}");

                seriesTitleCache[seriesId.Value] = titles;
            }

            items.Add(
                new
                {
                    group = titles.GroupTitle,
                    series = titles.SeriesTitle,
                    file = Path.GetFileNameWithoutExtension(fileName),
                    path = normalized,
                    videoId = videoId,
                }
            );
        }
        return Ok(new { status = "ok", items });
    }

    [HttpGet("animethemes/webm/stream")]
    [HttpHead("animethemes/webm/stream")]
    /// <summary>
    /// Streams a .webm theme file from a VFS path for in-browser playback.
    /// </summary>
    /// <param name="path">The full path to the .webm file.</param>
    public IActionResult AnimeThemesWebmStream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { status = "error", message = "path is required" });

        string resolved = _plexLibrary.MapPlexPathToShokoPath(path);
        string fullPath = Path.GetFullPath(resolved);

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { status = "error", message = "File not found." });

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "video/webm", enableRangeProcessing: true);
    }

    [HttpGet("animethemes/webm/favourites")]
    /// <summary>
    /// Returns the list of videoIds marked as favourites from favs_animethemes.cache.
    /// </summary>
    public IActionResult GetAnimeThemesFavourites()
    {
        string path = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtFavsFileName);
        if (!System.IO.File.Exists(path))
            return Ok(Array.Empty<int>());

        try
        {
            var ids = System
                .IO.File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                .Select(l => int.TryParse(l, out int id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            return Ok(ids);
        }
        catch
        {
            return Ok(Array.Empty<int>());
        }
    }

    [HttpPost("animethemes/webm/favourites")]
    /// <summary>
    /// Toggles a videoId in the favourites file.
    /// </summary>
    public IActionResult UpdateAnimeThemesFavourite([FromBody] int videoId)
    {
        if (videoId <= 0)
            return BadRequest(new { message = "Invalid VideoId. Ensure you have generated the AnimeThemes Mapping CSV." });

        string path = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtFavsFileName);
        var ids = new HashSet<int>();

        if (System.IO.File.Exists(path))
        {
            try
            {
                var lines = System.IO.File.ReadAllLines(path);
                foreach (var l in lines)
                    if (int.TryParse(l.Trim(), out int id))
                        ids.Add(id);
            }
            catch { }
        }

        bool isFav = false;
        if (ids.Contains(videoId))
            ids.Remove(videoId);
        else
        {
            ids.Add(videoId);
            isFav = true;
        }

        try
        {
            System.IO.File.WriteAllLines(path, ids.Select(i => i.ToString()));
            return Ok(new { videoId, isFavourite = isFav });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    #endregion
}
