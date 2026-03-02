using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers;

namespace ShokoRelay.Plex
{
    /// <summary>
    /// Miscellaneous utility routines used by Plex-facing code such as poster resolution and GUID parsing.
    /// </summary>
    public static class PlexHelper
    {
        private static readonly Regex _showIdRegex = new(@"/show/(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// Parse a Plex GUID string and return the embedded Shoko series ID if present. The GUID contains a "/show/{id}" segment when it maps to a series.
        /// </summary>
        /// <param name="guid">The Plex GUID string to parse.</param>
        /// <returns>The extracted series ID, or <c>null</c> if the GUID does not contain a valid series reference.</returns>
        public static int? ExtractShokoSeriesIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var match = _showIdRegex.Match(guid);
            if (!match.Success)
                return null;

            if (int.TryParse(match.Groups[1].Value, out var id))
                return id;

            return null;
        }

        /// <summary>
        /// Search configured import roots for a custom collection poster image that matches either the group ID, collection ID, or normalized collection title. Returns the first matching file path or <c>null</c> if none found.
        /// </summary>
        /// <param name="series">Series whose import roots are searched for poster files.</param>
        /// <param name="collectionName">Display name of the collection (used for fuzzy filename matching).</param>
        /// <param name="collectionId">Numeric collection ID to match against poster filenames.</param>
        /// <param name="metadataService">Optional metadata service for override-aware root resolution.</param>
        /// <returns>The full path to the matching poster file, or <c>null</c>.</returns>
        public static string? FindCollectionPosterPath(IShokoSeries series, string collectionName, int collectionId, IMetadataService? metadataService = null)
        {
            if (series == null)
                return null;

            int groupId = series.TopLevelGroupID;
            if (groupId <= 0)
                return null;

            string? normalizedTitle = NormalizeCollectionKey(collectionName);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                return null;

            var roots = ResolveImportRoots(series, metadataService);
            if (roots.Count == 0)
                return null;

            string postersFolderName = Vfs.VfsShared.ResolveCollectionPostersFolderName();

            foreach (var root in roots)
            {
                string postersPath = Path.Combine(root, postersFolderName);
                if (!Directory.Exists(postersPath))
                    continue;

                foreach (var file in Directory.EnumerateFiles(postersPath))
                {
                    string extension = Path.GetExtension(file);
                    if (string.IsNullOrWhiteSpace(extension) || !PlexConstants.LocalMediaAssets.Artwork.Contains(extension))
                        continue;

                    string baseName = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(baseName))
                        continue;

                    if (IsIdMatch(baseName, groupId) || IsIdMatch(baseName, collectionId) || NormalizeCollectionKey(baseName) == normalizedTitle)
                        return file;
                }
            }

            return null;
        }

