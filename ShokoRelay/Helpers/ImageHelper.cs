using Microsoft.AspNetCore.Http;
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
    /// Utilities for generating image URLs and arrays suitable for Plex metadata and the web dashboard. Requires an <see cref="IHttpContextAccessor"/> to be set for base URL resolution.
    /// </summary>
    public static class ImageHelper
    {
        public static IHttpContextAccessor? HttpContextAccessor { get; set; }

        /// <summary>
        /// Determine the base URL of the running server from the current request context. Falls back to the default plugin port if no context is available.
        /// </summary>
        public static string GetBaseUrl()
        {
            var ctx = HttpContextAccessor?.HttpContext;
            if (ctx is not null)
                return $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            return "http://localhost:8111";
        }

        /// <summary>
        /// Construct a full URL for the given <paramref name="image"/>, optionally overriding the <c>imageType</c> portion of the path.
        /// </summary>
        public static string GetImageUrl(IImage image, string? imageTypeOverride = null) => $"{GetBaseUrl()}/api/v3/Image/{image.Source}/{imageTypeOverride ?? image.ImageType.ToString()}/{image.ID}";

        /// <summary>
        /// Build an array of <see cref="ImageInfo"/> records from the supplied <paramref name="images"/> collection.
        /// The <paramref name="title"/> is used as the `alt` text. When <paramref name="addEveryImage"/> is false the helper will only include the preferred image for each type.
        /// </summary>
        public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage)
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
                        url = GetImageUrl(i),
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
        public static ImageInfo[] BuildCoverPosterArray(IWithImages seriesImages, string alt, bool addEveryImage, IEnumerable<string>? seasonPosters = null)
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

            return GenerateImageArray(seriesImages, alt, addEveryImage).Where(i => i.type == "coverPoster").ToArray();
        }
    }
}
