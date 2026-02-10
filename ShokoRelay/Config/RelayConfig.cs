using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config
{
    public static class ConfigConstants
    {
        public const string ConfigFileName = "ShokoRelayConfig.json";
        public const string SecretsFileName = "plex.token";
        public const string PluginSubfolder = "ShokoRelay";
    }

    public enum SummaryMode
    {
        FullySanitize = 0,
        AllowInfoLines = 1,
        AllowMiscLines = 2,
        AllowBoth = 3,
    }

    public enum AudienceRatingMode
    {
        AniDB = 0,
        TMDB = 1,
        None = 2,
    }

    public enum MinimumTagWeight
    {
        Zero = 0,
        OneHundred = 100,
        TwoHundred = 200,
        ThreeHundred = 300,
        FourHundred = 400,
        FiveHundred = 500,
        SixHundred = 600,
    }

    public class RelayConfig
    {
        [Display(Name = "Series Title Language", Description = "Priority, separated by a comma.")]
        [DefaultValue("SHOKO, X-JAT, EN")]
        public string SeriesTitleLanguage { get; set; } = "SHOKO, X-JAT, EN";

        [Display(Name = "Series Alt Title Language", Description = "Priority, separated by a comma.")]
        [DefaultValue("EN, X-JAT, SHOKO")]
        public string SeriesAltTitleLanguage { get; set; } = "EN, X-JAT, SHOKO";

        [Display(Name = "Episode Title Language", Description = "Priority, separated by a comma.")]
        [DefaultValue("SHOKO, EN, X-JAT")]
        public string EpisodeTitleLanguage { get; set; } = "SHOKO, EN, X-JAT";

        [Display(Name = "Move Common Series Title Prefixes", Description = "Enable to put 'OVA', etc. at the end of the series title.")]
        [DefaultValue(true)]
        public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

        [Display(Name = "Assumed Content Ratings", Description = "Enable to use content ratings derived from AniDB tags.")]
        [DefaultValue(true)]
        public bool AssumedContentRatings { get; set; } = true;

        [Display(Name = "Crew Listings", Description = "Enable staff listings in the Cast & Crew section.")]
        [DefaultValue(true)]
        public bool CrewListings { get; set; } = true;

        [Display(Name = "Collection Posters", Description = "Enable to use the primary series poster for collection posters.")]
        [DefaultValue(true)]
        public bool CollectionPosters { get; set; } = true;

        [Display(Name = "TMDB Episode Numbering", Description = "Enable to prefer TMDB episode numbering when available.")]
        [DefaultValue(true)]
        public bool TMDBEpNumbering { get; set; } = true;

        [Display(Name = "TMDB Episode Group Names", Description = "Enable to prefer TMDB titles for grouped episodes (fixes duped titles).")]
        [DefaultValue(true)]
        public bool TMDBEpGroupNames { get; set; } = true;

        [Display(Name = "TMDB Season Posters", Description = "Enable to use TMDB posters for multi-season series. [Not Implemented]")]
        [DefaultValue(true)]
        public bool TMDBSeasonPosters { get; set; } = true;

        [Display(Name = "TMDB Thumbnails", Description = "Enable to use TMDB episode thumbnails.")]
        [DefaultValue(false)]
        public bool TMDBThumbnails { get; set; } = false;

        [Display(Name = "Add Every Image", Description = "Enable to add all images instead of just the preferred one.")]
        [DefaultValue(false)]
        public bool AddEveryImage { get; set; } = false;

        [Display(Name = "Plex Theme Music", Description = "Enable to grab theme music files from Plex. [Not Implemented]")]
        [DefaultValue(true)]
        public bool PlexThemeMusic { get; set; } = false;

        [Display(Name = "Audience Rating Mode", Description = "Select the preferred source for audience ratings. [Not Implemented]")]
        [DefaultValue(AudienceRatingMode.AniDB)]
        public AudienceRatingMode AudienceRatingMode { get; set; } = AudienceRatingMode.AniDB;

        [Display(Name = "Summary Mode", Description = "Select the summary sanitization level.")]
        [DefaultValue(SummaryMode.FullySanitize)]
        public SummaryMode SummaryMode { get; set; } = SummaryMode.FullySanitize;

        [Display(Name = "Minimum Tag Weight", Description = "Select the minimum AniDB tag weight to apply to a series [Not Implemented]")]
        [DefaultValue(MinimumTagWeight.Zero)]
        public MinimumTagWeight MinimumTagWeight { get; set; } = MinimumTagWeight.Zero;

        [Display(Name = "Tag Blacklist", Description = "A list of tags to exclude from series, separated by a comma.")]
        [DefaultValue("")]
        public string TagBlacklist { get; set; } = "";

        [Display(Name = "Crossover Overrides", Description = "Crossover episodes to force map to a single series. {\"AniDBEpID\": AniDBSeriesID},")]
        public Dictionary<int, int> CrossoverOverrides { get; set; } = new() { { 146131, 8142 }, { 147453, 69 } };

        [Display(Name = "VFS Root Folder", Description = "The location of the virtual links inside each import root.")]
        [DefaultValue("!ShokoRelayVFS")]
        public string VfsRootPath { get; set; } = "!ShokoRelayVFS";

        [Display(Name = "Collection Posters Root Folder", Description = "The location of the local collection posters inside each import root.")]
        [DefaultValue("!CollectionPosters")]
        public string CollectionPostersRootFolder { get; set; } = "!CollectionPosters";

        [Display(Name = "Anime Themes Root Folder", Description = "The location of the AnimeThemes files inside each import root.")]
        [DefaultValue("!AnimeThemes")]
        public string AnimeThemesRootPath { get; set; } = "!AnimeThemes";

        [Display(Name = "Anime Themes Root Folder", Description = "The base path for the AnimeThemes files (used when generating mapping files).")]
        [DefaultValue("/animethemes/")]
        public string AnimeThemesPathMapping { get; set; } = "/animethemes/";

        [Display(Name = "FFmpeg Path", Description = "Optional folder containing FFmpeg/FFprobe. Leave empty to use plugin root or PATH.")]
        [DefaultValue("")]
        public string FFmpegPath { get; set; } = "";

        [Display(Name = "Plex Auth", Description = "Legacy authentication settings for Plex device registration.")]
        [Browsable(false)]
        public PlexAuthConfig PlexAuth { get; set; } = new();

        [Display(Name = "Plex Library", Description = "Plex server connection settings for refresh and collections.")]
        [Browsable(false)]
        public PlexLibraryConfig PlexLibrary { get; set; } = new();

        [Display(Name = "Shoko API Key", Description = "API key used to fetch season posters from a local Shoko server. Leave empty to disable.")]
        [Browsable(false)]
        [DefaultValue("")]
        public string ShokoApiKey { get; set; } = "";
    }

    public class RelaySecrets
    {
        public PlexAuthSecrets PlexAuth { get; set; } = new();

        public bool IsEmpty => PlexAuth.IsEmpty;
    }

    public class PlexAuthSecrets
    {
        public string ClientIdentifier { get; set; } = "";

        public bool IsEmpty => string.IsNullOrWhiteSpace(ClientIdentifier);
    }
}
