using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
    }
}
