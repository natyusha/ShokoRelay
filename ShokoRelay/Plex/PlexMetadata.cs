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
    /// <summary>
    /// Converts Shoko series/episode/season metadata into Plex-compatible property dictionaries. Also constructs external GUID, rating, network, and country arrays.
    /// </summary>
    public class PlexMetadata
    {
        private readonly IMetadataService _metadataService;

        public PlexMetadata(IMetadataService metadataService)
        {
            _metadataService = metadataService;
        }

        /// <summary>
        /// Derive a cache-busting token from the entity's <see cref="IWithUpdateDate.LastUpdatedAt"/> timestamp.
        /// When AniDB replaces a poster the image ID stays the same, but the series' update date changes so
        /// Plex sees a new URL and re-downloads the image.
        /// </summary>
        private static string? GetCacheBuster(object? entity) => entity is IWithUpdateDate upd ? new DateTimeOffset(upd.LastUpdatedAt).ToUnixTimeSeconds().ToString() : null;

        /// <summary>
        /// Compose a Plex rating key string from a metadata type and numeric identifiers.
        /// </summary>
        /// <param name="type">Metadata type ("collection", "show", "season", "episode").</param>
        /// <param name="id">Primary numeric id (series/episode/group).</param>
        /// <param name="season">Optional season number (used for season/episode types).</param>
        /// <param name="part">Optional part index for multi-part episodes.</param>
        /// <returns>A composite rating key string.</returns>
        public string GetRatingKey(string type, int id, int? season = null, int? part = null) =>
            type switch
            {
                "collection" => $"{PlexConstants.CollectionPrefix}{id}",
                "show" => id.ToString(),
                "season" => $"{id}{PlexConstants.SeasonPrefix}{season}",
                "episode" => part.HasValue ? $"{PlexConstants.EpisodePrefix}{id}{PlexConstants.PartPrefix}{part}" : $"{PlexConstants.EpisodePrefix}{id}",
                _ => id.ToString(),
            };

        /// <summary>
        /// Compose a Plex GUID URI from a metadata type and numeric identifiers, using the <see cref="ShokoRelayInfo.AgentScheme"/>.
        /// </summary>
        /// <param name="type">Metadata type ("collection", "show", "season", "episode").</param>
        /// <param name="id">Primary numeric id.</param>
        /// <param name="season">Optional season number.</param>
        /// <param name="part">Optional part index.</param>
        /// <returns>A fully-qualified agent GUID URI.</returns>
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

        // Build Country array from TMDB production-country ISO codes. Use the first linked TMDB *show* for a Shoko series; if none exists, fall back to the first linked TMDB *movie* for that series.
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

        /// <summary>
        /// Get the collection name for a series based on its top-level Shoko group, returning <c>null</c> when the group contains only one series.
        /// </summary>
        /// <param name="series">The series to check.</param>
        /// <returns>The group's preferred title, or <c>null</c> if no collection applies.</returns>
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

        /// <summary>
        /// Build a Plex-compatible metadata dictionary for a collection backed by a Shoko group.
        /// </summary>
        /// <param name="group">Shoko group representing the collection.</param>
        /// <param name="primarySeries">Primary series used for images.</param>
        /// <returns>A dictionary of Plex metadata properties.</returns>
        public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries)
        {
            var images = (IWithImages)primarySeries;
            var cb = GetCacheBuster(primarySeries);
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
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster, cacheBuster: cb) : null,
                ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop, cacheBuster: cb) : null,
                ["titleSort"]             = collectiontitle,
                //["summary"]             = There is no summary source for groups
                //["Image"]               = Likely an image array will be used here
            };
            // csharpier-ignore-end
        }

        /// <summary>
        /// Build a Plex-compatible metadata dictionary for a series (show), including images, tags, cast, ratings, studios, and networks.
        /// </summary>
        /// <param name="series">The series to map.</param>
        /// <param name="titles">Pre-resolved display, sort, and original title tuple.</param>
        /// <returns>A dictionary of Plex metadata properties.</returns>
        public Dictionary<string, object?> MapSeries(ISeries series, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
        {
            var images = (IWithImages)series;
            var cb = GetCacheBuster(series);
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var studios = CastHelper.GetStudioTags(series);
            var plexTheme =
                ShokoRelay.Settings.PlexThemeMusic && series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault()?.TvdbShowID is int tvdb && tvdb > 0 ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null;
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
                ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster, cacheBuster: cb) : null,
                ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop, cacheBuster: cb) : null,
                ["contentRating"]         = contentRating.Rating,
                ["originalTitle"]         = titles.OriginalTitle,
                ["titleSort"]             = titles.SortTitle,
                ["year"]                  = series.AirDate?.Year,
                ["summary"]               = TextHelper.SanitizeSummaryWithFallback(((IWithDescriptions)series).PreferredDescription?.Value, (series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.PreferredDescription?.Value, ShokoRelay.Settings.SummaryMode),
                ["isAdult"]               = contentRating.IsAdult,
                ["duration"]              = totalDuration,
                //["tagline"]             = TMDB has this but it is not exposed
                ["studio"]                = studios.FirstOrDefault()?.tag,
                ["theme"]                 = plexTheme,

                ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, ShokoRelay.Settings.AddEveryImage, cb),
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

        /// <summary>
        /// Build a Plex-compatible metadata dictionary for a single season, enriching with TMDB season titles, summaries, and posters when available.
        /// </summary>
        /// <param name="series">The parent series.</param>
        /// <param name="seasonNum">Season number being mapped.</param>
        /// <param name="seriesTitle">Display title of the parent series.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary of Plex metadata properties for the season.</returns>
        public Dictionary<string, object?> MapSeason(ISeries series, int seasonNum, string seriesTitle, System.Threading.CancellationToken cancellationToken = default)
        {
            // ensure overrides loaded and pick canonical primary series for downstream lookups
            OverrideHelper.EnsureLoaded();
            int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);
            var primarySeries = series;
            if (primaryId != series.ID)
            {
                var s = _metadataService.GetShokoSeriesByID(primaryId);
                if (s != null)
                    primarySeries = s;
            }

            // assemble groupList containing primary (and any override extras when TMDB ep numbering active) for metadata lookups
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
            var cb = GetCacheBuster(primarySeries);
            var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
            var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
            var seasonTitle = GetSeasonFolder(seasonNum);
            string? seasonSummary = null;

            // Determine whether we should ignore TMDB season metadata because the series is running under a TMDB alternate ordering.
            // Season titles/descriptions and posters are taken from the default ordering and will not align with the adjusted episode numbers, so they must be skipped in that scenario.
            bool ignoreTmdbSeasonInfo = !string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(primarySeries));

            // TMDB metadata is preferred for season title/summary when available.
            ITmdbSeason? tmdbSeason = null;
            if (!ignoreTmdbSeasonInfo)
            {
                foreach (var s in groupList)
                {
                    tmdbSeason = s.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == seasonNum);
                    if (tmdbSeason != null)
                        break;
                }
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

            // Only request season posters when there is more than one non-extra season present.
            // Extras/specials are represented as negative season numbers and should be ignored for this count.
            var extras = groupList.Skip(1).Cast<ISeries>().ToList();
            var fileData = extras.Count > 0 ? MapHelper.GetSeriesFileDataMerged(primarySeries, extras) : MapHelper.GetSeriesFileData(primarySeries);
            int nonExtraSeasonCount = fileData.Seasons.Count(s => s >= 0);

            // Determine season date from earliest airdate among fileData mappings (reflects VFS overrides); fallback to group list if none found.
            DateOnly? seasonDate = fileData
                .Mappings.Where(m => m.Coords.Season == seasonNum)
                .SelectMany(m => m.Episodes)
                .Where(e => e.AirDate.HasValue)
                .Select(e => e.AirDate!.Value)
                .OrderBy(d => d)
                .FirstOrDefault();

            // FirstOrDefault returns default(DateOnly) when empty; treat that as null so we don't accidentally emit 0001-01-01.
            if (seasonDate == default(DateOnly))
                seasonDate = null;

            List<string>? seasonPosters = null; // Default: no season-specific posters

            // Populate TMDB season posters when enabled, the series has >1 normal seasons, and TMDB season metadata is available on the Shoko series.
            // Skip posters if we are ignoring TMDB season info due to an alternate ordering.
            if (ShokoRelay.Settings.TmdbSeasonPosters && nonExtraSeasonCount > 1 && tmdbSeason != null && !ignoreTmdbSeasonInfo)
            {
                var orderedUrls = tmdbSeason
                    .GetImages(ImageEntityType.Poster)
                    .OrderByDescending(i => i.IsPreferred)
                    .ThenByDescending(i => i.IsLocked)
                    .Select(i => ImageHelper.GetImageUrl(i, cacheBuster: cb))
                    .ToList();

                if (tmdbSeason.DefaultPoster is not null)
                {
                    var defaultUrl = ImageHelper.GetImageUrl(tmdbSeason.DefaultPoster, cacheBuster: cb);
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
                thumb = ImageHelper.GetImageUrl(poster, cacheBuster: cb);

            var coverPosters = ImageHelper.BuildCoverPosterArray(images, seasonTitle, ShokoRelay.Settings.AddEveryImage, seasonPosters, cb).ToList();
            // csharpier-ignore-start
            var contentRating = RatingHelper.GetContentRatingAndAdult(series);
            return new Dictionary<string, object?>
            {
                ["ratingKey"] = GetRatingKey("season", series.ID, seasonNum),
                ["key"]                   = $"/metadata/{GetRatingKey("season", series.ID, seasonNum)}/children",
                ["guid"]                  = GetGuid("season", series.ID, seasonNum),
                ["type"]                  = "season",
                ["title"]                 = seasonTitle,
                ["originallyAvailableAt"] = seasonDate?.ToString("yyyy-MM-dd"),
                ["thumb"]                 = thumb,
                ["contentRating"]         = contentRating.Rating,
                //['originalTitle']       =
                ["titleSort"]             = !string.IsNullOrWhiteSpace(seasonTitle) ? seasonTitle : null,
                ["year"]                  = seasonDate?.Year,
                ["summary"]               = seasonSummary ?? string.Empty,
                ["isAdult"]               = contentRating.IsAdult,

                ["parentRatingKey"]       = GetRatingKey("show", series.ID),
                ["parentKey"]             = $"/metadata/{series.ID}",
                ["parentGuid"]            = GetGuid("show", series.ID),
                ["parentType"]            = "show",
                ["parentTitle"]           = seriesTitle,
                ["parentThumb"]           = poster != null ? ImageHelper.GetImageUrl(poster, cacheBuster: cb) : null,
                ["parentArt"]             = backdrop != null ? ImageHelper.GetImageUrl(backdrop, cacheBuster: cb) : null,
                ["index"]                 = seasonNum,

                ["Image"]                 = coverPosters.ToArray(),
                //["OriginalImage"]       = // Should be able to implement this but might make more sense to leave it to Shoko
            };
            // csharpier-ignore-end
        }

        /// <summary>
        /// Build a Plex-compatible metadata dictionary for a single episode. Supports multi-part episodes and TMDB title/summary overrides for secondary parts.
        /// </summary>
        /// <param name="ep">Episode metadata.</param>
        /// <param name="mapped">Pre-calculated Plex season/episode coordinates.</param>
        /// <param name="series">Parent series for context (images, titles, content rating).</param>
        /// <param name="titles">Pre-resolved display, sort, and original title tuple for the series.</param>
        /// <param name="partIndex">Optional 1-based part index for multi-part episodes.</param>
        /// <param name="tmdbEpisode">Optional TMDB episode object used for title/summary overrides on secondary parts.</param>
        /// <returns>A dictionary of Plex metadata properties for the episode.</returns>
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
            var cb = GetCacheBuster(series);
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
                imageArray = ImageHelper.GenerateImageArray(images, epTitle, ShokoRelay.Settings.AddEveryImage, cb).Where(img => img.type == "snapshot").ToArray();
            }

            string? extraSubtype = null;
            if (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var exInfo))
                extraSubtype = exInfo.Subtype;

            // Prefer a season-specific poster for the parent thumb when available (fallback to series poster).
            // Ignore season posters too when alternate ordering is active since they will be sourced
            // from the default TMDB ordering and likely don't correspond to the adjusted episode numbers.
            string? parentThumb = null;
            bool ignoreSeasonInfo = !string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(series));
            if (ShokoRelay.Settings.TmdbSeasonPosters && mapped.Season >= 0 && !ignoreSeasonInfo)
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
                        .Select(i => ImageHelper.GetImageUrl(i, cacheBuster: cb))
                        .ToList();

                    if (seasonObj.DefaultPoster is not null)
                    {
                        parentThumb = ImageHelper.GetImageUrl(seasonObj.DefaultPoster, cacheBuster: cb);
                    }
                    else if (orderedUrls?.Any() == true)
                    {
                        parentThumb = orderedUrls[0];
                    }
                }
            }

            if (parentThumb == null && seriesPoster != null)
                parentThumb = ImageHelper.GetImageUrl(seriesPoster, cacheBuster: cb);

            var contentRating = RatingHelper.GetContentRatingAndAdult(series);
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
                ["thumb"]                 = thumb != null ? ImageHelper.GetImageUrl(thumb, cacheBuster: cb) : null,
                //["art"]
                ["contentRating"]         = contentRating.Rating,
                //["originalTitle"]
                ["titleSort"]             = epTitle,
                ["year"]                  = ep.AirDate?.Year,
                ["summary"]               = TextHelper.SanitizeSummaryWithFallback(epSummary, (ep as IShokoEpisode)?.TmdbEpisodes?.FirstOrDefault()?.PreferredDescription?.Value, ShokoRelay.Settings.SummaryMode),
                ["isAdult"]               = contentRating.IsAdult,
                ["duration"]              = (int)ep.Runtime.TotalMilliseconds,

                ["parentRatingKey"]       = GetRatingKey("season", series.ID, mapped.Season),
                ["parentKey"]             = $"/metadata/{GetRatingKey("season", series.ID, mapped.Season)}",
                ["parentGuid"]            = GetGuid("season", series.ID, mapped.Season),
                ["parentType"]            = "season",
                ["parentTitle"]           = GetSeasonFolder(mapped.Season),
                ["parentThumb"]           = parentThumb,
                ["parentArt"]             = seriesBackdrop != null ? ImageHelper.GetImageUrl(seriesBackdrop, cacheBuster: cb) : null, // Not documented
                ["index"]                 = mapped.Episode,

                ["grandparentRatingKey"]  = GetRatingKey("show", series.ID),
                ["grandparentKey"]        = $"/metadata/{series.ID}",
                ["grandparentGuid"]       = GetGuid("show", series.ID),
                ["grandparentType"]       = "show",
                ["grandparentTitle"]      = titles.DisplayTitle,
                ["grandparentThumb"]      = seriesPoster != null ? ImageHelper.GetImageUrl(seriesPoster, cacheBuster: cb) : null,
                ["grandparentArt"]        = seriesBackdrop != null ? ImageHelper.GetImageUrl(seriesBackdrop, cacheBuster: cb) : null, // Not documented
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
