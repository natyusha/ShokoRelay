using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoRelay.AnimeThemes;

/// <summary>HTTP client for AnimeThemes API interactions with rate limiting and JSON deserialization.</summary>
/// <param name="httpClient">HTTP client.</param>
/// <param name="logger">Logger instance.</param>
public class AnimeThemesApi(HttpClient? httpClient = null, ILogger<AnimeThemesApi>? logger = null)
{
    #region Setup & State

    private static readonly TimeSpan s_minRequestInterval = TimeSpan.FromSeconds(0.7);

    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private DateTimeOffset _rateLimitReset = DateTimeOffset.MinValue;
    private int _rateLimitRemaining = 90;

    #endregion

    #region API Methods

    /// <summary>Fetch video metadata with included audio, theme info, anime data, and song artists in a single optimized call.</summary>
    /// <param name="videoBaseName">The base name of the video file to query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="VideoWithAudioEntry"/> containing metadata, or null if not found.</returns>
    public Task<VideoWithAudioEntry?> FetchVideoWithArtistsAsync(string videoBaseName, CancellationToken cancellationToken)
    {
        string query =
            @"query($basename: Mixed) {
          videoPagination(where: { column: BASENAME, operator: EQ, value: $basename }) {
            data {
              id basename nc subbed lyrics uncen resolution source overlap audio { link }
              animethemeentries {
                nodes {
                  version episodes nsfw spoiler
                  animetheme {
                    id slug
                    anime { id title { romaji } season year resources { nodes { externalId site } } }
                    song { title { romaji } performances { artist { name { main } } } }
                  }
                }
              }
            }
          }
        }";
        return PostGraphQlAsync<VideoPaginationResponse>(query, new { basename = videoBaseName }, cancellationToken).ContinueWith(t => t.Result?.VideoPagination?.Data?.FirstOrDefault(), cancellationToken);
    }

    /// <summary>Fetch anime and available themes for mp3 generation with a given AniDB ID.</summary>
    /// <param name="anidbId">The AniDB ID of the series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of <see cref="AnimeThemesEntry"/> containing theme metadata, or null if not found.</returns>
    public Task<List<AnimeThemesEntry>?> FetchAnimeThemesAsync(int anidbId, CancellationToken cancellationToken)
    {
        string query =
            @"query($anidbId: [Int!]) {
          findAnimeByExternalSite(site: ANIDB, id: $anidbId) {
            title { romaji } slug
            animethemes { id slug }
          }
        }";
        return PostGraphQlAsync<FindAnimeResponse>(query, new { anidbId = new[] { anidbId } }, cancellationToken).ContinueWith(t => t.Result?.Anime, cancellationToken);
    }

    /// <summary>Fetch animetheme details for mp3 generation, including artists.</summary>
    /// <param name="themeId">The internal AnimeThemes theme ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ThemeWithAudioWrapper"/> containing audio and artist info, or null if not found.</returns>
    public Task<ThemeWithAudioWrapper?> FetchAnimeThemeWithArtistsAsync(int themeId, CancellationToken cancellationToken)
    {
        string query =
            @"query($themeId: Mixed) {
          animethemePagination(where: { column: ID, operator: EQ, value: $themeId }) {
            data {
              id slug
              song { title { romaji } performances { artist { name { main } } } }
              animethemeentries {
                version episodes nsfw spoiler
                videos {
                  nodes { id basename link audio { link } }
                }
              }
            }
          }
        }";
        return PostGraphQlAsync<AnimeThemePaginationResponse>(query, new { themeId = themeId }, cancellationToken).ContinueWith(t => t.Result?.AnimethemePagination?.Data?.FirstOrDefault(), cancellationToken);
    }

    /// <summary>Fetch a paginated list of anime matching specific filters for bulk WebM downloads.</summary>
    /// <param name="year">Optional broadcast year filter.</param>
    /// <param name="season">Optional broadcast season filter.</param>
    /// <param name="name">Optional anime name filter.</param>
    /// <param name="page">The current pagination page.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="AnimePagination"/> containing a page of anime results.</returns>
    public Task<AnimePagination?> FetchAnimePageAsync(int? year, string? season, string? name, int page, CancellationToken cancellationToken)
    {
        var filters = new List<string>();
        if (year.HasValue)
            filters.Add($"{{ column: YEAR, operator: EQ, value: {year.Value} }}");
        if (!string.IsNullOrWhiteSpace(season))
            filters.Add($"{{ column: SEASON, operator: EQ, value: {season.ToUpperInvariant()} }}");
        if (!string.IsNullOrWhiteSpace(name))
            filters.Add($"{{ column: TITLE_ROMAJI, operator: LIKE, value: \"%{name.Replace("\"", "\\\"")}%\" }}");

        string whereClause = filters.Count > 0 ? $"where: {{ AND: [{string.Join(", ", filters)}] }}" : "";
        string query =
            $@"query($page: Int) {{
          animePagination(page: $page, first: 15, {whereClause}) {{
            paginatorInfo {{ hasMorePages }}
            data {{
              id title {{ romaji }} season year
              animethemes {{ id slug }}
            }}
          }}
        }}";

        return PostGraphQlAsync<AnimePaginationResponse>(query, new { page }, cancellationToken).ContinueWith(t => t.Result?.AnimePagination, cancellationToken);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Generic GraphQL request execution with automatic rate limiting and error handling.</summary>
    /// <typeparam name="T">The expected strong-typed data structure.</typeparam>
    /// <param name="query">The GraphQL query string.</param>
    /// <param name="variables">The variables object to supply to the query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized object of type T, or default on error.</returns>
    private async Task<T?> PostGraphQlAsync<T>(string query, object variables, CancellationToken cancellationToken)
        where T : class
    {
        await RateLimitAsync(cancellationToken).ConfigureAwait(false);

        string json = JsonSerializer.Serialize(new { query, variables }, _jsonOptions);

        for (int i = 0; i < 2; i++)
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(AnimeThemesHelper.AtApiBase, content, cancellationToken).ConfigureAwait(false);

            UpdateRateLimits(response);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (_rateLimitReset > DateTimeOffset.UtcNow ? _rateLimitReset - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60));
                logger?.LogWarning("AnimeThemes: Rate limit exceeded (429). Retrying after {Seconds:F1}s", wait.TotalSeconds);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("AnimeThemes: API returned {StatusCode}", response.StatusCode);
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<GraphQlResponse<T>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);

            if (result?.Errors != null && result.Errors.Count > 0)
                logger?.LogWarning("AnimeThemes: GraphQL returned errors: {Errors}", string.Join(", ", result.Errors.Select(e => e.Message)));

            return result?.Data;
        }

        return default;
    }

    /// <summary>Inspects rate limit headers to dynamically update remaining quota and reset timestamps.</summary>
    /// <param name="response">The HTTP response containing rate limit headers.</param>
    private void UpdateRateLimits(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remVals) && int.TryParse(remVals.FirstOrDefault(), out int remaining))
            _rateLimitRemaining = remaining;

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) && long.TryParse(resetVals.FirstOrDefault(), out long resetMs))
            _rateLimitReset = DateTimeOffset.FromUnixTimeMilliseconds(resetMs);
    }

    /// <summary>Enforces proactive pacing and halts requests if the remaining quota has been exhausted.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task RateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;

            // Preemptively wait if the server quota is completely exhausted
            if (_rateLimitRemaining <= 0 && _rateLimitReset > now)
            {
                var quotaWait = _rateLimitReset - now;
                logger?.LogDebug("AnimeThemes: Quota exhausted. Waiting {Seconds:F1}s for rate limit reset window", quotaWait.TotalSeconds);
                await Task.Delay(quotaWait, cancellationToken).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
                _rateLimitRemaining = 90;
            }

            var intervalWait = _lastRequest + s_minRequestInterval - now;
            if (intervalWait > TimeSpan.Zero)
                await Task.Delay(intervalWait, cancellationToken).ConfigureAwait(false);

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    #endregion

    #region Data Models

    /// <summary>Standard GraphQL response envelope.</summary>
    /// <typeparam name="T">The type of the data payload.</typeparam>
    /// <param name="Data">Deserialized payload data.</param>
    /// <param name="Errors">List of GraphQL execution or validation errors.</param>
    public sealed record GraphQlResponse<T>(T? Data, List<GraphQlError>? Errors)
        where T : class;

    /// <summary>GraphQL error object.</summary>
    /// <param name="Message">Error description text.</param>
    public sealed record GraphQlError(string? Message);

    /// <summary>Response for video pagination queries.</summary>
    /// <param name="VideoPagination">The paginated video results container.</param>
    public sealed record VideoPaginationResponse(VideoPagination? VideoPagination);

    /// <summary>Container for paginated video entries.</summary>
    /// <param name="Data">Collection of video entries.</param>
    public sealed record VideoPagination(List<VideoWithAudioEntry>? Data);

    /// <summary>Response for anime filtering by external site queries.</summary>
    /// <param name="Anime">Matching anime entities.</param>
    public sealed record FindAnimeResponse([property: JsonPropertyName("findAnimeByExternalSite")] List<AnimeThemesEntry>? Anime);

    /// <summary>Response for anime theme pagination queries.</summary>
    /// <param name="AnimethemePagination">The paginated anime theme results container.</param>
    public sealed record AnimeThemePaginationResponse(AnimeThemePagination? AnimethemePagination);

    /// <summary>Container for paginated anime theme entries.</summary>
    /// <param name="Data">Collection of anime theme metadata wrappers.</param>
    public sealed record AnimeThemePagination(List<ThemeWithAudioWrapper>? Data);

    /// <summary>Response for paginated anime queries.</summary>
    /// <param name="AnimePagination">Paginated anime response container.</param>
    public sealed record AnimePaginationResponse(AnimePagination? AnimePagination);

    /// <summary>Multilingual title representation containing romanized text.</summary>
    /// <param name="Romaji">Romanized title string.</param>
    public sealed record TitleInfo(string? Romaji);

    /// <summary>Name representation containing primary display text.</summary>
    /// <param name="Main">Primary artist or creator name.</param>
    public sealed record NameInfo(string? Main);

    /// <summary>Represents an external database resource link.</summary>
    /// <param name="ExternalId">Numeric identifier on the external site.</param>
    /// <param name="Site">Identifier of the external site.</param>
    public sealed record ResourceItem([property: JsonPropertyName("externalId")] int? ExternalId, string? Site);

    /// <summary>Connection container for external resource references.</summary>
    /// <param name="Nodes">List of linked resource nodes.</param>
    public sealed record ResourceConnection(List<ResourceItem>? Nodes);

    /// <summary>Information about a performing artist or creator.</summary>
    /// <param name="Name">Name details of the artist.</param>
    public sealed record ArtistInfo(NameInfo? Name);

    /// <summary>Performance link associating an artist with a musical composition.</summary>
    /// <param name="Artist">The performing artist details.</param>
    public sealed record PerformanceInfo(ArtistInfo? Artist);

    /// <summary>Metadata for a musical song, including title and participating performances.</summary>
    /// <param name="Title">The song composition title.</param>
    /// <param name="Performances">List of credited artist performances.</param>
    public sealed record SongInfo(TitleInfo? Title, List<PerformanceInfo>? Performances);

    /// <summary>Link to an audio or video resource.</summary>
    /// <param name="Link">The direct resource URL.</param>
    public sealed record AudioLink(string? Link);

    /// <summary>Minimal anime metadata associated with a video theme entry.</summary>
    /// <param name="Id">Anime identifier.</param>
    /// <param name="Title">Title of the anime.</param>
    /// <param name="Season">Airing season name.</param>
    /// <param name="Year">Airing broadcast year.</param>
    /// <param name="Resources">Linked external database resources.</param>
    public sealed record VideoAnimeOnly(int Id, TitleInfo? Title, string? Season, int? Year, ResourceConnection? Resources);

    /// <summary>Detailed theme metadata including song information and series context.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    /// <param name="Song">Song metadata details.</param>
    /// <param name="Anime">Associated anime details.</param>
    public sealed record VideoThemeFull(int Id, string? Slug, SongInfo? Song, VideoAnimeOnly? Anime);

    /// <summary>Wrapper for a theme entry linked to a specific video version.</summary>
    /// <param name="Version">Version number of the theme entry.</param>
    /// <param name="Episodes">Episode range string.</param>
    /// <param name="Nsfw">Not Safe For Work status flag.</param>
    /// <param name="Spoiler">Spoiler status flag.</param>
    /// <param name="Animetheme">Associated theme metadata.</param>
    public sealed record VideoThemeEntryWrapper(int Version, string? Episodes, bool Nsfw, bool Spoiler, VideoThemeFull? Animetheme);

    /// <summary>Relay connection container for theme entries linked to a video.</summary>
    /// <param name="Nodes">List of theme entry nodes.</param>
    public sealed record VideoThemeEntryConnection(List<VideoThemeEntryWrapper>? Nodes);

    /// <summary>Entry representing a video file and its associated technical attributes and audio links.</summary>
    /// <param name="Id">The unique video identifier.</param>
    /// <param name="Basename">Physical basename of the video file.</param>
    /// <param name="Nc">No Credits status flag.</param>
    /// <param name="Subbed">Subtitles present flag.</param>
    /// <param name="Lyrics">Timed lyrics present flag.</param>
    /// <param name="Uncen">Uncensored status flag.</param>
    /// <param name="Resolution">Vertical video resolution in pixels.</param>
    /// <param name="Source">Media source type (e.g., BD, TV, Web).</param>
    /// <param name="Overlap">Overlap type status.</param>
    /// <param name="Animethemeentries">Connection to associated theme entries.</param>
    /// <param name="Audio">Associated audio stream details.</param>
    public sealed record VideoWithAudioEntry(
        int Id,
        string? Basename,
        bool Nc,
        bool Subbed,
        bool Lyrics,
        bool Uncen,
        int? Resolution,
        string? Source,
        string? Overlap,
        VideoThemeEntryConnection? Animethemeentries,
        AudioLink? Audio
    );

    /// <summary>Minimal theme information containing identifiers.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    public sealed record AnimeTheme(int Id, string? Slug);

    /// <summary>Entry representing an anime and its available themes.</summary>
    /// <param name="Title">Romanized anime title.</param>
    /// <param name="Slug">Anime URL slug identifier.</param>
    /// <param name="Animethemes">List of themes associated with the anime.</param>
    public sealed record AnimeThemesEntry(TitleInfo? Title, string? Slug, List<AnimeTheme>? Animethemes);

    /// <summary>Entry representing a video and its audio link within a theme.</summary>
    /// <param name="Id">Video identifier.</param>
    /// <param name="Basename">The original physical filename of the video.</param>
    /// <param name="Link">The direct video resource URL.</param>
    /// <param name="Audio">Associated audio stream link.</param>
    public sealed record ThemeVideoEntry(int Id, string? Basename, string? Link, AudioLink? Audio);

    /// <summary>Relay connection container for videos associated with a theme entry.</summary>
    /// <param name="Nodes">List of video nodes.</param>
    public sealed record ThemeVideoConnection(List<ThemeVideoEntry>? Nodes);

    /// <summary>Entry representing a specific version or episode range for a theme.</summary>
    /// <param name="Version">Version number of the entry.</param>
    /// <param name="Episodes">Episode range string.</param>
    /// <param name="Nsfw">Not Safe For Work status flag.</param>
    /// <param name="Spoiler">Spoiler status flag.</param>
    /// <param name="Videos">Connection to associated video entries.</param>
    public sealed record ThemeAudioEntry(int Version, string? Episodes, bool Nsfw, bool Spoiler, ThemeVideoConnection? Videos);

    /// <summary>Wrapper for theme metadata including song info and video entries.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    /// <param name="Song">Song composition details.</param>
    /// <param name="Animethemeentries">List of theme entry versions and associations.</param>
    public sealed record ThemeWithAudioWrapper(int Id, string? Slug, SongInfo? Song, List<ThemeAudioEntry>? Animethemeentries);

    /// <summary>Anime entity containing themes for pagination listings.</summary>
    /// <param name="Id">Anime identifier.</param>
    /// <param name="Title">Title of the anime.</param>
    /// <param name="Season">Airing season.</param>
    /// <param name="Year">Airing broadcast year.</param>
    /// <param name="Animethemes">Associated themes.</param>
    public sealed record AnimeThemeAnime(int Id, TitleInfo? Title, string? Season, int? Year, List<AnimeTheme>? Animethemes);

    /// <summary>Pagination information for GraphQL queries.</summary>
    /// <param name="HasMorePages">True if additional result pages exist.</param>
    public sealed record PaginatorInfo(bool HasMorePages);

    /// <summary>Container for paginated anime list responses.</summary>
    /// <param name="Data">Page of anime entries.</param>
    /// <param name="PaginatorInfo">Pagination metadata.</param>
    public sealed record AnimePagination(List<AnimeThemeAnime>? Data, PaginatorInfo? PaginatorInfo);

    #endregion
}
