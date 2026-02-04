using Microsoft.AspNetCore.Mvc;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Helpers;
using ShokoRelay.Meta;
using static ShokoRelay.Meta.PlexMapping;
using static ShokoRelay.Helpers.MapHelper;

namespace ShokoRelay.Controllers
{
    # region Models
    public record SeriesContext(
        ISeries Series,
        string ApiUrl,
        (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles,
        string ContentRating,
        SeriesFileData FileData
    );

    public record PlexMatchBody(string? Filename);
    #endregion

    [ApiVersion("3.0")]
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    public class ShokoRelayController : ControllerBase
    {
        private readonly IVideoService _videoService;
        private readonly IMetadataService _metadataService;
        private readonly PlexMatching _plexMatcher;
        private readonly PlexMetadata _mapper;

        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        private const string SeasonPrefix  = "s";
        private const string EpisodePrefix = "e";
        private const string PartPrefix    = "p";

        public ShokoRelayController(
            IVideoService videoService,
            IMetadataService metadataService,
            PlexMatching plexMatcher,
            PlexMetadata mapper)
        {
            _videoService = videoService;
            _metadataService = metadataService;
            _plexMatcher = plexMatcher;
            _mapper = mapper;
        }

        private SeriesContext? GetSeriesContext(string ratingKey)
        {
            int seriesId;

            if (ratingKey.StartsWith(EpisodePrefix))
            {
                var epPart = ratingKey.Substring(EpisodePrefix.Length);
                if (epPart.Contains(PartPrefix))
                    epPart = epPart.Split(PartPrefix)[0];

                var ep = _metadataService.GetShokoEpisodeByID(int.Parse(epPart));
                if (ep?.Series == null) return null;
                seriesId = ep.Series.ID;
            }
            else if (ratingKey.Contains(SeasonPrefix))
            {
                if (!int.TryParse(ratingKey.Split(SeasonPrefix)[0], out seriesId)) return null;
            }
            else
            {
                if (!int.TryParse(ratingKey, out seriesId)) return null;
            }

            var series = _metadataService.GetShokoSeriesByID(seriesId);
            if (series == null) return null;

            return new SeriesContext(
                series,
                BaseUrl,
                TextHelper.ResolveFullSeriesTitles(series),
                RatingHelper.GetContentRatingAndAdult(series).Rating ?? "",
                GetSeriesFileData(series)
            );
        }

        private IActionResult WrapInContainer(object metadata) => Ok(new
        {
            MediaContainer = new
            {
                size = 1,
                totalSize = 1,
                offset = 0,
                identifier = ShokoRelayInfo.AgentScheme,
                Metadata = new[] { metadata }
            }
        });

        private IActionResult WrapInPagedContainer(IEnumerable<object> metadataList)
        {
            int start = int.TryParse(Request.Headers["X-Plex-Container-Start"], out var s) ? s :
                        int.TryParse(Request.Query["X-Plex-Container-Start"], out var sq) ? sq : 0;

            int size = int.TryParse(Request.Headers["X-Plex-Container-Size"], out var z) ? z :
                       int.TryParse(Request.Query["X-Plex-Container-Size"], out var zq) ? zq : 50;

            var allItems = metadataList.ToList();
            var pagedData = allItems.Skip(start).Take(size).ToArray();

            return Ok(new
            {
                MediaContainer = new
                {
                    offset = start,
                    totalSize = allItems.Count,
                    identifier = ShokoRelayInfo.AgentScheme,
                    size = pagedData.Length,
                    Metadata = pagedData
                }
            });
        }

        [HttpGet]
        public IActionResult GetMediaProvider() => Ok(new
        {
            MediaProvider = new
            {
                identifier = ShokoRelayInfo.AgentScheme,
                title = ShokoRelayInfo.Name,
                version = ShokoRelayInfo.Version,
                Types = new[]
                {
                    new { type = 2, Scheme = new[] { new { scheme = ShokoRelayInfo.AgentScheme } } },
                    new { type = 3, Scheme = new[] { new { scheme = ShokoRelayInfo.AgentScheme } } },
                    new { type = 4, Scheme = new[] { new { scheme = ShokoRelayInfo.AgentScheme } } }
                },
                Feature = new[]
                {
                    new { type = "metadata"   , key = "/metadata" },
                    new { type = "match"      , key = "/match" },
                    new { type = "collection" , key = "/collection" }
                }
            }
        });

        [HttpGet("metadata/{ratingKey}")]
        public IActionResult GetMetadata(string ratingKey, [FromQuery] int includeChildren = 0)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null) return NotFound();

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
                if (episode == null) return NotFound();

                var coords = GetPlexCoordinates(episode);
                object? tmdbEpisode = null;

                if (partIndex.HasValue && ShokoRelay.Settings.TMDBStructure &&
                    episode is IShokoEpisode shokoEp && shokoEp.TmdbEpisodes?.Any() == true)
                {
                    var tmdbEps = shokoEp.TmdbEpisodes
                        .OrderBy(te => te.SeasonNumber ?? 0)
                        .ThenBy(te => te.EpisodeNumber)
                        .ToList();

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
                var seasonMeta = _mapper.MapSeason(ctx.Series, sNum, ctx.Titles.DisplayTitle);

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
                var seasons = ctx.FileData.Seasons
                    .Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle))
                    .ToList();

