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
        private const string SeasonPre  = "s";
        private const string EpisodePre = "e";
        private const string PartPre    = "p";

        public PlexMetadata(IMetadataService metadataService)
        {
            _metadataService = metadataService;
        }

        public string GetRatingKey(string type, int id, int? season = null, int? part = null) => type switch
        {
            "show"    => id.ToString(),
            "season"  => $"{id}{SeasonPre}{season}",
            "episode" => part.HasValue ? $"{EpisodePre}{id}{PartPre}{part}" : $"{EpisodePre}{id}",
            _         => id.ToString()
        };

        public string GetGuid(string type, int id, int? season = null, int? part = null) => type switch
        {
            "show"    => $"{ShokoRelayInfo.AgentScheme}://show/{id}",
            "season"  => $"{ShokoRelayInfo.AgentScheme}://season/{id}{SeasonPre}{season}",
            "episode" => part.HasValue 
                         ? $"{ShokoRelayInfo.AgentScheme}://episode/{EpisodePre}{id}{PartPre}{part}" 
                         : $"{ShokoRelayInfo.AgentScheme}://episode/{EpisodePre}{id}",
            _         => $"{ShokoRelayInfo.AgentScheme}://{id}"
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

        public Dictionary<string, object?> MapSeries(ISeries series, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
        {
            var images        = (IWithImages)series;
            var poster        = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop      = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var studios       = CastHelper.GetStudioTags(series);
            var contentRating = RatingHelper.GetContentRatingAndAdult(series);
            var totalDuration = series.Episodes.Any() 
                ? (int)series.Episodes.Sum(e => e.Runtime.TotalMilliseconds) 
                : (int?)null;

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
                ["Genre"]                 = TextHelper.GetFilteredTags(series),
                ["Guid"]                  = BuildTmdbGuidArray(series).ToArray(),
                //["Country"]             = TMDB has this but it is not exposed
                ["Role"]                  = CastHelper.GetCastAndCrew(series),
                ["Director"]              = CastHelper.GetDirectors(series),
                ["Producer"]              = CastHelper.GetProducers(series),
                ["Writer"]                = CastHelper.GetWriters(series),
                //["Similar]              = AniDB has this but it is not exposed
                ["Studio"]                = studios,
                ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null // Not documented
                //["Rating"]              = TMDB/AniDB has this but it is not exposed
                //["Network"]             = TMDB has this but it is not exposed
                //["SeasonType"]          = Can be implemented now but serves no purpose
            };
        }

        public Dictionary<string, object?> MapSeason(ISeries series, int seasonNum, string seriesTitle)
        {
            var images = (IWithImages)series;
            var poster   = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seasonTitle = GetSeasonTitle(seasonNum);
            var firstEpisode = series.Episodes
                .Select(e => new { Ep = e, Map = GetPlexCoordinates(e) })
                .Where(x => x.Map.Season == seasonNum)
                .OrderBy(x => x.Map.Episode)
                .FirstOrDefault();

            return new Dictionary<string, object?>
            {
                ["ratingKey"]             = GetRatingKey("season", series.ID, seasonNum),
                ["key"]                   = $"/metadata/{GetRatingKey("season", series.ID, seasonNum)}/children",
                ["guid"]                  = GetGuid("season", series.ID, seasonNum),
                ["type"]                  = "season",
                ["title"]                 = seasonTitle,
                ["originallyAvailableAt"] = firstEpisode?.Ep.AirDate?.ToString("yyyy-MM-dd"),
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster) : null, // Season poster from series images until season specific images are exposed
                //["art"]                 = No Source for this (yet)
                ["contentRating"]         = RatingHelper.GetContentRatingAndAdult(series).Rating,
                ["year"]                  = firstEpisode?.Ep.AirDate?.Year,
                //["summary"]             = TMDB has this but it is not exposed
                ["isAdult"]               = RatingHelper.GetContentRatingAndAdult(series).IsAdult,

                ["parentRatingKey"]       = GetRatingKey("show", series.ID),
                ["parentKey"]             = $"/metadata/{series.ID}",
                ["parentGuid"]            = GetGuid("show", series.ID),
                ["parentType"]            = "show",
                ["parentTitle"]           = seriesTitle,
                ["parentThumb"]           = poster != null ? ImageHelper.GetImageUrl(poster) : null,
                ["parentArt"]             = backdrop != null ? ImageHelper.GetImageUrl(backdrop) : null,
                ["index"]                 = seasonNum,
                
                ["Image"]                 = ImageHelper.GenerateImageArray(images, seasonTitle, ShokoRelay.Settings.AddEveryImage)
                                                .Where(img => img.type == "coverPoster") // Season poster from series images until season specific images are exposed
                                                .ToArray(),
                //["Guid"]                = Should be able to get TMDB season IDs
            };
        }

        public Dictionary<string, object?> MapEpisode(IEpisode ep, PlexCoords mapped, ISeries series,
            (string DisplayTitle, string SortTitle, string? OriginalTitle) titles,
            int? partIndex = null, object? tmdbEpisode = null)
        {
            var images       = (IWithImages)ep;
            var seriesImages = (IWithImages)series;
            var thumb        = ShokoRelay.Settings.TMDBThumbnails 
                               ? images.GetImages(ImageEntityType.Thumbnail).FirstOrDefault() 
                               : null;
            var seriesBackdrop = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seriesPoster   = seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault();

            string epTitle   = TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
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
                imageArray = ImageHelper
                    .GenerateImageArray(images, epTitle, ShokoRelay.Settings.AddEveryImage)
                    .Where(img => img.type == "snapshot")
                    .ToArray();
            }

            return new Dictionary<string, object?>
            {
                ["ratingKey"]             = GetRatingKey("episode", ep.ID, null, partIndex),
                ["key"]                   = $"/metadata/{GetRatingKey("episode", ep.ID, null, partIndex)}",
                ["guid"]                  = GetGuid("episode", ep.ID, null, partIndex),
                ["type"]                  = "episode",
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
                //["Rating"]              = TMDB/AniDB has this but it is not exposed
            };
        }
    }
}