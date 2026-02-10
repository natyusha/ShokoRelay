using System.Linq;
using Microsoft.AspNetCore.Http;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.Enums;

namespace ShokoRelay.Helpers
{
    public sealed class ImageInfo
    {
        public string alt { get; init; } = "";
        public string type { get; init; } = "";
        public string url { get; init; } = "";
    }

    public static class ImageHelper
    {
        public static IHttpContextAccessor? HttpContextAccessor { get; set; }

        public static string GetBaseUrl()
        {
            var ctx = HttpContextAccessor?.HttpContext;
            if (ctx is not null)
                return $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            return "http://localhost:8111";
        }

        public static string GetImageUrl(IImageMetadata image, string? imageTypeOverride = null) =>
            $"{GetBaseUrl()}/api/v{ShokoRelayInfo.ApiVersion}/Image/{image.Source}/{imageTypeOverride ?? image.ImageType.ToString()}/{image.ID}";

        public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage)
        {
            IEnumerable<IImageMetadata> Filter(ImageEntityType type)
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

            // backgroundSquare excluded as there is no provider for them yet
            return Project(ImageEntityType.Backdrop, "background")
                .Concat(Project(ImageEntityType.Logo, "clearLogo"))
                .Concat(Project(ImageEntityType.Poster, "coverPoster"))
                .Concat(Project(ImageEntityType.Thumbnail, "snapshot"))
                .ToArray();
        }

        /// <summary>
        /// Build a `coverPoster` image array for a season. If `seasonPosters` is provided, use those (honoring `addEveryImage`).
        /// Otherwise fall back to the series `coverPoster` images returned by <see cref="GenerateImageArray"/>.
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
