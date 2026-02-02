using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Services;
using ShokoRelay.Helpers;
using static ShokoRelay.Meta.PlexMapping;

namespace ShokoRelay.Meta
{
    public class PlexMetadata
    {
        private readonly IMetadataService _metadataService;
        private const string SeasonPrefix = "s";
        private const string EpisodePrefix = "e";
        private const string PartPrefix = "p";

        public PlexMetadata(IMetadataService metadataService)
        {
            _metadataService = metadataService;
        }

        public string GetRatingKey(string type, int id, int? season = null, int? part = null) => type switch
        {
            "show"    => id.ToString(),
            "season"  => $"{id}{SeasonPrefix}{season}",
            "episode" => part.HasValue ? $"{EpisodePrefix}{id}{PartPrefix}{part}" : $"{EpisodePrefix}{id}",
            _         => id.ToString()
        };

        public string GetGuid(string type, int id, int? season = null, int? part = null) => type switch
        {
            "show"    => $"{ShokoRelayInfo.AgentScheme}://show/{id}",
            "season"  => $"{ShokoRelayInfo.AgentScheme}://season/{id}{SeasonPrefix}{season}",
            "episode" => part.HasValue 
                         ? $"{ShokoRelayInfo.AgentScheme}://episode/{EpisodePrefix}{id}{PartPrefix}{part}" 
                         : $"{ShokoRelayInfo.AgentScheme}://episode/{EpisodePrefix}{id}",
            _         => $"{ShokoRelayInfo.AgentScheme}://{id}"
        };

        public dynamic MapSeries(
            ISeries series,
            string apiBaseUrl,
            string apiRoot,
            (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
        {
            var images   = (IWithImages)series;
            var poster   = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();

            return new Dictionary<string, object?>
            {
                ["guid"]                  = GetGuid("show", series.ID),
                ["ratingKey"]             = GetRatingKey("show", series.ID),
                ["key"]                   = $"/metadata/{GetRatingKey("show", series.ID)}/children",
                ["type"]                  = "show",
                ["title"]                 = titles.DisplayTitle,
                ["titleSort"]             = titles.SortTitle,
                ["originalTitle"]         = titles.OriginalTitle,
                ["contentRating"]         = RatingHelper.GetContentRating(series),
                ["summary"]               = TextHelper.SummarySanitizer(
                                                ((IWithDescriptions)series).PreferredDescription, 
                                                ShokoRelay.Settings.SanitizeSummary) ?? "",
                ["originallyAvailableAt"] = series.AirDate?.ToString("yyyy-MM-dd"),
                ["year"]                  = series.AirDate?.Year,
                ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop, apiBaseUrl, apiRoot) : null,
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster, apiBaseUrl, apiRoot) : null,
                ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, apiBaseUrl, apiRoot, ShokoRelay.Settings.AddEveryImage),
                ["Genre"]                 = TextHelper.GetFilteredTags(series),
                ["Role"]                  = CastHelper.GetCastAndCrew(series, apiBaseUrl),
                ["Studio"]                = CastHelper.GetStudioTags(series),
                ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null
            };
        }

