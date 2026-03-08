using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// HTTP client for AnimeThemes API interactions with rate limiting and JSON deserialization.
/// </summary>
public class AnimeThemesApi
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(0.7); // ~86 req/min to stay under the 90 that is enforced by AnimeThemes

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <summary>
    /// Constructs a new AnimeThemesApi with an optional external HttpClient.
    /// </summary>
    /// <param name="httpClient">Optional HttpClient to use. If null, a new one is created.</param>
    public AnimeThemesApi(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        AnimeThemesHelper.EnsureUserAgent(_http);
    }

    /// <summary>
    /// Fetch video metadata with included audio, theme info, anime data, and song artists in a single optimized call.
    /// </summary>
    public async Task<VideoWithAudioResponse?> FetchVideoWithArtistsAsync(string videoBaseName, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/video/{Uri.EscapeDataString(videoBaseName)}?include=animethemeentries.animetheme.anime,animethemeentries.animetheme.song.artists";
        return await GetJsonAsync<VideoWithAudioResponse>(url, ct);
    }

    /// <summary>
    /// Fetch anime with AniDB resources to extract the AniDB ID for a given video ID.
    /// </summary>
    public async Task<AnimeResourceResponse?> FetchAnimeResourcesAsync(int videoId, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/anime?filter[has]=animethemes.animethemeentries.videos,animethemes&include=resources&filter[resource][site]=AniDB&filter[video][id]={videoId}";
        return await GetJsonAsync<AnimeResourceResponse>(url, ct);
    }

    /// <summary>
    /// Fetch anime and available themes for mp3 generation with a given AniDB ID and optional slug filter.
    /// </summary>
    public async Task<AnimeThemesResponse?> FetchAnimeThemesAsync(int anidbId, string? slugFilter, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/anime?filter[has]=resources&filter[site]=AniDB&filter[external_id]={anidbId}&include=animethemes{slugFilter ?? ""}";
        return await GetJsonAsync<AnimeThemesResponse>(url, ct);
    }

    /// <summary>
    /// Fetch animetheme details for mp3 generation, including artists
    /// </summary>
    public async Task<ThemeWithAudioResponse?> FetchAnimeThemeWithArtistsAsync(int themeId, CancellationToken ct)
    {
        string url = $"{AnimeThemesHelper.AtApiBase}/animetheme/{themeId}?include=animethemeentries.videos.audio,song.artists";
        return await GetJsonAsync<ThemeWithAudioResponse>(url, ct);
    }

    /// <summary>
    /// Generic JSON deserialization with automatic rate limiting and error handling.
    /// </summary>
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

    /// <summary>Video with audio, theme entries, anime, song, and artists included in single call.</summary>
    public sealed record VideoWithAudioResponse(VideoWithAudioEntry? Video);

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

    public sealed record VideoThemeEntryWrapper(int Version, string? Episodes, bool NSFW, bool Spoiler, VideoThemeFull? Animetheme);

    public sealed record VideoThemeFull(int Id, string? Slug, SongInfo? Song, VideoAnimeOnly? Anime);

    public sealed record VideoAnimeOnly(int Id, string? Name, string? Season, int? Year);

    public sealed record SongInfo(string? Title, List<ArtistInfo>? Artists);

    public sealed record ArtistInfo(string? Name);

    public sealed record AudioLink(string? Link);

    /// <summary>Theme with videos, audio, song, and artists included in single call.</summary>
    public sealed record ThemeWithAudioResponse(ThemeWithAudioWrapper? Animetheme);

    public sealed record ThemeWithAudioWrapper(int Id, string? Slug, SongInfo? Song, List<ThemeAudioEntry>? Animethemeentries);

    public sealed record ThemeAudioEntry(int Version, string? Episodes, bool NSFW, bool Spoiler, List<ThemeVideoEntry>? Videos);

    public sealed record ThemeVideoEntry(int Id, AudioLink? Audio);

    /// <summary>Anime with theme list and resources.</summary>
    public sealed record AnimeThemesResponse(List<AnimeThemesEntry>? Anime);

    public sealed record AnimeThemesEntry(string? Name, string? Slug, List<AnimeTheme>? Animethemes);

    public sealed record AnimeTheme(int Id, string? Slug);

    /// <summary>Anime with resource list for AniDB ID lookup.</summary>
    public sealed record AnimeResourceResponse(List<AnimeResourceEntry>? Anime);

    public sealed record AnimeResourceEntry(List<ResourceItem>? Resources, string? Season, int? Year);

    public sealed record ResourceItem([property: JsonPropertyName("external_id")] int? ExternalId, string Site);
}
