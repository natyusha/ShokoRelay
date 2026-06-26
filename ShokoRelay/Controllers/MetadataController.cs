using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Video.Services;
using static ShokoRelay.Plex.PlexMapping;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Provides the core Metadata Provider endpoints for the Plex Agent.</summary>
[ApiController]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
[Route(ShokoRelayConstants.BasePath)]
[Route(ShokoRelayConstants.BasePath + "/options/{overrides}")]
public class MetadataController(IMetadataService metadataService, PlexMetadata mapper, ConfigProvider configProvider, PlexClient plexLibrary, IVideoService videoService)
    : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Provider Descriptor

    /// <summary>Announces the media provider capabilities to Plex.</summary>
    /// <returns>A media provider descriptor object.</returns>
    [HttpGet]
    public IActionResult GetMediaProvider()
    {
        var supportedTypes = new[] { PlexConstants.TypeShow, PlexConstants.TypeSeason, PlexConstants.TypeEpisode };
        var typePayload = supportedTypes.Select(t => new { type = t, Scheme = new[] { new { scheme = ShokoRelayConstants.AgentScheme } } });
        var featurePayload = new[] { new { type = "metadata", key = "/metadata" }, new { type = "match", key = "/matches" }, new { type = "collection", key = "/collections" } };
        return Ok(
            new
            {
                MediaProvider = new
                {
                    identifier = ShokoRelayConstants.AgentScheme,
                    title = ShokoRelayConstants.Name,
                    version = ShokoRelayConstants.Version,
                    Types = typePayload,
                    Feature = featurePayload,
                },
            }
        );
    }

    #endregion

    #region Matching

    /// <summary>Attempts to match a Plex media lookup request to a Shoko series.</summary>
    /// <param name="body">Match parameters including filename and title.</param>
    /// <returns>A match result MediaContainer.</returns>
    [Route("matches")]
    [HttpGet]
    [HttpPost]
    public IActionResult Match([FromBody] PlexMatchBody? body = null)
    {
        string? rawPath = body?.Filename ?? Request.Query["filename"];
        string? title = body?.Title ?? Request.Query["title"];
        int? manual = body?.Manual ?? (int.TryParse(Request.Query["manual"], out var m) ? m : null);
        int? seriesId;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            if (manual == 1 && int.TryParse(title, out var manualId))
                seriesId = manualId;
            else
                return EmptyMatch();
        }
        else
            seriesId = TextHelper.ExtractSeriesId(rawPath);
        if (!seriesId.HasValue)
            return EmptyMatch();

        var series = MetadataService.GetShokoSeriesByID(seriesId.Value);
        if (series == null)
        {
            Logger.Info("Metadata: No Shoko series found for id {SeriesId}", seriesId.Value);
            return EmptyMatch();
        }
        var poster = (series as IWithImages)?.GetPreferredImage(ImageEntityType.Primary, Settings.TmdbImageLanguage);
        return Ok(
            new
            {
                MediaContainer = new
                {
                    size = 1,
                    identifier = ShokoRelayConstants.AgentScheme,
                    Metadata = new[]
                    {
                        new
                        {
                            guid = series.GetPlexGuid(),
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

    /// <summary>Represents the JSON body of a Plex matching request.</summary>
    /// <param name="Filename">The filename of the media being matched.</param>
    /// <param name="Title">The title string, often used for manual Shoko ID entry in Plex.</param>
    /// <param name="Manual">Flag indicating if the match was triggered manually (1 for true).</param>
    public record PlexMatchBody([property: JsonProperty("filename")] string? Filename, [property: JsonProperty("title")] string? Title = null, [property: JsonProperty("manual")] int? Manual = null);

    #endregion

    #region Metadata Retrieval

    /// <summary>Returns detailed Plex-formatted metadata for a specific ratingKey.</summary>
    /// <remarks>
    /// **Supported RatingKey Formats:**
    /// - '123' (Shoko Series ID) / 'a123' (AniDB Series ID)
    /// - '123s4' (Shoko Series Season 4) / 'a123s4' (AniDB Series Season 4)
    /// - 'e567' (Shoko Episode ID) / 'ae567' (AniDB Episode ID)
    /// - 'e567p2' (Shoko Episode Part 2) / 'ae567p2' (AniDB Episode Part 2)
    /// - _AniDB IDs resolve to Shoko IDs and must be known to Shoko_
    /// </remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <param name="includeChildren">Whether to embed immediate children (seasons/episodes).</param>
    /// <returns>Metadata MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}")]
    public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0)
    {
        var ctx = mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();

        if (PlexHelper.IsEpisodeKey(ratingKey))
        {
            var (episode, partIdx, m) = TryResolveEpisodeContext(ctx, ratingKey);
            return episode == null || m == null ? NotFound() : WrapInContainer(mapper.MapEpisode(episode, m.Coords, ctx.Series, ctx.Titles, partIdx, m.TmdbEpisode));
        }
        if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            if (!int.TryParse(ratingKey.Split(PlexConstants.SeasonPrefix)[1], out int sNum))
                return NotFound();
            var seasonMeta = mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle);
            if (includeChildren == 1)
            {
                var episodes = mapper.BuildEpisodeList(ctx, sNum);
                ((IDictionary<string, object?>)seasonMeta)["Children"] = new { size = episodes.Count, Metadata = episodes };
            }
            return WrapInContainer(seasonMeta);
        }
        var showMeta = mapper.MapSeries(ctx.Series, ctx.Titles);
        if (includeChildren == 1)
        {
            var seasons = ctx.FileData.Seasons.Select(s => mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle)).ToList();
            ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
        }
        return WrapInContainer(showMeta);
    }

    /// <summary>Lists the immediate children for the provided ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Paged MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/children")]
    public IActionResult GetChildren(string ratingKey)
    {
        var ctx = mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();
        if (ratingKey.Contains(PlexConstants.SeasonPrefix))
            return !int.TryParse(ratingKey.Split(PlexConstants.SeasonPrefix)[1], out int sNum) ? NotFound() : WrapInPagedContainer(mapper.BuildEpisodeList(ctx, sNum));
        var seasons = ctx.FileData.Seasons.Select(s => mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle)).ToList();
        return WrapInPagedContainer(seasons);
    }

    /// <summary>Lists the second-level children (episodes) for a show-level ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Paged MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/grandchildren")]
    public IActionResult GetGrandchildren(string ratingKey)
    {
        var ctx = mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();
        var allEpisodes = ctx
            .FileData.Mappings.OrderBy(m => m.Coords.Season)
            .ThenBy(m => m.Coords.Episode)
            .ThenBy(m => m.PartIndex ?? 0)
            .Select(m => mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode))
            .ToList();
        return WrapInPagedContainer(allEpisodes);
    }

    /// <summary>Enumerates all available artwork for the specified ratingKey.</summary>
    /// <remarks>See <c>/metadata/{ratingKey}</c> for supported ratingKey formats and resolution behavior.</remarks>
    /// <param name="ratingKey">Custom Plex-style rating key.</param>
    /// <returns>Image MediaContainer.</returns>
    [HttpGet("metadata/{ratingKey}/images")]
    public IActionResult GetMetadataImages(string ratingKey)
    {
        var ctx = mapper.GetSeriesContext(ratingKey);
        if (ctx == null)
            return NotFound();
        object[] images;
        if (PlexHelper.IsEpisodeKey(ratingKey))
        {
            var (episode, partIdx, m) = TryResolveEpisodeContext(ctx, ratingKey);
            images = (episode != null && m != null) ? ExtractImages(mapper.MapEpisode(episode, m.Coords, ctx.Series, ctx.Titles, partIdx, m.TmdbEpisode)) : [];
        }
        else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            if (!int.TryParse(ratingKey.Split(PlexConstants.SeasonPrefix)[1], out int sNum))
                return NotFound();
            images = ExtractImages(mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle));
        }
        else
            images = ExtractImages(mapper.MapSeries(ctx.Series, ctx.Titles));
        return Ok(
            new
            {
                MediaContainer = new
                {
                    offset = 0,
                    totalSize = images.Length,
                    identifier = ShokoRelayConstants.AgentScheme,
                    size = images.Length,
                    Image = images,
                },
            }
        );
    }

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
    public IActionResult GetCollection(int groupId)
    {
        var group = MetadataService.GetShokoGroupByID(groupId);
        if (group == null)
            return NotFound();
        var primarySeries = group.MainSeries ?? group.Series?.FirstOrDefault();
        return primarySeries == null ? NotFound() : WrapInContainer(mapper.MapCollection(group, primarySeries));
    }

    /// <summary>Serves the local image for a user-defined, automatically generated, or smart collection by matching its ID or name.</summary>
    /// <param name="groupId">The Shoko group ID (prefixed with 'sc' for smart collections) or Plex collection ID.</param>
    /// <param name="name">Optional collection name.</param>
    /// <param name="suffix">Optional image type suffix (e.g. -logo, -backdrop, -square).</param>
    /// <returns>Physical file result.</returns>
    [HttpGet("collections/user/{groupId}")]
    public IActionResult GetCollectionImage(string groupId, [FromQuery] string? name = null, [FromQuery] string? suffix = null)
    {
        string? posterPath = null;
        string actualSuffix = suffix ?? "";
        List<string> globalRoots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

        // If prefixed with 'sc', it's a Plex smart collection with a parsed ID (bypassing Shoko Group lookup)
        if (groupId.StartsWith(PlexConstants.SmartCollectionPrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(groupId[2..], out int cid))
            posterPath = PlexHelper.FindCollectionImagePath(null, name ?? string.Empty, cid, [actualSuffix], MetadataService, globalRoots);
        else if (int.TryParse(groupId, out int gid))
        {
            var group = MetadataService.GetShokoGroupByID(gid);
            var primarySeries = group?.MainSeries ?? group?.Series?.FirstOrDefault();
            if (primarySeries != null)
                posterPath = PlexHelper.FindCollectionImagePathByGroup(primarySeries, gid, actualSuffix, MetadataService);

            if (string.IsNullOrEmpty(posterPath) || !IoFile.Exists(posterPath))
                posterPath = PlexHelper.FindCollectionImagePath(null, name ?? string.Empty, gid, [actualSuffix], MetadataService, globalRoots);
        }

        return string.IsNullOrWhiteSpace(posterPath) || !IoFile.Exists(posterPath) ? NotFound() : PhysicalFile(posterPath, ImageHelper.GetMimeType(Path.GetExtension(posterPath)) ?? "application/octet-stream");
    }

    #endregion

    #region Private Helpers

    /// <summary>Parses the ratingKey and resolves the corresponding episode, part index, and file mapping from the VFS collection.</summary>
    /// <param name="ctx">The series context containing the mapping collection.</param>
    /// <param name="ratingKey">The custom Plex rating key for the episode.</param>
    /// <returns>A tuple containing the resolved IShokoEpisode, the 1-based PartIndex, and the FileMapping.</returns>
    private (IShokoEpisode? Episode, int? PartIndex, MapHelper.FileMapping? Mapping) TryResolveEpisodeContext(PlexMetadata.SeriesContext ctx, string ratingKey)
    {
        int? partIdx = null;
        if (!PlexHelper.IsEpisodeKey(ratingKey))
            return (null, null, null);

        bool isAniDbEp = ratingKey.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase);
        var idPart = ratingKey[(isAniDbEp ? PlexConstants.AniDbPrefix.Length + PlexConstants.EpisodePrefix.Length : PlexConstants.EpisodePrefix.Length)..];
        var parts = idPart.Split(PlexConstants.PartPrefix);
        if (!int.TryParse(parts[0], out int id))
            return (null, null, null);
        if (parts.Length > 1 && int.TryParse(parts[1], out int p))
            partIdx = p;

        var episode = isAniDbEp ? MetadataService.GetShokoEpisodeByAnidbID(id) : MetadataService.GetShokoEpisodeByID(id);
        if (episode == null)
            return (null, partIdx, null);
        var mapping =
            ctx.FileData.Mappings.FirstOrDefault(x => x.Episodes.Any(e => e.ID == episode.ID) && x.PartIndex == partIdx) ?? ctx.FileData.Mappings.FirstOrDefault(x => x.Episodes.Any(e => e.ID == episode.ID));
        if (mapping == null)
            return (episode, partIdx, null);

        // Handle Episode Groups (One Shoko ID mapped to multiple TMDB IDs)
        if (EnforceTmdbNumbering && episode.TmdbEpisodes?.Count > 1 && int.TryParse(Request.Query["index"], out int reqIndex))
        {
            string? prefId = MapHelper.GetPreferredTmdbOrderingId(ctx.Series);
            var matchedTmdbEp = SelectPreferredTmdbOrdering(episode.TmdbEpisodes, prefId).FirstOrDefault(te => GetOrderingCoords(te, prefId).Episode == reqIndex);
            if (matchedTmdbEp != null)
                mapping = mapping with { TmdbEpisode = matchedTmdbEp };
        }
        return (episode, partIdx, mapping);
    }

    /// <summary>Extracts the Image array from a mapped metadata object.</summary>
    private static object[] ExtractImages(object metadata) => metadata is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr ? arr : [];

    #endregion
}
