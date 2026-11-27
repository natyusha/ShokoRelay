using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Shoko.Abstractions.Video.Services;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Controllers;

/// <summary>Provides the core Metadata Provider endpoints for the Plex Agent.</summary>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="mapper">Plex metadata mapper.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="logger">Logger instance.</param>
[ApiController]
[Route(ShokoRelayConstants.BasePath)]
[Route(ShokoRelayConstants.BasePath + "/movie")]
[Route(ShokoRelayConstants.BasePath + "/options/{overrides}")]
[Route(ShokoRelayConstants.BasePath + "/movie/options/{overrides}")]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
public class MetadataController(
    IMetadataService metadataService,
    PlexMetadata mapper,
    ConfigProvider configProvider,
    PlexClient plexClient,
    IVideoService videoService,
    IFileSystem fileSystem,
    ILogger<MetadataController> logger
) : ShokoRelayBaseController(configProvider, metadataService, plexClient, logger)
{
    #region Provider Descriptor

    /// <summary>Announces the media provider capabilities to Plex based on the requested endpoint type (TV or Movie).</summary>
    /// <returns>A media provider descriptor object.</returns>
    [HttpGet]
    public IActionResult GetMediaProvider() =>
        SafeRequest(() =>
        {
            bool isMovie = IsMovieRoute() || string.Equals(Request.Query["type"], "1");
            return Ok(
                new
                {
                    MediaProvider = new
                    {
                        identifier = isMovie ? ShokoRelayConstants.MovieAgentScheme : ShokoRelayConstants.AgentScheme,
                        title = isMovie ? $"{ShokoRelayConstants.Name} Movie" : ShokoRelayConstants.Name,
                        version = ShokoRelayConstants.Version,
                        Types = (isMovie ? [PlexConstants.TypeMovie] : new[] { PlexConstants.TypeShow, PlexConstants.TypeSeason, PlexConstants.TypeEpisode }).Select(typeId => new
                        {
                            type = typeId,
                            Scheme = new[] { new { scheme = isMovie ? ShokoRelayConstants.MovieAgentScheme : ShokoRelayConstants.AgentScheme } },
                        }),
                        Feature = new[] { new { type = "metadata", key = "/metadata" }, new { type = "match", key = "/matches" }, new { type = "collection", key = "/collections" } },
                    },
                }
            );
        });

    #endregion

    #region Matching

    /// <summary>Attempts to match a Plex media lookup request to a Shoko series, season, episode, or movie.</summary>
    /// <remarks>
    ///   Manual matching (<c>manual=1</c>) is intentionally limited to direct Shoko/AniDB/Movie IDs (<c>123</c>, <c>a123</c>, <c>m890</c>) rather than title or external GUID searches.
    ///   Users should not need manual matching because all library media originates from the VFS and already encodes the expected identifier in its path.
    /// </remarks>
    /// <param name="body">Match parameters including filename, title, media type, and indices.</param>
    /// <returns>A match result MediaContainer.</returns>
    [HttpGet]
    [HttpPost]
    [Route("matches")]
    public IActionResult Match([FromBody] PlexMatchBody? body = null) =>
        SafeRequest(() =>
        {
            string? rawPath = body?.Filename ?? Request.Query["filename"];
            string? title = body?.Title ?? Request.Query["title"];
            int? manual = body?.Manual ?? (int.TryParse(Request.Query["manual"], out var manualFlag) ? manualFlag : null);
            int? manualEpisodeId =
                manual == 1 && title?.StartsWith(PlexConstants.MoviePrefix, StringComparison.OrdinalIgnoreCase) == true && int.TryParse(title[PlexConstants.MoviePrefix.Length..], out var movieEpisodeId)
                    ? movieEpisodeId
                    : null;
            int? manualId = int.TryParse(title, out var manualSeriesId)
                ? manualSeriesId
                : (
                    title?.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase) == true && int.TryParse(title[1..], out var anidbId)
                        ? MetadataService.GetShokoSeriesByAnidbID(anidbId)?.LocalID
                        : (manualEpisodeId.HasValue ? MetadataService.GetShokoEpisodeByID(manualEpisodeId.Value)?.ShokoSeriesID : null)
                );
            int? id = (manual == 1 && manualId.HasValue) ? manualId : (string.IsNullOrWhiteSpace(rawPath) ? null : ExtractSeriesId(rawPath));

            if (!id.HasValue)
                return EmptyMatch();

            string? typeText = body?.Type?.ToString() ?? Request.Query["type"];
            int typeInt = typeText?.ToLowerInvariant() switch
            {
                "1" or "movie" => PlexConstants.TypeMovie,
                "3" or "season" => PlexConstants.TypeSeason,
                "4" or "episode" => PlexConstants.TypeEpisode,
                var typeValue when int.TryParse(typeValue, out int parsed) => parsed,
                _ => PlexConstants.TypeShow,
            };

            bool isMoviePath = rawPath?.Contains(VfsShared.ResolveMovieRootFolderName(), StringComparison.OrdinalIgnoreCase) == true;
            if (typeInt == PlexConstants.TypeMovie || IsMovieRoute() || isMoviePath)
                typeInt = PlexConstants.TypeMovie;

            var episode = manualEpisodeId.HasValue
                ? MetadataService.GetShokoEpisodeByID(manualEpisodeId.Value)
                : (manual != 1 && (isMoviePath || MetadataService.GetShokoSeriesByID(id.Value) == null) ? MetadataService.GetShokoEpisodeByID(id.Value) : null); // In case the extracted ID was a movie episode ID
            var series = (typeInt == PlexConstants.TypeMovie && episode?.Series != null && (manual != 1 || manualEpisodeId.HasValue)) ? episode.Series : MetadataService.GetShokoSeriesByID(id.Value);

            if (series == null && episode?.Series != null)
            {
                series = episode.Series;
                typeInt = PlexConstants.TypeMovie;
            }

            if (series == null)
            {
                Logger.LogInformation("Metadata: No Shoko series or episode found for id {Id}", id.Value);
                return EmptyMatch();
            }

            if (typeInt is PlexConstants.TypeShow or PlexConstants.TypeSeason or PlexConstants.TypeEpisode)
            {
                if (mapper.GetSeriesContext(series.ID.ID) is not { } context)
                    return EmptyMatch();

                bool includeChildren = (body?.IncludeChildren ?? (int.TryParse(Request.Query["includeChildren"], out var includeChildrenFlag) ? includeChildrenFlag : 0)) == 1;
                Dictionary<string, object?> metadata;

                if (typeInt == PlexConstants.TypeSeason)
                {
                    int seasonNumber = body?.Index ?? 1;
                    metadata = mapper.MapSeason(context, seasonNumber);
                    if (includeChildren)
                    {
                        var episodes = mapper.BuildEpisodeList(context, seasonNumber);
                        metadata["Children"] = new { size = episodes.Count, Metadata = episodes };
                    }
                }
                else if (typeInt == PlexConstants.TypeEpisode)
                {
                    int targetSeason = body?.ParentIndex ?? 1;
                    int targetEpisode = body?.Index ?? 1;
                    if (
                        mapper
                            .BuildEpisodeList(context, targetSeason)
                            .OfType<Dictionary<string, object?>>()
                            .FirstOrDefault(item => item.TryGetValue("index", out var indexValue) && indexValue is int episodeNumber && episodeNumber == targetEpisode)
                        is not { } matchedMetadata
                    )
                        return EmptyMatch();

                    metadata = matchedMetadata;
                }
                else
                {
                    metadata = mapper.MapSeries(context);
                    // Force remote URLs for Fix Match UI to bypass browser Mixed Content blocks
                    metadata["thumb"] = context.Series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, forceRemote: true)
                        ?? context.TmdbMovie?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, forceRemote: true);

                    if (includeChildren)
                    {
                        var seasons = context.FileData.Seasons.Select(season => mapper.MapSeason(context, season)).ToList();
                        metadata["Children"] = new { size = seasons.Count, Metadata = seasons };
                    }
                }

                metadata["score"] = 100;
                return WrapInContainer(metadata);
            }

            // Fallback to Movie (type 1)
            episode ??= series.Episodes.FirstOrDefault(seriesEpisode => seriesEpisode.Type == EpisodeType.Episode && !MapHelper.IsHidden(seriesEpisode))
                ?? series.Episodes.FirstOrDefault(seriesEpisode => seriesEpisode.Type == EpisodeType.Episode);
            if (episode == null)
                return EmptyMatch();

            var movieContext = mapper.GetSeriesContext(series.ID.ID);
            var tmdbMovie = episode.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? movieContext?.TmdbMovie;

            return Ok(
                new
                {
                    MediaContainer = new
                    {
                        size = 1,
                        identifier = ShokoRelayConstants.MovieAgentScheme,
                        Metadata = new[]
                        {
                            new
                            {
                                guid = episode.GetPlexMovieGuid(),
                                type = "movie",
                                title = TextHelper.ResolveMovieTitle(episode, series, tmdbMovie, isStandaloneMovie: true, seriesTitle: movieContext?.Titles.DisplayTitle),
                                year = episode.AirDate?.Year ?? tmdbMovie?.ReleaseDate?.Year ?? series.AirDate?.Year,
                                score = 100,
                                thumb = (episode.EpisodeNumber > 1 && tmdbMovie != null ? tmdbMovie.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, true) : null)
                                    ?? series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, true)
                                    ?? tmdbMovie?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage, true),
                            },
                        },
                    },
                }
            );
        });

    /// <summary>Represents the JSON body of a Plex matching request.</summary>
    /// <param name="Filename">The filename of the media being matched.</param>
    /// <param name="Title">The title string, often used for manual Shoko ID entry in Plex.</param>
    /// <param name="Manual">Flag indicating if the match was triggered manually (1 for true).</param>
    /// <param name="Type">The media type indicator (1 or "movie" for standalone movies, 2 for shows, 3 for seasons, 4 for episodes).</param>
    /// <param name="Index">The index of the item (e.g., episode or season number).</param>
    /// <param name="ParentIndex">The index of the parent item (e.g., season number for an episode).</param>
    /// <param name="IncludeChildren">Flag indicating if child metadata should be embedded.</param>
    public record PlexMatchBody(
        [property: JsonProperty("filename")] string? Filename,
        [property: JsonProperty("title")] string? Title = null,
        [property: JsonProperty("manual")] int? Manual = null,
        [property: JsonProperty("type")] object? Type = null,
        [property: JsonProperty("index")] int? Index = null,
        [property: JsonProperty("parentIndex")] int? ParentIndex = null,
        [property: JsonProperty("includeChildren")] int? IncludeChildren = null
    );

    #endregion

    #region Metadata Retrieval

    /// <summary>Returns detailed Plex-formatted metadata for a specific ratingKey.</summary>
    /// <remarks>
    ///   <b>Supported RatingKey Formats:</b>
    ///   - <c>123</c>    (Shoko Series ID)       / <c>a123</c>    (AniDB Series ID)
    ///   - <c>123s4</c>  (Shoko Series Season 4) / <c>a123s4</c>  (AniDB Series Season 4)
    ///   - <c>e567</c>   (Shoko Episode ID)      / <c>ae567</c>   (AniDB Episode ID)
    ///   - <c>e567p2</c> (Shoko Episode Part 2)  / <c>ae567p2</c> (AniDB Episode Part 2)
    ///   - <c>m890</c>   (Shoko Episode ID mapped as a Movie)
    ///   - <i>AniDB IDs resolve to Shoko IDs and must be known to Shoko</i>
    /// </remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <param name="includeChildren">Whether to embed immediate children (seasons/episodes).</param>
    /// <returns>Metadata MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}")]
    public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0) =>
        SafeRequest(() =>
        {
            bool isMovieEndpoint = IsMovieRoute();
            if (
                PlexHelper.IsMovieKey(ratingKey)
                || (
                    isMovieEndpoint
                    && int.TryParse(ratingKey, out int movieEpisodeId)
                    && MetadataService.GetShokoEpisodeByID(movieEpisodeId) is { ShokoSeriesID: var movieSeriesId }
                    && mapper.GetSeriesContext(movieSeriesId.ToString()) is not null
                )
            )
            {
                var (episode, movieContext, tmdbMovie) = TryResolveMovieContext(ratingKey, allowNumeric: isMovieEndpoint);
                return episode != null && movieContext != null
                    ? WrapInContainer(mapper.MapMovie(episode, movieContext, tmdbMovie), ShokoRelayConstants.MovieAgentScheme)
                    : TryResolveFallbackNumericMovie(ratingKey);
            }

            var context = mapper.GetSeriesContext(ratingKey);
            if (context == null)
                return TryResolveFallbackNumericMovie(ratingKey);

            if (PlexHelper.IsEpisodeKey(ratingKey))
            {
                var (episode, partIndex, mapping) = TryResolveEpisodeContext(context, ratingKey);
                return episode == null || mapping == null ? NotFound() : WrapInContainer(mapper.MapEpisode(episode, mapping.Coords, context, partIndex, mapping.TmdbEpisode));
            }
            if (TryParseSeasonNumber(ratingKey, out int seasonNumber, out bool isSeasonKey))
            {
                if (!isSeasonKey)
                    return NotFound();
                var seasonMetadata = mapper.MapSeason(context, seasonNumber);
                if (includeChildren == 1)
                {
                    var episodes = mapper.BuildEpisodeList(context, seasonNumber);
                    seasonMetadata["Children"] = new { size = episodes.Count, Metadata = episodes };
                }
                return WrapInContainer(seasonMetadata);
            }
            var showMetadata = mapper.MapSeries(context);
            if (includeChildren == 1)
            {
                var seasons = context.FileData.Seasons.Select(season => mapper.MapSeason(context, season)).ToList();
                showMetadata["Children"] = new { size = seasons.Count, Metadata = seasons };
            }
            return WrapInContainer(showMetadata);
        });

    /// <summary>Lists the immediate children for the provided ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Paged MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/children")]
    public IActionResult GetChildren(string ratingKey) =>
        SafeRequest(() =>
            PlexHelper.IsMovieKey(ratingKey) || PlexHelper.IsEpisodeKey(ratingKey) ? EmptyMatch()
            : mapper.GetSeriesContext(ratingKey) is not { } context ? NotFound()
            : TryParseSeasonNumber(ratingKey, out int seasonNumber, out bool isSeasonKey) ? (!isSeasonKey ? NotFound() : WrapInPagedContainer(mapper.BuildEpisodeList(context, seasonNumber)))
            : WrapInPagedContainer(context.FileData.Seasons.Select(season => mapper.MapSeason(context, season)))
        );

    /// <summary>Lists the second-level children (episodes) for a show-level ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Paged MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/grandchildren")]
    public IActionResult GetGrandchildren(string ratingKey) =>
        SafeRequest(() =>
            PlexHelper.IsMovieKey(ratingKey) ? EmptyMatch()
            : mapper.GetSeriesContext(ratingKey) is not { } context ? NotFound()
            : WrapInPagedContainer(context.FileData.Seasons.SelectMany(season => mapper.BuildEpisodeList(context, season)), ShokoRelayConstants.AgentScheme)
        );

    /// <summary>Enumerates all available artwork for the specified ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Image MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/images")]
    public IActionResult GetMetadataImages(string ratingKey) =>
        SafeRequest(() =>
        {
            IActionResult OkImages(object[] imageList, string identifier) =>
                Ok(
                    new
                    {
                        MediaContainer = new
                        {
                            offset = 0,
                            totalSize = imageList.Length,
                            identifier,
                            size = imageList.Length,
                            Image = imageList,
                        },
                    }
                );

            object[] images;
            bool isMovieEndpoint = IsMovieRoute();
            if (
                PlexHelper.IsMovieKey(ratingKey)
                || (
                    isMovieEndpoint
                    && int.TryParse(ratingKey, out int movieEpisodeId)
                    && MetadataService.GetShokoEpisodeByID(movieEpisodeId) is { ShokoSeriesID: var movieSeriesId }
                    && mapper.GetSeriesContext(movieSeriesId.ToString()) is not null
                )
            )
            {
                var (episode, movieContext, tmdbMovie) = TryResolveMovieContext(ratingKey, allowNumeric: isMovieEndpoint);
                return episode == null || movieContext == null ? NotFound() : OkImages(ExtractImages(mapper.MapMovie(episode, movieContext, tmdbMovie)), ShokoRelayConstants.MovieAgentScheme);
            }

            var context = mapper.GetSeriesContext(ratingKey);
            if (context == null)
            {
                var (fallbackEpisode, fallbackContext, fallbackTmdbMovie) = TryResolveMovieContext(ratingKey, allowNumeric: true);
                return fallbackEpisode == null || fallbackContext == null
                    ? NotFound()
                    : OkImages(ExtractImages(mapper.MapMovie(fallbackEpisode, fallbackContext, fallbackTmdbMovie)), ShokoRelayConstants.MovieAgentScheme);
            }

            if (PlexHelper.IsEpisodeKey(ratingKey))
            {
                var (episode, partIndex, mapping) = TryResolveEpisodeContext(context, ratingKey);
                images = (episode != null && mapping != null) ? ExtractImages(mapper.MapEpisode(episode, mapping.Coords, context, partIndex, mapping.TmdbEpisode)) : [];
            }
            else if (TryParseSeasonNumber(ratingKey, out int seasonNumber, out bool isSeasonKey))
            {
                if (!isSeasonKey)
                    return NotFound();
                images = ExtractImages(mapper.MapSeason(context, seasonNumber));
            }
            else
                images = ExtractImages(mapper.MapSeries(context));

            return OkImages(images, ShokoRelayConstants.AgentScheme);
        });

    /// <summary>Returns an empty extras container to satisfy Plex's automated metadata queries.</summary>
    /// <returns>An empty MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/extras")]
    public IActionResult GetMetadataExtras() => EmptyMatch();

    #endregion

    #region Collections

    /// <summary>Retrieves metadata for a Plex collection corresponding to a Shoko Group ID.</summary>
    /// <param name="groupId">The Shoko group identifier.</param>
    /// <returns>Collection metadata.</returns>
    [HttpGet("collections/{groupId}")]
    public IActionResult GetCollection(int groupId) =>
        SafeRequest(() =>
            MetadataService.GetShokoGroupByID(groupId) is { } group && (group.MainSeries ?? group.Series?.FirstOrDefault()) is { } primarySeries
                ? WrapInContainer(mapper.MapCollection(group, primarySeries))
                : NotFound()
        );

    /// <summary>Serves the local image for a user-defined, automatically generated, or smart collection by matching its ID or name.</summary>
    /// <param name="groupId">The Shoko group ID (prefixed with 'sc' for smart collections) or Plex collection ID.</param>
    /// <param name="name">Optional collection name.</param>
    /// <param name="suffix">Optional image type suffix (e.g. -logo, -backdrop, -square).</param>
    /// <returns>File stream result.</returns>
    [HttpGet("collections/user/{groupId}")]
    public IActionResult GetCollectionImage(string groupId, [FromQuery] string? name = null, [FromQuery] string? suffix = null) =>
        SafeRequest(() =>
        {
            string? posterPath = null;
            string actualSuffix = suffix ?? "";
            List<string> globalRoots = VfsShared.GetManagedFolderPaths(videoService);

            // If prefixed with 'sc', it's a Plex smart collection with a parsed ID (bypassing Shoko Group lookup)
            if (groupId.StartsWith(PlexConstants.SmartCollectionPrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(groupId[2..], out int smartCollectionId))
                posterPath = PlexHelper.FindCollectionImagePath(null, name ?? string.Empty, smartCollectionId, [actualSuffix], MetadataService, globalRoots, fileSystem);
            else if (int.TryParse(groupId, out int shokoGroupId))
            {
                var group = MetadataService.GetShokoGroupByID(shokoGroupId);
                var primarySeries = group?.MainSeries ?? group?.Series?.FirstOrDefault();
                if (primarySeries != null)
                    posterPath = PlexHelper.FindCollectionImagePathByGroup(primarySeries, shokoGroupId, actualSuffix, MetadataService, fileSystem);

                if (string.IsNullOrEmpty(posterPath) || !fileSystem.FileExists(posterPath))
                    posterPath = PlexHelper.FindCollectionImagePath(null, name ?? string.Empty, shokoGroupId, [actualSuffix], MetadataService, globalRoots, fileSystem);
            }

            return string.IsNullOrWhiteSpace(posterPath) || !fileSystem.FileExists(posterPath)
                ? NotFound()
                : File(
                    fileSystem.OpenFile(posterPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    ImageHelper.GetMimeType(Path.GetExtension(posterPath)) ?? "application/octet-stream"
                );
        });

    #endregion

    #region Exception Handling

    /// <summary>Timestamp of the last logged abstraction error to throttle log output.</summary>
    private static DateTime s_lastErrorLog = DateTime.MinValue;

    /// <summary>Lock object synchronizing error logging throttle updates.</summary>
    private static readonly Lock s_errorLogLock = new();

    /// <summary>Safely executes a metadata request, catching API abstraction mismatches to prevent Kestrel log spam.</summary>
    /// <param name="action">The controller action to execute.</param>
    /// <returns>The resulting IActionResult or a 500 error on failure.</returns>
    private IActionResult SafeRequest(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            lock (s_errorLogLock)
            {
                if ((DateTime.UtcNow - s_lastErrorLog).TotalMinutes > 5)
                {
                    Logger.LogError(exception, "Plex Metadata Request Failed: An API abstraction mismatch occurred. Please check for Shoko Relay plugin updates");
                    s_lastErrorLog = DateTime.UtcNow;
                }
            }
            return StatusCode(500, new { error = "Plugin API Abstraction Mismatch", message = exception.Message });
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>Extracts the numeric Series or Episode ID from a file path string.</summary>
    /// <param name="text">The path string to parse.</param>
    /// <returns>The extracted integer ID, or null if none found.</returns>
    private static int? ExtractSeriesId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var segments = PlexHelper.NormalizePathForPlex(text).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int segmentIndex = segments.Length - 1; segmentIndex >= 0; segmentIndex--)
            if (int.TryParse(segments[segmentIndex], out int id))
                return id;

        return null;
    }

    /// <summary>Parses the ratingKey and resolves the corresponding episode, series context, and TMDB movie metadata for a standalone movie.</summary>
    /// <param name="ratingKey">The custom Plex rating key for the movie.</param>
    /// <param name="allowNumeric">When <c>true</c>, also accepts bare numeric episode rating keys.</param>
    /// <returns>A tuple containing the resolved episode, series context, and TMDB movie metadata.</returns>
    private (IShokoEpisode? Episode, PlexMetadata.SeriesContext? Context, IMovie? TmdbMovie) TryResolveMovieContext(string ratingKey, bool allowNumeric = false)
    {
        bool isMovieKey = PlexHelper.IsMovieKey(ratingKey);
        if (!isMovieKey && !allowNumeric)
            return (null, null, null);

        var idPart = isMovieKey ? ratingKey[PlexConstants.MoviePrefix.Length..] : ratingKey;
        int partSplit = idPart.IndexOf(PlexConstants.PartPrefix, StringComparison.OrdinalIgnoreCase);
        string idText = partSplit >= 0 ? idPart[..partSplit] : idPart;

        if (!int.TryParse(idText, out int id))
            return (null, null, null);

        var episode = MetadataService.GetShokoEpisodeByID(id);
        var context = episode != null ? mapper.GetSeriesContext(episode.ShokoSeriesID.ToString()) : null;
        var tmdbMovie = episode?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();

        return (episode, context, tmdbMovie);
    }

    /// <summary>Attempts to resolve a bare numeric rating key as a standalone movie episode when no series context matches.</summary>
    /// <param name="ratingKey">The numeric rating key to evaluate.</param>
    /// <returns>A movie metadata container if found; otherwise, <see cref="ControllerBase.NotFound()"/>.</returns>
    private IActionResult TryResolveFallbackNumericMovie(string ratingKey) =>
        int.TryParse(ratingKey, out int numericId)
        && MetadataService.GetShokoEpisodeByID(numericId) is { } fallbackEpisode
        && mapper.GetSeriesContext(fallbackEpisode.ShokoSeriesID.ToString()) is { } fallbackContext
            ? WrapInContainer(mapper.MapMovie(fallbackEpisode, fallbackContext, fallbackEpisode.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault()), ShokoRelayConstants.MovieAgentScheme)
            : NotFound();

    /// <summary>Parses the ratingKey and resolves the corresponding episode, part index, and file mapping from the VFS collection.</summary>
    /// <param name="context">The series context containing the mapping collection.</param>
    /// <param name="ratingKey">The custom Plex rating key for the episode.</param>
    /// <returns>A tuple containing the resolved IShokoEpisode, the 1-based PartIndex, and the FileMapping.</returns>
    private (IShokoEpisode? Episode, int? PartIndex, MapHelper.FileMapping? Mapping) TryResolveEpisodeContext(PlexMetadata.SeriesContext context, string ratingKey)
    {
        int? partIndex = null;
        if (!PlexHelper.IsEpisodeKey(ratingKey))
            return (null, null, null);

        bool isAniDbEpisode = ratingKey.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase);
        var idPart = ratingKey[(isAniDbEpisode ? PlexConstants.AniDbPrefix.Length + PlexConstants.EpisodePrefix.Length : PlexConstants.EpisodePrefix.Length)..];

        int partSplit = idPart.IndexOf(PlexConstants.PartPrefix, StringComparison.OrdinalIgnoreCase);
        string idText = partSplit >= 0 ? idPart[..partSplit] : idPart;

        if (!int.TryParse(idText, out int id))
            return (null, null, null);
        if (partSplit >= 0 && int.TryParse(idPart[(partSplit + PlexConstants.PartPrefix.Length)..], out int parsedPart))
            partIndex = parsedPart;

        var episode = isAniDbEpisode ? MetadataService.GetShokoEpisodeByAnidbID(id) : MetadataService.GetShokoEpisodeByID(id);
        if (episode == null)
            return (null, partIndex, null);
        var mapping = context.FileData.Mappings.FirstOrDefault(fileMapping => fileMapping.Episodes.Any(mappedEpisode => mappedEpisode.ID == episode.ID) && fileMapping.PartIndex == partIndex)
            ?? context.FileData.Mappings.FirstOrDefault(fileMapping => fileMapping.Episodes.Any(mappedEpisode => mappedEpisode.ID == episode.ID));
        if (mapping == null)
            return (episode, partIndex, null);

        // Handle Episode Groups (One Shoko ID mapped to multiple TMDB IDs, bypassed when custom VFS overrides dictate coordinates)
        if (EnforceTmdbNumbering && mapping.Episodes.Count == 1 && !OverrideHelper.HasCustomCoords(episode, MetadataService) && episode.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 1 } tmdbEpisodesList)
        {
            string? preferredOrderingId = MapHelper.GetPreferredTmdbOrderingId(context.Series);
            var tmdbEpisodes = SelectPreferredTmdbOrdering(tmdbEpisodesList, preferredOrderingId);
            var matchedTmdbEpisode = int.TryParse(Request.Query["index"], out int requestedIndex)
                ? tmdbEpisodes.FirstOrDefault(tmdbEpisode => GetOrderingCoords(tmdbEpisode, preferredOrderingId).Episode == requestedIndex)
                : (partIndex.HasValue && partIndex.Value > 0 && partIndex.Value <= tmdbEpisodes.Count ? tmdbEpisodes[partIndex.Value - 1] : null);
            if (matchedTmdbEpisode != null)
            {
                var (seasonNumber, episodeNumber) = GetOrderingCoords(matchedTmdbEpisode, preferredOrderingId);
                mapping = mapping with
                {
                    TmdbEpisode = matchedTmdbEpisode,
                    Coords = new PlexCoords { Season = seasonNumber ?? mapping.Coords.Season, Episode = episodeNumber },
                };
            }
        }
        // Resolve individual coordinates for joined multi-episode files so secondary episodes keep their own index
        else if (mapping.Episodes.Count > 1 && mapping.Episodes.Select(mappedEpisode => mappedEpisode.Type).Distinct().Count() == 1)
        {
            var episodeCoords = GetPlexCoordinates(episode, context.PreferredOrderingId, metadataService: MetadataService);
            episodeCoords.Season = mapping.Coords.Season;
            mapping = mapping with { Coords = episodeCoords };
        }
        return (episode, partIndex, mapping);
    }

    /// <summary>Attempts to parse a season number from a season-formatted Plex rating key.</summary>
    /// <param name="ratingKey">The rating key to inspect.</param>
    /// <param name="seasonNumber">Outputs the parsed season number when valid.</param>
    /// <param name="isValid">Outputs <c>true</c> if the season suffix parsed cleanly as an integer.</param>
    /// <returns><c>true</c> if the key contains a season prefix; otherwise <c>false</c>.</returns>
    private static bool TryParseSeasonNumber(string ratingKey, out int seasonNumber, out bool isValid)
    {
        int seasonIndex = ratingKey.IndexOf(PlexConstants.SeasonPrefix, StringComparison.OrdinalIgnoreCase);
        if (seasonIndex < 0)
        {
            seasonNumber = 0;
            isValid = false;
            return false;
        }

        isValid = int.TryParse(ratingKey[(seasonIndex + PlexConstants.SeasonPrefix.Length)..], out seasonNumber);
        return true;
    }

    /// <summary>Extracts the Image array from a mapped metadata object.</summary>
    /// <param name="metadata">The mapped metadata dictionary.</param>
    /// <returns>An array of image metadata objects.</returns>
    private static object[] ExtractImages(object metadata) => metadata is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var image) && image is object[] arrayValue ? arrayValue : [];

    #endregion
}
