using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoRelay.Plex
{
    /// <summary>
    /// Helper routines and data models for interacting with the Plex server API. Contains JSON serialization settings and container parsing logic.
    /// </summary>
    public static class PlexApi
    {
        /// <summary>
        /// Shared <see cref="JsonSerializerOptions"/> used for deserializing Plex responses. Ignores null values and treats property names case‑insensitively.
        /// </summary>
        public static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        /// <summary>
        /// Read and deserialize a <see cref="PlexMediaContainer"/> from an HTTP response produced by a Plex server. Handles the outer wrapper object used by Plex.
        /// </summary>
        /// <param name="response">HTTP response to decode.</param>
        /// <param name="cancellationToken">Cancellation token for the async I/O operations.</param>
        /// <returns>The parsed <see cref="PlexMediaContainer"/>, or <c>null</c> if not present.</returns>
        public static async Task<PlexMediaContainer?> ReadContainerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var wrapper = await JsonSerializer.DeserializeAsync<PlexMediaContainerWrapper>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return wrapper?.MediaContainer;
        }
    }

    /// <summary>
    /// Internal wrapper type matching the Plex JSON structure where the media container is nested under a top-level <c>MediaContainer</c> property.
    /// </summary>
    public class PlexMediaContainerWrapper
    {
        [JsonPropertyName("MediaContainer")]
        public PlexMediaContainer? MediaContainer { get; set; }
    }

    /// <summary>
    /// Represents the Plex <c>MediaContainer</c> element returned by various API endpoints; contains a list of metadata items and pagination information.
    /// </summary>
    public class PlexMediaContainer
    {
        [JsonPropertyName("Metadata")]
        public List<PlexMetadataItem>? Metadata { get; set; }

        [JsonPropertyName("size")]
        public int? Size { get; set; }

        [JsonPropertyName("totalSize")]
        public int? TotalSize { get; set; }
    }

    /// <summary>
    /// Data model for individual items contained in a Plex media container. Many properties are optional depending on the endpoint and query parameters used.
    /// </summary>
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

        [JsonPropertyName("childCount")]
        public int? ChildCount { get; set; }

        [JsonPropertyName("User")]
        public PlexMetadataUser? User { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("userRating")]
        public double? UserRating { get; set; }

        [JsonPropertyName("Collection")]
        public List<PlexTag>? Collection { get; set; }
    }

    /// <summary>
    /// Represents a tag entry (e.g. collection, genre, label) within a <see cref="PlexMetadataItem"/>.
    /// </summary>
    public class PlexTag
    {
        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }

    /// <summary>
    /// Embedded user-specific information within a <see cref="PlexMetadataItem"/>, e.g. per-user last viewed timestamp.
    /// </summary>
    public class PlexMetadataUser
    {
        [JsonPropertyName("lastViewedAt")]
        public long? LastViewedAt { get; set; }
    }
}
