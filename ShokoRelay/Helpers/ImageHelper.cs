using Newtonsoft.Json;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;

namespace ShokoRelay.Helpers;

#region Data Models

/// <summary>DTO describing an image for the dashboard and Plex endpoints.</summary>
public sealed class ImageInfo
{
    /// <summary>Alternate text for the image.</summary>
    [JsonProperty("alt")]
    public string Alt { get; init; } = "";

    /// <summary>The type of image (e.g., coverPoster, background).</summary>
    [JsonProperty("type")]
    public string Type { get; init; } = "";

    /// <summary>The fully qualified URL to the image.</summary>
    [JsonProperty("url")]
    public string Url { get; init; } = "";
}

#endregion

/// <summary>Utilities for generating image URLs and arrays suitable for Plex metadata and the web dashboard.</summary>
public static class ImageHelper
{
    #region URL Generation

    /// <summary>Construct a full URL for the given image, including an optional cache-buster.</summary>
    /// <param name="image">Image metadata object.</param>
    /// <param name="cacheBuster">Optional token to defeat Plex caching.</param>
    /// <returns>A full URL string.</returns>
    public static string GetImageUrl(IImage image, string? cacheBuster = null)
    {
        var url = $"{ServerBaseUrl}/api/v3/Image/{image.ID}";
        return string.IsNullOrEmpty(cacheBuster) ? url : $"{url}?t={cacheBuster}";
    }

    #endregion

    #region Image Builders

    /// <summary>Filters and returns only enabled, desired, and locally available images from the supplied entity.</summary>
    /// <param name="entity">The metadata entity providing images.</param>
    /// <param name="type">The specific image type to retrieve.</param>
    /// <returns>A collection of available images.</returns>
    public static IEnumerable<IImage> GetAvailableImages(this IWithImages entity, ImageEntityType type) => entity.GetImages(imageType: type).Where(i => i.IsEnabled && i.IsAvailable && i.IsDesired);

    /// <summary>Build an array of ImageInfo records from the supplied images collection.</summary>
    /// <param name="images">The object providing images.</param>
    /// <param name="title">Alt text for entries.</param>
    /// <param name="addEveryImage">Whether to include all images or only preferred ones.</param>
    /// <param name="cacheBuster">Optional cache-buster token.</param>
    /// <returns>An array of ImageInfo objects.</returns>
    public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage, string? cacheBuster = null)
    {
        IEnumerable<IImage> Filter(ImageEntityType type) =>
            images.GetAvailableImages(type) is var all && addEveryImage ? all.OrderByDescending(i => i.IsPreferred) : (all.FirstOrDefault(i => i.IsPreferred) is { } pref ? [pref] : all.Take(1));

        IEnumerable<ImageInfo> Project(ImageEntityType type, string kind) =>
            Filter(type)
                .Select(i => new ImageInfo
                {
                    Alt = title,
                    Type = kind,
                    Url = GetImageUrl(i, cacheBuster),
                });

        return
        [
            .. Project(ImageEntityType.Backdrop, "background"),
            .. Project(ImageEntityType.Logo, "clearLogo"),
            .. Project(ImageEntityType.Primary, "coverPoster"),
            .. Project(ImageEntityType.Primary, "snapshot"),
        ];
    }

    /// <summary>Create a set of cover poster images specifically for a season entry.</summary>
    /// <param name="seriesImages">Fallback series images.</param>
    /// <param name="alt">Alt text for entries.</param>
    /// <param name="addEveryImage">Whether to include all images.</param>
    /// <param name="seasonPosters">Optional pre-resolved URLs.</param>
    /// <param name="cacheBuster">Optional cache-buster token.</param>
    /// <returns>An array of ImageInfo objects.</returns>
    public static ImageInfo[] BuildCoverPosterArray(IWithImages seriesImages, string alt, bool addEveryImage, IEnumerable<string>? seasonPosters = null, string? cacheBuster = null)
    {
        return seasonPosters != null && seasonPosters.Any()
            ? addEveryImage
                ?
                [
                    .. seasonPosters.Select(url => new ImageInfo
                    {
                        Alt = alt,
                        Type = "coverPoster",
                        Url = url,
                    }),
                ]
                :
                [
                    new ImageInfo
                    {
                        Alt = alt,
                        Type = "coverPoster",
                        Url = seasonPosters.First(),
                    },
                ]
            : [.. GenerateImageArray(seriesImages, alt, addEveryImage, cacheBuster).Where(i => i.Type == "coverPoster")];
    }

    #endregion
}