                ((IDictionary<string, object?>)showMeta)["Children"] = new { size = seasons.Count, Metadata = seasons };
            }

            return WrapInContainer(showMeta);
        }

        [HttpGet("metadata/{ratingKey}/children")]
        public IActionResult GetChildren(string ratingKey)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null) return NotFound();

            if (ratingKey.Contains(SeasonPrefix))
            {
                int sNum = int.Parse(ratingKey.Split(SeasonPrefix)[1]);
                return WrapInPagedContainer(BuildEpisodeList(ctx, sNum));
            }

            var seasons = ctx.FileData.Seasons
                .Select(s => _mapper.MapSeason(ctx.Series, s, ctx.Titles.DisplayTitle))
                .ToList();

            return WrapInPagedContainer(seasons);
        }

        [HttpGet("metadata/{ratingKey}/grandchildren")]
        public IActionResult GetGrandchildren(string ratingKey)
        {
            var ctx = GetSeriesContext(ratingKey);
            if (ctx == null) return NotFound();

            var allEpisodes = ctx.FileData.Mappings
                .OrderBy(m => m.Coords.Season)
                .ThenBy(m => m.Coords.Episode)
                .Select(m => _mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode))
                .ToList();

            return WrapInPagedContainer(allEpisodes);
        }

        [Route("match")]
        [HttpPost]
        [HttpGet]
        public IActionResult Match([FromQuery] string? name, [FromBody] PlexMatchBody? body = null)
        {
            string? rawPath = name ?? body?.Filename;
            if (string.IsNullOrEmpty(rawPath))
                return Ok(new { MediaContainer = new { size = 0, Metadata = new object[0] } });

            var videoFile = _videoService.GetVideoFileByRelativePath(rawPath.Replace('\\', '/'));
            var series = videoFile?.Video?.Series?.FirstOrDefault();

            if (series == null)
                return Ok(new { MediaContainer = new { size = 0, Metadata = new object[0] } });

            var poster = ((IWithImages)series).GetImages(ImageEntityType.Poster).FirstOrDefault();

            return Ok(new
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
                            thumb = poster != null ? ImageHelper.GetImageUrl(poster) : null
                        }
                    }
                }
            });
        }

        [HttpGet("plexmatch")]
        public IActionResult GeneratePlexMatch([FromQuery] string? path)
        {
            if (string.IsNullOrEmpty(path))
                return BadRequest("Please provide a 'path' parameter.");

            var res = new List<string>();
            var err = new List<string>();
            _plexMatcher.ProcessFolder(path, res, err, cleanup: true);

            return Ok(new { status = $"Process complete for {path}", generated = res.Count, errors = err });
        }

        private List<object> BuildEpisodeList(SeriesContext ctx, int seasonNum)
        {
            var items = new List<(PlexCoords Coords, object Meta)>();

            foreach (var m in ctx.FileData.GetForSeason(seasonNum))
            {
                if (m.Episodes.Count == 1)
                {
                    items.Add((m.Coords, _mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode)));
                    continue;
                }

                foreach (var ep in m.Episodes)
                {
                    var coordsEp = GetPlexCoordinates(ep);
                    if (coordsEp.Season != seasonNum) continue;
                    items.Add((coordsEp, _mapper.MapEpisode(ep, coordsEp, ctx.Series, ctx.Titles)));
                }
            }

            return items.OrderBy(x => x.Coords.Episode).Select(x => x.Meta).ToList();
        }
    }
}