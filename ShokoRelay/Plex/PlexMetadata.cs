using System.Collections.Concurrent;
using System.Globalization;
using Shoko.Abstractions.Metadata.Containers;
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
        public long ExpirationTicks
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        } = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(1).Ticks;

        /// <summary>Lazily-evaluated series context instance.</summary>
        public Lazy<SeriesContext> LazyContext { get; } = new(factory, LazyThreadSafetyMode.PublicationOnly);
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

    /// <summary>Clears all cached series contexts across the provider.</summary>
    public void ClearCache() => _contextCache.Clear();

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
            var preferredOrderingId = MapHelper.GetPreferredTmdbOrderingId(primarySeries);
            var (rating, isAdult) = ContentRatingHelper.GetContentRatingAndAdult(primarySeries, tmdbMovie ?? (tmdbSeries as IWithContentRatings));
            return new SeriesContext(primarySeries, tmdbSeries, tmdbMovie, TextHelper.ResolveFullSeriesTitles(primarySeries), rating ?? "", isAdult, fileData, preferredOrderingId);
        }

        var globalSettings = configProvider.GetSettings();

        // Bypass the shared cache when per-request /options/ or query overrides are active
        if (!ReferenceEquals(Settings, globalSettings))
            return CreateSeriesContext();

        long now = DateTime.UtcNow.Ticks;

        // Purge stale cache entries to prevent unbounded growth during extended uptime
        if (_contextCache.Count > 50)
            foreach (var entry in _contextCache)
                if (!ReferenceEquals(entry.Value.Config, globalSettings) || entry.Value.ExpirationTicks < now)
                    _contextCache.TryRemove(entry.Key, out _);

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

        var context = cached.LazyContext.Value;
        cached.ExpirationTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(1).Ticks;
        return context;
    }

    #endregion

    #region Collections

    /// <summary>Builds a Plex-compatible metadata dictionary for a Shoko group collection.</summary>
    /// <param name="group">The Shoko group.</param>
    /// <param name="primarySeries">The series providing the artwork.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapCollection(IShokoGroup group, ISeries primarySeries) =>
        // csharpier-ignore
        new()
        {
            ["ratingKey"] = group.GetPlexRatingKey(),
            ["guid"] = group.GetPlexGuid(),
            ["key"] = $"/collection/{group.LocalID}",
            ["type"] = "collection",
            ["subtype"] = "show",
            ["title"] = !string.IsNullOrWhiteSpace(group.PreferredTitle?.Value) ? group.PreferredTitle.Value : $"Group {group.LocalID}",
            ["thumb"] = (primarySeries as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["art"] = (primarySeries as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["titleSort"] = !string.IsNullOrWhiteSpace(group.PreferredTitle?.Value) ? group.PreferredTitle.Value : $"Group {group.LocalID}",
            ["summary"] = TextHelper.GetSanitizedDescription(primarySeries),
            //["Image"]               = Likely an image array will be used here
        };

    /// <summary>Returns the collection name for a series based on its top-level Shoko group.</summary>
    /// <remarks>Count how many distinct Primary IDs exist in this group. This ensures that VFS Overrides are respected if they merge the entirety of a Shoko Group into a single series in Plex.</remarks>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="context">Optional pre-resolved series context to reuse cached file mappings and titles.</param>
    /// <returns>The group's preferred title, or null if no collection applies.</returns>
    public string? GetCollectionName(ISeries series, SeriesContext? context = null) =>
        series is IShokoSeries { TopLevelGroupID: > 0 } shokoSeries
        && metadataService.GetShokoGroupByID(shokoSeries.TopLevelGroupID) is { } group
        && group.Series.Select(groupSeries => OverrideHelper.GetPrimary(groupSeries.LocalID, metadataService)).Distinct().Count() > 1
        && group is IWithTitles { PreferredTitle.Value: { } title }
            ? title
        // Standalone Multi-Part Movie fallback: If no group collection exists, but it's a multi-part movie across separate entries, group the parts into a collection named after the series display title
        : series is IShokoSeries movieSeries
        && MapHelper.IsMovie(movieSeries)
        && ((context ?? GetSeriesContext(movieSeries.ID.ID))?.FileData ?? MapHelper.GetSeriesFileData(movieSeries, metadataService))
            .Mappings.Where(mapping => mapping.PrimaryEpisode.Type == EpisodeType.Episode)
            .Select(mapping => mapping.PrimaryEpisode.ID)
            .Distinct()
            .Skip(1)
            .Any()
            ? (context?.Titles.DisplayTitle ?? series.GetDisplayTitle())
        : null;

    #endregion

    #region Series

    /// <summary>Builds a Plex-compatible metadata dictionary for a series (show).</summary>
    /// <param name="context">The resolved series context.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeries(SeriesContext context)
    {
        var series = context.Series;
        var images = (IWithImages)series;
        var tmdbSeries = context.TmdbSeries;
        var tmdbMovie = context.TmdbMovie;
        var duration = series.Episodes is { Count: > 0 } episodes
            ? (int?)Math.Min(int.MaxValue, episodes.Sum(episode => episode.Runtime.TotalMilliseconds))
            : (tmdbMovie?.Runtime.HasValue == true ? (int?)Math.Min(int.MaxValue, tmdbMovie.Runtime.Value.TotalMilliseconds) : null);
        var studios = CastHelper.GetStudioTags(series, metadataService);
        double? tmdbRating =
            tmdbSeries?.Rating > 0 ? tmdbSeries.Rating
            : tmdbMovie?.Rating > 0 ? tmdbMovie.Rating
            : null;
        double? anidbRating = series.Rating > 0 ? series.Rating : (series.Episodes.FirstOrDefault() is { Rating: > 0 } episode ? episode.Rating : null);
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = series.GetPlexRatingKey(),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey()}/children",
            ["guid"]                  = series.GetPlexGuid(),
            ["type"]                  = "show",
            ["title"]                 = context.Titles.DisplayTitle,
            ["originallyAvailableAt"] = series.AirDate?.ToDateOnly().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? tmdbMovie?.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["thumb"]                 = images.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage) ?? (tmdbMovie as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["art"]                   = images.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage) ?? (tmdbMovie as IWithImages)?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["contentRating"]         = context.ContentRating,
            ["originalTitle"]         = context.Titles.OriginalTitle,
            ["titleSort"]             = context.Titles.SortTitle,
            ["year"]                  = series.AirDate?.Year ?? tmdbMovie?.ReleaseDate?.Year,
            ["summary"]               = TextHelper.GetSanitizedDescription(series),
            ["isAdult"]               = context.IsAdult,
            ["duration"]              = duration,
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = Settings.PlexThemeMusic && tmdbSeries?.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } tvdb ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null,

            ["Image"]                 = ImageHelper.GenerateImageArray(images, context.Titles.DisplayTitle, Settings.AddEveryImage, Settings.TmdbImageLanguage),
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
            ["Collection"]            = GetCollectionName(series, context) is string collectionName ? new[] { new { tag = collectionName } } : null,
            ["Network"]               = BuildNetworkArray(tmdbSeries),
            ["Rating"]                = BuildRatingArray(tmdbRating, anidbRating),
            //[SeasonType]            = Not relevant
        };
    }

    #endregion

    #region Movies

    /// <summary>Builds a Plex-compatible metadata dictionary for a standalone movie mapped from an episode.</summary>
    /// <param name="episode">The episode metadata acting as the movie.</param>
    /// <param name="context">The resolved series context.</param>
    /// <param name="episodeTmdbMovie">The optional episode-specific TMDB movie metadata override.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapMovie(IEpisode episode, SeriesContext context, IMovie? episodeTmdbMovie = null)
    {
        var series = context.Series;
        var seriesImages = (IWithImages)series;
        var tmdbMovie = episodeTmdbMovie ?? context.TmdbMovie;
        var movieImages = tmdbMovie as IWithImages;
        var tmdbSeries = context.TmdbSeries;

        var mappedFile = context.FileData.Mappings.FirstOrDefault(fileMapping => fileMapping.Episodes.Any(mappedEpisode => mappedEpisode.ID == episode.ID));
        List<IEpisode> fileMainEpisodes =
            mappedFile != null ? [.. mappedFile.Episodes.Where(mappedEpisode => mappedEpisode.Type == EpisodeType.Episode)]
            : episode is IShokoEpisode { Videos: { Count: > 0 } videos } shokoEpisode
                ?
                [
                    .. videos
                        .SelectMany(video => video.Episodes)
                        .Where(linkedEpisode => linkedEpisode.Type == EpisodeType.Episode && linkedEpisode.ShokoSeriesID == shokoEpisode.ShokoSeriesID && !MapHelper.IsHidden(linkedEpisode))
                        .DistinctBy(linkedEpisode => linkedEpisode.LocalID),
                ]
            : [];
        bool isMultiEpisodeMovie = fileMainEpisodes.Count > 1;

        string title = TextHelper.ResolveMovieTitle(episode, series, tmdbMovie, isStandaloneMovie: true, seriesTitle: context.Titles.DisplayTitle, isMultiEpFile: isMultiEpisodeMovie);
        string? originallyAvailableAt = episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? tmdbMovie?.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? series.AirDate?.ToDateOnly().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string description = isMultiEpisodeMovie ? string.Empty : TextHelper.GetDescriptionByLanguage(episode, Settings.DescriptionLanguage);
        if (string.IsNullOrWhiteSpace(description))
            description = TextHelper.GetDescriptionByLanguage(series, Settings.DescriptionLanguage);

        string? tmdbDescription = tmdbMovie?.PreferredOverview?.Value ?? tmdbSeries?.PreferredOverview?.Value;
        var (rating, isAdult) =
            episodeTmdbMovie != null && episodeTmdbMovie.ID != context.TmdbMovie?.ID ? ContentRatingHelper.GetContentRatingAndAdult(series, episodeTmdbMovie) : (context.ContentRating, context.IsAdult);
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
            ["originallyAvailableAt"] = originallyAvailableAt,
            ["thumb"]                 = thumbUrl,
            ["art"]                   = artUrl,
            ["contentRating"]         = rating,
            ["originalTitle"]         = context.Titles.OriginalTitle,
            ["titleSort"]             = string.IsNullOrWhiteSpace(context.Titles.OriginalTitle) ? title : $"{title} – {context.Titles.OriginalTitle}",
            ["year"]                  = episode.AirDate?.Year ?? tmdbMovie?.ReleaseDate?.Year ?? series.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(description, tmdbDescription, Settings.SummaryMode),
            ["isAdult"]               = isAdult,
            ["duration"]              = isMultiEpisodeMovie ? (int)Math.Min(int.MaxValue, fileMainEpisodes.Sum(mainEpisode => mainEpisode.Runtime.TotalMilliseconds)) : (int)Math.Min(int.MaxValue, episode.Runtime.TotalMilliseconds),
            //["tagline"]             = TMDB has this but it is not exposed
            ["studio"]                = studios.FirstOrDefault()?.Tag,
            ["theme"]                 = Settings.PlexThemeMusic && tmdbSeries?.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } tvdb ? $"https://tvthemes.plexapp.com/{tvdb}.mp3" : null,

            ["Image"]                 = imagesArray,
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
            ["Collection"]            = GetCollectionName(series, context) is string collectionName ? new[] { new { tag = collectionName } } : null,
            ["Rating"]                = BuildRatingArray(movieTmdbRating, movieAnidbRating)
        };
    }

    #endregion

    #region Seasons

    /// <summary>Builds a Plex-compatible metadata dictionary for a single season.</summary>
    /// <param name="context">The resolved series context.</param>
    /// <param name="seasonNumber">The season index.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapSeason(SeriesContext context, int seasonNumber)
    {
        var series = context.Series;
        var seasonTitle = GetSeasonFolder(seasonNumber);
        var seasonDate = context.FileData.GetForSeason(seasonNumber).SelectMany(mapping => mapping.Episodes).Where(episode => episode.AirDate.HasValue).Min(episode => episode.AirDate);
        string? seasonSummary = null;

        // When using VFS overrides find a Shoko series in the group which contains the TMDB metadata for the requisite season number.
        bool ignoreTmdb = !string.IsNullOrEmpty(context.PreferredOrderingId);
        ISeason? tmdbSeason = !ignoreTmdb ? FindTmdbSeason(series, seasonNumber) : null;

        if (tmdbSeason != null)
        {
            seasonTitle = tmdbSeason.PreferredTitle?.Value ?? seasonTitle;
            seasonSummary = TextHelper.SummarySanitizer(tmdbSeason.PreferredOverview?.Value, Settings.SummaryMode);
        }

        List<string>? posters =
            (Settings.TmdbSeasonPosters && tmdbSeason != null)
                ?
                [
                    .. ImageHelper
                        .FilterImagesByLanguage(tmdbSeason.GetAvailableImages(ImageEntityType.Primary), Settings.TmdbImageLanguage, true)
                        .Select(image => ImageHelper.GetImageUrl(image, forceRemote: true)),
                ]
                : null;

        string? thumb = null;
        if (Settings.TmdbSeasonPosters && tmdbSeason != null)
            thumb = tmdbSeason.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage);
        // csharpier-ignore
        return new()
        {
            ["ratingKey"]             = series.GetPlexRatingKey(seasonNumber),
            ["key"]                   = $"/metadata/{series.GetPlexRatingKey(seasonNumber)}/children",
            ["guid"]                  = series.GetPlexGuid(seasonNumber),
            ["type"]                  = "season",
            ["title"]                 = seasonTitle,
            ["originallyAvailableAt"] = seasonDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["thumb"]                 = thumb ?? series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["contentRating"]         = context.ContentRating,
            //['originalTitle']       = No source for original season titles
            ["titleSort"]             = seasonTitle,
            ["year"]                  = seasonDate?.Year,
            ["summary"]               = seasonSummary ?? string.Empty,
            ["isAdult"]               = context.IsAdult,

            ["parentRatingKey"]       = series.GetPlexRatingKey(),
            ["parentKey"]             = $"/metadata/{series.ID.ID}",
            ["parentGuid"]            = series.GetPlexGuid(),
            ["parentType"]            = "show",
            ["parentTitle"]           = context.Titles.DisplayTitle,
            ["parentThumb"]           = series.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["parentArt"]             = series.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["index"]                 = seasonNumber,

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
    /// <param name="context">The resolved series context.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <param name="tmdbEpisode">Optional TMDB episode metadata override.</param>
    /// <returns>A dictionary of Plex metadata properties.</returns>
    public Dictionary<string, object?> MapEpisode(IEpisode episode, PlexCoords mapped, SeriesContext context, int? partIndex = null, IEpisode? tmdbEpisode = null)
    {
        var series = context.Series;
        var actualSeries = (episode as IShokoEpisode)?.Series ?? series;

        var tmdbEpisodes = (episode as IShokoEpisode)?.GetLinkedEpisodes(MetadataSource.TMDB);
        var resolvedTmdbEpisode = tmdbEpisode ?? tmdbEpisodes?.FirstOrDefault() ?? episode.AsTmdb();
        var tmdbMovie = (episode as IShokoEpisode)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault() ?? actualSeries.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();

        var images = (IWithImages)episode;
        var seriesImages = (IWithImages)series;

        bool isMovieOverride = episode.Type == EpisodeType.Episode && (tmdbMovie != null || actualSeries.Type == AnimeType.Movie);

        string episodeTitle = isMovieOverride
            ? TextHelper.ResolveMovieTitle(episode, actualSeries, tmdbMovie, seriesTitle: actualSeries.LocalID == series.LocalID ? context.Titles.DisplayTitle : null)
            : TextHelper.ResolveEpisodeTitle(
                episode,
                actualSeries.LocalID == series.LocalID ? context.Titles.DisplayTitle : (actualSeries.GetDisplayTitle() ?? context.Titles.DisplayTitle),
                resolvedTmdbEpisode
            );

        string episodeDescription =
            Settings.TmdbEpGroupNames && tmdbEpisode != null && TextHelper.GetDescriptionByLanguage(tmdbEpisode, Settings.DescriptionLanguage) is { Length: > 0 } groupOverview
                ? groupOverview
                : TextHelper.GetDescriptionByLanguage(episode, Settings.DescriptionLanguage);
        if (isMovieOverride && string.IsNullOrWhiteSpace(episodeDescription))
            episodeDescription = (tmdbMovie != null ? TextHelper.GetDescriptionByLanguage(tmdbMovie, Settings.DescriptionLanguage) : null) is { Length: > 0 } movieOverview
                ? movieOverview
                : TextHelper.GetDescriptionByLanguage(actualSeries, Settings.DescriptionLanguage);

        string? parentThumb =
            Settings.TmdbSeasonPosters && mapped.Season >= 0 && string.IsNullOrEmpty(context.PreferredOrderingId)
                ? FindTmdbSeason(series, mapped.Season)?.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage)
                : null;

        string? thumb = Settings.TmdbThumbnails
            ? (images.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage) ?? tmdbMovie?.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage))
            : null;

        var (rating, isAdult) =
            isMovieOverride && actualSeries.LocalID != series.LocalID
                ? ContentRatingHelper.GetContentRatingAndAdult(actualSeries, resolvedTmdbEpisode as IWithContentRatings ?? tmdbMovie)
                : (context.ContentRating, context.IsAdult);

        double? episodeTmdbRating =
            resolvedTmdbEpisode?.Rating > 0 ? resolvedTmdbEpisode.Rating
            : tmdbMovie?.Rating > 0 ? tmdbMovie.Rating
            : null;
        double? episodeAnidbRating =
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
            ["subtype"]               = (mapped.Season < 0 && TryGetExtraSeason(mapped.Season, out var extraSeason)) ? extraSeason.Subtype : null,
            ["title"]                 = episodeTitle,
            ["originallyAvailableAt"] = episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["thumb"]                 = thumb,
            //["art"]                 = No source for episode level background images
            ["contentRating"]         = rating,
            //["originalTitle"]       = No source for original episode titles
            ["titleSort"]             = episodeTitle,
            ["year"]                  = episode.AirDate?.Year,
            ["summary"]               = TextHelper.SanitizeSummaryWithFallback(episodeDescription, resolvedTmdbEpisode != null ? TextHelper.GetDescriptionByLanguage(resolvedTmdbEpisode, Settings.DescriptionLanguage) : null, Settings.SummaryMode),
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
            ["grandparentTitle"]      = context.Titles.DisplayTitle,
            ["grandparentThumb"]      = seriesImages.GetPreferredImageUrl(ImageEntityType.Primary, Settings.TmdbImageLanguage),
            ["grandparentArt"]        = seriesImages.GetPreferredImageUrl(ImageEntityType.Backdrop, Settings.TmdbImageLanguage),
            ["parentIndex"]           = mapped.Season,

            ["Image"]                 = Settings.TmdbThumbnails ? ImageHelper.BuildSnapshotArray(images, episodeTitle, Settings.AddEveryImage, Settings.TmdbImageLanguage) : [],
            ["Guid"]                  = BuildEpisodeXrefGuidArray(resolvedTmdbEpisode, tmdbMovie, tmdbEpisodes),
            //["OriginalImage"]       = Should be able to implement this but might make more sense to leave it to Shoko
            //["Role"]                = CastHelper.GetCastAndCrew(episode, metadataService), // Large array not used by Plex clients and present in grandparent
            ["Director"]              = CastHelper.GetDirectors(episode, metadataService),
            ["Producer"]              = CastHelper.GetProducers(episode, metadataService),
            ["Writer"]                = CastHelper.GetWriters(episode, metadataService),
            ["Rating"]                = BuildRatingArray(episodeTmdbRating, episodeAnidbRating)
        };
    }

    #endregion

    #region Episodes List

    /// <summary>Builds a sorted list of mapped episode metadata objects for a given season.</summary>
    /// <param name="context">Series context containing file data and title information.</param>
    /// <param name="seasonNumber">Season number whose episodes should be returned.</param>
    /// <returns>An ordered list of episode metadata objects for the season.</returns>
    public List<object> BuildEpisodeList(SeriesContext context, int seasonNumber)
    {
        var items = new List<(PlexCoords Coords, int? Part, object Meta)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? preferredOrderingId = context.PreferredOrderingId;
        bool enforceTmdb = EnforceTmdbNumbering;

        foreach (var mapping in context.FileData.GetForSeason(seasonNumber))
        {
            // TMDB Episode Groups: One Shoko episode maps to multiple TMDB entries (bypassed when custom VFS overrides dictate coordinates).
            if (
                enforceTmdb
                && mapping.Episodes.Count == 1
                && mapping.PrimaryEpisode is IShokoEpisode shokoEpisode
                && !OverrideHelper.HasCustomCoords(shokoEpisode, metadataService: metadataService)
                && shokoEpisode.GetLinkedEpisodes(MetadataSource.TMDB) is { Count: > 1 } tmdbEpisodeLinks
            )
            {
                var tmdbEpisodes = SelectPreferredTmdbOrdering(tmdbEpisodeLinks, preferredOrderingId);
                for (int episodeIndex = 0; episodeIndex < tmdbEpisodes.Count; episodeIndex++)
                {
                    var tmdbEpisode = tmdbEpisodes[episodeIndex];
                    var (season, episode) = GetOrderingCoords(tmdbEpisode, preferredOrderingId);
                    if (season != seasonNumber)
                        continue;
                    var tmdbCoords = new PlexCoords { Season = season ?? 0, Episode = episode };
                    int? effectivePart = mapping.PartIndex ?? (episodeIndex + 1); // Assign a virtual part index based on the TMDB segment order to ensure unique ratingKeys (e.g. e123p1, e123p2)
                    var metadata = MapEpisode(shokoEpisode, tmdbCoords, context, effectivePart, tmdbEpisode);
                    if (metadata is Dictionary<string, object?> metadataDict && metadataDict.TryGetValue("ratingKey", out var ratingKeyObj) && ratingKeyObj is string ratingKey && seenKeys.Add(ratingKey))
                        items.Add((tmdbCoords, effectivePart, metadata));
                }
                continue;
            }

            // Joined Episodes: Multiple Shoko episodes of the same type sharing a file.
            if (mapping.Episodes.Count > 1 && mapping.Episodes.Select(episode => episode.Type).Distinct().Count() == 1)
            {
                foreach (var episode in mapping.Episodes)
                {
                    var episodeCoords = GetPlexCoordinates(episode, preferredOrderingId, metadataService: metadataService);
                    episodeCoords.Season = mapping.Coords.Season;
                    if (episodeCoords.Season != seasonNumber)
                        continue;
                    var metadata = MapEpisode(episode, episodeCoords, context, mapping.PartIndex);
                    if (metadata is Dictionary<string, object?> metadataDict && metadataDict.TryGetValue("ratingKey", out var ratingKeyObj) && ratingKeyObj is string ratingKey && seenKeys.Add(ratingKey))
                        items.Add((episodeCoords, mapping.PartIndex, metadata));
                }
                continue;
            }

            // Standard or Relation-linked files: Use the PrimaryEpisode resolved by MapHelper, which filters out unwanted relations.
            var primaryMetadata = MapEpisode(mapping.PrimaryEpisode, mapping.Coords, context, mapping.PartIndex, mapping.TmdbEpisode);
            if (primaryMetadata is Dictionary<string, object?> primaryDict && primaryDict.TryGetValue("ratingKey", out var primaryRatingKey) && primaryRatingKey is string primaryKey && seenKeys.Add(primaryKey))
                items.Add((mapping.Coords, mapping.PartIndex, primaryMetadata));
        }
        return [.. items.OrderBy(item => item.Coords.Episode).ThenBy(item => item.Part ?? 0).Select(item => item.Meta)];
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
        tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).Select(tag => (object)new { tag }).ToArray() is { Length: > 0 } arrayValue ? arrayValue : null;

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB/IMDB) for a series or movie.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="tmdbSeries">The TMDB series metadata object.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata override.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildXrefGuidArray(ISeries series, ISeries? tmdbSeries = null, IMovie? tmdbMovie = null)
    {
        var resolvedMovie = tmdbMovie ?? (series as IShokoSeries)?.GetLinkedMovies(MetadataSource.TMDB)?.FirstOrDefault();
        return resolvedMovie != null ? CreateXrefGuids($"tmdb://{resolvedMovie.ID.ID}", resolvedMovie.GetCrossSourceID("imdb", MetadataEntityType.Movie) is { } imdbId ? $"imdb://{imdbId}" : null)
            : tmdbSeries != null ? CreateXrefGuids($"tmdb://{tmdbSeries.ID.ID}", tmdbSeries.GetCrossSourceID("tvdb", MetadataEntityType.Series) is { } seriesTvdbId ? $"tvdb://{seriesTvdbId}" : null)
            : [];
    }

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB) for a season.</summary>
    /// <param name="tmdbSeason">The TMDB season metadata object.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildSeasonXrefGuidArray(ISeason? tmdbSeason) => CreateXrefGuids(tmdbSeason?.ID.ID is { Length: > 0 } id ? $"tmdb://{id}" : null);

    /// <summary>Builds an array of external cross-reference GUIDs (TMDB/TVDB) for an episode.</summary>
    /// <param name="tmdbEpisode">The pre-resolved TMDB episode metadata object.</param>
    /// <param name="tmdbMovie">The optional TMDB movie metadata override.</param>
    /// <param name="tmdbEpisodes">The pre-resolved collection of TMDB episodes mapped to the Shoko episode.</param>
    /// <returns>An array of objects containing external IDs.</returns>
    private object[] BuildEpisodeXrefGuidArray(IEpisode? tmdbEpisode, IMovie? tmdbMovie, IReadOnlyList<IEpisode>? tmdbEpisodes) =>
        CreateXrefGuids([
            tmdbEpisode != null ? $"tmdb://{tmdbEpisode.ID.ID}" : (tmdbMovie != null ? $"tmdb://{tmdbMovie.ID.ID}" : null),
            tmdbEpisode?.GetCrossSourceID("tvdb", MetadataEntityType.Episode) is { } episodeTvdbId
                ? $"tvdb://{episodeTvdbId}"
                : (tmdbMovie?.GetCrossSourceID("tvdb", MetadataEntityType.Movie) is { } movieTvdbId ? $"tvdb://{movieTvdbId}" : null),
            .. tmdbEpisodes is { Count: > 0 } list
                ? list.SelectMany(episode => episode.GetCrossSourceID("tvdb", MetadataEntityType.Episode) is { } tvdbId ? [$"tmdb://{episode.ID.ID}", $"tvdb://{tvdbId}"] : new[] { $"tmdb://{episode.ID.ID}" })
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
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code =>
                    {
                        try
                        {
                            return new RegionInfo(code.Trim().ToUpperInvariant()).EnglishName;
                        }
                        catch
                        {
                            return code.Trim();
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
            ? list.Select(suggestion => suggestion.SuggestedID.TryGetNumericID(out int anidbId) ? metadataService.GetShokoSeriesByAnidbID(anidbId) : null)
                .OfType<IShokoSeries>()
                .Select(linkedSeries => metadataService.GetShokoSeriesByID(OverrideHelper.GetPrimary(linkedSeries.LocalID, metadataService)) ?? linkedSeries)
                .DistinctBy(primarySeries => primarySeries.LocalID)
                .Select(primarySeries => (object)new { guid = primarySeries.GetPlexGuid(), tag = primarySeries.PreferredTitle?.Value ?? primarySeries.DefaultTitle?.Value })
                .ToArray()
                is { Length: > 0 } arrayValue
                ? arrayValue
                : null
            : null;

    /// <summary>Extracts the broadcasting network information for a series.</summary>
    /// <param name="tmdbSeries">The TMDB series metadata object.</param>
    /// <returns>An array of objects containing network tags, or null if none found.</returns>
    private object[]? BuildNetworkArray(ISeries? tmdbSeries) => tmdbSeries is { } sourceSeries ? CreateTagArray(sourceSeries.Networks.Select(network => network.Name)) : null;

    /// <summary>Formats numeric ratings into a Plex-compatible audience rating array.</summary>
    /// <param name="tmdbRating">The TMDB rating value (0-10).</param>
    /// <param name="anidbRating">The AniDB rating value (0-10).</param>
    /// <returns>An array containing Plex rating objects, or null if none are valid.</returns>
    private static object[]? BuildRatingArray(double? tmdbRating, double? anidbRating)
    {
        var ratings = new List<object>();
        static object CreateRating(string image, double value) =>
            new
            {
                image,
                type = "audience",
                value = (float)value,
            };

        if (Settings.AnidbAsRottenTomatoes && anidbRating > 0)
            ratings.Add(CreateRating("rottentomatoes://image.rating.upright", anidbRating.Value));

        double? tmdbValue = tmdbRating > 0 ? tmdbRating : (!Settings.AnidbAsRottenTomatoes ? anidbRating : null);
        if (tmdbValue > 0)
            ratings.Add(CreateRating("themoviedb://image.rating", tmdbValue.Value));

        return ratings.Count > 0 ? [.. ratings] : null;
    }

    /// <summary>Finds the first linked TMDB season matching the specified season number across the series' override group.</summary>
    /// <param name="series">The primary Shoko series.</param>
    /// <param name="seasonNumber">The target season number.</param>
    /// <returns>The matching TMDB <see cref="ISeason"/>, or <c>null</c> if none exists.</returns>
    private ISeason? FindTmdbSeason(IShokoSeries series, int seasonNumber) =>
        OverrideHelper
            .GetGroup(series.LocalID, metadataService)
            .Select(metadataService.GetShokoSeriesByID)
            .OfType<IShokoSeries>()
            .SelectMany(groupSeries => groupSeries.GetLinkedSeasons(MetadataSource.TMDB) ?? [])
            .FirstOrDefault(tmdbSeason => tmdbSeason.SeasonNumber == seasonNumber);

    #endregion
}
