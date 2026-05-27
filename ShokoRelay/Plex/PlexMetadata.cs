using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Tmdb;
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
    /// <param name="ratingKey">Plex rating key representing a show, season or episode.</param>
    /// <returns>A SeriesContext containing resolved series data and mappings, or null if not found.</returns>
    public SeriesContext? GetSeriesContext(string ratingKey)
    {
        int seriesId = 0;

        if (ratingKey.StartsWith(PlexConstants.AniDbPrefix + PlexConstants.EpisodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // AniDB Episode Alias (ae{ID} or ae{ID}p{Part})
            var epIdPart = ratingKey[(PlexConstants.AniDbPrefix.Length + PlexConstants.EpisodePrefix.Length)..].Split(PlexConstants.PartPrefix)[0];
            if (int.TryParse(epIdPart, out var aid))
                seriesId = _metadataService.GetShokoEpisodeByAnidbID(aid)?.Series?.ID ?? 0;
        }
        else if (ratingKey.StartsWith(PlexConstants.EpisodePrefix))
        {
            // Shoko Episode ID (e{ID} or e{ID}p{Part})
            var epIdPart = ratingKey[PlexConstants.EpisodePrefix.Length..].Split(PlexConstants.PartPrefix)[0];
            if (int.TryParse(epIdPart, out var epId))
                seriesId = _metadataService.GetShokoEpisodeByID(epId)?.Series?.ID ?? 0;
        }
        else
        {
            // Isolate the show component (supports {ID}, a{AniDB}, {ID}s{Season}, or a{AniDB}s{Season})
            var seriesPart = ratingKey.Split(PlexConstants.SeasonPrefix)[0];
            if (seriesPart.StartsWith(PlexConstants.AniDbPrefix) && int.TryParse(seriesPart[PlexConstants.AniDbPrefix.Length..], out var anidb))
                seriesId = _metadataService.GetShokoSeriesByAnidbID(anidb)?.ID ?? 0;
            else
                int.TryParse(seriesPart, out seriesId);
        }

        var series = _metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return null;

        int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);
        var primarySeries = _metadataService.GetShokoSeriesByID(primaryId) ?? series;

        var group = OverrideHelper.GetGroup(primaryId, _metadataService);
        var extras = group.Skip(1).Select(id => _metadataService.GetShokoSeriesByID(id)).OfType<ISeries>().ToList();
        var fileData = extras.Count > 0 ? MapHelper.GetSeriesFileDataMerged(primarySeries, extras, _metadataService) : MapHelper.GetSeriesFileData(primarySeries, _metadataService);

        return new SeriesContext(primarySeries, TextHelper.ResolveFullSeriesTitles(primarySeries), ContentRatingHelper.GetContentRatingAndAdult(primarySeries).Rating ?? "", fileData);
    }

    #endregion

    #region Series

    /// <summary>Builds a Plex-compatible metadata dictionary for a series (show).</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="titles">The resolved title tuple.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeries(ISeries series, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
    {
        var cb = GetCacheBuster(series);
        var images = (IWithImages)series;
        var description = TextHelper.GetDescriptionByLanguage(series, Settings.DescriptionLanguage);
        var tmdbDescription = (series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.PreferredDescription?.Value;
        var studios = CastHelper.GetStudioTags(series);
        var (rating, isAdult) = ContentRatingHelper.GetContentRatingAndAdult(series);
        var plexTheme = Settings.PlexThemeMusic && series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault()?.TvdbShowID is int tvdb && tvdb > 0 ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null;
        return new Dictionary<string, object?>
        {
            // csharpier-ignore-start
            ["ratingKey"]             = series.GetPlexRatingKey(),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey()}/children",
            ["guid"]                  = series.GetPlexGuid(),
            ["type"]                  = "show",
            ["title"]                 = titles.DisplayTitle,
            ["originallyAvailableAt"] = series.AirDate?.ToDateOnly().ToString("yyyy-MM-dd", null),
            ["thumb"]                 = images.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null,
            ["art"]                   = images.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } a ? ImageHelper.GetImageUrl(a, cacheBuster: cb) : null,
            ["contentRating"]         = rating,
            ["originalTitle"]         = titles.OriginalTitle,
            ["titleSort"]             = titles.SortTitle,
            ["year"]                  = series.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(description, tmdbDescription, Settings.SummaryMode),
            ["isAdult"]               = isAdult,
            ["duration"]              = series.Episodes.Any() ? (int)series.Episodes.Sum(e => e.Runtime.TotalMilliseconds) : (int?)null,
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = plexTheme,

            ["Image"]                 = ImageHelper.GenerateImageArray(images, titles.DisplayTitle, Settings.AddEveryImage, cb),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            ["Genre"]                 = TagHelper.GetFilteredTags(series),
            ["Guid"]                  = BuildXrefGuidArray(series),
            ["Country"]               = BuildCountryArray(series),
            ["Role"]                  = CastHelper.GetCastAndCrew(series),
            ["Director"]              = CastHelper.GetDirectors(series),
            ["Producer"]              = CastHelper.GetProducers(series),
            ["Writer"]                = CastHelper.GetWriters(series),
            ["Similar"]               = BuildSimilarArray(series),
            ["Studio"]                = studios,
            ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null,
            ["Network"]               = BuildNetworkArray(series),
            ["Rating"]                = BuildRatingArray(series),
            //[SeasonType]            = Not relevant
            // csharpier-ignore-end
        };
    }

    #endregion

    #region Seasons

    /// <summary>Builds a Plex-compatible metadata dictionary for a single season.</summary>
    /// <param name="series">The Shoko series metadata.</param>
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
        var images = ps;
        var seasonTitle = GetSeasonFolder(seasonNum);
        string? seasonSummary = null;

        // When using VFS overrides find a Shoko series in the group which contains the TMDB metadata for the requisite season number.
        var groupIds = OverrideHelper.GetGroup(ps.ID, _metadataService);
        var sourceSeries = groupIds.Select(id => _metadataService.GetShokoSeriesByID(id)).OfType<IShokoSeries>().FirstOrDefault(s => s.TmdbSeasons?.Any(ts => ts.SeasonNumber == seasonNum) == true) ?? ps;

        bool ignoreTmdb = !string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(ps));
        var tmdbSeason = ignoreTmdb ? null : sourceSeries.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == seasonNum);

        if (tmdbSeason != null)
        {
            seasonTitle = tmdbSeason.PreferredTitle?.Value ?? seasonTitle;
            seasonSummary = TextHelper.SummarySanitizer(tmdbSeason.PreferredDescription?.Value, Settings.SummaryMode);
        }

        // Only apply TMDB season posters if there is more than one season present in the consolidated VFS. This accounts for extra seasons brought in via overrides.
        int totalSeasons = ctx.FileData.Seasons.Count(s => s >= 0);
        List<string>? posters =
            (Settings.TmdbSeasonPosters && totalSeasons > 1 && tmdbSeason != null)
                ? [.. tmdbSeason.GetAvailableImages(ImageEntityType.Primary).OrderByDescending(i => i.IsPreferred).Select(i => ImageHelper.GetImageUrl(i, cacheBuster: cb))]
                : null;

        string? thumb = (posters?.Count > 0) ? posters[0] : (images.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null);
        var seasonDate = ctx.FileData.Mappings.Where(m => m.Coords.Season == seasonNum).SelectMany(m => m.Episodes).Where(e => e.AirDate.HasValue).Select(e => e.AirDate).OrderBy(d => d).FirstOrDefault();
        return new Dictionary<string, object?>
        {
            // csharpier-ignore-start
            ["ratingKey"]             = series.GetPlexRatingKey(seasonNum),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey(seasonNum)}/children",
            ["guid"]                  = series.GetPlexGuid(seasonNum),
            ["type"]                  = "season",
            ["title"]                 = seasonTitle,
            ["originallyAvailableAt"] = seasonDate?.ToString("yyyy-MM-dd", null),
            ["thumb"]                 = thumb,
            ["contentRating"]         = ctx.ContentRating,
            //['originalTitle']       = No source for original season titles
            ["titleSort"]             = seasonTitle,
            ["year"]                  = seasonDate?.Year,
            ["summary"]               = seasonSummary ?? string.Empty,
            ["isAdult"]               = ContentRatingHelper.GetContentRatingAndAdult(series).IsAdult,

            ["parentRatingKey"]       = series.GetPlexRatingKey(),
            ["parentKey"]             = $"/metadata/{series.ID}",
            ["parentGuid"]            = series.GetPlexGuid(),
            ["parentType"]            = "show",
            ["parentTitle"]           = seriesTitle,
            ["parentThumb"]           = images.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } sp ? ImageHelper.GetImageUrl(sp, cacheBuster: cb) : null,
            ["parentArt"]             = images.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } sa ? ImageHelper.GetImageUrl(sa, cacheBuster: cb) : null,
            ["index"]                 = seasonNum,

            // Force addEveryImage to true if TMDB season posters are present, otherwise fallback to the configuration. (Remove this once Shoko's WebUI supports selecting the preferred poster)
            ["Image"]                 = ImageHelper.BuildCoverPosterArray(images, seasonTitle, posters != null || Settings.AddEveryImage, posters, cb).ToArray(),
            ["Guid"]                  = BuildSeasonXrefGuidArray(tmdbSeason),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            // csharpier-ignore-end
        };
    }

    #endregion

    #region Episodes

    /// <summary>Builds a Plex-compatible metadata dictionary for a single episode.</summary>
    /// <param name="ep">The episode metadata.</param>
    /// <param name="mapped">The resolved Plex coordinates.</param>
    /// <param name="series">The Shoko series metadata.</param>
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
        string epTitle = tmdbEpisode is IWithTitles { PreferredTitle.Value: { Length: > 0 } pt } ? pt : TextHelper.ResolveEpisodeTitle(ep, titles.DisplayTitle);
        string epDescription = tmdbEpisode is IWithDescriptions { PreferredDescription.Value: { Length: > 0 } pd } ? pd : TextHelper.GetDescriptionByLanguage(ep, Settings.DescriptionLanguage);

        string? parentThumb = null;
        if (Settings.TmdbSeasonPosters && mapped.Season >= 0 && string.IsNullOrEmpty(MapHelper.GetPreferredTmdbOrderingId(series)))
        {
            var s = _metadataService.GetShokoSeriesByID(OverrideHelper.GetPrimary(series.ID, _metadataService));
            var seasonObj = s?.TmdbSeasons?.FirstOrDefault(ts => ts.SeasonNumber == mapped.Season);
            parentThumb = seasonObj?.PrimaryImage is not null ? ImageHelper.GetImageUrl(seasonObj.PrimaryImage, cacheBuster: cb) : null;
        }
        return new Dictionary<string, object?>
        {
            // csharpier-ignore-start
            ["ratingKey"]             = ep.GetPlexRatingKey(partIndex),
            ["key"]                   = $"/metadata/{ep.GetPlexRatingKey(partIndex)}",
            ["guid"]                  = ep.GetPlexGuid(partIndex),
            ["type"]                  = "episode",
            ["subtype"]               = (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var ex)) ? ex.Subtype : null,
            ["title"]                 = epTitle,
            ["originallyAvailableAt"] = ep.AirDate?.ToString("yyyy-MM-dd", null),
            ["thumb"]                 = Settings.TmdbThumbnails && images.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } t ? ImageHelper.GetImageUrl(t, cacheBuster: cb) : null,
            //["art"]                 = No source for episode level background images
            ["contentRating"]         = ContentRatingHelper.GetContentRatingAndAdult(series).Rating,
            //["originalTitle"]       = No source for original episode titles
            ["titleSort"]             = epTitle,
            ["year"]                  = ep.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(epDescription, (ep as IShokoEpisode)?.TmdbEpisodes?.FirstOrDefault()?.PreferredDescription?.Value, Settings.SummaryMode),
            ["isAdult"]               = ContentRatingHelper.GetContentRatingAndAdult(series).IsAdult,
            ["duration"]              = (int)ep.Runtime.TotalMilliseconds,

            ["parentRatingKey"]       = series.GetPlexRatingKey(mapped.Season),
            ["parentKey"]             = $"/metadata/{series.GetPlexRatingKey(mapped.Season)}",
            ["parentGuid"]            = series.GetPlexGuid(mapped.Season),
            ["parentType"]            = "season",
            ["parentTitle"]           = GetSeasonFolder(mapped.Season),
            ["parentThumb"]           = parentThumb ?? (seriesImages.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } p ? ImageHelper.GetImageUrl(p, cacheBuster: cb) : null),
            ["parentArt"]             = seriesImages.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } a ? ImageHelper.GetImageUrl(a, cacheBuster: cb) : null,
            ["index"]                 = mapped.Episode,

            ["grandparentRatingKey"]  = series.GetPlexRatingKey(),
            ["grandparentKey"]        = $"/metadata/{series.ID}",
            ["grandparentGuid"]       = series.GetPlexGuid(),
            ["grandparentType"]       = "show",
            ["grandparentTitle"]      = titles.DisplayTitle,
            ["grandparentThumb"]      = seriesImages.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } gp ? ImageHelper.GetImageUrl(gp, cacheBuster: cb) : null,
            ["grandparentArt"]        = seriesImages.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } ga ? ImageHelper.GetImageUrl(ga, cacheBuster: cb) : null,
            ["parentIndex"]           = mapped.Season,

            ["Image"]                 = Settings.TmdbThumbnails ? [.. ImageHelper.GenerateImageArray(images, epTitle, Settings.AddEveryImage, cb).Where(img => img.Type == "snapshot")] : Array.Empty<ImageInfo>(),
            ["Guid"]                  = BuildEpisodeXrefGuidArray(ep, tmdbEpisode),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            //["Role"]                = CastHelper.GetCastAndCrew(ep), // Large array not used by Plex clients and present in grandparent series metadata
            ["Director"]              = CastHelper.GetDirectors(ep),
            ["Producer"]              = CastHelper.GetProducers(ep),
            ["Writer"]                = CastHelper.GetWriters(ep),
            ["Rating"]                = BuildRatingArray(ep)
            // csharpier-ignore-end
        };
    }

    #endregion

    #region Episodes List

    /// <summary>Builds a sorted list of mapped episode metadata objects for a given season.</summary>
    /// <param name="ctx">Series context containing file data and title information.</param>
    /// <param name="seasonNum">Season number whose episodes should be returned.</param>
    /// <returns>An ordered list of episode metadata objects for the season.</returns>
    public List<object> BuildEpisodeList(SeriesContext ctx, int seasonNum)
    {
        var items = new List<(PlexCoords Coords, int? Part, object Meta)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? prefId = MapHelper.GetPreferredTmdbOrderingId(ctx.Series);

        foreach (var m in ctx.FileData.GetForSeason(seasonNum))
        {
            // TMDB Episode Groups: One Shoko episode maps to multiple TMDB entries.
            if (EnforceTmdbNumbering && m.Episodes.Count == 1 && m.PrimaryEpisode is IShokoEpisode { TmdbEpisodes.Count: > 1 } se)
            {
                var tmdbEpisodes = SelectPreferredTmdbOrdering(se.TmdbEpisodes, prefId);
                for (int i = 0; i < tmdbEpisodes.Count; i++)
                {
                    var tmdbEp = tmdbEpisodes[i];
                    var (season, episode) = GetOrderingCoords(tmdbEp, prefId);
                    if (season != seasonNum)
                        continue;
                    var tmdbCoords = new PlexCoords { Season = season ?? 0, Episode = episode };
                    int? effectivePart = m.PartIndex ?? (i + 1); // Assign a virtual part index based on the TMDB segment order to ensure unique ratingKeys (e.g. e123p1, e123p2)
                    var meta = MapEpisode(se, tmdbCoords, ctx.Series, ctx.Titles, effectivePart, tmdbEp);
                    if (meta is Dictionary<string, object?> d && d.TryGetValue("ratingKey", out var rk) && rk is string key && seenKeys.Add(key))
                        items.Add((tmdbCoords, effectivePart, meta));
                }
                continue;
            }

            // Joined Episodes: Multiple Shoko episodes of the same type sharing a file.
            if (m.Episodes.Count > 1 && m.Episodes.Select(x => x.Type).Distinct().Count() == 1)
            {
                foreach (var ep in m.Episodes)
                {
                    var coordsEp = GetPlexCoordinates(ep, prefId);
                    if (coordsEp.Season != seasonNum)
                        continue;
                    var meta = MapEpisode(ep, coordsEp, ctx.Series, ctx.Titles, m.PartIndex);
                    if (meta is Dictionary<string, object?> d && d.TryGetValue("ratingKey", out var rk) && rk is string key && seenKeys.Add(key))
                        items.Add((coordsEp, m.PartIndex, meta));
                }
                continue;
            }

            // Standard or Relation-linked files: Use the PrimaryEpisode resolved by MapHelper, which filters out unwanted relations.
            var primaryMeta = MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode);
            if (primaryMeta is Dictionary<string, object?> pDict && pDict.TryGetValue("ratingKey", out var pRk) && pRk is string pKey && seenKeys.Add(pKey))
                items.Add((m.Coords, m.PartIndex, primaryMeta));
        }
        return [.. items.OrderBy(x => x.Coords.Episode).ThenBy(x => x.Part ?? 0).Select(x => x.Meta)];
    }

    #endregion

    #region Collections

    /// <summary>Builds a Plex-compatible metadata dictionary for a Shoko group collection.</summary>
    /// <param name="group">The Shoko group.</param>
    /// <param name="primarySeries">The series providing the artwork.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries) =>
        new()
        {
            // csharpier-ignore-start
            ["ratingKey"]             = group.GetPlexRatingKey(),
            ["guid"]                  = group.GetPlexGuid(),
            ["key"]                   = $"/collection/{group.ID}",
            ["type"]                  = "collection",
            ["subtype"]               = "show",
            ["title"]                 = group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle.Value : $"Group {group.ID}",
            ["thumb"]                 = (primarySeries as IWithImages)?.GetAvailableImages(ImageEntityType.Primary).FirstOrDefault() is { } poster ? ImageHelper.GetImageUrl(poster, GetCacheBuster(primarySeries)) : null,
            ["art"]                   = (primarySeries as IWithImages)?.GetAvailableImages(ImageEntityType.Backdrop).FirstOrDefault() is { } backdrop ? ImageHelper.GetImageUrl(backdrop, GetCacheBuster(primarySeries)) : null,
            ["titleSort"]             = group is IWithTitles t && !string.IsNullOrWhiteSpace(t.PreferredTitle?.Value) ? t.PreferredTitle.Value : $"Group {group.ID}",
            //["summary"]             = There is no summary source for groups
            //["Image"]               = Likely an image array will be used here
            // csharpier-ignore-end
        };

    /// <summary>Returns the collection name for a series based on its top-level Shoko group.</summary>
    /// <remarks>Count how many distinct Primary IDs exist in this group. This ensures that VFS Overrides are respected if they merge the entirety of a Shoko Group into a single series in Plex.</remarks>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>The group's preferred title, or null if no collection applies.</returns>
    public string? GetCollectionName(ISeries series) =>
        series is IShokoSeries { TopLevelGroupID: > 0 } ss
        && _metadataService.GetShokoGroupByID(ss.TopLevelGroupID) is { } group
        && group.Series.Select(s => OverrideHelper.GetPrimary(s.ID, _metadataService)).Distinct().Count() > 1
        && group is IWithTitles { PreferredTitle.Value: { } title }
            ? title
            : null;

    #endregion

    #region Key / Array Builders

    /// <summary>Generates a cache-busting string based on the last update timestamp of the provided entity.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <returns>A Unix timestamp string if the entity implements <see cref="IWithUpdateDate"/>, otherwise null.</returns>
    private static string? GetCacheBuster(object? entity) => entity is IWithUpdateDate upd ? new DateTimeOffset(upd.LastUpdatedAt).ToUnixTimeSeconds().ToString() : null;

    /// <summary>Generates a deduplicated, non-null array of Plex-compatible external cross-reference object GUIDs.</summary>
    /// <param name="rawIds">A collection of potential raw external resource paths.</param>
    /// <returns>An array of cross-reference objects containing valid GUIDs.</returns>
    private static object[] CreateXrefGuids(params string?[] rawIds) => [.. rawIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Select(id => (object)new { id })];

    /// <summary>Deduplicates and formats a collection of strings into an array of anonymous tags for Plex.</summary>
    /// <param name="tags">The source tag names to process.</param>
    /// <returns>An array of anonymous tag objects, or null if empty.</returns>
    private static object[]? CreateTagArray(IEnumerable<string>? tags) =>
        tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Select(t => (object)new { tag = t }).ToArray() is { Length: > 0 } arr ? arr : null;

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB) for a series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildXrefGuidArray(ISeries series) =>
        series is IShokoSeries ss && ss.TmdbShows?.FirstOrDefault() is { } ts ? CreateXrefGuids($"tmdb://{ts.ID}", ts.TvdbShowID > 0 ? $"tvdb://{ts.TvdbShowID}" : null)
        : series is ITmdbShow s ? CreateXrefGuids(s.ID > 0 ? $"tmdb://{s.ID}" : null, s.TvdbShowID > 0 ? $"tvdb://{s.TvdbShowID}" : null)
        : [];

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB) for a season.</summary>
    /// <param name="tmdbSeason">The TMDB season metadata object.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildSeasonXrefGuidArray(ITmdbSeason? tmdbSeason) => CreateXrefGuids(tmdbSeason?.ID is { Length: > 0 } id ? $"tmdb://{id}" : null);

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB) for an episode.</summary>
    /// <remarks>Prefer explicit TMDB overrides (from Episode Groups or Multi-part files) then fallback to standard Shoko metadata.</remarks>
    /// <param name="ep">The base episode metadata.</param>
    /// <param name="tmdbEpisodeOverride">An optional TMDB episode object (used for groups/parts).</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildEpisodeXrefGuidArray(IEpisode ep, object? tmdbEpisodeOverride) =>
        CreateXrefGuids([
            tmdbEpisodeOverride is ITmdbEpisode te ? $"tmdb://{te.ID}" : null,
            tmdbEpisodeOverride is ITmdbEpisode te2 && te2.TvdbEpisodeID > 0 ? $"tvdb://{te2.TvdbEpisodeID}" : null,
            tmdbEpisodeOverride is ITmdbEpisodeOrderingInformation oi ? $"tmdb://{oi.EpisodeID}" : null,
            .. ep is IShokoEpisode se && se.TmdbEpisodes != null ? se.TmdbEpisodes.SelectMany(t => t.TvdbEpisodeID > 0 ? [$"tmdb://{t.ID}", $"tvdb://{t.TvdbEpisodeID}"] : new[] { $"tmdb://{t.ID}" }) : [],
        ]);

    /// <summary>Resolves production country codes to English names for a series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of objects containing country tags, or null if none found.</returns>
    private object[]? BuildCountryArray(ISeries series) =>
        ((series as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.ProductionCountries ?? (series as IShokoSeries)?.TmdbMovies?.FirstOrDefault()?.ProductionCountries ?? (series as ITmdbShow)?.ProductionCountries)
            is { } codes
            ? CreateTagArray(
                codes
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c =>
                    {
                        try
                        {
                            return new System.Globalization.RegionInfo(c.Trim().ToUpperInvariant()).EnglishName;
                        }
                        catch
                        {
                            return c.Trim();
                        }
                    })
            )
            : null;

    /// <summary>Builds an array of similar anime titles and GUIDs for items that exist in the user's local Shoko collection.</summary>
    /// <remarks>Only include similar anime if they actually exist in the local Shoko collection. This ensures Plex can successfully retrieve metadata and artwork for the linked items.</remarks>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of objects containing guid and tag (title), or null if no local matches found.</returns>
    private object[]? BuildSimilarArray(ISeries series) =>
        series is IShokoSeries { AnidbAnime.Similar: { Count: > 0 } list }
            ? list.Select(s => _metadataService.GetShokoSeriesByAnidbID(s.SimilarID))
                .OfType<IShokoSeries>()
                .Select(ls => (object)new { guid = ls.GetPlexGuid(), tag = ls.PreferredTitle?.Value ?? ls.DefaultTitle?.Value })
                .ToArray()
                is { Length: > 0 } arr
                ? arr
                : null
            : null;

    /// <summary>Extracts the broadcasting network information for a series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of objects containing network tags, or null if none found.</returns>
    private object[]? BuildNetworkArray(ISeries series) =>
        ((series as IShokoSeries)?.TmdbShows?.FirstOrDefault() ?? (series as ITmdbShow)) is { } src && src.GetType().GetProperty("TmdbNetworks")?.GetValue(src) is System.Collections.IEnumerable list
            ? CreateTagArray(list.Cast<object>().Select(n => (n as ITmdbNetwork)?.Name ?? n.GetType().GetProperty("Name")?.GetValue(n) as string).OfType<string>())
            : null;

    /// <summary>Formats a numeric rating into a Plex-compatible audience rating object.</summary>
    /// <param name="r">The raw rating value (0-10).</param>
    /// <returns>An array containing the Plex rating object, or null if invalid.</returns>
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

    /// <summary>Resolves and formats the audience rating for a series.</summary>
    /// <param name="s">The series metadata.</param>
    /// <returns>A formatted rating array, or null.</returns>
    private object? BuildRatingArray(ISeries s) => BuildRatingArray((s as IShokoSeries)?.TmdbShows?.FirstOrDefault()?.Rating ?? (s as ITmdbShow)?.Rating);

    /// <summary>Resolves and formats the audience rating for an episode.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <returns>A formatted rating array, or null.</returns>
    private object? BuildRatingArray(IEpisode e) => BuildRatingArray((e as IShokoEpisode)?.TmdbEpisodes?.FirstOrDefault()?.Rating ?? (e as ITmdbEpisode)?.Rating);

    #endregion
}
