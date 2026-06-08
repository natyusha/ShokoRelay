using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using ShokoRelay.Vfs;

namespace ShokoRelay.Plex;

/// <summary>Miscellaneous utility routines used by Plex-facing code.</summary>
public static class PlexHelper
{
    #region ID Parsing/Extraction

    /// <summary>Regex which extracts the ID from a Show GUID.</summary>
    private static readonly Regex s_showIdRegex = new(@"/show/(\d+)", RegexOptions.Compiled);

    /// <summary>Regex which extracts the ID from an Episode GUID.</summary>
    private static readonly Regex s_episodeIdRegex = new(@"/episode/e(\d+)", RegexOptions.Compiled);

    /// <summary>Parse a Plex GUID string and return the embedded Shoko series ID.</summary>
    /// <param name="guid">Plex GUID.</param>
    /// <returns>Extracted ID or null.</returns>
    public static int? ExtractShokoSeriesIdFromGuid(string? guid) => ExtractIdFromGuid(guid, s_showIdRegex);

    /// <summary>Parse Shoko episode ID from GUID.</summary>
    /// <param name="guid">Plex GUID.</param>
    /// <returns>Extracted ID or null.</returns>
    public static int? ExtractShokoEpisodeIdFromGuid(string? guid) => ExtractIdFromGuid(guid, s_episodeIdRegex);