        public dynamic MapSeason(
            ISeries series,
            int seasonNum,
            string apiBaseUrl,
            string apiRoot,
            string seriesTitle)
        {
            var images = (IWithImages)series;
            var seasonTitle = PlexMapping.GetSeasonTitle(seasonNum);
            var firstEpisode = series.Episodes
                .Select(e => new { Ep = e, Map = GetPlexCoordinates(e) })
                .Where(x => x.Map.Season == seasonNum)
                .OrderBy(x => x.Map.Episode)
                .FirstOrDefault();
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();

            return new Dictionary<string, object?>
            {
                ["guid"]                  = GetGuid("season", series.ID, seasonNum),
                ["ratingKey"]             = GetRatingKey("season", series.ID, seasonNum),
                ["key"]                   = $"/metadata/{GetRatingKey("season", series.ID, seasonNum)}/children",
                ["type"]                  = "season",
                ["index"]                 = seasonNum,
                ["title"]                 = seasonTitle,
                ["parentTitle"]           = seriesTitle,
                ["parentType"]            = "show",
                ["parentRatingKey"]       = GetRatingKey("show", series.ID),
                ["parentGuid"]            = GetGuid("show", series.ID),
                ["parentKey"]             = $"/metadata/{series.ID}",
                ["parentArt"]             = backdrop != null ? ImageHelper.GetImageUrl(backdrop, apiBaseUrl, apiRoot) : null,
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster, apiBaseUrl, apiRoot) : null,
                ["originallyAvailableAt"] = firstEpisode?.Ep.AirDate?.ToString("yyyy-MM-dd"),
                ["year"]                  = firstEpisode?.Ep.AirDate?.Year,
                ["Image"]                 = ImageHelper.GenerateImageArray(images, seasonTitle, apiBaseUrl, apiRoot, ShokoRelay.Settings.AddEveryImage)
                                                .Where(img => (string)((dynamic)img).type == "coverPoster")
                                                .ToArray()
            };
        }

        public dynamic MapEpisode(
            IEpisode ep,
            PlexCoords mapped,
            ISeries series,
            string apiBaseUrl,
            string apiRoot,
            (string DisplayTitle, string SortTitle, string? OriginalTitle) titles,
            int? partIndex = null,
            object? tmdbEpisode = null)
        {
            var images       = (IWithImages)ep;
            var seriesImages = (IWithImages)series;
            var thumb        = ShokoRelay.Settings.TMDBThumbnails 
                               ? images.GetImages(ImageEntityType.Thumbnail).FirstOrDefault() 
                               : null;
            var seriesBackdrop = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seriesPoster   = seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault();

            string epTitle   = TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
            string epSummary = ((IWithDescriptions)ep).PreferredDescription;

            // For parts > 1, override with TMDB episode title/summary
            if (partIndex.HasValue && partIndex.Value > 1 && tmdbEpisode is not null)
            {
                if (tmdbEpisode is IWithTitles wt && !string.IsNullOrEmpty(wt.PreferredTitle))
                    epTitle = wt.PreferredTitle;
                if (tmdbEpisode is IWithDescriptions wd && !string.IsNullOrEmpty(wd.PreferredDescription))
                    epSummary = wd.PreferredDescription;
            }

            string? thumbUrl  = thumb != null ? ImageHelper.GetImageUrl(thumb, apiBaseUrl, apiRoot) : null;
            string? artUrl    = seriesBackdrop != null ? ImageHelper.GetImageUrl(seriesBackdrop, apiBaseUrl, apiRoot) : null;
            string? posterUrl = seriesPoster != null ? ImageHelper.GetImageUrl(seriesPoster, apiBaseUrl, apiRoot) : null;

            return new Dictionary<string, object?>
            {
                ["guid"]                  = GetGuid("episode", ep.ID, null, partIndex),
                ["ratingKey"]             = GetRatingKey("episode", ep.ID, null, partIndex),
                ["key"]                   = $"/metadata/{GetRatingKey("episode", ep.ID, null, partIndex)}",
                ["summary"]               = TextHelper.SummarySanitizer(epSummary, ShokoRelay.Settings.SanitizeSummary) ?? "",
                ["type"]                  = "episode",
                ["contentRating"]         = RatingHelper.GetContentRating(series),
                ["thumb"]                 = thumbUrl,
                ["title"]                 = epTitle,
                ["grandparentTitle"]      = titles.DisplayTitle,
                ["grandparentType"]       = "show",
                ["grandparentArt"]        = artUrl,
                ["grandparentThumb"]      = posterUrl,
                ["grandparentRatingKey"]  = GetRatingKey("show", series.ID),
                ["grandparentGuid"]       = GetGuid("show", series.ID),
                ["grandparentKey"]        = $"/metadata/{series.ID}",
                ["parentTitle"]           = PlexMapping.GetSeasonTitle(mapped.Season),
                ["parentType"]            = "season",
                ["parentThumb"]           = posterUrl,
                ["parentRatingKey"]       = GetRatingKey("season", series.ID, mapped.Season),
                ["parentGuid"]            = GetGuid("season", series.ID, mapped.Season),
                ["parentKey"]             = $"/metadata/{GetRatingKey("season", series.ID, mapped.Season)}",
                ["index"]                 = mapped.Episode,
                ["parentIndex"]           = mapped.Season,
                ["originallyAvailableAt"] = ep.AirDate?.ToString("yyyy-MM-dd"),
                ["year"]                  = ep.AirDate?.Year,
                ["Image"]                 = !string.IsNullOrEmpty(thumbUrl)
                                            ? new[] { new { alt = epTitle, type = "snapshot", url = thumbUrl } }
                                            : Array.Empty<object>(),
                ["Rating"]                = new[] { new { type = "critic", value = series.Rating } },
                ["Director"]              = CastHelper.GetDirectors(ep),
                ["Producer"]              = CastHelper.GetProducers(ep),
                ["Writer"]                = CastHelper.GetWriters(ep)
            };
        }

        private string? GetCollectionName(ISeries series)
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

            return group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle)
                ? titled.PreferredTitle
                : null;
        }
    }
}