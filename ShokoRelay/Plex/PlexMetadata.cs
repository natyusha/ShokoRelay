using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Helpers;
using ShokoRelay.Integrations.Shoko;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Plex
{
    public class PlexMetadata
    {
        private readonly IMetadataService _metadataService;
        private readonly ShokoClient _shokoClient;

        public PlexMetadata(IMetadataService metadataService, ShokoClient shokoClient)
        {
            _metadataService = metadataService;
            _shokoClient = shokoClient;
        }

        public string GetRatingKey(string type, int id, int? season = null, int? part = null) =>
            type switch
            {
                "collection" => $"{PlexConstants.CollectionPrefix}{id}",
                "show" => id.ToString(),
                "season" => $"{id}{PlexConstants.SeasonPrefix}{season}",
                "episode" => part.HasValue ? $"{PlexConstants.EpisodePrefix}{id}{PlexConstants.PartPrefix}{part}" : $"{PlexConstants.EpisodePrefix}{id}",
                _ => id.ToString(),
            };

        public string GetGuid(string type, int id, int? season = null, int? part = null) =>
            type switch
            {
                "collection" => $"{ShokoRelayInfo.AgentScheme}://collections/{id}",
                "show" => $"{ShokoRelayInfo.AgentScheme}://show/{id}",
                "season" => $"{ShokoRelayInfo.AgentScheme}://season/{id}{PlexConstants.SeasonPrefix}{season}",
                "episode" => part.HasValue
                    ? $"{ShokoRelayInfo.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{id}{PlexConstants.PartPrefix}{part}"
                    : $"{ShokoRelayInfo.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{id}",
                _ => $"{ShokoRelayInfo.AgentScheme}://{id}",
            };

        private object[] BuildTmdbGuidArray(ISeries series)
        {
            var guids = new List<object>();

            if (series is IShokoSeries shokoSeries)
            {
                // Try to get TMDB Show ID
                var tmdbShows = shokoSeries.TmdbShows;
                var tmdbShow = tmdbShows?.FirstOrDefault();
                if (tmdbShow != null)
                {
                    guids.Add(new { id = $"tmdb://{tmdbShow.ID}" });
                }
            }

            return guids.ToArray();
        }

        private object? BuildTmdbRatingArray(double rating)
        {
            if (rating <= 0)
                return null;

            return new[]
            {
                new
                {
                    image = "themoviedb://image.rating",
                    type = "audience",
                    value = (float)rating,
                },
            };
        }

        public string? GetCollectionName(ISeries series)
        {
            if (series is not IShokoSeries shokoSeries)
                return null;

            var groupId = shokoSeries.TopLevelGroupID;
            if (groupId <= 0)
                return null;

            var group = _metadataService.GetShokoGroupByID(groupId);
            if (group is not IShokoGroup shokoGroup)
                return null;

            if ((shokoGroup.Series?.Count ?? 0) <= 1)
                return null;

            return group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle) ? titled.PreferredTitle : null;
        }

        public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries)
        {
            var images = (IWithImages)primarySeries;
            var poster = images?.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images?.GetImages(ImageEntityType.Backdrop).FirstOrDefault();

            string collectiontitle = group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle) ? titled.PreferredTitle : $"Group {group.ID}";

            string? summary = (group as IWithDescriptions)?.PreferredDescription;
            // csharpier-ignore-start
            return new Dictionary<string, object?>
            {
                ["ratingKey"]             = GetRatingKey("collection", group.ID),
                ["guid"]                  = GetGuid("collection", group.ID),
                ["key"]                   = $"/collection/{group.ID}",
                ["type"]                  = "collection",
                ["subtype"]               = "show",
                ["title"]                 = collectiontitle,
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop) : null,
                ["titleSort"]             = collectiontitle,
                //["summary"]             = there is no summary source for groups
                //["Image"]               = Likely an image array will be used here
            };
            // csharpier-ignore-end
        }

        public Dictionary<string, object?> MapSeries(ISeries series, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
        {
            var images = (IWithImages)series;
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var studios = CastHelper.GetStudioTags(series);
            var contentRating = RatingHelper.GetContentRatingAndAdult(series);
            var totalDuration = series.Episodes.Any() ? (int)series.Episodes.Sum(e => e.Runtime.TotalMilliseconds) : (int?)null;
            // csharpier-ignore-start
            return new Dictionary<string, object?>
            {
                ["ratingKey"]             = GetRatingKey("show", series.ID),
                ["key"]                   = $"/metadata/{GetRatingKey("show", series.ID)}/children",
                ["guid"]                  = GetGuid("show", series.ID),
                ["type"]                  = "show",
                ["title"]                 = titles.DisplayTitle,
                ["originallyAvailableAt"] = series.AirDate?.ToString("yyyy-MM-dd"),
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop) : null,
                ["contentRating"]         = contentRating.Rating,
                ["originalTitle"]         = titles.OriginalTitle,
                ["titleSort"]             = titles.SortTitle,
                ["year"]                  = series.AirDate?.Year,
                ["summary"]               = TextHelper.SummarySanitizer(((IWithDescriptions)series).PreferredDescription, ShokoRelay.Settings.SummaryMode) ?? "",
                ["isAdult"]               = contentRating.IsAdult,
                ["duration"]              = totalDuration,
                //["tagline"]             = TMDB has this but it is not exposed
                ["studio"]                = studios.FirstOrDefault()?.tag,
                //["theme"]               = $"https://tvthemes.plexapp.com/{TVDBID}.mp3" // TMDB has this but it is not exposed

                ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, ShokoRelay.Settings.AddEveryImage),
                //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
                ["Genre"]                 = TagHelper.GetFilteredTags(series),
                ["Guid"]                  = BuildTmdbGuidArray(series).ToArray(),
                //["Country"]             = TMDB has this but it is not exposed
                ["Role"]                  = CastHelper.GetCastAndCrew(series),
                ["Director"]              = CastHelper.GetDirectors(series),
                ["Producer"]              = CastHelper.GetProducers(series),
                ["Writer"]                = CastHelper.GetWriters(series),
                //["Similar]              = AniDB has this but it is not exposed
                ["Studio"]                = studios,
                ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null, // Not documented
                ["Rating"]                = BuildTmdbRatingArray(series.Rating),
                //["Network"]             = TMDB has this but it is not exposed
                //["SeasonType"]          = Can be implemented now but serves no purpose
            };
            // csharpier-ignore-end
        }

        public async Task<Dictionary<string, object?>> MapSeasonAsync(ISeries series, int seasonNum, string seriesTitle, System.Threading.CancellationToken cancellationToken = default)
        {
            var images = (IWithImages)series;
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seasonTitle = GetSeasonTitle(seasonNum);
            var firstEpisode = series.Episodes.Select(e => new { Ep = e, Map = GetPlexCoordinates(e) }).Where(x => x.Map.Season == seasonNum).OrderBy(x => x.Map.Episode).FirstOrDefault();

            // Only request season posters when there is more than one non-extra season present.
            // Extras/specials are represented as negative season numbers and should be ignored for this count.
            var fileData = MapHelper.GetSeriesFileData(series);
            int nonExtraSeasonCount = fileData.Seasons.Count(s => s >= 0);

            List<string>? seasonPosters = null;
            if (nonExtraSeasonCount > 1 && _shokoClient != null && seasonNum >= 0)
            {
                try
                {
                    var map = await _shokoClient.GetSeasonPostersByTmdbAsync(series.ID, cancellationToken).ConfigureAwait(false);
                    if (map != null && map.TryGetValue(seasonNum, out var posters) && posters != null && posters.Count > 0)
                        seasonPosters = posters;
                }
                catch
                {
                    seasonPosters = null; // ignore errors and fall back to series poster
                }
            }

            // The thumb should be the first season poster if available, otherwise the series poster if present.
            string? thumb = null;
            if (seasonPosters != null && seasonPosters.Count > 0)
                thumb = seasonPosters[0];
            else if (poster != null)
                thumb = ImageHelper.GetImageUrl(poster);

            var coverPosters = ImageHelper.BuildCoverPosterArray(images, seasonTitle, ShokoRelay.Settings.AddEveryImage, seasonPosters).ToList();
            // csharpier-ignore-start
            return new Dictionary<string, object?>
            {
                ["ratingKey"] = GetRatingKey("season", series.ID, seasonNum),
                ["key"]                   = $"/metadata/{GetRatingKey("season", series.ID, seasonNum)}/children",
                ["guid"]                  = GetGuid("season", series.ID, seasonNum),
                ["type"]                  = "season",
                ["title"]                 = seasonTitle,
                ["originallyAvailableAt"] = firstEpisode?.Ep.AirDate?.ToString("yyyy-MM-dd"),
                ["thumb"]                 = thumb, // Season poster from Shoko if available, otherwise fall back to series poster
                ["contentRating"]         = RatingHelper.GetContentRatingAndAdult(series).Rating,
                ["year"]                  = firstEpisode?.Ep.AirDate?.Year,
                ["isAdult"]               = RatingHelper.GetContentRatingAndAdult(series).IsAdult,

                ["parentRatingKey"]       = GetRatingKey("show", series.ID),
                ["parentKey"]             = $"/metadata/{series.ID}",
                ["parentGuid"]            = GetGuid("show", series.ID),
                ["parentType"]            = "show",
                ["parentTitle"]           = seriesTitle,
                ["parentThumb"]           = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                ["parentArt"]             = backdrop != null ? ImageHelper.GetImageUrl(backdrop) : null,
                ["index"]                 = seasonNum,

                ["Image"]                 = coverPosters.ToArray(),
            };
            // csharpier-ignore-end
        }

        public Dictionary<string, object?> MapEpisode(
            IEpisode ep,
            PlexCoords mapped,
            ISeries series,
            (string DisplayTitle, string SortTitle, string? OriginalTitle) titles,
            int? partIndex = null,
            object? tmdbEpisode = null
        )
        {
            var images = (IWithImages)ep;
            var seriesImages = (IWithImages)series;
            var thumb = ShokoRelay.Settings.TMDBThumbnails ? images.GetImages(ImageEntityType.Thumbnail).FirstOrDefault() : null;
            var seriesBackdrop = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seriesPoster = seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault();

            string epTitle = TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
            string epSummary = ep.PreferredDescription;

            // For parts > 1, override with TMDB episode title/summary
            if (partIndex.HasValue && partIndex.Value > 1 && tmdbEpisode is not null)
            {
                if (tmdbEpisode is IWithTitles wt && !string.IsNullOrEmpty(wt.PreferredTitle))
                    epTitle = wt.PreferredTitle;
                if (tmdbEpisode is IWithDescriptions wd && !string.IsNullOrEmpty(wd.PreferredDescription))
                    epSummary = wd.PreferredDescription;
            }

            // Respect the TMDBThumbnails setting
            ImageInfo[] imageArray = Array.Empty<ImageInfo>();
            if (ShokoRelay.Settings.TMDBThumbnails)
            {
                imageArray = ImageHelper.GenerateImageArray(images, epTitle, ShokoRelay.Settings.AddEveryImage).Where(img => img.type == "snapshot").ToArray();
            }

            string? extraSubtype = null;
            if (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var exInfo))
                extraSubtype = exInfo.Subtype;
            // csharpier-ignore-start
            var dict = new Dictionary<string, object?>
            {
                ["ratingKey"]             = GetRatingKey("episode", ep.ID, null, partIndex),
                ["key"]                   = $"/metadata/{GetRatingKey("episode", ep.ID, null, partIndex)}",
                ["guid"]                  = GetGuid("episode", ep.ID, null, partIndex),
                ["type"]                  = "episode",
                ["subtype"]               = extraSubtype,
                ["title"]                 = epTitle,
                ["originallyAvailableAt"] = ep.AirDate?.ToString("yyyy-MM-dd"),
                ["thumb"]                 = thumb != null ? ImageHelper.GetImageUrl(thumb) : null,
                //["art"]
                ["contentRating"]         = RatingHelper.GetContentRatingAndAdult(series).Rating,
                //["originalTitle"]
                ["titleSort"]             = epTitle,
                ["year"]                  = ep.AirDate?.Year,
                ["summary"]               = TextHelper.SummarySanitizer(epSummary, ShokoRelay.Settings.SummaryMode) ?? "",
                ["isAdult"]               = RatingHelper.GetContentRatingAndAdult(series).IsAdult,
                ["duration"]              = (int)ep.Runtime.TotalMilliseconds,

                ["parentRatingKey"]       = GetRatingKey("season", series.ID, mapped.Season),
                ["parentKey"]             = $"/metadata/{GetRatingKey("season", series.ID, mapped.Season)}",
                ["parentGuid"]            = GetGuid("season", series.ID, mapped.Season),
                ["parentType"]            = "season",
                ["parentTitle"]           = GetSeasonTitle(mapped.Season),
                ["parentThumb"]           = seriesPoster != null ? ImageHelper.GetImageUrl(seriesPoster) : null, // Season poster from series images until season specific images are exposed
                ["parentArt"]             = seriesBackdrop != null ? ImageHelper.GetImageUrl(seriesBackdrop) : null, // Not documented
                ["index"]                 = mapped.Episode,

                ["grandparentRatingKey"]  = GetRatingKey("show", series.ID),
                ["grandparentKey"]        = $"/metadata/{series.ID}",
                ["grandparentGuid"]       = GetGuid("show", series.ID),
                ["grandparentType"]       = "show",
                ["grandparentTitle"]      = titles.DisplayTitle,
                ["grandparentThumb"]      = seriesPoster != null ? ImageHelper.GetImageUrl(seriesPoster) : null,
                ["grandparentArt"]        = seriesBackdrop != null ? ImageHelper.GetImageUrl(seriesBackdrop) : null, // Not documented
                ["parentIndex"]           = mapped.Season,

                ["Image"]                 = imageArray,
                //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
                ["Role"]                  = CastHelper.GetCastAndCrew(ep),
                ["Director"]              = CastHelper.GetDirectors(ep),
                ["Producer"]              = CastHelper.GetProducers(ep),
                ["Writer"]                = CastHelper.GetWriters(ep),
                //["Rating"]              = BuildTmdbRatingArray(ep.Rating) // not exposed for episodes yet
            };
            // csharpier-ignore-end

            return dict;
        }
    }
}
