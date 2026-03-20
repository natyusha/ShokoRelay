using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Plex;

/// <summary>Converts Shoko series/episode/season metadata into Plex-compatible property dictionaries.</summary>
public class PlexMetadata(IMetadataService metadataService)
{
    #region Fields & Constructor

    private readonly IMetadataService _metadataService = metadataService;

    #endregion

    #region Series Context

    /// <summary>Aggregates relevant information about a Shoko series for controller responses.</summary>
    /// <param name="Series">The primary series metadata.</param>
    /// <param name="Titles">A tuple containing Display, Sort, and Original titles.</param>
    /// <param name="ContentRating">The resolved content rating string.</param>
    /// <param name="FileData">The mapping data for the series' files.</param>
    public record SeriesContext(IShokoSeries Series, (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles, string ContentRating, MapHelper.SeriesFileData FileData);

    /// <summary>Resolves a Plex-style ratingKey into a <see cref="SeriesContext"/>.</summary>
    /// <param name="ratingKey">Plex rating key representing show, season or episode.</param>
    /// <returns>A SeriesContext containing resolved series data and mappings, or null if not found.</returns>
    public SeriesContext? GetSeriesContext(string ratingKey)
    {
        int seriesId = 0;

        if (ratingKey.Length > 1 && ratingKey[0] == 'a' && int.TryParse(ratingKey[1..], out var anidb))
            seriesId = _metadataService.GetShokoSeriesByAnidbID(anidb)?.ID ?? 0;
        else if (ratingKey.StartsWith(PlexConstants.EpisodePrefix))
            seriesId = _metadataService.GetShokoEpisodeByID(int.Parse(ratingKey[1..].Split(PlexConstants.PartPrefix)[0]))?.Series?.ID ?? 0;
        else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
            int.TryParse(ratingKey.Split(PlexConstants.SeasonPrefix)[0], out seriesId);
        else
            int.TryParse(ratingKey, out seriesId);

        var series = _metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return null;

        OverrideHelper.EnsureLoaded();
        int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);
        var primarySeries = _metadataService.GetShokoSeriesByID(primaryId) ?? series;

        var group = OverrideHelper.GetGroup(primaryId, _metadataService);
        var extras = group.Skip(1).Select(id => _metadataService.GetShokoSeriesByID(id)).OfType<IShokoSeries>().Cast<ISeries>().ToList();
        var fileData = extras.Count > 0 ? MapHelper.GetSeriesFileDataMerged(primarySeries, extras) : MapHelper.GetSeriesFileData(primarySeries);

        return new SeriesContext(primarySeries, TextHelper.ResolveFullSeriesTitles(primarySeries), RatingHelper.GetContentRatingAndAdult(primarySeries).Rating ?? "", fileData);
    }

    #endregion

    #region Shows

