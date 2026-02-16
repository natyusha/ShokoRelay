namespace ShokoRelay.Plex
{
    /// <summary>
    /// Centralized Plex constants for type codes, rating key prefixes, and supported features.
    /// </summary>
    public static class PlexConstants
    {
        // csharpier-ignore-start
        // Rating key / guid prefixes
        public const string CollectionPrefix    = "c";
        public const string SeasonPrefix        = "s";
        public const string EpisodePrefix       = "e";
        public const string PartPrefix          = "p";

        // Metadata type numbers (per Plex API documentation)
        public const int TypeMovie              =  1;
        public const int TypeShow               =  2;
        public const int TypeSeason             =  3;
        public const int TypeEpisode            =  4;
        public const int TypeTrailer            =  5;
        public const int TypePerson             =  7;
        public const int TypeArtist             =  8;
        public const int TypeAlbum              =  9;
        public const int TypeTrack              = 10;
        public const int TypeClip               = 12;
        public const int TypePhoto              = 13;
        public const int TypePhotoAlbum         = 14;
        public const int TypePlaylist           = 15;
        public const int TypePlaylistFolder     = 16;
        public const int TypeCollection         = 18;

        // Extra season numbers for episode types
        public const int SeasonStandard         =  1;
        public const int SeasonSpecials         =  0;
        public const int SeasonCredits          = -1;
        public const int SeasonTrailers         = -2;
        public const int SeasonParody           = -3;
        public const int SeasonOther            = -4;
        public const int SeasonUnknown          = -9;
        // csharpier-ignore-end

        // Optional subtype lists (for reference / validation)
        public static readonly string[] CollectionSubtypes = { "movie", "show", "artist", "album" };
        public static readonly string[] ExtrasSubtypes =
        {
            "trailer",
            "deletedScene",
            "interview",
            "musicVideo",
            "behindTheScenes",
            "sceneOrSample",
            "liveMusicVideo",
            "lyricMusicVideo",
            "concert",
            "featurette",
            "short",
            "other",
        };

        // Extra buckets used only when no TMDB match is present (and other episodes don't have an empty season to fallback to - Includes Plex extras subtype to match the Plex API spec
        public static readonly IReadOnlyDictionary<int, (string Folder, string Subtype)> ExtraSeasons = new Dictionary<int, (string Folder, string Subtype)>
        {
            { SeasonCredits, ("Shorts", "short") },
            { SeasonTrailers, ("Trailers", "trailer") },
            { SeasonParody, ("Scenes", "sceneOrSample") },
            { SeasonOther, ("Featurettes", "featurette") },
            { SeasonUnknown, ("Other", "other") },
        };

        // Local media asset extensions for VFS linking
        public static class LocalMediaAssets
        {
            public static readonly HashSet<string> Artwork = new(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".jpe", ".jpeg", ".jpg", ".png", ".tbn", ".tif", ".tiff", ".webp" };

            public static readonly HashSet<string> ThemeSongs = new(StringComparer.OrdinalIgnoreCase) { ".mp3" };

            public static readonly HashSet<string> Subtitles = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".smi", ".ssa", ".ass", ".vtt" };
        }
    }
}
