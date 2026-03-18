using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoRelay.Plex;

/// <summary>Helper routines and data models for interacting with the Plex server API.</summary>
public static class PlexApi
{
    #region JSON Configuration

    /// <summary>Shared JsonSerializerOptions used for deserializing Plex responses.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    #endregion

    #region Deserialization Logic

    /// <summary>Read and deserialize a PlexMediaContainer from an HTTP response.</summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed container, or null if deserialization fails.</returns>
    public static async Task<PlexMediaContainer?> ReadContainerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var wrapper = await JsonSerializer.DeserializeAsync<PlexMediaContainerWrapper>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return wrapper?.MediaContainer;
    }

    #endregion
}

#region Data Models

/// <summary>Internal wrapper type matching the Plex JSON structure for media containers.</summary>
public class PlexMediaContainerWrapper
{
    /// <summary>The nested media container.</summary>
    [JsonPropertyName("MediaContainer")]
    public PlexMediaContainer? MediaContainer { get; set; }
}

/// <summary>Represents the Plex MediaContainer element returned by API endpoints.</summary>
public class PlexMediaContainer
{
    /// <summary>List of metadata items in the container.</summary>
    [JsonPropertyName("Metadata")]
    public List<PlexMetadataItem>? Metadata { get; set; }

    /// <summary>Number of items in this specific response.</summary>
    [JsonPropertyName("size")]
    public int? Size { get; set; }

    /// <summary>Total number of items across all pages.</summary>
    [JsonPropertyName("totalSize")]
    public int? TotalSize { get; set; }
}

/// <summary>Data model for individual metadata items contained in a Plex response.</summary>
public class PlexMetadataItem
{
    /// <summary>The Plex unique rating key.</summary>
    [JsonPropertyName("ratingKey")]
    public string? RatingKey { get; set; }

    /// <summary>The display title of the item.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The ID of the library section containing this item.</summary>
    [JsonPropertyName("librarySectionID")]
    public int? LibrarySectionId { get; set; }

    /// <summary>The unique metadata GUID.</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    /// <summary>The media type (e.g., show, season, episode).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The index/number (e.g., episode number or season number).</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>The total number of times this item has been viewed.</summary>
    [JsonPropertyName("viewCount")]
    public int? ViewCount { get; set; }

    /// <summary>Unix timestamp of the last viewing.</summary>
    [JsonPropertyName("lastViewedAt")]
    public long? LastViewedAt { get; set; }

    /// <summary>Number of child items (e.g., episode count in a season).</summary>
    [JsonPropertyName("childCount")]
    public int? ChildCount { get; set; }

    /// <summary>User-specific state information.</summary>
    [JsonPropertyName("User")]
    public PlexMetadataUser? User { get; set; }

    /// <summary>Global critic rating value.</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    /// <summary>Personal user rating value.</summary>
    [JsonPropertyName("userRating")]
    public double? UserRating { get; set; }

    /// <summary>List of collection tags assigned to the item.</summary>
    [JsonPropertyName("Collection")]
    public List<PlexTag>? Collection { get; set; }
}

/// <summary>Represents a tag entry within a Plex metadata item.</summary>
/// <param name="Tag">The tag value.</param>
public record PlexTag([property: JsonPropertyName("tag")] string? Tag);

/// <summary>Embedded user-specific information within a Plex metadata item.</summary>
/// <param name="LastViewedAt">Unix timestamp of when this user last viewed the item.</param>
public record PlexMetadataUser([property: JsonPropertyName("lastViewedAt")] long? LastViewedAt);

#endregion
