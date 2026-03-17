using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace ShokoRelay.AnimeThemes;

/// <summary>HTTP client for AnimeThemes API interactions with rate limiting and JSON deserialization.</summary>
public class AnimeThemesApi
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(0.7);

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <summary>Constructs a new AnimeThemesApi with an optional external HttpClient.</summary>
    /// <param name="httpClient">Optional HttpClient to use. If null, a new one is created.</param>
    public AnimeThemesApi(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        AnimeThemesHelper.EnsureUserAgent(_http);
    }

    #endregion

    #region API Methods

    /// <summary>Fetch video metadata with included audio, theme info, anime data, and song artists in a single optimized call.</summary>
    /// <param name="videoBaseName">The base name of the video file to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="VideoWithAudioResponse"/> containing metadata, or null if not found.</returns>
    public async Task<VideoWithAudioResponse?> FetchVideoWithArtistsAsync(string videoBaseName, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/video/{Uri.EscapeDataString(videoBaseName)}?include=animethemeentries.animetheme.anime,animethemeentries.animetheme.song.artists";
        return await GetJsonAsync<VideoWithAudioResponse>(url, ct);
    }

    /// <summary>Fetch anime with AniDB resources to extract the AniDB ID for a given video ID.</summary>
    /// <param name="videoId">The internal AnimeThemes video ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="AnimeResourceResponse"/> containing resource links, or null if not found.</returns>
    public async Task<AnimeResourceResponse?> FetchAnimeResourcesAsync(int videoId, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/anime?filter[has]=animethemes.animethemeentries.videos,animethemes&include=resources&filter[resource][site]=AniDB&filter[video][id]={videoId}";
        return await GetJsonAsync<AnimeResourceResponse>(url, ct);
    }

    /// <summary>Fetch anime and available themes for mp3 generation with a given AniDB ID and optional slug filter.</summary>
    /// <param name="anidbId">The AniDB ID of the series.</param>
    /// <param name="slugFilter">Optional URL filter for specific slugs (e.g. OP/ED).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="AnimeThemesResponse"/> containing theme metadata, or null if not found.</returns>
    public async Task<AnimeThemesResponse?> FetchAnimeThemesAsync(int anidbId, string? slugFilter, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/anime?filter[has]=resources&filter[site]=AniDB&filter[external_id]={anidbId}&include=animethemes{slugFilter ?? ""}";
        return await GetJsonAsync<AnimeThemesResponse>(url, ct);
    }

    /// <summary>Fetch animetheme details for mp3 generation, including artists.</summary>
    /// <param name="themeId">The internal AnimeThemes theme ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ThemeWithAudioResponse"/> containing audio and artist info, or null if not found.</returns>
    public async Task<ThemeWithAudioResponse?> FetchAnimeThemeWithArtistsAsync(int themeId, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/animetheme/{themeId}?include=animethemeentries.videos.audio,song.artists";
        return await GetJsonAsync<ThemeWithAudioResponse>(url, ct);
    }

    #endregion

    #region Internal Logic

    /// <summary>Generic JSON deserialization with automatic rate limiting and error handling.</summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="url">The target API URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized object of type T, or default on error.</returns>
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        await RateLimitAsync(ct);
        AnimeThemesHelper.EnsureUserAgent(_http);

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                Logger.Warn("AnimeThemes API returned Forbidden for {Url} (check user-agent/config)", url);
            else
                Logger.Warn("AnimeThemes API returned {Status} for {Url}", response.StatusCode, url);
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, ct);
    }

    /// <summary>Enforces the API rate limit by delaying requests if they occur too rapidly.</summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = _lastRequest + RateLimitDelay - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    #endregion

    #region Data Models

    /// <summary>Response wrapper for a video metadata request.</summary>
    /// <param name="Video">The video metadata entry.</param>
    public sealed record VideoWithAudioResponse(VideoWithAudioEntry? Video);

    /// <summary>Entry representing a video file and its associated technical attributes and audio links.</summary>
    /// <param name="Id">The unique video identifier.</param>
    /// <param name="NC">No Credits flag.</param>
    /// <param name="Subbed">Subtitles flag.</param>
    /// <param name="Lyrics">Lyrics flag.</param>
    /// <param name="Uncen">Uncensored status.</param>
    /// <param name="Resolution">Vertical video resolution.</param>
    /// <param name="Source">Media source (e.g., BD, TV).</param>
    /// <param name="Overlap">Overlap type status.</param>
    /// <param name="Animethemeentries">Associated theme entries.</param>
    /// <param name="Audio">Associated audio link.</param>
    public sealed record VideoWithAudioEntry(
        int Id,
        bool NC,
        bool Subbed,
        bool Lyrics,
        bool Uncen,
        int? Resolution,
        string? Source,
        string? Overlap,
        List<VideoThemeEntryWrapper>? Animethemeentries,
        AudioLink? Audio
    );

    /// <summary>Wrapper for a theme entry linked to a specific video version.</summary>
    /// <param name="Version">Version number of the theme entry.</param>
    /// <param name="Episodes">Episode range string.</param>
    /// <param name="NSFW">Not Safe For Work status.</param>
    /// <param name="Spoiler">Spoiler status.</param>
    /// <param name="Animetheme">Associated full theme details.</param>
    public sealed record VideoThemeEntryWrapper(int Version, string? Episodes, bool NSFW, bool Spoiler, VideoThemeFull? Animetheme);

    /// <summary>Detailed theme metadata including song information and series context.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    /// <param name="Song">Song metadata details.</param>
    /// <param name="Anime">Associated anime details.</param>
    public sealed record VideoThemeFull(int Id, string? Slug, SongInfo? Song, VideoAnimeOnly? Anime);

    /// <summary>Minimal anime metadata associated with a theme.</summary>
    /// <param name="Id">Anime identifier.</param>
    /// <param name="Name">Anime title.</param>
    /// <param name="Season">Airing season.</param>
    /// <param name="Year">Airing year.</param>
    public sealed record VideoAnimeOnly(int Id, string? Name, string? Season, int? Year);

    /// <summary>Metadata for a song, including title and participating artists.</summary>
    /// <param name="Title">Song title.</param>
    /// <param name="Artists">List of performing artists.</param>
    public sealed record SongInfo(string? Title, List<ArtistInfo>? Artists);

    /// <summary>Information about a performing artist.</summary>
    /// <param name="Name">Artist name.</param>
    public sealed record ArtistInfo(string? Name);

    /// <summary>Link to an audio resource.</summary>
    /// <param name="Link">The direct audio resource URL.</param>
    public sealed record AudioLink(string? Link);

    /// <summary>Response wrapper for a theme metadata request.</summary>
    /// <param name="Animetheme">The theme metadata entry.</param>
    public sealed record ThemeWithAudioResponse(ThemeWithAudioWrapper? Animetheme);

    /// <summary>Wrapper for theme metadata including song info and video entries.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    /// <param name="Song">Song metadata details.</param>
    /// <param name="Animethemeentries">Theme entry versions and associations.</param>
    public sealed record ThemeWithAudioWrapper(int Id, string? Slug, SongInfo? Song, List<ThemeAudioEntry>? Animethemeentries);

    /// <summary>Entry representing a specific version or episode range for a theme.</summary>
    /// <param name="Version">Version number of the entry.</param>
    /// <param name="Episodes">Episode range string.</param>
    /// <param name="NSFW">Not Safe For Work status.</param>
    /// <param name="Spoiler">Spoiler status.</param>
    /// <param name="Videos">Associated video entries.</param>
    public sealed record ThemeAudioEntry(int Version, string? Episodes, bool NSFW, bool Spoiler, List<ThemeVideoEntry>? Videos);

    /// <summary>Entry representing a video and its audio link within a theme.</summary>
    /// <param name="Id">Video identifier.</param>
    /// <param name="Audio">Associated audio link.</param>
    public sealed record ThemeVideoEntry(int Id, AudioLink? Audio);

    /// <summary>Response wrapper for an anime theme list request.</summary>
    /// <param name="Anime">List of anime entries.</param>
    public sealed record AnimeThemesResponse(List<AnimeThemesEntry>? Anime);

    /// <summary>Entry representing an anime and its available themes.</summary>
    /// <param name="Name">Anime title.</param>
    /// <param name="Slug">Anime slug identifier.</param>
    /// <param name="Animethemes">List of themes associated with the anime.</param>
    public sealed record AnimeThemesEntry(string? Name, string? Slug, List<AnimeTheme>? Animethemes);

    /// <summary>Minimal theme information containing identifiers.</summary>
    /// <param name="Id">Theme identifier.</param>
    /// <param name="Slug">Theme type slug.</param>
    public sealed record AnimeTheme(int Id, string? Slug);

    /// <summary>Response wrapper for an anime resource request.</summary>
    /// <param name="Anime">List of anime entries with resource mappings.</param>
    public sealed record AnimeResourceResponse(List<AnimeResourceEntry>? Anime);

    /// <summary>Entry containing resource links and seasonal metadata for an anime.</summary>
    /// <param name="Resources">External resource items.</param>
    /// <param name="Season">Airing season.</param>
    /// <param name="Year">Airing year.</param>
    public sealed record AnimeResourceEntry(List<ResourceItem>? Resources, string? Season, int? Year);

    /// <summary>Represents an external resource link.</summary>
    /// <param name="ExternalId">The external site identifier.</param>
    /// <param name="Site">The name of the site.</param>
    public sealed record ResourceItem([property: JsonPropertyName("external_id")] int? ExternalId, string Site);

    #endregion
}
