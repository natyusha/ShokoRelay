using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers;

/// <summary>DTO describing an image for the dashboard and Plex endpoints.</summary>
public sealed class ImageInfo
{
    /// <summary>Alternate text for the image.</summary>
    public string Alt { get; init; } = "";

    /// <summary>The type of image (e.g., coverPoster, background).</summary>
    public string Type { get; init; } = "";

    /// <summary>The fully qualified URL to the image.</summary>
    public string Url { get; init; } = "";
}

/// <summary>Utilities for generating image URLs and arrays suitable for Plex metadata and the web dashboard.</summary>
public static class ImageHelper
{
    /// <summary>Construct a full URL for the given image, including an optional cache-buster.</summary>
    /// <param name="image">Image metadata object.</param>
    /// <param name="imageTypeOverride">Optional string to override the path type.</param>
    /// <param name="cacheBuster">Optional token to defeat Plex caching.</param>
    /// <returns>A full URL string.</returns>
    public static string GetImageUrl(IImage image, string? imageTypeOverride = null, string? cacheBuster = null)
    {
        var url = $"{ShokoRelay.ServerBaseUrl}/api/v3/Image/{image.Source}/{imageTypeOverride ?? image.ImageType.ToString()}/{image.ID}";
        return string.IsNullOrEmpty(cacheBuster) ? url : $"{url}?t={cacheBuster}";
    }

    /// <summary>Build an array of ImageInfo records from the supplied images collection.</summary>
    /// <param name="images">Object providing images.</param>
    /// <param name="title">Alt text for entries.</param>
    /// <param name="addEveryImage">Whether to include all images or only preferred ones.</param>
    /// <param name="cacheBuster">Optional cache-buster token.</param>
    /// <returns>An array of ImageInfo objects.</returns>
    public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage, string? cacheBuster = null)
    {
        IEnumerable<IImage> Filter(ImageEntityType type)
        {
            var all = images.GetImages(type);
            if (addEveryImage)
                return all;
            var pref = all.FirstOrDefault(i => i.IsPreferred);
            return pref is not null ? [pref] : all.Take(1);
        }
        IEnumerable<ImageInfo> Project(ImageEntityType type, string kind) =>
            Filter(type)
                .Select(i => new ImageInfo
                {
                    Alt = title,
                    Type = kind,
                    Url = GetImageUrl(i, cacheBuster: cacheBuster),
                });

        return
        [
            .. Project(ImageEntityType.Backdrop, "background"),
            .. Project(ImageEntityType.Logo, "clearLogo"),
            .. Project(ImageEntityType.Poster, "coverPoster"),
            .. Project(ImageEntityType.Thumbnail, "snapshot"),
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
}
