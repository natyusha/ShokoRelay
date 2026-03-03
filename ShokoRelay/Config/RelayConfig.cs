using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config
{
    /// <summary>
    /// Constants describing default file and folder names used by the configuration provider.
    /// </summary>
    public static class ConfigConstants
    {
        public const string ConfigFileName = "preferences.json";
        public const string SecretsFileName = "plex.token";
        public const string PluginSubfolder = "ShokoRelay";
        public const string ConfigSubfolder = "config";
    }

    /// <summary>
    /// Levels of sanitization to apply when transferring the series summary to Plex.
    /// </summary>
    public enum SummaryMode
    {
        [Display(Name = "Fully Sanitize")]
        FullySanitize = 0,

        [Display(Name = "Allow Info Lines")]
        AllowInfoLines = 1,

        [Display(Name = "Allow Misc. Lines")]
        AllowMiscLines = 2,

        [Display(Name = "Allow Both")]
        AllowBoth = 3,
    }

    /// <summary>
    /// Source to use for generic critic ratings applied to Plex metadata.
    /// </summary>
    public enum CriticRatingMode
    {
        AniDB = 0,
        TMDB = 1,
        None = 2,
    }

    /// <summary>
    /// Preferred source(s) for genre/tag information when populating Plex.
    /// </summary>
    public enum TagSources
    {
        Combined = 0,

        [Display(Name = "AniDB Only")]
        AniDB = 1,

        [Display(Name = "TMDB Only")]
        TMDB = 2,

        [Display(Name = "User Only")]
        UserOnly = 3,
    }

    /// <summary>
    /// AniDB tag weight thresholds used to filter which tags are exposed to Plex.
    /// </summary>
    public enum MinimumTagWeight
    {
        [Display(Name = "000 ❯ ☆☆☆")]
        Zero = 0,

        [Display(Name = "100 ❯ ⯪☆☆")]
        OneHundred = 100,

        [Display(Name = "200 ❯ ★☆☆")]
        TwoHundred = 200,

        [Display(Name = "300 ❯ ★⯪☆")]
        ThreeHundred = 300,

        [Display(Name = "400 ❯ ★★☆")]
        FourHundred = 400,

        [Display(Name = "500 ❯ ★★⯪")]
        FiveHundred = 500,

        [Display(Name = "600 ❯ ★★★")]
        SixHundred = 600,
    }

    /// <summary>
    /// Strongly typed representation of the user's configuration options that are persisted in <c>preferences.json</c>.
    /// </summary>
    public class RelayConfig
    {
        #region Automation Config

        [Display(Name = "Extra Plex Users", Description = "Comma-separated Plex usernames (stored in preferences.json)")]
        [Browsable(false)]
        [DefaultValue("")]
        public string ExtraPlexUsers { get; set; } = "";

        [Display(Name = "Plex Automation Frequency (hours)", Description = "Run Plex automation tasks every N hours. Set to 0 to disable")]
        [Browsable(false)]
        [DefaultValue(0)]
        public int PlexAutomationFrequencyHours { get; set; } = 0;

        [Browsable(false)]
        [Display(Name = "Scan On VFS Refresh", Description = "Trigger Plex library scans when the VFS is refreshed.")]
        [DefaultValue(false)]
        public bool ScanOnVfsRefresh { get; set; } = false;

        [Display(Name = "Auto Scrobble", Description = "Enable instant handling of Plex webhook events (media.scrobble and, if ratings included, media.rate)")]
        [Browsable(false)]
        [DefaultValue(false)]
        public bool AutoScrobble { get; set; } = false;

        [Display(Name = "UTC Offset Hours", Description = "Offset from UTC midnight used as the anchor for scheduling (-12 to +14)")]
        [Browsable(false)]
        [Range(-12, 14, ErrorMessage = "UTC Offset must be between -12 and +14")]
        [DefaultValue(0)]
        public int UtcOffsetHours { get; set; } = 0;

        [Display(Name = "Auto Import Frequency (hours)", Description = "Run Shoko import detection every N hours. Set to 0 to disable")]
        [Browsable(false)]
        [DefaultValue(0)]
        public int ShokoImportFrequencyHours { get; set; } = 0;

        [Display(Name = "Auto Sync Watched Frequency (hours)", Description = "Run watched-state sync every N hours. Set to 0 to disable")]
        [Browsable(false)]
        [DefaultValue(0)]
        public int ShokoSyncWatchedFrequencyHours { get; set; } = 0;

        [Display(Name = "Include Ratings for Scheduled Sync", Description = "When enabled, Plex->Shoko sync/scrobbles will also include user ratings/votes")]
        [Browsable(false)]
        [DefaultValue(false)]
        public bool ShokoSyncWatchedIncludeRatings { get; set; } = false;

        [Display(Name = "Exclude Admin for Scheduled Sync", Description = "When enabled, Plex->Shoko sync/scrobbles will ignore items scrobbled by the Plex token owner/admin")]
        [Browsable(false)]
        [DefaultValue(false)]
        public bool ShokoSyncWatchedExcludeAdmin { get; set; } = false;

        [Display(Name = "AnimeThemes MP3 Playback", Description = "Enable to play generated theme MP3 files in the dashboard after generation")]
        [Browsable(false)]
        [DefaultValue(false)]
        public bool AnimeThemesMp3Playback { get; set; } = false;

        [Display(Name = "AnimeThemes MP3 Loop", Description = "Playback mode for MP3 themes: loop, shuffle or off")]
        [Browsable(false)]
        [DefaultValue("loop")]
        public string AnimeThemesMp3Loop { get; set; } = "loop";

        #endregion

        #region Provider Config

        [Display(Name = "Series Title Language", Description = "Priority, comma separated")]
        [DefaultValue("SHOKO, X-JAT, EN")]
        public string SeriesTitleLanguage { get; set; } = "SHOKO, X-JAT, EN";

        [Display(Name = "Series Alt Title Language", Description = "Priority, comma separated")]
        [DefaultValue("EN, X-JAT, SHOKO")]
        public string SeriesAltTitleLanguage { get; set; } = "EN, X-JAT, SHOKO";

        [Display(Name = "Episode Title Language", Description = "Priority, comma separated")]
        [DefaultValue("SHOKO, EN, X-JAT")]
        public string EpisodeTitleLanguage { get; set; } = "SHOKO, EN, X-JAT";

        [Display(Name = "Move Common Series Title Prefixes", Description = "Enable to append 'Gekijouban', 'OVA', etc. to the end of the series title, after an em dash '—'")]
        [DefaultValue(true)]
        public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

        [Display(Name = "Assumed Content Ratings", Description = "Enable to use content ratings and descriptors that are derived from AniDB tags")]
        [DefaultValue(true)]
        public bool AssumedContentRatings { get; set; } = true;

        [Display(Name = "Crew Listings", Description = "Enable to include staff listings in Plex's Cast & Crew section")]
        [DefaultValue(true)]
        public bool CrewListings { get; set; } = true;

        [Display(Name = "Collection Posters", Description = "Enable to set the primary series poster in a Shoko group as Plex's collection poster")]
        [DefaultValue(true)]
        public bool CollectionPosters { get; set; } = true;

        [Display(Name = "Plex Theme Music", Description = "Enable to grab theme music files from Plex using TheTVDB IDs")]
        [DefaultValue(true)]
        public bool PlexThemeMusic { get; set; } = true;

        [Display(Name = "TMDB Episode Numbering", Description = "Enable to apply TMDB episode numbering to the provider and VFS *requires a VFS rebuild to change")]
        [DefaultValue(true)]
        public bool TmdbEpNumbering { get; set; } = true;

        [Display(Name = "TMDB Episode Group Names", Description = "Enable to prefer TMDB titles for grouped episodes, which often fixes duped titles")]
        [DefaultValue(true)]
        public bool TmdbEpGroupNames { get; set; } = true;

        [Display(Name = "TMDB Season Posters", Description = "Enable to use TMDB season posters for multi-season series")]
        [DefaultValue(true)]
        public bool TmdbSeasonPosters { get; set; } = true;

        [Display(Name = "TMDB Thumbnails", Description = "Enable to use TMDB episode thumbnails instead of the ones generated by Plex")]
        [DefaultValue(false)]
        public bool TmdbThumbnails { get; set; } = false;

        [Display(Name = "Add Every Image", Description = "Enable to add all images instead of Shoko's preferred one (seasons always do this)")]
        [DefaultValue(false)]
        public bool AddEveryImage { get; set; } = false;

        [Display(Name = "Summary Mode", Description = "Select the summary sanitization level")]
        [DefaultValue(SummaryMode.FullySanitize)]
        public SummaryMode SummaryMode { get; set; } = SummaryMode.FullySanitize;

        [Display(Name = "Critic Rating Mode", Description = "Select the preferred source for generic critic ratings in Plex *used by 'Apply Critic Ratings'")]
        [DefaultValue(CriticRatingMode.AniDB)]
        public CriticRatingMode CriticRatingMode { get; set; } = CriticRatingMode.AniDB;

        [Display(Name = "Tag Sources", Description = "Select the preferred source(s) for genres in Plex")]
        [DefaultValue(TagSources.Combined)]
        public TagSources TagSources { get; set; } = TagSources.Combined;

        [Display(Name = "Minimum Tag Weight", Description = "Select the minimum AniDB tag weight to apply to a series in Plex")]
        [DefaultValue(MinimumTagWeight.Zero)]
        public MinimumTagWeight MinimumTagWeight { get; set; } = MinimumTagWeight.Zero;

        [Display(Name = "Tag Blacklist", Description = "A list of tags to exclude from series in Plex, comma separated")]
        [DefaultValue("")]
        public string TagBlacklist { get; set; } = "";

        [Display(Name = "Path Mappings", Description = "Mappings for Plex base paths to Shoko base paths")]
        public Dictionary<string, string> PathMappings { get; set; } = new();

        [Display(Name = "VFS Root Path", Description = "The location of the virtual links inside each import root")]
        [DefaultValue("!ShokoRelayVFS")]
        public string VfsRootPath { get; set; } = "!ShokoRelayVFS";

        [Display(Name = "AnimeThemes Root Path", Description = "The location of AnimeThemes .webm files inside each import root")]
        [DefaultValue("!AnimeThemes")]
        public string AnimeThemesRootPath { get; set; } = "!AnimeThemes";

        [Display(Name = "Collection Posters Root Path", Description = "The location of custom local collection posters inside each import root")]
        [DefaultValue("!CollectionPosters")]
        public string CollectionPostersRootPath { get; set; } = "!CollectionPosters";

        [Display(Name = "FFmpeg Path", Description = "An optional folder containing FFmpeg/FFprobe. Leave empty to use the plugin root or PATH")]
        [DefaultValue("")]
        public string FFmpegPath { get; set; } = "";

        [Display(Name = "Parallelism", Description = "The maximum number of concurrent operations *used by VFS and AnimeThemes batch operations")]
        [DefaultValue(4)]
        public int Parallelism { get; set; } = 4;

        #endregion
    }

    /// <summary>
    /// Container for any secrets required by the plugin (currently only Plex auth info).
    /// </summary>
    public class RelaySecrets
    {
        public PlexAuthSecrets PlexAuth { get; set; } = new();

        public bool IsEmpty => PlexAuth.IsEmpty;
    }

    /// <summary>
    /// Plex-specific authentication secrets stored separately from user-visible preferences. Presently only the client identifier is required.
    /// </summary>
    public class PlexAuthSecrets
    {
        public string ClientIdentifier { get; set; } = "";

        public bool IsEmpty => string.IsNullOrWhiteSpace(ClientIdentifier);
    }
}
