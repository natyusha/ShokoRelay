using System.Collections.Frozen;

namespace ShokoRelay.Plex
{
    /// <summary>Centralized Plex constants for type codes, rating key prefixes, and supported features.</summary>
    public static class PlexConstants
    {
        #region Rating Key Prefixes

        // csharpier-ignore-start
        /// <summary>Prefix for AniDB rating keys.</summary>
        public const string AniDbPrefix           = "a";
        /// <summary>Prefix for collection rating keys.</summary>
        public const string CollectionPrefix      = "c";
        /// <summary>Prefix for Plex smart collection rating keys.</summary>
        public const string SmartCollectionPrefix = "sc";
        /// <summary>Prefix for season rating keys.</summary>
        public const string SeasonPrefix          = "s";
        /// <summary>Prefix for episode rating keys.</summary>
        public const string EpisodePrefix         = "e";
        /// <summary>Prefix for multi-part file rating keys.</summary>
        public const string PartPrefix            = "p";

        #endregion

        #region Metadata Type IDs

        /// <summary>Plex metadata type ID for movies.</summary>
        public const int TypeMovie                =  1;
        /// <summary>Plex metadata type ID for shows.</summary>
        public const int TypeShow                 =  2;
        /// <summary>Plex metadata type ID for seasons.</summary>
        public const int TypeSeason               =  3;
        /// <summary>Plex metadata type ID for episodes.</summary>
        public const int TypeEpisode              =  4;
        /// <summary>Plex metadata type ID for trailers.</summary>
        public const int TypeTrailer              =  5;
        /// <summary>Plex metadata type ID for people.</summary>
        public const int TypePerson               =  7;
        /// <summary>Plex metadata type ID for artists.</summary>
        public const int TypeArtist               =  8;
        /// <summary>Plex metadata type ID for albums.</summary>
        public const int TypeAlbum                =  9;
        /// <summary>Plex metadata type ID for tracks.</summary>
        public const int TypeTrack                = 10;
        /// <summary>Plex metadata type ID for clips.</summary>
        public const int TypeClip                 = 12;
        /// <summary>Plex metadata type ID for photos.</summary>
        public const int TypePhoto                = 13;
        /// <summary>Plex metadata type ID for photo albums.</summary>
        public const int TypePhotoAlbum           = 14;
        /// <summary>Plex metadata type ID for playlists.</summary>
        public const int TypePlaylist             = 15;
        /// <summary>Plex metadata type ID for playlist folders.</summary>
        public const int TypePlaylistFolder       = 16;
        /// <summary>Plex metadata type ID for collections.</summary>
        public const int TypeCollection           = 18;

        #endregion

        #region Season Numbering

        /// <summary>Standard season number (1).</summary>
        public const int SeasonStandard           =  1;
        /// <summary>Specials season number (0).</summary>
        public const int SeasonSpecials           =  0;
        /// <summary>Credits season number (-1).</summary>
        public const int SeasonCredits            = -1;
        /// <summary>Trailers season number (-2).</summary>
        public const int SeasonTrailers           = -2;
        /// <summary>Parody season number (-3).</summary>
        public const int SeasonParody             = -3;
        /// <summary>Other season number (-4).</summary>
        public const int SeasonOther              = -4;
        /// <summary>Unknown season number (-9).</summary>
        public const int SeasonUnknown            = -9;
        // csharpier-ignore-end

        #endregion

        #region Subtypes & Extras

        /// <summary>Optional subtype lists (for reference / validation).</summary>
        public static readonly string[] CollectionSubtypes = ["movie", "show", "artist", "album"];

        /// <summary>Subfolder names recognized by Plex for show and season-level extras.</summary>
        public static readonly string[] LocalExtraDirs = ["Behind The Scenes", "Deleted Scenes", "Featurettes", "Interviews", "Scenes", "Shorts", "Trailers", "Other"];

        /// <summary>Filename suffixes recognized by Plex for episode-level inline extras.</summary>
        public static readonly string[] LocalExtraSuffixes = ["-behindthescenes", "-deleted", "-featurette", "-interview", "-scene", "-short", "-trailer", "-other"];

        /// <summary>Extra buckets used only when no TMDB match is present.</summary>
        public static readonly IReadOnlyDictionary<int, (string Folder, string Subtype)> ExtraSeasons = new Dictionary<int, (string Folder, string Subtype)>
        {
            { SeasonCredits, ("Shorts", "short") },
            { SeasonTrailers, ("Trailers", "trailer") },
            { SeasonParody, ("Scenes", "sceneOrSample") },
            { SeasonOther, ("Featurettes", "featurette") },
            { SeasonUnknown, ("Other", "other") },
        };

        #endregion

        #region Local Media Assets

        /// <summary>Extension-set lookup for recognized artwork file types.</summary>
        public static class LocalMediaAssets
        {
            /// <summary>Image file extensions considered local artwork by Plex.</summary>
            public static readonly FrozenSet<string> Artwork = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".bmp",
                ".gif",
                ".jpe",
                ".jpeg",
                ".jpg",
                ".png",
                ".tbn",
                ".tif",
                ".tiff",
                ".webp",
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

            /// <summary>Series level metadata including: Audio extensions that Plex treats as theme songs and NFO Metadata files.</summary>
            public static readonly FrozenSet<string> SeriesMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".nfo" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

            /// <summary>Episode level sidecar files including: Text-based subtitle extensions supported by Plex and NFO Metadata files.</summary>
            public static readonly FrozenSet<string> EpisodeMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".smi", ".ssa", ".ass", ".vtt", ".nfo" }.ToFrozenSet(
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>Represents a configuration for a specific collection artwork type.</summary>
        /// <param name="Prefix">The Plex metadata sub-endpoint (e.g. posters, arts, clearLogos, squareArts).</param>
        /// <param name="Suffix">The representative hyphenated suffix used in the URL.</param>
        /// <param name="Suffixes">The prioritized array of all allowed local filename suffixes.</param>
        /// <param name="Label">The diagnostic label for logging.</param>
        /// <param name="DefaultFallback">Whether Shoko fallback images are allowed for this type.</param>
        public sealed record CollectionImageConfig(string Prefix, string Suffix, string[] Suffixes, string Label, bool DefaultFallback);

        /// <summary>The global prioritized list of supported collection artwork configurations.</summary>
        public static readonly IReadOnlyList<CollectionImageConfig> CollectionImageConfigs =
        [
            new("posters", "", ["-folder", "-poster", "-show", ""], "poster", true),
            new("arts", "-backdrop", ["-art", "-backdrop", "-background", "-fanart"], "backdrop", true),
            new("clearLogos", "-logo", ["-clearlogo", "-logo"], "logo", true),
            new("squareArts", "-square", ["-square", "-squareArt", "-backgroundSquare"], "square art", false),
        ];

        #endregion
    }
}
