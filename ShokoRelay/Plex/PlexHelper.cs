using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Helpers;

namespace ShokoRelay.Plex;

/// <summary>Miscellaneous utility routines used by Plex-facing code.</summary>
public static class PlexHelper
{
    private static readonly Regex _showIdRegex = new(@"/show/(\d+)", RegexOptions.Compiled);

    /// <summary>Parse a Plex GUID string and return the embedded Shoko series ID.</summary>
    /// <param name="guid">Plex GUID.</param>
    /// <returns>Extracted ID or null.</returns>
    public static int? ExtractShokoSeriesIdFromGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;
        var match = _showIdRegex.Match(guid);
        return !match.Success ? null
            : int.TryParse(match.Groups[1].Value, out var id) ? id
            : null;
    }

    /// <summary>Search for a custom collection poster image matching the series context.</summary>
    /// <param name="series">Target series.</param>
    /// <param name="collectionName">Collection name.</param>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="metadataService">Optional metadata service.</param>
    /// <returns>Path to poster file or null.</returns>
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

    /// <summary>Locate a local collection poster by matching against a group ID.</summary>
    /// <param name="series">Target series.</param>
    /// <param name="groupId">Shoko group ID.</param>
    /// <returns>Path to poster or null.</returns>
    public static string? FindCollectionPosterPathByGroup(IShokoSeries series, int groupId)
    {
        if (series == null || groupId <= 0)
            return null;
        var group = series.TopLevelGroup;
        string? groupTitle = group?.PreferredTitle?.Value ?? group?.DefaultTitle?.Value;
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
                if (IsIdMatch(baseName, groupId))
                    return file;
                if (!string.IsNullOrWhiteSpace(groupTitle) && string.Equals(baseName, groupTitle, StringComparison.OrdinalIgnoreCase))
                    return file;
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

    /// <summary>Determine the filesystem import roots for a series.</summary>
    /// <param name="series">Shoko series.</param>
    /// <param name="metadataService">Optional metadata service.</param>
    /// <returns>Unique set of root paths.</returns>
    public static HashSet<string> ResolveImportRoots(IShokoSeries series, IMetadataService? metadataService = null)
    {
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        OverrideHelper.EnsureLoaded();
        var seriesList = new List<IShokoSeries> { series };
        if (metadataService != null && ShokoRelay.Settings.TmdbEpNumbering)
        {
            int primaryId = OverrideHelper.GetPrimary(series.ID, metadataService);
            var group = OverrideHelper.GetGroup(primaryId, metadataService);
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
                if (!string.IsNullOrWhiteSpace(importRoot))
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
            if (!invalid.Contains(c))
                sb.Append(c);
        }
        string cleaned = TextHelper.CondenseSpaces(sb.ToString().Trim());
        return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
    }

    /// <summary>Build a URL for serving a collection poster image.</summary>
    /// <param name="series">Series.</param>
    /// <param name="collectionName">Collection name.</param>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="metadataService">Optional metadata service.</param>
    /// <param name="allowPrimarySeriesFallback">Whether to use primary series poster as fallback.</param>
    /// <param name="baseUrl">Base URL override.</param>
    /// <returns>URL string or null.</returns>
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
        var posterPath = FindCollectionPosterPath(series, collectionName, collectionId, metadataService);
        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            string b = string.IsNullOrWhiteSpace(baseUrl) ? ShokoRelay.ServerBaseUrl : baseUrl?.TrimEnd('/') ?? string.Empty;
            return $"{b}{ShokoRelayInfo.BasePath}/collections/user/{series.TopLevelGroupID}";
        }
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

/// <summary>Payload delivered by Plex webhooks.</summary>
public class PlexWebhookPayload
{
    /// <summary>Information about the Plex server.</summary>
    [JsonPropertyName("Server")]
    public PlexServer? Server { get; set; }

    /// <summary>Plex server identity details.</summary>
    public class PlexServer
    {
        /// <summary>Server title.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>Server unique UUID.</summary>
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }
    }

    /// <summary>The event type.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>True if triggered by a user.</summary>
    [JsonPropertyName("user")]
    public bool? User { get; set; }

    /// <summary>True if triggered by the server owner.</summary>
    [JsonPropertyName("owner")]
    public bool? Owner { get; set; }

    /// <summary>Account info.</summary>
    [JsonPropertyName("Account")]
    public PlexAccount? Account { get; set; }

    /// <summary>Metadata associated with the item.</summary>
    [JsonPropertyName("Metadata")]
    public PlexMetadata? Metadata { get; set; }

    /// <summary>Plex user account info.</summary>
    public class PlexAccount
    {
        /// <summary>User account title.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    /// <summary>Metadata section of a Plex webhook payload.</summary>
    public class PlexMetadata
    {
        /// <summary>Unique metadata GUID.</summary>
        [JsonPropertyName("guid")]
        public string? Guid { get; set; }

        /// <summary>Grandparent (Show) title.</summary>
        [JsonPropertyName("grandparentTitle")]
        public string? GrandparentTitle { get; set; }

        /// <summary>Parent (Season) index.</summary>
        [JsonPropertyName("parentIndex")]
        public int? ParentIndex { get; set; }

        /// <summary>Item title.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>Item index (episode number).</summary>
        [JsonPropertyName("index")]
        public int? Index { get; set; }

        /// <summary>Unix timestamp of viewing.</summary>
        [JsonPropertyName("lastViewedAt")]
        public long? LastViewedAt { get; set; }

        /// <summary>Library section numeric ID.</summary>
        [JsonPropertyName("librarySectionId")]
        public int? LibrarySectionId { get; set; }

        /// <summary>Metadata type string.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Rating value from 0-10.</summary>
        [JsonPropertyName("userRating")]
        public double? UserRating { get; set; }
    }
}
