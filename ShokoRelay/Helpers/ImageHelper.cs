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

    /// <summary>Construct a full URL for the given image.</summary>
    /// <param name="image">Image metadata object.</param>
    /// <param name="forceRemote">If true, forces the returned URL to point to the remote TMDB CDN, bypassing the local Shoko Server API.</param>
    /// <returns>A full URL string.</returns>
    public static string GetImageUrl(IImage image, bool forceRemote = false)
    {
        if (forceRemote && image.Source == DataSource.TMDB && !string.IsNullOrEmpty(image.ResourceID))
        {
            string path = image.ResourceID.Replace('\\', '/');
            if (!path.StartsWith('/'))
                path = "/" + path;
            return $"https://image.tmdb.org/t/p/original{path}";
        }
        return $"{ServerBaseUrl}/api/v3/Image/{image.ID}";
    }

    #endregion

    #region Image Builders

    /// <summary>Maps a file extension to its corresponding MIME content type for collection poster images.</summary>
    /// <param name="ext">The file extension string.</param>
    /// <returns>A MIME type string or null if unsupported.</returns>
    public static string? GetMimeType(string ext) =>
        string.IsNullOrWhiteSpace(ext)
            ? null
            : ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".tbn" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => null,
            };

    /// <summary>Filters and returns only enabled, desired, and locally available images from the supplied entity.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="type">The specific image type to retrieve.</param>
    /// <returns>A collection of available images.</returns>
    public static IEnumerable<IImage> GetAvailableImages(this IWithImages entity, ImageEntityType type) => entity.GetImages(imageType: type).Where(i => i.IsEnabled && i.IsAvailable && i.IsDesired);

    /// <summary>Gets the URL of the preferred image for the entity based on the language setting.</summary>
    /// <param name="entity">The Shoko metadata entity.</param>
    /// <param name="type">The specific image type to retrieve.</param>
    /// <param name="languageSetting">The prioritized language setting string.</param>
    /// <returns>The URL of the preferred image, or null if none exists.</returns>
    public static string? GetPreferredImageUrl(this IWithImages entity, ImageEntityType type, string languageSetting) =>
        FilterImagesByLanguage(entity.GetAvailableImages(type), languageSetting, false).FirstOrDefault() is { } img ? GetImageUrl(img) : null;

    /// <summary>Filters and orders images based on a prioritized list of language codes.</summary>
    /// <param name="images">The collection of images to filter.</param>
    /// <param name="imageLanguage">The prioritized language setting string.</param>
    /// <param name="addEveryImage">Whether to return all images ordered by priority or just the first matching one.</param>
    /// <returns>A filtered and ordered collection of images.</returns>
    public static IEnumerable<IImage> FilterImagesByLanguage(IEnumerable<IImage> images, string imageLanguage, bool addEveryImage)
    {
        var all = images.ToList();
        if (all.Count == 0)
            return all;

        if (string.IsNullOrWhiteSpace(imageLanguage) || imageLanguage.Trim().Equals("SHOKO", StringComparison.OrdinalIgnoreCase))
            return addEveryImage ? all.OrderByDescending(i => i.IsPreferred) : (all.FirstOrDefault(i => i.IsPreferred) is { } pref ? [pref] : all.Take(1));

        var langs = imageLanguage.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ordered = new List<IImage>();
        var remaining = new List<IImage>(all);

        foreach (var lang in langs)
        {
            if (lang.Equals("shoko", StringComparison.OrdinalIgnoreCase))
            {
                var p = remaining.FirstOrDefault(i => i.IsPreferred);
                if (p != null)
                {
                    ordered.Add(p);
                    remaining.Remove(p);
                }
                if (!addEveryImage && ordered.Count > 0)
                    return ordered.Take(1);
            }
            else
            {
                var matches = remaining
                    .Where(i =>
                    {
                        var prop = i.GetType().GetProperty("LanguageCode") ?? i.GetType().GetProperty("Language");
                        var code = prop?.GetValue(i)?.ToString() ?? "";
                        return code.Equals(lang, StringComparison.OrdinalIgnoreCase) || (lang.Equals("none", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(code));
                    })
                    .ToList();

                foreach (var match in matches)
                {
                    ordered.Add(match);
                    remaining.Remove(match);
                }
                if (!addEveryImage && ordered.Count > 0)
                    return ordered.Take(1);
            }
        }

        if (addEveryImage)
            ordered.AddRange(remaining.OrderByDescending(i => i.IsPreferred));
        else if (ordered.Count == 0 && remaining.Count > 0)
            ordered.Add(remaining.FirstOrDefault(i => i.IsPreferred) ?? remaining.First());

        return ordered;
    }

    /// <summary>Build an array of ImageInfo records from the supplied images collection.</summary>
    /// <param name="images">The object providing images.</param>
    /// <param name="title">Alt text for entries.</param>
    /// <param name="addEveryImage">Whether to include all images or only preferred ones.</param>
    /// <param name="imageLanguage">The prioritized language setting string.</param>
    /// <returns>An array of ImageInfo objects.</returns>
    public static ImageInfo[] GenerateImageArray(IWithImages images, string title, bool addEveryImage, string imageLanguage)
    {
        IEnumerable<ImageInfo> Project(ImageEntityType type, string kind) =>
            FilterImagesByLanguage(images.GetAvailableImages(type), imageLanguage, addEveryImage)
                .Select(i => new ImageInfo
                {
                    Alt = title,
                    Type = kind,
                    Url = GetImageUrl(i, forceRemote: addEveryImage),
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
    /// <remarks>
    /// Season posters are hardcoded by the caller to always use direct CDN routing when TMDB season posters are present.
    /// This forces 'addEveryImage' to true to bypass local metadata limitations until Shoko's WebUI supports selecting the preferred poster.
    /// </remarks>
    /// <param name="seriesImages">Fallback series images.</param>
    /// <param name="alt">Alt text for entries.</param>
    /// <param name="addEveryImage">Whether to include all images.</param>
    /// <param name="imageLanguage">The prioritized language setting string.</param>
    /// <param name="seasonPosters">Optional pre-resolved URLs.</param>
    /// <returns>An array of ImageInfo objects.</returns>
    public static ImageInfo[] BuildCoverPosterArray(IWithImages seriesImages, string alt, bool addEveryImage, string imageLanguage, IEnumerable<string>? seasonPosters = null)
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
            : [.. GenerateImageArray(seriesImages, alt, addEveryImage, imageLanguage).Where(i => i.Type == "coverPoster")];
    }

    #endregion
}
