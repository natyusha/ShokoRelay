using System.Collections.Concurrent;
using System.Globalization;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Plex;

/// <summary>Converts Shoko series/episode/season/movie metadata into Plex-compatible property dictionaries.</summary>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="configProvider">Configuration provider.</param>
public class PlexMetadata(IMetadataService metadataService, ConfigProvider configProvider)
{
    #region Series Context

    /// <summary>Cache container storing a lazily-evaluated context alongside its originating config reference and post-build expiration timestamp.</summary>
    /// <param name="config">The global configuration instance active when this entry was constructed.</param>
    /// <param name="factory">The factory delegate used to generate the series context.</param>
    private sealed class ContextCacheEntry(RelayConfig config, Func<SeriesContext> factory)
    {
        /// <summary>The global configuration instance active when this entry was constructed.</summary>
        public RelayConfig Config { get; } = config;

        /// <summary>Expiration timestamp in UTC ticks after which the cached entry is considered stale.</summary>
        public long ExpirationTicks { get; set; } = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(1).Ticks;

        /// <summary>Lazily-evaluated series context instance.</summary>
        public Lazy<SeriesContext> LazyContext { get; } = new(factory);
    }

    /// <summary>Thread-safe cache for parsed series contexts to prevent thread-pool starvation during heavy concurrent Plex API requests.</summary>
    private readonly ConcurrentDictionary<int, ContextCacheEntry> _contextCache = new();

    /// <summary>Lock object used to prevent cache stampedes (thundering herd) when cache entries expire during high-traffic bursts.</summary>
    private readonly Lock _cacheLock = new();

    /// <summary>Invalidates the cached SeriesContext for a specific series or override group.</summary>
    /// <param name="seriesId">The Shoko series ID to invalidate.</param>
    public void InvalidateCache(int seriesId)
    {
        int primaryId = OverrideHelper.GetPrimary(seriesId, metadataService);
        _contextCache.TryRemove(primaryId, out _);
    }

