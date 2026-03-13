using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Plex;

namespace ShokoRelay.Controllers;

/// <summary>
/// Provides the core Metadata Provider endpoints for the Plex Agent.
/// Handles series matching, detailed metadata retrieval, and artwork enumeration.
/// </summary>
[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayInfo.BasePath)]
public class MetadataController(IMetadataService metadataService, PlexMetadata mapper, ConfigProvider configProvider, PlexClient plexLibrary)
    : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    private readonly PlexMetadata _mapper = mapper;

    #region Provider Descriptor

    /// <summary>
    /// Announces the media provider capabilities to Plex.
    /// This endpoint is called by Plex agents to discover supported types, schemes, and feature routes.
    /// </summary>
    /// <returns>A media provider descriptor JSON.</returns>
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

    #endregion

    #region Matching

    /// <summary>
    /// Attempts to match a Plex media lookup request to a Shoko series. Priority is given to numeric IDs found in the filename or manual overrides in the request body.
    /// </summary>
    /// <param name="body">Optional JSON body containing filename, title, and manual match flags.</param>
    /// <returns>A MediaContainer containing the matched series or an empty match result.</returns>
    [Route("matches")]
    [HttpPost]
    [HttpGet]
    public IActionResult Match([FromBody] PlexMatchBody? body = null)
    {
        string? rawPath = body?.Filename;

        int? seriesId;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            if (body?.Manual == 1 && int.TryParse(body.Title, out var manualId))
                seriesId = manualId;
            else
                return EmptyMatch();
        }
        else
        {
            seriesId = Helpers.TextHelper.ExtractSeriesId(rawPath);
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
                            thumb = poster != null ? Helpers.ImageHelper.GetImageUrl(poster) : null,
                        },
                    },
                },
            }
        );
    }

    /// <summary>
    /// Represents the JSON body of a Plex matching request.
    /// </summary>
    /// <param name="Filename">The path of the file being matched.</param>
    /// <param name="Title">The title string (used for manual ID entry in Plex).</param>
    /// <param name="Manual">Flag indicating if the match was triggered manually.</param>
    public record PlexMatchBody(string? Filename, string? Title = null, int? Manual = null);

    #endregion

    #region Metadata Retrieval

    /// <summary>
    /// Returns detailed Plex-formatted metadata for a specific ratingKey.
    /// </summary>
    /// <param name="ratingKey">Plex-style rating key representing a show (123), season (123s1), or episode (e456).</param>
    /// <param name="includeChildren">Flag (0 or 1) indicating if immediate children should be embedded in the response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MediaContainer with the requested metadata object.</returns>
    [HttpGet("metadata/{ratingKey}")]
    public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0)
    {
        var ctx = _mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        // Level: Episode
        if (ratingKey.StartsWith(PlexConstants.EpisodePrefix))
        {
            var epIdPart = ratingKey[PlexConstants.EpisodePrefix.Length..].Split(PlexConstants.PartPrefix)[0];
            int epId = int.Parse(epIdPart);
            int? partIdx = ratingKey.Contains(PlexConstants.PartPrefix) ? int.Parse(ratingKey.Split(PlexConstants.PartPrefix)[1]) : null;

            var episode = _metadataService.GetShokoEpisodeByID(epId);
            if (episode == null)
                return NotFound();

            var m = ctx.FileData.Mappings.FirstOrDefault(x => x.Episodes.Any(e => e.ID == epId));
            return m == null ? NotFound() : WrapInContainer(_mapper.MapEpisode(episode, m.Coords, ctx.Series, ctx.Titles, partIdx, m.TmdbEpisode));
        }

        // Level: Season
        if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            int sNum = int.Parse(ratingKey.Split(PlexConstants.SeasonPrefix)[1]);
            var seasonMeta = _mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle);

            if (includeChildren == 1)
            {
                var episodes = _mapper.BuildEpisodeList(ctx, sNum);
                ((IDictionary<string, object?>)seasonMeta)["Children"] = new { size = episodes.Count, Metadata = episodes };
            }

            return WrapInContainer(seasonMeta);
        }

        // Level: Show
        var showMeta = _mapper.MapSeries(ctx.Series, ctx.Titles);

        if (includeChildren == 1)
        {
            var seasons = ctx.FileData.Seasons.Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle)).ToList();
            ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
        }

        return WrapInContainer(showMeta);
    }

    /// <summary>
    /// Lists the immediate children (seasons for a show, or episodes for a season) of the provided ratingKey.
    /// </summary>
    /// <param name="ratingKey">Parent rating key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged MediaContainer with the child objects.</returns>
    [HttpGet("metadata/{ratingKey}/children")]
    public IActionResult GetChildren(string ratingKey)
    {
        var ctx = _mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            int sNum = int.Parse(ratingKey.Split(PlexConstants.SeasonPrefix)[1]);
            return WrapInPagedContainer(_mapper.BuildEpisodeList(ctx, sNum));
        }

        var seasons = ctx.FileData.Seasons.Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle)).ToList();

        return WrapInPagedContainer(seasons);
    }

    /// <summary>
    /// Lists the second-level children (episodes) for a given show-level ratingKey.
    /// </summary>
    /// <param name="ratingKey">Show-level rating key.</param>
    /// <returns>A paged MediaContainer with the grandchild objects (episodes).</returns>
    [HttpGet("metadata/{ratingKey}/grandchildren")]
    public IActionResult GetGrandchildren(string ratingKey)
    {
        var ctx = _mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        var allEpisodes = ctx
            .FileData.Mappings.OrderBy(m => m.Coords.Season)
            .ThenBy(m => m.Coords.Episode)
            .Select(m => _mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode))
            .ToList();

        return WrapInPagedContainer(allEpisodes);
    }

    /// <summary>
    /// Enumerates all available artwork (posters, banners, backdrops) for the specified ratingKey.
    /// </summary>
    /// <param name="ratingKey">Target rating key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MediaContainer containing an array of Image objects.</returns>
    [HttpGet("metadata/{ratingKey}/images")]
    public IActionResult GetImages(string ratingKey)
    {
        var ctx = _mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        object[] images;
        if (ratingKey.Contains(PlexConstants.EpisodePrefix))
        {
            var parts = ratingKey[PlexConstants.EpisodePrefix.Length..].Split(PlexConstants.PartPrefix);
            int epId = int.Parse(parts[0]);
            int? partIdx = parts.Length > 1 ? int.Parse(parts[1]) : null;

            var episode = ctx.Series.Episodes.FirstOrDefault(e => e.ID == epId);
            var m = ctx.FileData.Mappings.FirstOrDefault(x => x.Episodes.Any(e => e.ID == epId));

            images = (episode != null && m != null) ? ExtractImages(_mapper.MapEpisode(episode, m.Coords, ctx.Series, ctx.Titles, partIdx, m.TmdbEpisode)) : [];
        }
        else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            int sNum = int.Parse(ratingKey.Split(PlexConstants.SeasonPrefix)[1]);
            images = ExtractImages(_mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle));
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

    #region Collections

    /// <summary>
    /// Retrieves metadata for a Plex collection corresponding to a Shoko Group ID.
    /// </summary>
    /// <param name="groupId">The ID of the Shoko Group.</param>
    /// <returns>A MediaContainer with collection metadata.</returns>
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

    /// <summary>
    /// Serves the local poster image for a user-defined collection.
    /// </summary>
    /// <param name="groupId">The Shoko Group ID associated with the collection.</param>
    /// <returns>A PhysicalFileResult of the image asset.</returns>
    [HttpGet("collections/user/{groupId}")]
    public IActionResult GetCollectionPoster(int groupId)
    {
        var group = _metadataService.GetShokoGroupByID(groupId);
        if (group == null)
            return NotFound();

        var primarySeries = group.MainSeries ?? group.Series?.FirstOrDefault();
        if (primarySeries == null)
            return NotFound();

        var posterPath = PlexHelper.FindCollectionPosterPathByGroup(primarySeries, groupId);
        return string.IsNullOrWhiteSpace(posterPath) || !System.IO.File.Exists(posterPath)
            ? NotFound()
            : PhysicalFile(posterPath, GetCollectionContentTypeForExtension(Path.GetExtension(posterPath)) ?? "application/octet-stream");
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Extract the Image array from a Plex metadata dictionary object.
    /// </summary>
    private static object[] ExtractImages(object metadata) => metadata is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr ? arr : [];

    #endregion
}