    private static int? ExtractIdFromGuid(string? guid, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;
        var match = regex.Match(guid);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>Extracts the corresponding Shoko Series ID from any valid Plex Rating Key.</summary>
    /// <remarks>
    /// **Supported RatingKey Formats:**
    /// - '123' (Shoko Series ID) / 'a123' (AniDB Series ID)
    /// - '123s4' (Shoko Series Season 4) / 'a123s4' (AniDB Series Season 4)
    /// - 'e567' (Shoko Episode ID) / 'ae567' (AniDB Episode ID)
    /// - 'e567p2' (Shoko Episode Part 2) / 'ae567p2' (AniDB Episode Part 2)
    /// - _AniDB IDs resolve to Shoko IDs and must be known to Shoko_
    /// </remarks>
    /// <param name="ratingKey">Plex rating key representing a show, season or episode.</param>
    /// <param name="metadataService">The metadata service used to look up episodes/series.</param>
    /// <returns>The resolved Shoko Series ID, or 0 if not found.</returns>
    public static int ExtractShokoSeriesIdFromRatingKey(string ratingKey, IMetadataService metadataService)
    {
        if (string.IsNullOrWhiteSpace(ratingKey))
            return 0;
        if (IsEpisodeKey(ratingKey))
        {
            // Shoko Episode ID (e{ID} or e{ID}p{Part}) // AniDB Episode Alias (ae{ID} or ae{ID}p{Part})
            bool isAniDb = ratingKey.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase);
            var epIdPart = ratingKey[(isAniDb ? PlexConstants.AniDbPrefix.Length + PlexConstants.EpisodePrefix.Length : PlexConstants.EpisodePrefix.Length)..].Split(PlexConstants.PartPrefix)[0];
            return int.TryParse(epIdPart, out var id) ? (isAniDb ? metadataService.GetShokoEpisodeByAnidbID(id) : metadataService.GetShokoEpisodeByID(id))?.Series?.ID ?? 0 : 0;
        }
        // Isolate the show component (supports {ID}, a{AniDB}, {ID}s{Season}, or a{AniDB}s{Season})
        var seriesPart = ratingKey.Split(PlexConstants.SeasonPrefix)[0];
        return seriesPart.StartsWith(PlexConstants.AniDbPrefix, StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(seriesPart[PlexConstants.AniDbPrefix.Length..], out var anidb)
                ? metadataService.GetShokoSeriesByAnidbID(anidb)?.ID ?? 0
                : 0
            : int.TryParse(seriesPart, out var sid)
                ? sid
                : 0;
    }

    /// <summary>Determines if a rating key represents an episode.</summary>
    /// <param name="ratingKey">The rating key to check.</param>
    /// <returns>True if the key represents an episode.</returns>
    public static bool IsEpisodeKey(string ratingKey) =>
        ratingKey.StartsWith(PlexConstants.EpisodePrefix, StringComparison.OrdinalIgnoreCase)
        || ratingKey.StartsWith(PlexConstants.AniDbPrefix + PlexConstants.EpisodePrefix, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Poster Discovery

    /// <summary>Locate a local custom image for a collection by matching against its name or ID across active roots, considering optional suffixes.</summary>
    /// <param name="series">Optional Shoko series to restrict the root path search to.</param>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="collectionId">The collection ID.</param>
    /// <param name="suffixes">The allowed filename suffixes for this image type.</param>
    /// <param name="metadataService">Metadata service used to resolve roots.</param>
    /// <param name="globalRoots">Optional pre-resolved list of active VFS root directories.</param>
    /// <returns>Path to poster file or null.</returns>
    public static string? FindCollectionImagePath(IShokoSeries? series, string collectionName, int collectionId, string[] suffixes, IMetadataService metadataService, IReadOnlyList<string>? globalRoots = null)
    {
        string? normalizedTitle = NormalizeCollectionKey(collectionName);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return null;

        List<string> roots =
            series != null ? [.. ResolveImportRoots(series, metadataService)]
            : globalRoots != null ? [.. globalRoots]
            : [.. (metadataService.GetAllShokoSeries() ?? []).SelectMany(s => ResolveImportRoots(s, metadataService)).Distinct(VfsShared.PathComparer)];

        if (roots.Count == 0)
            return null;

        int groupId = series?.TopLevelGroupID ?? 0;

        string postersFolderName = VfsShared.ResolveCollectionImagesFolderName();
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

                foreach (var suffix in suffixes)
                {
                    if (series != null)
                    {
                        if (IsIdMatch(baseName, groupId, suffix) || IsIdMatch(baseName, collectionId, suffix) || IsNameMatch(baseName, normalizedTitle, suffix))
                            return file;
                    }
                    else
                    {
                        if (IsNameMatch(baseName, normalizedTitle, suffix) || IsIdMatch(baseName, collectionId, suffix))
                            return file;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Locate a local collection image by matching against a Shoko group ID or group title.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="groupId">The Shoko group identifier.</param>
    /// <param name="suffix">Optional image type suffix (e.g. -logo, -backdrop, -square).</param>
    /// <param name="metadataService">Metadata service used for root and override resolution.</param>
    /// <returns>Path to poster or null.</returns>
    public static string? FindCollectionImagePathByGroup(IShokoSeries series, int groupId, string suffix, IMetadataService metadataService)
    {
        if (series == null || groupId <= 0)
            return null;
        var group = series.TopLevelGroup;
        string? groupTitle = NormalizeCollectionKey(group?.PreferredTitle?.Value ?? group?.DefaultTitle?.Value);
        var roots = ResolveImportRoots(series, metadataService);
        if (roots.Count == 0)
            return null;

        string postersFolderName = VfsShared.ResolveCollectionImagesFolderName();
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
                if (IsIdMatch(baseName, groupId, suffix))
                    return file;
                if (!string.IsNullOrWhiteSpace(groupTitle) && IsNameMatch(baseName, groupTitle, suffix))
                    return file;
            }
        }
        return null;
    }

    #endregion

    #region Path Resolution

    /// <summary>Determine the filesystem import roots for a series.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="metadataService">Metadata service used for root and override resolution.</param>
    /// <returns>Unique set of root paths.</returns>
    public static HashSet<string> ResolveImportRoots(IShokoSeries series, IMetadataService metadataService)
    {
        var roots = new HashSet<string>(VfsShared.PathComparer);
        foreach (var video in MapHelper.GetActiveVideos(series, metadataService))
        {
            foreach (var file in video.Files ?? [])
                if (file != null && VfsShared.ResolveImportRootPath(file) is string importRoot)
                    roots.Add(importRoot);
        }
        return roots;
    }

    #endregion

    #region URL Building

    /// <summary>Build a URL for serving a collection image (poster, logo, or backdrop).</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="collectionName">Collection name.</param>
    /// <param name="collectionId">Collection ID.</param>
    /// <param name="suffix">Optional image type suffix (e.g. -logo, -backdrop, -square).</param>
    /// <param name="suffixes">The prioritized array of all allowed local filename suffixes.</param>
    /// <param name="metadataService">Metadata service used for root and override resolution.</param>
    /// <param name="allowPrimarySeriesFallback">Whether to use primary series poster as fallback.</param>
    /// <param name="baseUrl">Base URL override.</param>
    /// <returns>URL string or null.</returns>
    public static string? GetCollectionImageUrl(
        IShokoSeries series,
        string collectionName,
        int collectionId,
        string suffix,
        string[] suffixes,
        IMetadataService metadataService,
        bool allowPrimarySeriesFallback = true,
        string? baseUrl = null
    )
    {
        if (series == null || metadataService == null)
            return null;
        var posterPath = FindCollectionImagePath(series, collectionName, collectionId, suffixes, metadataService);
        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            long ticks = 0;
            try
            {
                ticks = new FileInfo(posterPath).LastWriteTimeUtc.Ticks;
            }
            catch { }
            string b = string.IsNullOrWhiteSpace(baseUrl) ? ServerBaseUrl : baseUrl?.TrimEnd('/') ?? string.Empty;
            return $"{b}{ShokoRelayConstants.BasePath}/collections/user/{series.TopLevelGroupID}?suffix={suffix}&t={ticks}";
        }
        if (allowPrimarySeriesFallback && metadataService != null)
        {
            var group = metadataService.GetShokoGroupByID(series.TopLevelGroupID);
            var primarySeries = group?.MainSeries ?? group?.Series?.FirstOrDefault();
            if (primarySeries != null)
            {
                var imgType =
                    suffix is "-logo" or "-clearlogo" ? ImageEntityType.Logo
                    : suffix is "-art" or "-backdrop" or "-background" or "-fanart" ? ImageEntityType.Backdrop
                    : ImageEntityType.Primary;

                var posterImage = (primarySeries as IWithImages)?.GetAvailableImages(imgType).FirstOrDefault();
                if (posterImage != null)
                    return ImageHelper.GetImageUrl(posterImage);
            }
        }
        return null;
    }

    #endregion

    #region Internal Helpers

    private static bool IsIdMatch(string value, int id, string suffix)
    {
        if (id <= 0)
            return false;
        string s = value.Trim();
        if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            s = s[..^suffix.Length].Trim();
        else if (suffix != "")
            return false;
        return int.TryParse(s.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? s[1..] : s, out int parsed) && parsed == id;
    }

    private static bool IsNameMatch(string baseName, string normalizedTitle, string suffix)
    {
        if (!baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        string cleanedBase = baseName;
        if (suffix != "")
            cleanedBase = baseName[..^suffix.Length].Trim();
        return NormalizeCollectionKey(cleanedBase) == normalizedTitle;
    }

    private static string? NormalizeCollectionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = TextHelper.CondenseSpaces(new string([.. value.Where(c => !invalid.Contains(c))]).Trim());
        return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
    }

    #endregion
}

#region Webhook Models

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

        /// <summary>The current playback position in milliseconds.</summary>
        [JsonPropertyName("viewOffset")]
        public long? ViewOffset { get; set; }

        /// <summary>The total duration of the item in milliseconds.</summary>
        [JsonPropertyName("duration")]
        public long? Duration { get; set; }

        /// <summary>Library section numeric ID.</summary>
        [JsonPropertyName("librarySectionID")]
        public int? LibrarySectionId { get; set; }

        /// <summary>Metadata type string.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Rating value from 0-10.</summary>
        [JsonPropertyName("userRating")]
        public double? UserRating { get; set; }
    }
}

#endregion
