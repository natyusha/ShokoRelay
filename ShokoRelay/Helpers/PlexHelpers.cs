using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Plex;

namespace ShokoRelay.Helpers
{
    public static class PlexHelpers
    {
        public static int? ExtractShokoSeriesIdFromGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var match = Regex.Match(guid, @"/show/(\d+)");
            if (!match.Success)
                return null;

            if (int.TryParse(match.Groups[1].Value, out var id))
                return id;

            return null;
        }

        public static string? FindCollectionPosterPath(IShokoSeries series, string collectionName, int collectionId)
        {
            if (series == null)
                return null;

            int groupId = series.TopLevelGroupID;
            if (groupId <= 0)
                return null;

            string? normalizedTitle = NormalizeCollectionKey(collectionName);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                return null;

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

                    if (IsIdMatch(baseName, groupId) || IsIdMatch(baseName, collectionId) || NormalizeCollectionKey(baseName) == normalizedTitle)
                        return file;
                }
            }

            return null;
        }

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
                        var strippedGroup = StripInvalidWindowsChars(groupTitle).ToLowerInvariant();
                        var strippedBase = StripInvalidWindowsChars(baseName).ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(strippedGroup) && strippedGroup == strippedBase)
                            return file;
                    }
                }
            }

            return null;
        }

        public static HashSet<string> ResolveImportRoots(IShokoSeries series)
        {
            var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var fileData = MapHelper.GetSeriesFileData(series);
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

        private static string StripInvalidWindowsChars(string value)
        {
            return TextHelper.StripInvalidWindowsChars(value);
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

            string cleaned = sb.ToString().Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");

            return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
        }

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
            var posterPath = FindCollectionPosterPath(series, collectionName, collectionId);
            if (!string.IsNullOrWhiteSpace(posterPath))
            {
                string b = string.IsNullOrWhiteSpace(baseUrl) ? ImageHelper.GetBaseUrl() : baseUrl?.TrimEnd('/') ?? string.Empty;
                // Prefer the plugin-style provider base for generated collection poster URLs
                return $"{b}/api/plugin/ShokoRelay/collections/user/{series.TopLevelGroupID}";
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

    // DTO for Plex webhook payloads
    public class PlexWebhookPayload
    {
        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("Account")]
        public PlexAccount? Account { get; set; }

        [JsonPropertyName("Metadata")]
        public PlexMetadata? Metadata { get; set; }

        public class PlexAccount
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }

        public class PlexMetadata
        {
            [JsonPropertyName("guid")]
            public string? Guid { get; set; }

            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("lastViewedAt")]
            public long? LastViewedAt { get; set; }

            [JsonPropertyName("librarySectionId")]
            public int? LibrarySectionId { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }
        }
    }
}
