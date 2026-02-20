using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoRelay.Plex
{
    public static class PlexApi
    {
        public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static async Task<PlexMediaContainer?> ReadContainerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var wrapper = await JsonSerializer.DeserializeAsync<PlexMediaContainerWrapper>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return wrapper?.MediaContainer;
        }
    }

    public class PlexMediaContainerWrapper
    {
        [JsonPropertyName("MediaContainer")]
        public PlexMediaContainer? MediaContainer { get; set; }
    }

    public class PlexMediaContainer
    {
        [JsonPropertyName("Metadata")]
        public List<PlexMetadataItem>? Metadata { get; set; }

        [JsonPropertyName("size")]
        public int? Size { get; set; }

        [JsonPropertyName("totalSize")]
        public int? TotalSize { get; set; }
    }

    public class PlexMetadataItem
    {
        [JsonPropertyName("ratingKey")]
        public string? RatingKey { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("librarySectionID")]
        public int? LibrarySectionId { get; set; }

        [JsonPropertyName("guid")]
        public string? Guid { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("viewCount")]
        public int? ViewCount { get; set; }

        [JsonPropertyName("lastViewedAt")]
        public long? LastViewedAt { get; set; }

        // Per-user information (when requesting with a user token the response may include this)
        [JsonPropertyName("User")]
        public PlexMetadataUser? User { get; set; }

        // Per-user numeric rating (when available for the requesting token)
        [JsonPropertyName("userRating")]
        public double? UserRating { get; set; }
    }

    public class PlexMetadataUser
    {
        [JsonPropertyName("lastViewedAt")]
        public long? LastViewedAt { get; set; }
    }
}