    /// <summary>Aggregates relevant information about a Shoko series for controller responses.</summary>
    /// <param name="Series">The primary series metadata.</param>
    /// <param name="TmdbSeries">The linked TMDB series metadata, if present.</param>
    /// <param name="TmdbMovie">The linked TMDB movie metadata, if present.</param>
    /// <param name="Titles">A tuple containing Display, Sort, and Original titles.</param>
    /// <param name="ContentRating">The resolved content rating string.</param>
    /// <param name="IsAdult">Whether the series is flagged as adult content.</param>
    /// <param name="FileData">The mapping data for the series' files.</param>
    /// <param name="PreferredOrderingId">The preferred TMDB alternate ordering identifier, if configured.</param>
    public record SeriesContext(
        IShokoSeries Series,
        ISeries? TmdbSeries,
        IMovie? TmdbMovie,
        (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles,
        string ContentRating,
        bool IsAdult,
        MapHelper.SeriesFileData FileData,
        string? PreferredOrderingId
    );

    /// <summary>Resolves a Plex-style ratingKey into a <see cref="SeriesContext"/>.</summary>
    /// <param name="ratingKey">Plex rating key representing a show, season or episode.</param>
    /// <returns>A SeriesContext containing resolved series data and mappings, or null if not found.</returns>
    public SeriesContext? GetSeriesContext(string ratingKey)
    {
        int seriesId = PlexHelper.ExtractShokoSeriesIdFromRatingKey(ratingKey, metadataService);
        if (metadataService.GetShokoSeriesByID(seriesId) is not { } series)
            return null;

        int primaryId = OverrideHelper.GetPrimary(series.LocalID, metadataService);

        SeriesContext CreateSeriesContext()
        {
            var primarySeries = metadataService.GetShokoSeriesByID(primaryId) ?? series;
            var tmdbSeries = primarySeries.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() ?? primarySeries.AsTmdb();
            var tmdbMovie = primarySeries.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? primarySeries.Episodes.FirstOrDefault()?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();
            var fileData = MapHelper.GetConsolidatedSeriesFileData(primarySeries, metadataService);
            var prefId = MapHelper.GetPreferredTmdbOrderingId(primarySeries);
            var (rating, isAdult) = ContentRatingHelper.GetContentRatingAndAdult(primarySeries, tmdbMovie ?? (tmdbSeries as IWithContentRatings));
            return new SeriesContext(primarySeries, tmdbSeries, tmdbMovie, TextHelper.ResolveFullSeriesTitles(primarySeries), rating ?? "", isAdult, fileData, prefId);
        }

        var globalSettings = configProvider.GetSettings();

        // Bypass the shared cache when per-request /options/ or query overrides are active
        if (!ReferenceEquals(Settings, globalSettings))
            return CreateSeriesContext();

        long now = DateTime.UtcNow.Ticks;

        // Purge stale cache entries to prevent unbounded growth during extended uptime
        if (_contextCache.Count > 50)
            foreach (var kvp in _contextCache)
                if (!ReferenceEquals(kvp.Value.Config, globalSettings) || kvp.Value.ExpirationTicks < now)
                    _contextCache.TryRemove(kvp.Key, out _);

        var cached = _contextCache.GetOrAdd(primaryId, _ => new ContextCacheEntry(globalSettings, CreateSeriesContext));

        if (!ReferenceEquals(cached.Config, globalSettings) || cached.ExpirationTicks < now)
        {
            lock (_cacheLock)
            {
                if (_contextCache.TryGetValue(primaryId, out var fresh) && ReferenceEquals(fresh.Config, globalSettings) && fresh.ExpirationTicks >= now)
                    cached = fresh;
                else
                    _contextCache[primaryId] = cached = new ContextCacheEntry(globalSettings, CreateSeriesContext);
            }
        }

        var ctx = cached.LazyContext.Value;
        cached.ExpirationTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(1).Ticks;
        return ctx;
    }

    #endregion

    #region Collections

    /// <summary>Builds a Plex-compatible metadata dictionary for a Shoko group collection.</summary>
    /// <param name="group">The Shoko group.</param>
    /// <param name="primarySeries">The series providing the artwork.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries)
    {
        var tmdbSeries = (primarySeries as IShokoSeries)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() ?? primarySeries.AsTmdb();
        var desc = TextHelper.GetDescriptionByLanguage(primarySeries, Settings.DescriptionLanguage);
        var tmdbDesc = tmdbSeries?.PreferredOverview?.Value;
        var summary = TextHelper.SanitizeSummaryWithFallback(desc, tmdbDesc, Settings.SummaryMode);
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = group.GetPlexRatingKey(),
            ["guid"]                  = group.GetPlexGuid(),
            ["key"]                   = $"/collection/{group.LocalID}",
            ["type"]                  = "collection",
            ["subtype"]               = "show",
            ["title"]                 = group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle.Value : $"Group {group.LocalID}",
            ["thumb"]                 = (primarySeries as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["art"]                   = (primarySeries as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["titleSort"]             = group is IWithTitles t && !string.IsNullOrWhiteSpace(t.PreferredTitle?.Value) ? t.PreferredTitle.Value : $"Group {group.LocalID}",
            ["summary"]               = summary,
            //["Image"]               = Likely an image array will be used here
        };
    }

    /// <summary>Returns the collection name for a series based on its top-level Shoko group.</summary>
    /// <remarks>Count how many distinct Primary IDs exist in this group. This ensures that VFS Overrides are respected if they merge the entirety of a Shoko Group into a single series in Plex.</remarks>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>The group's preferred title, or null if no collection applies.</returns>
    public string? GetCollectionName(ISeries series) =>
        series is IShokoSeries { TopLevelGroupID: > 0 } ss
        && metadataService.GetShokoGroupByID(ss.TopLevelGroupID) is { } group
        && group.Series.Select(s => OverrideHelper.GetPrimary(s.LocalID, metadataService)).Distinct().Count() > 1
        && group is IWithTitles { PreferredTitle.Value: { } title }
            ? title
        // Standalone Multi-Part Movie fallback: If no group collection exists, but it's a multi-part movie, group the split parts into a collection named after the series display title
        : series is IShokoSeries shokoSeries && MapHelper.IsMovie(shokoSeries) && shokoSeries.Episodes.Count(e => e.Type == EpisodeType.Episode) > 1 ? series.GetDisplayTitle()
        : null;

    #endregion

    #region Series

    /// <summary>Builds a Plex-compatible metadata dictionary for a series (show).</summary>
    /// <param name="ctx">The resolved series context.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeries(SeriesContext ctx)
    {
        var series = ctx.Series;
        var images = (IWithImages)series;
        var tmdbSeries = ctx.TmdbSeries;
        var tmdbMovie = ctx.TmdbMovie;
        var description = TextHelper.GetDescriptionByLanguage(series, Settings.DescriptionLanguage);
        var tmdbDescription = tmdbSeries?.PreferredOverview?.Value ?? tmdbMovie?.PreferredOverview?.Value;
        var studios = CastHelper.GetStudioTags(series, metadataService);
        double? tmdbRating =
            tmdbSeries?.Rating > 0 ? tmdbSeries.Rating
            : tmdbMovie?.Rating > 0 ? tmdbMovie.Rating
            : null;
        double? anidbRating = series.Rating > 0 ? series.Rating : (series.Episodes.FirstOrDefault() is { Rating: > 0 } ep ? ep.Rating : null);
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = series.GetPlexRatingKey(),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey()}/children",
            ["guid"]                  = series.GetPlexGuid(),
            ["type"]                  = "show",
            ["title"]                 = ctx.Titles.DisplayTitle,
            ["originallyAvailableAt"] = series.AirDate?.ToDateOnly().ToString("yyyy-MM-dd", null) ?? tmdbMovie?.ReleaseDate?.ToString("yyyy-MM-dd", null),
            ["thumb"]                 = images.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage) ?? (tmdbMovie as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["art"]                   = images.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage) ?? (tmdbMovie as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["contentRating"]         = ctx.ContentRating,
            ["originalTitle"]         = ctx.Titles.OriginalTitle,
            ["titleSort"]             = ctx.Titles.SortTitle,
            ["year"]                  = series.AirDate?.Year ?? tmdbMovie?.ReleaseDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(description, tmdbDescription, Settings.SummaryMode),
            ["isAdult"]               = ctx.IsAdult,
            ["duration"]              = series.Episodes is { Count: > 0 } eps ? (int)eps.Sum(e => e.Runtime.TotalMilliseconds) : (tmdbMovie?.Runtime.HasValue == true ? (int)tmdbMovie.Runtime.Value.TotalMilliseconds : null),
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = Settings.PlexThemeMusic && tmdbSeries?.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } tvdb ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null,

            ["Image"]                 = ImageHelper.GenerateImageArray(images, ctx.Titles.DisplayTitle, Settings.AddEveryImage, Settings.TmdbImageLanguage),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            ["Genre"]                 = TagHelper.GetFilteredTags(series, tmdbSeries, metadataService),
            ["Guid"]                  = BuildXrefGuidArray(series, tmdbSeries, tmdbMovie),
            ["Country"]               = BuildCountryArray(series, tmdbSeries, tmdbMovie),
            ["Role"]                  = CastHelper.GetCastAndCrew(series, metadataService, tmdbSeries),
            ["Director"]              = CastHelper.GetDirectors(series, metadataService),
            ["Producer"]              = CastHelper.GetProducers(series, metadataService),
            ["Writer"]                = CastHelper.GetWriters(series, metadataService),
            ["Similar"]               = BuildSimilarArray(series),
            ["Studio"]                = studios,
            ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null,
            ["Network"]               = BuildNetworkArray(tmdbSeries),
            ["Rating"]                = BuildRatingArray(tmdbRating, anidbRating),
            //[SeasonType]            = Not relevant
        };
    }

    #endregion

    #region Movies

    /// <summary>Builds a Plex-compatible metadata dictionary for a standalone movie mapped from an episode.</summary>
    /// <param name="episode">The episode metadata acting as the movie.</param>
    /// <param name="series">The parent Shoko series metadata.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata override.</param>
    /// <param name="titles">The resolved title tuple.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapMovie(IEpisode episode, ISeries series, IMovie? tmdbMovie, (string DisplayTitle, string SortTitle, string? OriginalTitle) titles)
    {
        var seriesImages = (IWithImages)series;
        var movieImages = tmdbMovie as IWithImages;
        var tmdbSeries = (series as IShokoSeries)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault() ?? series.AsTmdb();
        string title = TextHelper.ResolveMovieTitle(episode, series, tmdbMovie);
        string description = TextHelper.GetDescriptionByLanguage(episode, Settings.DescriptionLanguage);
        if (string.IsNullOrWhiteSpace(description))
            description = TextHelper.GetDescriptionByLanguage(series, Settings.DescriptionLanguage);

        string? tmdbDescription = tmdbMovie?.PreferredOverview?.Value ?? tmdbSeries?.PreferredOverview?.Value;
        var (rating, isAdult) = ContentRatingHelper.GetContentRatingAndAdult(
            series,
            tmdbMovie ?? (series as IShokoSeries)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? (tmdbSeries as IWithContentRatings)
        );
        var studios = CastHelper.GetStudioTags(series, metadataService);

        var imagesArray = ImageHelper.GenerateMovieImageArray(seriesImages, movieImages, episode.EpisodeNumber, title, Settings.AddEveryImage, Settings.TmdbImageLanguage, out string? thumbUrl);
        string? artUrl = seriesImages.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage) ?? movieImages?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage);
        double? movieTmdbRating = tmdbMovie?.Rating ?? (episode as IShokoEpisode)?.GetLinkedEpisodes(MetadataSource.TMDB)?.FirstOrDefault()?.Rating ?? tmdbSeries?.Rating;
        double? movieAnidbRating = episode.Rating > 0 ? episode.Rating : series.Rating;
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = episode.GetPlexMovieRatingKey(),
            ["key"]                   = $"/metadata/{episode.GetPlexMovieRatingKey()}",
            ["guid"]                  = episode.GetPlexMovieGuid(),
            ["type"]                  = "movie",
            ["title"]                 = title,
            ["originallyAvailableAt"] = episode.AirDate?.ToString("yyyy-MM-dd", null) ?? tmdbMovie?.ReleaseDate?.ToString("yyyy-MM-dd", null) ?? series.AirDate?.ToDateOnly().ToString("yyyy-MM-dd", null),
            ["thumb"]                 = thumbUrl,
            ["art"]                   = artUrl,
            ["contentRating"]         = rating,
            ["originalTitle"]         = titles.OriginalTitle,
            ["titleSort"]             = string.IsNullOrWhiteSpace(titles.OriginalTitle) ? title : $"{title} – {titles.OriginalTitle}",
            ["year"]                  = episode.AirDate?.Year ?? tmdbMovie?.ReleaseDate?.Year ?? series.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(description, tmdbDescription, Settings.SummaryMode),
            ["isAdult"]               = isAdult,
            ["duration"]              = (int)episode.Runtime.TotalMilliseconds,
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = Settings.PlexThemeMusic && tmdbSeries?.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } tvdb ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null,

            ["Image"]                 = imagesArray,
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            ["Genre"]                 = TagHelper.GetFilteredTags(series, tmdbSeries, metadataService),
            ["Guid"]                  = BuildXrefGuidArray(series, tmdbSeries, tmdbMovie),
            ["Country"]               = BuildCountryArray(series, tmdbSeries),
            ["Role"]                  = CastHelper.GetCastAndCrew(series, metadataService, tmdbSeries),
            ["Director"]              = CastHelper.GetDirectors(series, metadataService),
            ["Producer"]              = CastHelper.GetProducers(series, metadataService),
            ["Writer"]                = CastHelper.GetWriters(series, metadataService),
            ["Similar"]               = BuildSimilarArray(series),
            ["Studio"]                = studios,
            ["Collection"]            = GetCollectionName(series) is string c ? new[] { new { tag = c } } : null,
            ["Rating"]                = BuildRatingArray(movieTmdbRating, movieAnidbRating)
        };
    }

    #endregion

    #region Seasons

    /// <summary>Builds a Plex-compatible metadata dictionary for a single season.</summary>
    /// <param name="ctx">The resolved series context.</param>
    /// <param name="seasonNum">The season index.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeason(SeriesContext ctx, int seasonNum)
    {
        var series = ctx.Series;
        var seasonTitle = GetSeasonFolder(seasonNum);
        var seasonDate = ctx.FileData.GetForSeason(seasonNum).SelectMany(m => m.Episodes).Where(e => e.AirDate.HasValue).Min(e => e.AirDate);
        string? seasonSummary = null;

        // When using VFS overrides find a Shoko series in the group which contains the TMDB metadata for the requisite season number.
        bool ignoreTmdb = !string.IsNullOrEmpty(ctx.PreferredOrderingId);
        ISeason? tmdbSeason = null;
        if (!ignoreTmdb)
        {
            var groupIds = OverrideHelper.GetGroup(series.LocalID, metadataService);
            tmdbSeason = groupIds
                .Select(metadataService.GetShokoSeriesByID)
                .OfType<IShokoSeries>()
                .SelectMany(s => s.GetLinkedSeasons(MetadataSource.TMDB) ?? [])
                .FirstOrDefault(ts => ts.SeasonNumber == seasonNum);
        }

        if (tmdbSeason != null)
        {
            seasonTitle = tmdbSeason.PreferredTitle?.Value ?? seasonTitle;
            seasonSummary = TextHelper.SummarySanitizer(tmdbSeason.PreferredOverview?.Value, Settings.SummaryMode);
        }

        List<string>? posters =
            (Settings.TmdbSeasonPosters && tmdbSeason != null)
                ? [.. ImageHelper.FilterImagesByLanguage(tmdbSeason.GetAvailableImages(ImageEntityType.Primary), Settings.TmdbImageLanguage, true).Select(i => ImageHelper.GetImageUrl(i, forceRemote: true))]
                : null;

        string? thumb = null;
        if (Settings.TmdbSeasonPosters && tmdbSeason != null)
            thumb = tmdbSeason.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage);
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = series.GetPlexRatingKey(seasonNum),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey(seasonNum)}/children",
            ["guid"]                  = series.GetPlexGuid(seasonNum),
            ["type"]                  = "season",
            ["title"]                 = seasonTitle,
            ["originallyAvailableAt"] = seasonDate?.ToString("yyyy-MM-dd", null),
            ["thumb"]                 = thumb ??= series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["contentRating"]         = ctx.ContentRating,
            //['originalTitle']       = No source for original season titles
            ["titleSort"]             = seasonTitle,
            ["year"]                  = seasonDate?.Year,
            ["summary"]               = seasonSummary ?? string.Empty,
            ["isAdult"]               = ctx.IsAdult,

            ["parentRatingKey"]       = series.GetPlexRatingKey(),
            ["parentKey"]             = $"/metadata/{series.ID.ID}",
            ["parentGuid"]            = series.GetPlexGuid(),
            ["parentType"]            = "show",
            ["parentTitle"]           = ctx.Titles.DisplayTitle,
            ["parentThumb"]           = series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["parentArt"]             = series.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["index"]                 = seasonNum,

            // Force addEveryImage to true if TMDB season posters are present, otherwise fallback to the configuration. (Remove this once Shoko's WebUI supports selecting the preferred poster)
            ["Image"]                 = ImageHelper.BuildCoverPosterArray(series, seasonTitle, posters != null || Settings.AddEveryImage, Settings.TmdbImageLanguage, posters),
            ["Guid"]                  = BuildSeasonXrefGuidArray(tmdbSeason),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
        };
    }

    #endregion

    #region Episodes

    /// <summary>Builds a Plex-compatible metadata dictionary for a single episode.</summary>
    /// <param name="episode">The episode metadata.</param>
    /// <param name="mapped">The resolved Plex coordinates.</param>
    /// <param name="ctx">The resolved series context.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <param name="tmdbEpisode">Optional TMDB episode metadata override.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapEpisode(IEpisode episode, PlexCoords mapped, SeriesContext ctx, int? partIndex = null, IEpisode? tmdbEpisode = null)
    {
        var series = ctx.Series;
        var actualSeries = (episode as IShokoEpisode)?.Series ?? series;

        var tmdbEps = (episode as IShokoEpisode)?.GetLinkedEpisodes(MetadataSource.TMDB);
        var tmdbEp = tmdbEpisode ?? tmdbEps?.FirstOrDefault() ?? episode.AsTmdb();
        var tmdbMovie = (episode as IShokoEpisode)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? actualSeries.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();

        var images = (IWithImages)episode;
        var seriesImages = (IWithImages)series;

        bool isMovieOverride = tmdbMovie != null || actualSeries.Type == AnimeType.Movie;

        string epTitle =
            tmdbEp is IWithTitles { PreferredTitle.Value: { Length: > 0 } pt } ? pt
            : isMovieOverride ? TextHelper.ResolveMovieTitle(episode, actualSeries, tmdbMovie)
            : TextHelper.ResolveEpisodeTitle(episode, actualSeries.GetDisplayTitle() ?? ctx.Titles.DisplayTitle, tmdbEp);

        string epDescription = tmdbEp is IWithOverviews { PreferredOverview.Value: { Length: > 0 } pd } ? pd : TextHelper.GetDescriptionByLanguage(episode, Settings.DescriptionLanguage);
        if (isMovieOverride && string.IsNullOrWhiteSpace(epDescription))
            epDescription = tmdbMovie?.PreferredOverview?.Value ?? TextHelper.GetDescriptionByLanguage(actualSeries, Settings.DescriptionLanguage);

        string? parentThumb = null;
        if (Settings.TmdbSeasonPosters && mapped.Season >= 0 && string.IsNullOrEmpty(ctx.PreferredOrderingId))
            parentThumb = series.GetLinkedSeasons(MetadataSource.TMDB)?.FirstOrDefault(ts => ts.SeasonNumber == mapped.Season)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage);

        string? thumb = Settings.TmdbThumbnails
            ? (images.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage) ?? tmdbMovie?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage))
            : null;

        var (rating, isAdult) =
            isMovieOverride && actualSeries.LocalID != series.LocalID ? ContentRatingHelper.GetContentRatingAndAdult(actualSeries, tmdbEp as IWithContentRatings ?? tmdbMovie) : (ctx.ContentRating, ctx.IsAdult);

        double? epTmdbRating =
            tmdbEp?.Rating > 0 ? tmdbEp.Rating
            : tmdbMovie?.Rating > 0 ? tmdbMovie.Rating
            : null;
        double? epAnidbRating =
            episode.Rating > 0 ? episode.Rating
            : series.Rating > 0 ? series.Rating
            : null;
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = episode.GetPlexRatingKey(partIndex),
            ["key"]                   = $"/metadata/{episode.GetPlexRatingKey(partIndex)}",
            ["guid"]                  = episode.GetPlexGuid(partIndex),
            ["type"]                  = "episode",
            ["subtype"]               = (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var ex)) ? ex.Subtype : null,
            ["title"]                 = epTitle,
            ["originallyAvailableAt"] = episode.AirDate?.ToString("yyyy-MM-dd", null),
            ["thumb"]                 = thumb,
            //["art"]                 = No source for episode level background images
            ["contentRating"]         = rating,
            //["originalTitle"]       = No source for original episode titles
            ["titleSort"]             = epTitle,
            ["year"]                  = episode.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(epDescription, tmdbEp?.PreferredOverview?.Value, Settings.SummaryMode),
            ["isAdult"]               = isAdult,
            ["duration"]              = (int)episode.Runtime.TotalMilliseconds,

            ["parentRatingKey"]       = series.GetPlexRatingKey(mapped.Season),
            ["parentKey"]             = $"/metadata/{series.GetPlexRatingKey(mapped.Season)}",
            ["parentGuid"]            = series.GetPlexGuid(mapped.Season),
            ["parentType"]            = "season",
            ["parentTitle"]           = GetSeasonFolder(mapped.Season),
            ["parentThumb"]           = parentThumb ?? seriesImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["parentArt"]             = seriesImages.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["index"]                 = mapped.Episode,

            ["grandparentRatingKey"]  = series.GetPlexRatingKey(),
            ["grandparentKey"]        = $"/metadata/{series.ID.ID}",
            ["grandparentGuid"]       = series.GetPlexGuid(),
            ["grandparentType"]       = "show",
            ["grandparentTitle"]      = ctx.Titles.DisplayTitle,
            ["grandparentThumb"]      = seriesImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["grandparentArt"]        = seriesImages.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["parentIndex"]           = mapped.Season,

            ["Image"]                 = Settings.TmdbThumbnails ? ImageHelper.BuildSnapshotArray(images, epTitle, Settings.AddEveryImage, Settings.TmdbImageLanguage) : [],
            ["Guid"]                  = BuildEpisodeXrefGuidArray(tmdbEp, tmdbMovie, tmdbEps),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            //["Role"]                = CastHelper.GetCastAndCrew(episode, metadataService), // Large array not used by Plex clients and present in grandparent
            ["Director"]              = CastHelper.GetDirectors(episode, metadataService),
            ["Producer"]              = CastHelper.GetProducers(episode, metadataService),
            ["Writer"]                = CastHelper.GetWriters(episode, metadataService),
            ["Rating"]                = BuildRatingArray(epTmdbRating, epAnidbRating)
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
        string? prefId = ctx.PreferredOrderingId;
        bool enforceTmdb = EnforceTmdbNumbering;

        foreach (var m in ctx.FileData.GetForSeason(seasonNum))
        {
            // TMDB Episode Groups: One Shoko episode maps to multiple TMDB entries.
            if (enforceTmdb && m.Episodes.Count == 1 && m.PrimaryEpisode is IShokoEpisode se && se.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 1 } tmdbEps)
            {
                var tmdbEpisodes = SelectPreferredTmdbOrdering(tmdbEps, prefId);
                for (int i = 0; i < tmdbEpisodes.Count; i++)
                {
                    var tmdbEp = tmdbEpisodes[i];
                    var (season, episode) = GetOrderingCoords(tmdbEp, prefId);
                    if (season != seasonNum)
                        continue;
                    var tmdbCoords = new PlexCoords { Season = season ?? 0, Episode = episode };
                    int? effectivePart = m.PartIndex ?? (i + 1); // Assign a virtual part index based on the TMDB segment order to ensure unique ratingKeys (e.g. e123p1, e123p2)
                    var meta = MapEpisode(se, tmdbCoords, ctx, effectivePart, tmdbEp);
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
                    coordsEp.Season = m.Coords.Season;
                    if (coordsEp.Season != seasonNum)
                        continue;
                    var meta = MapEpisode(ep, coordsEp, ctx, m.PartIndex);
                    if (meta is Dictionary<string, object?> d && d.TryGetValue("ratingKey", out var rk) && rk is string key && seenKeys.Add(key))
                        items.Add((coordsEp, m.PartIndex, meta));
                }
                continue;
            }

            // Standard or Relation-linked files: Use the PrimaryEpisode resolved by MapHelper, which filters out unwanted relations.
            var primaryMeta = MapEpisode(m.PrimaryEpisode, m.Coords, ctx, m.PartIndex, m.TmdbEpisode);
            if (primaryMeta is Dictionary<string, object?> pDict && pDict.TryGetValue("ratingKey", out var pRk) && pRk is string pKey && seenKeys.Add(pKey))
                items.Add((m.Coords, m.PartIndex, primaryMeta));
        }
        return [.. items.OrderBy(x => x.Coords.Episode).ThenBy(x => x.Part ?? 0).Select(x => x.Meta)];
    }

    #endregion


    #region Key & Array Builders

    /// <summary>Generates a deduplicated, non-null array of Plex-compatible external cross-reference object GUIDs.</summary>
    /// <param name="rawIds">A collection of potential raw external resource paths.</param>
    /// <returns>An array of cross-reference objects containing valid GUIDs.</returns>
    private static object[] CreateXrefGuids(params string?[] rawIds) => [.. rawIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Select(id => (object)new { id })];

    /// <summary>Deduplicates and formats a collection of strings into an array of anonymous tags for Plex.</summary>
    /// <param name="tags">The source tag names to process.</param>
    /// <returns>An array of anonymous tag objects, or null if empty.</returns>
    private static object[]? CreateTagArray(IEnumerable<string>? tags) =>
        tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Select(t => (object)new { tag = t }).ToArray() is { Length: > 0 } arr ? arr : null;

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB/IMDB) for a series or movie.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="tmdbSeries">The TMDB series metadata object.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata override.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildXrefGuidArray(ISeries series, ISeries? tmdbSeries = null, IMovie? tmdbMovie = null)
    {
        var tm = tmdbMovie ?? (series as IShokoSeries)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();
        return tm != null ? CreateXrefGuids($"tmdb://{tm.ID.ID}", tm.GetCrossSourceID("imdb", MetadataEntityType.Movie) is { } imdb ? $"imdb://{imdb}" : null)
            : tmdbSeries != null ? CreateXrefGuids($"tmdb://{tmdbSeries.ID.ID}", tmdbSeries.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } tsTvdb ? $"tvdb://{tsTvdb}" : null)
            : [];
    }

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB) for a season.</summary>
    /// <param name="tmdbSeason">The TMDB season metadata object.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildSeasonXrefGuidArray(ISeason? tmdbSeason) => CreateXrefGuids(tmdbSeason?.ID.ID is { Length: > 0 } id ? $"tmdb://{id}" : null);

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB) for an episode.</summary>
    /// <param name="tmdbEp">The pre-resolved TMDB episode metadata object.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata override.</param>
    /// <param name="tmdbEps">The pre-resolved collection of TMDB episodes mapped to the Shoko episode.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildEpisodeXrefGuidArray(IEpisode? tmdbEp, IMovie? tmdbMovie, IReadOnlyList<IEpisode>? tmdbEps) =>
        CreateXrefGuids([
            tmdbEp != null ? $"tmdb://{tmdbEp.ID.ID}" : (tmdbMovie != null ? $"tmdb://{tmdbMovie.ID.ID}" : null),
            tmdbEp?.GetCrossSourceID("tvdb", MetadataEntityType.Episode) is { } teTvdb
                ? $"tvdb://{teTvdb}"
                : (tmdbMovie?.GetCrossSourceID("tvdb", MetadataEntityType.Movie) is { } tmTvdb ? $"tvdb://{tmTvdb}" : null),
            .. tmdbEps is { Count: > 0 } list
                ? list.SelectMany(t => t.GetCrossSourceID("tvdb", MetadataEntityType.Episode) is { } tTvdb ? [$"tmdb://{t.ID.ID}", $"tvdb://{tTvdb}"] : new[] { $"tmdb://{t.ID.ID}" })
                : [],
        ]);

    /// <summary>Resolves production country codes to English names for a series or movie.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="tmdbSeries">The TMDB series metadata object.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata object.</param>
    /// <returns>An array of objects containing country tags, or null if none found.</returns>
    private object[]? BuildCountryArray(ISeries series, ISeries? tmdbSeries = null, IMovie? tmdbMovie = null) =>
        (tmdbSeries?.ProductionCountries ?? tmdbMovie?.ProductionCountries ?? (series as IShokoSeries)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault()?.ProductionCountries) is { } codes
            ? CreateTagArray(
                codes
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c =>
                    {
                        try
                        {
                            return new RegionInfo(c.Trim().ToUpperInvariant()).EnglishName;
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
        series is IShokoSeries { AnidbAnime.Suggestions: { Count: > 0 } list }
            ? list.Select(s => metadataService.GetShokoSeriesByAnidbID(s.SuggestedID.GetNumericID<int>()))
                .OfType<IShokoSeries>()
                .Select(ls => (object)new { guid = ls.GetPlexGuid(), tag = ls.PreferredTitle?.Value ?? ls.DefaultTitle?.Value })
                .ToArray()
                is { Length: > 0 } arr
                ? arr
                : null
            : null;

    /// <summary>Extracts the broadcasting network information for a series.</summary>
    /// <param name="tmdbSeries">The TMDB series metadata object.</param>
    /// <returns>An array of objects containing network tags, or null if none found.</returns>
    private object[]? BuildNetworkArray(ISeries? tmdbSeries) => tmdbSeries is { } src ? CreateTagArray(src.Networks.Select(n => n.Name)) : null;

    /// <summary>Formats numeric ratings into a Plex-compatible audience rating array.</summary>
    /// <param name="tmdbRating">The TMDB rating value (0-10).</param>
    /// <param name="anidbRating">The AniDB rating value (0-10).</param>
    /// <returns>An array containing Plex rating objects, or null if none are valid.</returns>
    private static object[]? BuildRatingArray(double? tmdbRating, double? anidbRating)
    {
        var ratings = new List<object>();
        static object CreateRating(string image, double val) =>
            new
            {
                image,
                type = "audience",
                value = (float)val,
            };

        if (Settings.AnidbAsRottenTomatoes && anidbRating > 0)
            ratings.Add(CreateRating("rottentomatoes://image.rating.upright", anidbRating.Value));

        double? tmdbVal = tmdbRating > 0 ? tmdbRating : (!Settings.AnidbAsRottenTomatoes ? anidbRating : null);
        if (tmdbVal > 0)
            ratings.Add(CreateRating("themoviedb://image.rating", tmdbVal.Value));

        return ratings.Count > 0 ? [.. ratings] : null;
    }

    #endregion
}