    /// <summary>Builds a Plex-compatible metadata dictionary for a series (show).</summary>
    /// <param name="series">The series metadata.</param>
    /// <param name="titles">The resolved title tuple.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeries(ISeries series, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
    {
        var cb = GetCacheBuster(series);
        var images = (IWithImages)series;
        var description = TextHelper.GetDescriptionByLanguage(series, ShokoRelay.Settings.SeriesDescriptionLanguage);
        var tmdbDescription = (series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.PreferredDescription?.Value;
        var studios = CastHelper.GetStudioTags(series);
        var (Rating, IsAdult) = RatingHelper.GetContentRatingAndAdult(series);
        var plexTheme = ShokoRelay.Settings.PlexThemeMusic && series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault()?.TvdbShowID is int tvdb && tvdb > 0 ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null;
        // csharpier-ignore-start
        return new Dictionary<string, object?>
        {
            ["ratingKey"]             = series.GetPlexRatingKey(),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey()}/children",
            ["guid"]                  = series.GetPlexGuid(),
            ["type"]                  = "show",
            ["title"]                 = titles.DisplayTitle,
            ["originallyAvailableAt"] = series.AirDate?.ToString("yyyy-MM-dd"),
            ["thumb"]                 = images.GetImages(ImageEntityType.Poster).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null,
            ["art"]                   = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault() is { } a ? ImageHelper.GetImageUrl(a, cacheBuster: cb) : null,
            ["contentRating"]         = Rating,
            ["originalTitle"]         = titles.OriginalTitle,
            ["titleSort"]             = titles.SortTitle,
            ["year"]                  = series.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(description, tmdbDescription, ShokoRelay.Settings.SummaryMode),
            ["isAdult"]               = IsAdult,
            ["duration"]              = series.Episodes.Any() ? (int)series.Episodes.Sum(e => e.Runtime.TotalMilliseconds) : (int?)null,
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = plexTheme,

            ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, ShokoRelay.Settings.AddEveryImage, cb),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            ["Genre"]                 = TagHelper.GetFilteredTags(series),
            ["Guid"]                  = BuildXrefGuidArray(series),
            ["Country"]               = BuildCountryArray(series),
            ["Role"]                  = CastHelper.GetCastAndCrew(series),
            ["Director"]              = CastHelper.GetDirectors(series),
            ["Producer"]              = CastHelper.GetProducers(series),
            ["Writer"]                = CastHelper.GetWriters(series),
            //["Similar]              = AniDB has this but it is not exposed
            ["Studio"]                = studios,
            ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null,
            ["Rating"]                = BuildRatingArray(series),
            ["Network"]               = BuildNetworkArray(series),
            //[SeasonType]            = Not relevant
        };
        // csharpier-ignore-end
    }

    #endregion

    #region Seasons

    /// <summary>Builds a sorted list of mapped episode metadata objects for a given season.</summary>
    /// <param name="ctx">Series context containing file data and title information.</param>
    /// <param name="seasonNum">Season number whose episodes should be returned.</param>
    /// <returns>An ordered list of episode metadata objects for the season.</returns>
    public List<object> BuildEpisodeList(SeriesContext ctx, int seasonNum)
    {
        var items = new List<(PlexCoords Coords, object Meta)>();

        foreach (var m in ctx.FileData.GetForSeason(seasonNum))
        {
            if (m.Episodes.Count == 1)
            {
                items.Add((m.Coords, MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode)));
                continue;
            }

            foreach (var ep in m.Episodes)
            {
                var coordsEp = GetPlexCoordinates(ep);
                if (coordsEp.Season != seasonNum)
                {
                    if (coordsEp.Season == PlexConstants.SeasonOther && m.Coords.Season == seasonNum)
                        coordsEp = new PlexCoords
                        {
                            Season = m.Coords.Season,
                            Episode = coordsEp.Episode,
                            EndEpisode = coordsEp.EndEpisode,
                        };
                    else
                        continue;
                }
                items.Add((coordsEp, MapEpisode(ep, coordsEp, ctx.Series, ctx.Titles)));
            }
        }
        return [.. items.OrderBy(x => x.Coords.Episode).Select(x => x.Meta)];
    }

    /// <summary>Builds a Plex-compatible metadata dictionary for a single season.</summary>
    /// <param name="series">The series metadata.</param>
    /// <param name="seasonNum">The season index.</param>
    /// <param name="seriesTitle">The title of the parent series.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeason(ISeries series, int seasonNum, string seriesTitle)
    {
        var ctx = GetSeriesContext(series.ID.ToString());
        if (ctx == null)
            return [];

        var ps = ctx.Series;
        var cb = GetCacheBuster(ps);
        var images = (IWithImages)ps;
        var seasonTitle = GetSeasonFolder(seasonNum);
        string? seasonSummary = null;

        bool ignoreTmdb = !string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(ps));
        var tmdbSeason = ignoreTmdb ? null : ps.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == seasonNum);

        if (tmdbSeason != null)
        {
            seasonTitle = tmdbSeason.PreferredTitle?.Value ?? seasonTitle;
            seasonSummary = TextHelper.SummarySanitizer(tmdbSeason.PreferredDescription?.Value, ShokoRelay.Settings.SummaryMode);
        }

        int nonExtraCount = ctx.FileData.Seasons.Count(s => s >= 0);
        List<string>? posters =
            (ShokoRelay.Settings.TmdbSeasonPosters && nonExtraCount > 1 && tmdbSeason != null)
                ? [.. tmdbSeason.GetImages(ImageEntityType.Poster).OrderByDescending(i => i.IsPreferred).Select(i => ImageHelper.GetImageUrl(i, cacheBuster: cb))]
                : null;

        string? thumb = (posters?.Count > 0) ? posters[0] : (images.GetImages(ImageEntityType.Poster).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null);
        var seasonDate = ctx.FileData.Mappings.Where(m => m.Coords.Season == seasonNum).SelectMany(m => m.Episodes).Where(e => e.AirDate.HasValue).Select(e => e.AirDate).OrderBy(d => d).FirstOrDefault();
        // csharpier-ignore-start
        return new Dictionary<string, object?>
        {
            ["ratingKey"]             = series.GetPlexRatingKey(seasonNum),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey(seasonNum)}/children",
            ["guid"]                  = series.GetPlexGuid(seasonNum),
            ["type"]                  = "season",
            ["title"]                 = seasonTitle,
            ["originallyAvailableAt"] = seasonDate?.ToString("yyyy-MM-dd"),
            ["thumb"]                 = thumb,
            ["contentRating"]         = ctx.ContentRating,
            //['originalTitle']       = No source for original season titles
            ["titleSort"]             = seasonTitle,
            ["year"]                  = seasonDate?.Year,
            ["summary"]               = seasonSummary ?? string.Empty,
            ["isAdult"]               = RatingHelper.GetContentRatingAndAdult(series).IsAdult,

            ["parentRatingKey"]       = series.GetPlexRatingKey(),
            ["parentKey"]             = $"/metadata/{series.ID}",
            ["parentGuid"]            = series.GetPlexGuid(),
            ["parentType"]            = "show",
            ["parentTitle"]           = seriesTitle,
            ["parentThumb"]           = images.GetImages(ImageEntityType.Poster).FirstOrDefault() is { } sp ? ImageHelper.GetImageUrl(sp, cacheBuster: cb) : null,
            ["parentArt"]             = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault() is { } sa ? ImageHelper.GetImageUrl(sa, cacheBuster: cb) : null,
            ["index"]                 = seasonNum,

            ["Image"]                 = ImageHelper.BuildCoverPosterArray(images, seasonTitle, ShokoRelay.Settings.AddEveryImage, posters, cb).ToArray(),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
        };
        // csharpier-ignore-end
    }

    #endregion

    #region Episodes

    /// <summary>Builds a Plex-compatible metadata dictionary for a single episode.</summary>
    /// <param name="ep">The episode metadata.</param>
    /// <param name="mapped">The resolved Plex coordinates.</param>
    /// <param name="series">The primary series metadata.</param>
    /// <param name="titles">The resolved title tuple.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <param name="tmdbEpisode">Optional TMDB episode metadata override.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapEpisode(
        IEpisode ep,
        PlexCoords mapped,
        ISeries series,
        (string DisplayTitle, string SortTitle, string? OriginalTitle) titles,
        int? partIndex = null,
        object? tmdbEpisode = null
    )
    {
        var cb = GetCacheBuster(series);
        var images = (IWithImages)ep;
        var seriesImages = (IWithImages)series;
        string epTitle = TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
        string epDescription = TextHelper.GetDescriptionByLanguage(ep, ShokoRelay.Settings.EpisodeDescriptionLanguage);

        if (partIndex > 1 && tmdbEpisode is not null)
        {
            if (tmdbEpisode is IWithTitles wt && !string.IsNullOrEmpty(wt.PreferredTitle?.Value))
                epTitle = wt.PreferredTitle.Value;
            if (tmdbEpisode is IWithDescriptions wd && !string.IsNullOrEmpty(wd.PreferredDescription?.Value))
                epDescription = wd.PreferredDescription.Value;
        }

        string? parentThumb = null;
        if (ShokoRelay.Settings.TmdbSeasonPosters && mapped.Season >= 0 && string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(series)))
        {
            var s = _metadataService.GetShokoSeriesByID(OverrideHelper.GetPrimary(series.ID, _metadataService));
            var seasonObj = s?.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == mapped.Season);
            parentThumb = seasonObj?.DefaultPoster is not null ? ImageHelper.GetImageUrl(seasonObj.DefaultPoster, cacheBuster: cb) : null;
        }
        // csharpier-ignore-start
        return new Dictionary<string, object?>
        {
            ["ratingKey"]             = ep.GetPlexRatingKey(partIndex),
            ["key"]                   = $"/metadata/{ep.GetPlexRatingKey(partIndex)}",
            ["guid"]                  = ep.GetPlexGuid(partIndex),
            ["type"]                  = "episode",
            ["subtype"]               = (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var ex)) ? ex.Subtype : null,
            ["title"]                 = epTitle,
            ["originallyAvailableAt"] = ep.AirDate?.ToString("yyyy-MM-dd"),
            ["thumb"]                 = ShokoRelay.Settings.TmdbThumbnails && images.GetImages(ImageEntityType.Thumbnail).FirstOrDefault() is { } t ? ImageHelper.GetImageUrl(t, cacheBuster: cb) : null,
            //["art"]                 = No source for episode level background images
            ["contentRating"]         = RatingHelper.GetContentRatingAndAdult(series).Rating,
            //["originalTitle"]       = No source for original episode titles
            ["titleSort"]             = epTitle,
            ["year"]                  = ep.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(epDescription, (ep as IShokoEpisode)?.TmdbEpisodes?.FirstOrDefault()?.PreferredDescription?.Value, ShokoRelay.Settings.SummaryMode),
            ["isAdult"]               = RatingHelper.GetContentRatingAndAdult(series).IsAdult,
            ["duration"]              = (int)ep.Runtime.TotalMilliseconds,

            ["parentRatingKey"]       = series.GetPlexRatingKey(mapped.Season),
            ["parentKey"]             = $"/metadata/{series.GetPlexRatingKey(mapped.Season)}",
            ["parentGuid"]            = series.GetPlexGuid(mapped.Season),
            ["parentType"]            = "season",
            ["parentTitle"]           = GetSeasonFolder(mapped.Season),
            ["parentThumb"]           = parentThumb ?? (seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null),
            ["parentArt"]             = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault() is { } a ? ImageHelper.GetImageUrl(a, cacheBuster: cb) : null,
            ["index"]                 = mapped.Episode,

            ["grandparentRatingKey"]  = series.GetPlexRatingKey(),
            ["grandparentKey"]        = $"/metadata/{series.ID}",
            ["grandparentGuid"]       = series.GetPlexGuid(),
            ["grandparentType"]       = "show",
            ["grandparentTitle"]      = titles.DisplayTitle,
            ["grandparentThumb"]      = seriesImages.GetImages(ImageEntityType.Poster).FirstOrDefault() is { } gp ? ImageHelper.GetImageUrl(gp, cacheBuster: cb) : null,
            ["grandparentArt"]        = seriesImages.GetImages(ImageEntityType.Backdrop).FirstOrDefault() is { } ga ? ImageHelper.GetImageUrl(ga, cacheBuster: cb) : null,
            ["parentIndex"]           = mapped.Season,

            ["Image"]                 = ShokoRelay.Settings.TmdbThumbnails ? [.. ImageHelper.GenerateImageArray(images, epTitle, ShokoRelay.Settings.AddEveryImage, cb).Where(img => img.Type == "snapshot")] : Array.Empty<ImageInfo>(),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            //["Role"]                = CastHelper.GetCastAndCrew(ep), // Large array not used by Plex clients and present in grandparent series metadata
            ["Director"]              = CastHelper.GetDirectors(ep),
            ["Producer"]              = CastHelper.GetProducers(ep),
            ["Writer"]                = CastHelper.GetWriters(ep),
            ["Rating"]                = BuildRatingArray(ep)
        };
        // csharpier-ignore-end
    }

    #endregion

    #region Collections

    /// <summary>Returns the collection name for a series based on its top-level Shoko group.</summary>
    /// <param name="series">The series to check.</param>
    /// <returns>The group's preferred title, or null if no collection applies.</returns>
    public string? GetCollectionName(ISeries series)
    {
        return series is not IShokoSeries { TopLevelGroupID: > 0 } ss ? null
            : _metadataService.GetShokoGroupByID(ss.TopLevelGroupID) is IShokoGroup { Series.Count: > 1 } g && g is IWithTitles { PreferredTitle.Value: { } title } ? title
            : null;
    }

    /// <summary>Builds a Plex-compatible metadata dictionary for a Shoko group collection.</summary>
    /// <param name="group">The Shoko group.</param>
    /// <param name="primarySeries">The series providing the artwork.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries)
    {
        var cb = GetCacheBuster(primarySeries);
        var images = (IWithImages)primarySeries;
        var poster = images.GetImages(ImageEntityType.Poster).FirstOrDefault();
        var backdrop = images.GetImages(ImageEntityType.Backdrop).FirstOrDefault();
        string title = group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle.Value : $"Group {group.ID}";
        // csharpier-ignore-start
        return new Dictionary<string, object?>
        {
            ["ratingKey"]             = group.GetPlexRatingKey(),
            ["guid"]                  = group.GetPlexGuid(),
            ["key"]                   = $"/collection/{group.ID}",
            ["type"]                  = "collection",
            ["subtype"]               = "show",
            ["title"]                 = title,
            ["thumb"]                 = poster != null ? ImageHelper.GetImageUrl(poster, cacheBuster: cb) : null,
            ["art"]                   = backdrop != null ? ImageHelper.GetImageUrl(backdrop, cacheBuster: cb) : null,
            ["titleSort"]             = title,
            //["summary"]             = There is no summary source for groups
            //["Image"]               = Likely an image array will be used here
        };
        // csharpier-ignore-end
    }

    #endregion

    #region Key / Array Builders

    private static string? GetCacheBuster(object? entity) => entity is IWithUpdateDate upd ? new DateTimeOffset(upd.LastUpdatedAt).ToUnixTimeSeconds().ToString() : null;

    private object[] BuildXrefGuidArray(ISeries series)
    {
        var guids = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                guids.Add(new { id });
        }

        if (series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault() is { } ts)
        {
            add($"tmdb://{ts.ID}");
            if (ts.TvdbShowID > 0)
                add($"tvdb://{ts.TvdbShowID}");
        }
        if (series is ITmdbShow s)
        {
            if (s.TvdbShowID > 0)
                add($"tvdb://{s.TvdbShowID}");
            if (s.ID > 0)
                add($"tmdb://{s.ID}");
        }
        return [.. guids];
    }

    private object[]? BuildNetworkArray(ISeries series)
    {
        var src = (series as IShokoSeries)?.TmdbShows?.FirstOrDefault() ?? (series as ITmdbShow);
        if (src?.GetType().GetProperty("TmdbNetworks")?.GetValue(src) is not System.Collections.IEnumerable list)
            return null;

        var result = new List<object>();
        foreach (var n in list)
        {
            var name = (n as ITmdbNetwork)?.Name ?? n?.GetType().GetProperty("Name")?.GetValue(n) as string;
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(new { tag = name });
        }
        return result.Count > 0 ? [.. result] : null;
    }

    private object[]? BuildCountryArray(ISeries series)
    {
        var codes =
            (series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.ProductionCountries
            ?? (series as IShokoSeries)?.TmdbMovies?.FirstOrDefault()?.ProductionCountries
            ?? (series as ITmdbShow)?.ProductionCountries;

        if (codes == null || !codes.Any())
            return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<object>();

        foreach (var c in codes.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string name;
            try
            {
                name = new System.Globalization.RegionInfo(c.Trim().ToUpperInvariant()).EnglishName;
            }
            catch
            {
                name = c.Trim();
            }
            if (seen.Add(name))
                result.Add(new { tag = name });
        }
        return result.Count > 0 ? [.. result] : null;
    }

    private object? BuildRatingArray(double? r) =>
        r > 0
            ? new[]
            {
                new
                {
                    image = "themoviedb://image.rating",
                    type = "audience",
                    value = (float)r.Value,
                },
            }
            : null;

    private object? BuildRatingArray(ISeries s) => BuildRatingArray((s as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.Rating ?? (s as ITmdbShow)?.Rating);

    private object? BuildRatingArray(IEpisode e) => BuildRatingArray((e as IShokoEpisode)?.TmdbEpisodes?.FirstOrDefault()?.Rating ?? (e as ITmdbEpisode)?.Rating);

    #endregion
}
