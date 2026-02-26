using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Plex
{
    public class PlexMetadata
    {
        private readonly IMetadataService _metadataService;

        public PlexMetadata(IMetadataService metadataService)
        {
            _metadataService = metadataService;
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

        private object[] BuildXrefGuidArray(ISeries series)
        {
            var guids = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void addGuid(string id)
            {
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    guids.Add(new { id });
            }

            if (series is IShokoSeries shokoSeries)
            {
                // Prefer the first linked TMDB show and include its external IDs (TMDB + TVDB)
                var tmdbShow = shokoSeries.TmdbShows?.FirstOrDefault();
                if (tmdbShow != null)
                {
                    addGuid($"tmdb://{tmdbShow.ID}");
                    if (tmdbShow.TvdbShowID is int tvdb && tvdb > 0)
                        addGuid($"tvdb://{tvdb}");
                }
            }

            // If the series itself is a TMDB object include its TVDB mapping as well
            if (series is ITmdbShow tmdbSelf)
            {
                if (tmdbSelf.TvdbShowID is int tvdbSelf && tvdbSelf > 0)
                    addGuid($"tvdb://{tvdbSelf}");

                // include TMDB id for completeness (will be deduped)
                if (tmdbSelf.ID > 0)
                    addGuid($"tmdb://{tmdbSelf.ID}");
            }

            return guids.ToArray();
        }

        // Build Network array from TMDB network objects (optional)
        private object[]? BuildNetworkArray(ISeries series)
        {
            object? networksSource = null;

            // Prefer the first TMDB show linked to a Shoko series
            if (series is IShokoSeries shokoSeries)
            {
                var tmdbShow = shokoSeries.TmdbShows?.FirstOrDefault();
                if (tmdbShow != null)
                    networksSource = tmdbShow.GetType().GetProperty("TmdbNetworks")?.GetValue(tmdbShow);
            }

            // If the series itself is a TMDB show, inspect it directly
            if (networksSource == null && series is ITmdbShow)
            {
                networksSource = series.GetType().GetProperty("TmdbNetworks")?.GetValue(series);
            }

            if (networksSource is not System.Collections.IEnumerable list)
                return null;

            var outList = new List<object>();
            foreach (var n in list)
            {
                if (n == null)
                    continue;

                if (n is ITmdbNetwork net)
                {
                    if (!string.IsNullOrWhiteSpace(net.Name))
                        outList.Add(new { tag = net.Name });
                }
                else
                {
                    var nameProp = n.GetType().GetProperty("Name");
                    var name = nameProp?.GetValue(n) as string;
                    if (!string.IsNullOrWhiteSpace(name))
                        outList.Add(new { tag = name });
                }
            }

            return outList.Count > 0 ? outList.ToArray() : null;
        }

        // Build Country array from TMDB production-country ISO codes.
        // Use the first linked TMDB *show* for a Shoko series; if none exists,
        // fall back to the first linked TMDB *movie* for that series.
        private object[]? BuildCountryArray(ISeries series)
        {
            IEnumerable<string>? codes = null;

            if (series is IShokoSeries shokoSeries)
            {
                var tmdbShow = shokoSeries.TmdbShows?.FirstOrDefault();
                if (tmdbShow?.ProductionCountries?.Any() == true)
                {
                    codes = tmdbShow.ProductionCountries;
                }
                else
                {
                    var tmdbMovie = shokoSeries.TmdbMovies?.FirstOrDefault();
                    if (tmdbMovie?.ProductionCountries?.Any() == true)
                        codes = tmdbMovie.ProductionCountries;
                }
            }

            // If the series itself is a TMDB show, prefer its ProductionCountries (interface returns ISO codes)
            if (codes == null && series is ITmdbShow tmdbSelf && tmdbSelf.ProductionCountries?.Any() == true)
                codes = tmdbSelf.ProductionCountries;

            if (codes == null || !codes.Any())
                return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outList = new List<object>();

            foreach (var raw in codes)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var code = raw.Trim();
                string countryName;
                try
                {
                    // RegionInfo accepts ISO-3166 alpha-2 codes (e.g. "JP", "US")
                    var region = new System.Globalization.RegionInfo(code.ToUpperInvariant());
                    countryName = region.EnglishName;
                }
                catch
                {
                    countryName = code;
                }

                if (!string.IsNullOrWhiteSpace(countryName) && seen.Add(countryName))
                    outList.Add(new { tag = countryName });
            }

            return outList.Count > 0 ? outList.ToArray() : null;
        }

        // helper which builds a Plex-style rating array from a numeric value
        private object? BuildRatingArray(double? rating)
        {
            if (!rating.HasValue)
                return null;

            return new[]
            {
                new
                {
                    image = "themoviedb://image.rating",
                    type = "audience",
                    value = (float)rating.Value,
                },
            };
        }

        // convenience overloads that compute the TMDB rating before delegating
        private object? BuildRatingArray(ISeries series)
        {
            double? tmdb = null;
            if (series is IShokoSeries shokoSeries)
            {
                tmdb = shokoSeries.TmdbShows?.FirstOrDefault()?.Rating;
            }
            if (tmdb == null && series is ITmdbShow && series.Rating > 0)
                tmdb = series.Rating;
            return BuildRatingArray(tmdb);
        }

        private object? BuildRatingArray(IEpisode ep)
        {
            double? tmdb = null;
            if (ep is IShokoEpisode shokoEp)
            {
                tmdb = shokoEp.TmdbEpisodes?.FirstOrDefault()?.Rating;
            }
            if (tmdb == null && ep is ITmdbEpisode && ep.Rating > 0)
                tmdb = ep.Rating;
            return BuildRatingArray(tmdb);
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

            return group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle?.Value : null;
        }

        public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries)
        {
            var images = (IWithImages)primarySeries;
            var poster = images?.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images?.GetImages(ImageEntityType.Backdrop).FirstOrDefault();

            string collectiontitle = group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle!.Value : $"Group {group.ID}";

            string? summary = (group as IWithDescriptions)?.PreferredDescription?.Value;
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
                //["summary"]             = There is no summary source for groups
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
            var plexTheme =
                ShokoRelay.Settings.PlexThemeMusic && series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault()?.TvdbShowID is int tvdb && tvdb > 0
                    ? $"https://tvthemes.plexapp.com/{tvdb}.mp3"
                    : null;
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
                ["summary"]               = TextHelper.SummarySanitizer(((IWithDescriptions)series).PreferredDescription?.Value, ShokoRelay.Settings.SummaryMode) ?? "",
                ["isAdult"]               = contentRating.IsAdult,
                ["duration"]              = totalDuration,
                //["tagline"]             = TMDB has this but it is not exposed
                ["studio"]                = studios.FirstOrDefault()?.tag,
                ["theme"]                 = plexTheme,

                ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, ShokoRelay.Settings.AddEveryImage),
                //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
                ["Genre"]                 = TagHelper.GetFilteredTags(series),
                ["Guid"]                  = BuildXrefGuidArray(series).ToArray(),
                ["Country"]               = BuildCountryArray(series),
                ["Role"]                  = CastHelper.GetCastAndCrew(series),
                ["Director"]              = CastHelper.GetDirectors(series),
                ["Producer"]              = CastHelper.GetProducers(series),
                ["Writer"]                = CastHelper.GetWriters(series),
                //["Similar]              = AniDB has this but it is not exposed
                ["Studio"]                = studios,
                ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null, // Not documented
                ["Rating"]                = BuildRatingArray(series),
                ["Network"]               = BuildNetworkArray(series),
                //[SeasonType]            = Not relevant
            };
            // csharpier-ignore-end
        }

        public Dictionary<string, object?> MapSeason(ISeries series, int seasonNum, string seriesTitle, System.Threading.CancellationToken cancellationToken = default)
        {
            // resolve overrides up front so that all lookups below use the
            // canonical (primary) series and any related extras
            OverrideHelper.EnsureLoaded();
            int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);
            var primarySeries = series;
            if (primaryId != series.ID)
            {
                var s = _metadataService.GetShokoSeriesByID(primaryId);
                if (s != null)
                    primarySeries = s;
            }

            // build list of group series for combined metadata lookups
            var groupList = new List<IShokoSeries>();
            if (primarySeries is IShokoSeries ps)
                groupList.Add(ps);
            if (ShokoRelay.Settings.TmdbEpNumbering)
            {
                var group = OverrideHelper.GetGroup(primaryId, _metadataService);
                foreach (var id in group.Skip(1))
                {
                    var s = _metadataService.GetShokoSeriesByID(id) as IShokoSeries;
                    if (s != null)
                        groupList.Add(s);
                }
            }

            var images = (IWithImages)primarySeries;
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seasonTitle = GetSeasonFolder(seasonNum);
            string? seasonSummary = null;

            // TMDB metadata is preferred for season title/summary when available.
            ITmdbSeason? tmdbSeason = null;
            foreach (var s in groupList)
            {
                tmdbSeason = s.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == seasonNum);
                if (tmdbSeason != null)
                    break;
            }
            if (tmdbSeason != null)
            {
                var tmdbTitle = tmdbSeason.PreferredTitle?.Value;
                if (!string.IsNullOrWhiteSpace(tmdbTitle))
                    seasonTitle = tmdbTitle;

                var tmdbDesc = tmdbSeason.PreferredDescription?.Value;
                if (!string.IsNullOrWhiteSpace(tmdbDesc))
                    seasonSummary = TextHelper.SummarySanitizer(tmdbDesc, ShokoRelay.Settings.SummaryMode) ?? tmdbDesc;
            }

            // first episode should consider all series in the group as well
            var firstEpisode = groupList
                .SelectMany(s => s.Episodes)
                .Select(e => new { Ep = e, Map = GetPlexCoordinates(e) })
                .Where(x => x.Map.Season == seasonNum)
                .OrderBy(x => x.Map.Episode)
                .FirstOrDefault();

            // Only request season posters when there is more than one non-extra season present.
            // Extras/specials are represented as negative season numbers and should be ignored for this count.
            var extras = groupList.Skip(1).Cast<ISeries>().ToList();
            var fileData = extras.Count > 0 ? MapHelper.GetSeriesFileDataMerged(primarySeries, extras) : MapHelper.GetSeriesFileData(primarySeries);
            int nonExtraSeasonCount = fileData.Seasons.Count(s => s >= 0);

            List<string>? seasonPosters = null; // Default: no season-specific posters

            // Populate TMDB season posters when enabled, the series has >1 normal seasons,
            // and TMDB season metadata is available on the Shoko series.
            if (ShokoRelay.Settings.TmdbSeasonPosters && nonExtraSeasonCount > 1 && tmdbSeason != null)
            {
                var orderedUrls = tmdbSeason.GetImages(ImageEntityType.Poster).OrderByDescending(i => i.IsPreferred).ThenByDescending(i => i.IsLocked).Select(i => ImageHelper.GetImageUrl(i)).ToList();

                if (tmdbSeason.DefaultPoster is not null)
                {
                    var defaultUrl = ImageHelper.GetImageUrl(tmdbSeason.DefaultPoster);
                    seasonPosters = new List<string> { defaultUrl };
                    if (orderedUrls?.Any() == true)
                        seasonPosters.AddRange(orderedUrls.Where(u => u != defaultUrl));
                }
                else if (orderedUrls?.Any() == true)
                {
                    seasonPosters = orderedUrls;
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
                ["thumb"]                 = thumb,
                ["contentRating"]         = RatingHelper.GetContentRatingAndAdult(series).Rating,
                //['originalTitle']       =
                ["titleSort"]             = !string.IsNullOrWhiteSpace(seasonTitle) ? seasonTitle : null,
                ["year"]                  = firstEpisode?.Ep.AirDate?.Year,
                ["summary"]               = seasonSummary ?? string.Empty,
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
                //["OriginalImage"]       = // Should be able to implement this but might make more sense to leave it to Shoko
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
            var thumb = ShokoRelay.Settings.TmdbThumbnails ? images.GetImages(ImageEntityType.Thumbnail).FirstOrDefault() : null;
            var seriesBackdrop = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seriesPoster = seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault();

            string epTitle = TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
            string epSummary = ep.PreferredDescription?.Value ?? "";

            // For parts > 1, override with TMDB episode title/summary
            if (partIndex.HasValue && partIndex.Value > 1 && tmdbEpisode is not null)
            {
                if (tmdbEpisode is IWithTitles wt && !string.IsNullOrEmpty(wt.PreferredTitle?.Value))
                    epTitle = wt.PreferredTitle?.Value ?? epTitle;
                if (tmdbEpisode is IWithDescriptions wd && !string.IsNullOrEmpty(wd.PreferredDescription?.Value))
                    epSummary = wd.PreferredDescription?.Value ?? "";
            }

            // Respect the TMDBThumbnails setting
            ImageInfo[] imageArray = Array.Empty<ImageInfo>();
            if (ShokoRelay.Settings.TmdbThumbnails)
            {
                imageArray = ImageHelper.GenerateImageArray(images, epTitle, ShokoRelay.Settings.AddEveryImage).Where(img => img.type == "snapshot").ToArray();
            }

            string? extraSubtype = null;
            if (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var exInfo))
                extraSubtype = exInfo.Subtype;

            // Prefer a season-specific poster for the parent thumb when available (fallback to series poster)
            string? parentThumb = null;
            if (ShokoRelay.Settings.TmdbSeasonPosters && mapped.Season >= 0)
            {
                // find tmdbSeason across override group
                ITmdbSeason? seasonObj = null;
                if (series is IShokoSeries baseSeries)
                {
                    // search primary + overrides
                    OverrideHelper.EnsureLoaded();
                    int pId = OverrideHelper.GetPrimary(series.ID, _metadataService);
                    var grp = OverrideHelper.GetGroup(pId, _metadataService);
                    foreach (var id in grp)
                    {
                        var s = _metadataService.GetShokoSeriesByID(id) as IShokoSeries;
                        if (s != null)
                        {
                            seasonObj = s.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == mapped.Season);
                            if (seasonObj != null)
                                break;
                        }
                    }
                }
                if (seasonObj != null)
                {
                    var orderedUrls = seasonObj
                        .GetImages(ImageEntityType.Poster)
                        .OrderByDescending(i => i.IsPreferred)
                        .ThenByDescending(i => i.IsLocked)
                        .Select(i => ImageHelper.GetImageUrl(i))
                        .ToList();

                    if (seasonObj.DefaultPoster is not null)
                    {
                        parentThumb = ImageHelper.GetImageUrl(seasonObj.DefaultPoster);
                    }
                    else if (orderedUrls?.Any() == true)
                    {
                        parentThumb = orderedUrls[0];
                    }
                }
            }

            if (parentThumb == null && seriesPoster != null)
                parentThumb = ImageHelper.GetImageUrl(seriesPoster);
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
                ["parentTitle"]           = GetSeasonFolder(mapped.Season),
                ["parentThumb"]           = parentThumb,
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
                //["Role"]                = CastHelper.GetCastAndCrew(ep), // Large array not used by Plex clients and present in grandparent series metadata
                ["Director"]              = CastHelper.GetDirectors(ep),
                ["Producer"]              = CastHelper.GetProducers(ep),
                ["Writer"]                = CastHelper.GetWriters(ep),
                ["Rating"]                = BuildRatingArray(ep)
            };
            // csharpier-ignore-end

            return dict;
        }
    }
}