        /// <summary>
        /// Locate a local collection poster by matching against the specified group ID (and optionally the group's title).
        /// </summary>
        /// <param name="series">Series whose import roots are searched.</param>
        /// <param name="groupId">The Shoko group ID to match against poster filenames.</param>
        /// <returns>The full path to the matching poster file, or <c>null</c>.</returns>
        public static string? FindCollectionPosterPathByGroup(IShokoSeries series, int groupId)
        {
            if (series == null)
                return null;
            if (groupId <= 0)
                return null;

            var group = series.TopLevelGroup;
            string? groupTitle = null;
            try
            {
                groupTitle = group?.PreferredTitle?.Value ?? group?.DefaultTitle?.Value;
            }
            catch
            {
                groupTitle = null;
            }

            var roots = ResolveImportRoots(series);
            if (roots.Count == 0)
                return null;

            string postersFolderName = Vfs.VfsShared.ResolveCollectionPostersFolderName();

            foreach (var root in roots)
            {
                string postersPath = Path.Combine(root, postersFolderName);
                if (!Directory.Exists(postersPath))
                    continue;

                foreach (var file in Directory.EnumerateFiles(postersPath))
                {
                    string extension = Path.GetExtension(file);
                    if (string.IsNullOrWhiteSpace(extension) || !PlexConstants.LocalMediaAssets.Artwork.Contains(extension))
                        continue;

                    string baseName = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(baseName))
                        continue;

                    // 1) ID match (e.g., "25932.png" or "c25932.png")
                    if (IsIdMatch(baseName, groupId))
                        return file;

                    // 2) Exact title match (case-insensitive)
                    if (!string.IsNullOrWhiteSpace(groupTitle) && string.Equals(baseName, groupTitle, StringComparison.OrdinalIgnoreCase))
                        return file;

                    // 3) Stripped invalid Windows filename characters match (e.g. "My: Group" -> "My Group")
                    if (!string.IsNullOrWhiteSpace(groupTitle))
                    {
                        var strippedGroup = TextHelper.StripInvalidWindowsChars(groupTitle).ToLowerInvariant();
                        var strippedBase = TextHelper.StripInvalidWindowsChars(baseName).ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(strippedGroup) && strippedGroup == strippedBase)
                            return file;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Determine the set of filesystem import root folders that contain files for the provided <paramref name="series"/>. Optionally pass a <paramref name="metadataService"/> for override-aware series grouping.
        /// </summary>
        /// <param name="series">Series whose video files are inspected for import roots.</param>
        /// <param name="metadataService">Optional metadata service for override-aware series grouping.</param>
        /// <returns>A set of unique import root directory paths.</returns>
        public static HashSet<string> ResolveImportRoots(IShokoSeries series, IMetadataService? metadataService = null)
        {
            var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // make sure overrides are loaded so mapping decisions use latest data
            OverrideHelper.EnsureLoaded();

            // gather list of series to consider (primary + extras)
            var seriesList = new List<IShokoSeries> { series };
            if (metadataService != null && ShokoRelay.Settings.TmdbEpNumbering)
            {
                int primaryId = OverrideHelper.GetPrimary(series.ID, metadataService!);
                var group = OverrideHelper.GetGroup(primaryId, metadataService!);
                foreach (var id in group.Skip(1))
                {
                    var s = metadataService.GetShokoSeriesByID(id);
                    if (s != null)
                        seriesList.Add(s);
                }
            }

            foreach (var s in seriesList)
            {
                var fileData = MapHelper.GetSeriesFileData(s);
                foreach (var mapping in fileData.Mappings)
                {
                    var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
                    if (location == null)
                        continue;

                    string? importRoot = Vfs.VfsShared.ResolveImportRootPath(location);
                    if (string.IsNullOrWhiteSpace(importRoot))
                        continue;

                    roots.Add(importRoot);
                }
            }

            return roots;
        }

        private static bool IsIdMatch(string value, int id)
        {
            if (id <= 0)
                return false;

            string trimmed = value.Trim();
            if (trimmed.StartsWith("c", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            return int.TryParse(trimmed, out int parsed) && parsed == id;
        }

        private static string? NormalizeCollectionKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (invalid.Contains(c))
                    continue;
                sb.Append(c);
            }

            string cleaned = TextHelper.CondenseSpaces(sb.ToString().Trim());

            return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
        }

        /// <summary>
        /// Build a URL suitable for serving a collection poster image. Prefers a locally stored poster (using <see cref="FindCollectionPosterPath"/>).
        /// Will optionally fall back to the primary series poster via <paramref name="metadataService"/>.
        /// </summary>
        public static string? GetCollectionPosterUrl(
            IShokoSeries series,
            string collectionName,
            int collectionId,
            IMetadataService? metadataService = null,
            bool allowPrimarySeriesFallback = true,
            string? baseUrl = null
        )
        {
            if (series == null)
                return null;

            // 1) Check for local poster file for the collection
            var posterPath = FindCollectionPosterPath(series, collectionName, collectionId, metadataService);
            if (!string.IsNullOrWhiteSpace(posterPath))
            {
                string b = string.IsNullOrWhiteSpace(baseUrl) ? ShokoRelay.ServerBaseUrl : baseUrl?.TrimEnd('/') ?? string.Empty;
                // Prefer the plugin-style provider base for generated collection poster URLs
                return $"{b}{ShokoRelayInfo.BasePath}/collections/user/{series.TopLevelGroupID}";
            }

            // 2) Fallback to the primary series poster if allowed
            if (allowPrimarySeriesFallback && metadataService != null)
            {
                var group = metadataService.GetShokoGroupByID(series.TopLevelGroupID);
                var primarySeries = group?.MainSeries ?? group?.Series?.FirstOrDefault();
                if (primarySeries != null)
                {
                    var posterImage = (primarySeries as IWithImages)?.GetImages(ImageEntityType.Poster).FirstOrDefault();
                    if (posterImage != null)
                        return ImageHelper.GetImageUrl(posterImage);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Data transfer object representing the JSON payload delivered by Plex webhooks. Only the fields used by the plugin are included.
    /// </summary>
    public class PlexWebhookPayload
    {
        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("user")]
        public bool? User { get; set; }

        [JsonPropertyName("owner")]
        public bool? Owner { get; set; }

        [JsonPropertyName("Account")]
        public PlexAccount? Account { get; set; }

        [JsonPropertyName("Metadata")]
        public PlexMetadata? Metadata { get; set; }

        /// <summary>
        /// Account information supplied in a webhook payload.
        /// </summary>
        public class PlexAccount
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }

        /// <summary>
        /// Metadata section of a Plex webhook payload. Includes GUID, title, viewed timestamp, section ID and other episode/show identifiers.
        /// </summary>
        public class PlexMetadata
        {
            [JsonPropertyName("guid")]
            public string? Guid { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("lastViewedAt")]
            public long? LastViewedAt { get; set; }

            [JsonPropertyName("librarySectionId")]
            public int? LibrarySectionId { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("userRating")]
            public double? UserRating { get; set; }
        }
    }
}
