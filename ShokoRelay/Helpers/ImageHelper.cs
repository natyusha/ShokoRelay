using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.Enums;

namespace ShokoRelay.Helpers
{
    public static class ImageHelper
    {
        public static string GetImageUrl(IImageMetadata image, string apiBaseUrl, string apiRoot)
            => $"{apiBaseUrl}/{apiRoot}/Image/{image.Source}/{image.ImageType}/{image.ID}";

        public static object[] GenerateImageArray(IWithImages images, string title, string baseUrl, string apiRoot, bool addEveryImage)
        {
            IEnumerable<IImageMetadata> Filter(ImageEntityType type)
            {
                var all = images.GetImages(type);
                if (addEveryImage) return all;
                var pref = all.FirstOrDefault(i => i.IsPreferred);
                return pref != null ? [pref] : all.Take(1);
            }

            return Filter(ImageEntityType.Backdrop)
                .Select(i => new { alt = title, type = "background", url = GetImageUrl(i, baseUrl, apiRoot) })
                .Concat(Filter(ImageEntityType.Poster)
                    .Select(i => new { alt = title, type = "coverPoster", url = GetImageUrl(i, baseUrl, apiRoot) }))
                .Concat(Filter(ImageEntityType.Logo)
                    .Select(i => new { alt = title, type = "clearLogo", url = GetImageUrl(i, baseUrl, apiRoot) }))
                .Where(img => !string.IsNullOrEmpty(img.url))
                .ToArray<object>();
        }
    }
}