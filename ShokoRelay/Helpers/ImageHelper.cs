using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers
{
    /// <summary>
    /// Simple DTO returned to the dashboard and Plex endpoints describing an image including its alternate text, type name, and fully qualified URL.
    /// </summary>
    public sealed class ImageInfo
    {
        public string alt { get; init; } = "";
        public string type { get; init; } = "";
        public string url { get; init; } = "";
    }

    /// <summary>
    /// Utilities for generating image URLs and arrays suitable for Plex metadata and the web dashboard.
    /// </summary>
    public static class ImageHelper
    {
        /// <summary>
        /// Construct a full URL for the given <paramref name="image"/>, optionally overriding the <c>imageType</c> portion of the path.
        /// When <paramref name="cacheBuster"/> is provided, it is appended as a <c>?t=</c> query parameter to defeat Plex image caching
        /// (e.g. when AniDB replaces a poster but the image ID stays the same).
        /// </summary>
        /// <param name="image">Image metadata to build the URL for.</param>
        /// <param name="imageTypeOverride">Optional image type string to use instead of the image's own type.</param>
        /// <param name="cacheBuster">Optional cache-busting token (typically a Unix timestamp derived from the series' <c>LastUpdatedAt</c>).</param>
        /// <returns>The fully-qualified image URL.</returns>
        public static string GetImageUrl(IImage image, string? imageTypeOverride = null, string? cacheBuster = null)
        {
            var url = $"{ShokoRelay.ServerBaseUrl}/api/v3/Image/{image.Source}/{imageTypeOverride ?? image.ImageType.ToString()}/{image.ID}";
            return string.IsNullOrEmpty(cacheBuster) ? url : $"{url}?t={cacheBuster}";
        }

        /// <summary>
        /// Build an array of <see cref="ImageInfo"/> records from the supplied <paramref name="images"/> collection.
        /// The <paramref name="title"/> is used as the `alt` text. When <paramref name="addEveryImage"/> is false the helper will only include the preferred image for each type.
        /// </summary>
        /// <param name="images">Source object providing images by type.</param>
        /// <param name="title">Alt text applied to each image entry.</param>
        /// <param name="addEveryImage">When <c>true</c>, include all images for each type; when <c>false</c>, include only the preferred image per type.</param>
        /// <param name="cacheBuster">Optional cache-busting token forwarded to <see cref="GetImageUrl"/>.</param>
        /// <returns>An array of <see cref="ImageInfo"/> objects covering backgrounds, logos, posters, and thumbnails.</returns>
        public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage, string? cacheBuster = null)
        {
            IEnumerable<IImage> Filter(ImageEntityType type)
            {
                var all = images.GetImages(type);
                if (addEveryImage)
                    return all;

                var pref = all.FirstOrDefault(i => i.IsPreferred);
                return pref is not null ? new[] { pref } : all.Take(1);
            }

            IEnumerable<ImageInfo> Project(ImageEntityType type, string kind) =>
                Filter(type)
                    .Select(i => new ImageInfo
                    {
                        alt = title,
                        type = kind,
                        url = GetImageUrl(i, cacheBuster: cacheBuster),
                    });

            return Project(ImageEntityType.Backdrop, "background")
                .Concat(Project(ImageEntityType.Logo, "clearLogo"))
                .Concat(Project(ImageEntityType.Poster, "coverPoster"))
                .Concat(Project(ImageEntityType.Thumbnail, "snapshot"))
                //.Concat(Project(ImageEntityType.Square, "backgroundSquare")) // backgroundSquare excluded as there is no provider for them yet
                .ToArray();
        }

        /// <summary>
        /// Create a set of cover poster images appropriate for a season entry.
        /// If <paramref name="seasonPosters"/> is supplied it will be used according to <paramref name="addEveryImage"/>, otherwise the helper falls back to the
        /// series-level images filtered by type.
        /// </summary>
        /// <param name="seriesImages">Series-level images to fall back to when no season posters are supplied.</param>
        /// <param name="alt">Alt text applied to each image entry.</param>
        /// <param name="addEveryImage">When <c>true</c>, include all available posters; when <c>false</c>, include only one.</param>
        /// <param name="seasonPosters">Optional pre-resolved poster URLs for the season.</param>
        /// <param name="cacheBuster">Optional cache-busting token forwarded to <see cref="GenerateImageArray"/>.</param>
        /// <returns>An array of <see cref="ImageInfo"/> objects containing cover poster entries.</returns>
        public static ImageInfo[] BuildCoverPosterArray(IWithImages seriesImages, string alt, bool addEveryImage, IEnumerable<string>? seasonPosters = null, string? cacheBuster = null)
        {
            if (seasonPosters != null && seasonPosters.Any())
            {
                if (addEveryImage)
                    return seasonPosters
                        .Select(url => new ImageInfo
                        {
                            alt = alt,
                            type = "coverPoster",
                            url = url,
                        })
                        .ToArray();

                return new[]
                {
                    new ImageInfo
                    {
                        alt = alt,
                        type = "coverPoster",
                        url = seasonPosters.First(),
                    },
                };
            }

            return GenerateImageArray(seriesImages, alt, addEveryImage, cacheBuster).Where(i => i.type == "coverPoster").ToArray();
        }
    }
}
