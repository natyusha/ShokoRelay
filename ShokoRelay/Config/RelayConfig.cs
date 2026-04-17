using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config;

#region Enum Definitions

/// <summary>Levels of sanitization to apply when transferring the series summary to Plex.</summary>
public enum SummaryMode
{
    /// <summary>Strip all notes and indicators.</summary>
    [Display(Name = "Fully Sanitize")]
    FullySanitize = 0,

    /// <summary>Allow info lines.</summary>
    [Display(Name = "Allow Info Lines")]
    AllowInfoLines = 1,

    /// <summary>Allow misc lines.</summary>
    [Display(Name = "Allow Misc. Lines")]
    AllowMiscLines = 2,

    /// <summary>Allow both info and misc lines.</summary>
    [Display(Name = "Allow Both")]
    AllowBoth = 3,
}

/// <summary>Source to use for generic critic ratings applied to Plex metadata.</summary>
public enum CriticRatingMode
{
    /// <summary>Use AniDB rating.</summary>
    AniDB = 0,

    /// <summary>Use TMDB rating.</summary>
    TMDB = 1,

    /// <summary>Do not apply ratings.</summary>
    None = 2,
}

/// <summary>Preferred source(s) for genre/tag information when populating Plex.</summary>
public enum TagSources
{
    /// <summary>Combine all sources.</summary>
    Combined = 0,

    /// <summary>Use AniDB tags only.</summary>
    [Display(Name = "AniDB Only")]
    AniDB = 1,

    /// <summary>Use TMDB genres/keywords only.</summary>
    [Display(Name = "TMDB Only")]
    TMDB = 2,

    /// <summary>Use Shoko custom tags only.</summary>
    [Display(Name = "User Only")]
    UserOnly = 3,
}

/// <summary>AniDB tag weight thresholds used to filter tags exposed to Plex.</summary>
public enum MinimumTagWeight
{
    /// <summary>Weight 0.</summary>
    [Display(Name = "000 ❯ ☆☆☆")]
    Zero = 0,

    /// <summary>Weight 100.</summary>
    [Display(Name = "100 ❯ ⯪☆☆")]
    OneHundred = 100,

    /// <summary>Weight 200.</summary>
    [Display(Name = "200 ❯ ★☆☆")]
    TwoHundred = 200,

    /// <summary>Weight 300.</summary>
    [Display(Name = "300 ❯ ★⯪☆")]
    ThreeHundred = 300,

    /// <summary>Weight 400.</summary>
    [Display(Name = "400 ❯ ★★☆")]
    FourHundred = 400,

    /// <summary>Weight 500.</summary>
    [Display(Name = "500 ❯ ★★⯪")]
    FiveHundred = 500,

    /// <summary>Weight 600.</summary>
    [Display(Name = "600 ❯ ★★★")]
    SixHundred = 600,
}

/// <summary>Levels of overlap allowed for AnimeThemes .webm files.</summary>
public enum OverlapLevel
{
    /// <summary>Allow all overlaps.</summary>
    [Display(Name = "All Overlaps Allowed")]
    All = 0,

    /// <summary>Only allow transitional overlaps.</summary>
    [Display(Name = "Transitional Overlaps Allowed")]
    TransitionOnly = 1,

    /// <summary>Only allow non-overlapped themes.</summary>
    [Display(Name = "No Overlaps Allowed")]
    None = 2,
}

#endregion

#region Provider Config

/// <summary>Main configuration class representing options stored in preferences.json.</summary>
public class RelayConfig
{
    /// <summary>Automation settings.</summary>
    public AutomationConfig Automation { get; set; } = new();

    /// <summary>Playback and UI settings.</summary>
    public PlaybackConfig Playback { get; set; } = new();

    /// <summary>Advanced system settings.</summary>
    public AdvancedConfig Advanced { get; set; } = new();

    /// <summary>Priority list of languages for series titles.</summary>
    [Display(Name = "Series Title Language", Description = "Priority, comma separated")]
    [DefaultValue("SHOKO, X-JAT, EN")]
    public string SeriesTitleLanguage { get; set; } = "SHOKO, X-JAT, EN";

    /// <summary>Priority list of languages for alternate series titles.</summary>
    [Display(Name = "Series Alt Title Language", Description = "Priority, comma separated *this field is searchable in Plex")]
    [DefaultValue("EN, X-JAT, SHOKO")]
    public string SeriesAltTitleLanguage { get; set; } = "EN, X-JAT, SHOKO";

    /// <summary>Priority list of languages for episode titles.</summary>
    [Display(Name = "Episode Title Language", Description = "Priority, comma separated")]
    [DefaultValue("SHOKO, EN, X-JAT")]
    public string EpisodeTitleLanguage { get; set; } = "SHOKO, EN, X-JAT";

    /// <summary>Priority list of languages for series descriptions.</summary>
    [Display(Name = "Series Description Language", Description = "Priority, comma separated")]
    [Browsable(false)]
    [DefaultValue("SHOKO")]
    public string SeriesDescriptionLanguage { get; set; } = "SHOKO";

    /// <summary>Priority list of languages for episode descriptions.</summary>
    [Display(Name = "Episode Description Language", Description = "Priority, comma separated")]
    [Browsable(false)]
    [DefaultValue("SHOKO")]
    public string EpisodeDescriptionLanguage { get; set; } = "SHOKO";

    /// <summary>Whether to append prefixes like OVA to the end of titles.</summary>
    [Display(Name = "Move Common Series Title Prefixes", Description = "Enable to append 'Gekijouban', 'OVA', etc. to the end of the series title, after an em dash '—'")]
    [DefaultValue(true)]
    public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

    /// <summary>Whether to derive ratings and descriptors from AniDB tags.</summary>
    [Display(Name = "Assumed Content Ratings", Description = "Enable to use content ratings and descriptors that are derived from AniDB tags")]
    [DefaultValue(true)]
    public bool AssumedContentRatings { get; set; } = true;

    /// <summary>Whether to include staff in Plex's cast list.</summary>
    [Display(Name = "Crew Listings", Description = "Enable to include staff listings in Plex's Cast & Crew section")]
    [DefaultValue(true)]
    public bool CrewListings { get; set; } = true;

    /// <summary>Whether to set the group poster as the Plex collection poster.</summary>
    [Display(Name = "Collection Posters", Description = "Enable to set the primary series poster in a Shoko group as Plex's collection poster")]
    [DefaultValue(true)]
    public bool CollectionPosters { get; set; } = true;

    /// <summary>Whether to grab theme music from Plex servers.</summary>
    [Display(Name = "Plex Theme Music", Description = "Enable to grab theme music files from Plex using TheTVDB IDs")]
    [DefaultValue(true)]
    public bool PlexThemeMusic { get; set; } = true;

    /// <summary>Whether to apply TMDB numbering to the VFS and metadata.</summary>
    [Display(Name = "TMDB Episode Numbering", Description = "Enable to apply TMDB episode numbering to the provider and VFS *requires a VFS rebuild to change")]
    [DefaultValue(true)]
    public bool TmdbEpNumbering { get; set; } = true;

    /// <summary>Whether to prefer TMDB titles for grouped episodes.</summary>
    [Display(Name = "TMDB Episode Group Names", Description = "Enable to prefer TMDB titles for grouped episodes, which often fixes duped titles")]
    [DefaultValue(true)]
    public bool TmdbEpGroupNames { get; set; } = true;

    /// <summary>Whether to use TMDB season-level posters.</summary>
    [Display(Name = "TMDB Season Posters", Description = "Enable to use TMDB season posters for multi-season series")]
    [DefaultValue(true)]
    public bool TmdbSeasonPosters { get; set; } = true;

    /// <summary>Whether to use TMDB episode thumbnails.</summary>
    [Display(Name = "TMDB Thumbnails", Description = "Enable to use TMDB episode thumbnails instead of the ones generated by Plex")]
    [DefaultValue(false)]
    public bool TmdbThumbnails { get; set; } = false;

    /// <summary>Whether to provide all images to Plex or just the preferred ones.</summary>
    [Display(Name = "Add Every Image", Description = "Enable to add all images instead of Shoko's preferred one (seasons always do this)")]
    [DefaultValue(false)]
    public bool AddEveryImage { get; set; } = false;

    /// <summary>The summary sanitization level.</summary>
    [Display(Name = "Summary Mode", Description = "Select the summary sanitization level")]
    [DefaultValue(SummaryMode.FullySanitize)]
    public SummaryMode SummaryMode { get; set; } = SummaryMode.FullySanitize;

    /// <summary>The source for critic ratings.</summary>
    [Display(Name = "Critic Rating Mode", Description = "Select the preferred source for generic critic ratings in Plex *used by 'Apply Critic Ratings'")]
    [DefaultValue(CriticRatingMode.AniDB)]
    public CriticRatingMode CriticRatingMode { get; set; } = CriticRatingMode.AniDB;

    /// <summary>The source(s) for genre tags.</summary>
    [Display(Name = "Tag Sources", Description = "Select the preferred source(s) for genres in Plex")]
    [DefaultValue(TagSources.Combined)]
    public TagSources TagSources { get; set; } = TagSources.Combined;

    /// <summary>The threshold weight for AniDB tags.</summary>
    [Display(Name = "Minimum Tag Weight", Description = "Select the minimum AniDB tag weight to apply to a series in Plex")]
    [DefaultValue(MinimumTagWeight.Zero)]
    public MinimumTagWeight MinimumTagWeight { get; set; } = MinimumTagWeight.Zero;

    /// <summary>Comma-separated list of tags to exclude.</summary>
    [Display(Name = "Tag Blacklist", Description = "A list of tags to exclude from series in Plex, comma separated")]
    [DefaultValue("")]
    public string TagBlacklist { get; set; } = "";
}

#endregion

#region Automation Config

/// <summary>Settings related to automated tasks and synchronization.</summary>
public class AutomationConfig
{
    /// <summary>Additional Plex usernames for scrobble handling.</summary>
    [Display(Name = "Extra Plex Users", Description = "Comma-separated Plex usernames (stored in preferences.json)")]
    [Browsable(false)]
    [DefaultValue("")]
    public string ExtraPlexUsers { get; set; } = "";

    /// <summary>Frequency for Plex metadata automation.</summary>
    [Display(Name = "Plex Automation Frequency (hours)", Description = "Run Plex automation tasks every N hours. Set to 0 to disable")]
    [Range(1, 168, ErrorMessage = "Plex Automation Frequency must be between 0 and 168")]
    [Browsable(false)]
    [DefaultValue(0)]
    public int PlexAutomationFrequencyHours { get; set; } = 0;

    /// <summary>Whether to trigger Plex scans on VFS changes.</summary>
    [Browsable(false)]
    [Display(Name = "Scan On VFS Refresh", Description = "Trigger Plex library scans when the VFS is refreshed.")]
    [DefaultValue(false)]
    public bool ScanOnVfsRefresh { get; set; } = false;

    /// <summary>Whether to handle Plex scrobble webhooks.</summary>
    [Display(Name = "Auto Scrobble", Description = "Enable instant handling of Plex webhook events (media.scrobble and, if ratings included, media.rate)")]
    [Browsable(false)]
    [DefaultValue(false)]
    public bool AutoScrobble { get; set; } = false;

    /// <summary>Anchor hour for scheduling.</summary>
    [Display(Name = "UTC Offset Hours", Description = "Offset from UTC midnight used as the anchor for scheduling (-12 to +14)")]
    [Range(-12, 14, ErrorMessage = "UTC Offset must be between -12 and +14")]
    [Browsable(false)]
    [DefaultValue(0)]
    public int UtcOffsetHours { get; set; } = 0;

    /// <summary>Frequency for Shoko import detection.</summary>
    [Display(Name = "Auto Import Frequency (hours)", Description = "Run Shoko import detection every N hours. Set to 0 to disable")]
    [Range(0, 24, ErrorMessage = "Auto Import Frequency must be between 0 and 24")]
    [Browsable(false)]
    [DefaultValue(0)]
    public int ShokoImportFrequencyHours { get; set; } = 0;

    /// <summary>Frequency for watched-state synchronization.</summary>
    [Display(Name = "Auto Sync Watched Frequency (hours)", Description = "Run watched-state sync every N hours. Set to 0 to disable")]
    [Range(0, 168, ErrorMessage = "Auto Sync Watched Frequency must be between 0 and 168")]
    [Browsable(false)]
    [DefaultValue(0)]
    public int ShokoSyncWatchedFrequencyHours { get; set; } = 0;

    /// <summary>Whether to include ratings in sync tasks.</summary>
    [Display(Name = "Include Ratings for Scheduled Sync", Description = "When enabled, Plex->Shoko sync/scrobbles will also include user ratings/votes")]
    [Browsable(false)]
    [DefaultValue(false)]
    public bool ShokoSyncWatchedIncludeRatings { get; set; } = false;

    /// <summary>Whether to ignore the Plex admin user during sync.</summary>
    [Display(Name = "Exclude Admin for Scheduled Sync", Description = "When enabled, Plex->Shoko sync/scrobbles will ignore items scrobbled by the Plex token owner/admin")]
    [Browsable(false)]
    [DefaultValue(false)]
    public bool ShokoSyncWatchedExcludeAdmin { get; set; } = false;
}

#endregion

#region Playback Config

/// <summary>Settings related to dashboard playback functionality.</summary>
public class PlaybackConfig
{
    /// <summary>Playback mode for WebM themes.</summary>
    [Display(Name = "AnimeThemes WEBM Mode", Description = "Playback mode for WEBM themes: loop, shuffle, next or off")]
    [Browsable(false)]
    [DefaultValue("loop")]
    public string AnimeThemesWebmMode { get; set; } = "loop";

    /// <summary>Whether to automatically play generated MP3s.</summary>
    [Display(Name = "AnimeThemes MP3 Playback", Description = "Enable to play generated theme MP3 files in the dashboard after generation")]
    [Browsable(false)]
    [DefaultValue(false)]
    public bool AnimeThemesMp3Playback { get; set; } = false;

    /// <summary>Playback mode for generated MP3 themes.</summary>
    [Display(Name = "AnimeThemes MP3 Mode", Description = "Playback mode for MP3 themes: loop, shuffle or off")]
    [Browsable(false)]
    [DefaultValue("loop")]
    public string AnimeThemesMp3Mode { get; set; } = "loop";
}

#endregion

#region Advanced Config

/// <summary>Low-level path and system configuration.</summary>
public class AdvancedConfig
{
    /// <summary>The base Shoko server URL.</summary>
    [Display(Name = "Shoko Server URL", Description = "A URL and port that Plex can access Shoko server from (e.g. http://localhost:8111)")]
    [RegularExpression(@"^https?://[a-zA-Z0-9.-]+(:\d+)?$", ErrorMessage = "Invalid URL format. Use http(s)://HOST:PORT")]
    [DefaultValue("")]
    public string ShokoServerUrl { get; set; } = "";

    /// <summary>Directory mappings between Shoko and Plex.</summary>
    [Display(Name = "Path Mappings", Description = "Mappings for Plex base paths to Shoko base paths. Enter one mapping per line")]
    public Dictionary<string, string> PathMappings { get; set; } = [];

    /// <summary>Folders to ignore when generating the VFS.</summary>
    [Display(Name = "Folder Exclusions", Description = "Folders within Shoko destinations which you do not want VFS generation to consider. One per line")]
    [DefaultValue("")]
    public string FolderExclusions { get; set; } = "";

    /// <summary>Whether to append metadata tags to AnimeThemes filenames.</summary>
    [Display(Name = "Append AnimeThemes Tags", Description = "Enable to append attributes like [SPOIL, SUBS] to AnimeThemes VFS filenames (extra names in Plex are the same)")]
    [DefaultValue(true)]
    public bool AnimeThemesAppendTags { get; set; } = true;

    /// <summary>Whether to include duplicate AnimeThemes entries when a version with no credits exists.</summary>
    [Display(Name = "Prefer NC AnimeThemes Entries", Description = "Enable to remove regular OP/ED entries if the exact same 'No Credits' version exists")]
    [DefaultValue(true)]
    public bool AnimeThemesPreferNc { get; set; } = true;

    /// <summary>Minimum overlap level for AnimeThemes.</summary>
    [Display(Name = "AnimeThemes Overlap Level", Description = "The amount of overlap allowed for AnimeThemes .webm files to be added to the VFS")]
    [DefaultValue(OverlapLevel.All)]
    public OverlapLevel AnimeThemesOverlapLevel { get; set; } = OverlapLevel.All;

    /// <summary>Folder name for the ShokoRelay VFS root.</summary>
    [Display(Name = "VFS Root Path", Description = "The location of the virtual links inside each import root")]
    [DefaultValue(ShokoRelayConstants.FolderVfsDefault)]
    public string VfsRootPath { get; set; } = ShokoRelayConstants.FolderVfsDefault;

    /// <summary>Folder name for downloaded themes.</summary>
    [Display(Name = "AnimeThemes Root Path", Description = "The location of AnimeThemes .webm files inside each import root")]
    [DefaultValue(ShokoRelayConstants.FolderAnimeThemesDefault)]
    public string AnimeThemesRootPath { get; set; } = ShokoRelayConstants.FolderAnimeThemesDefault;

    /// <summary>Folder name for custom collection posters.</summary>
    [Display(Name = "Collection Posters Root Path", Description = "The location of custom local collection posters inside each import root")]
    [DefaultValue(ShokoRelayConstants.FolderCollectionPostersDefault)]
    public string CollectionPostersRootPath { get; set; } = ShokoRelayConstants.FolderCollectionPostersDefault;

    /// <summary>Path to FFmpeg binaries.</summary>
    [Display(Name = "FFmpeg Path", Description = "An optional folder containing FFmpeg/FFprobe. Leave empty to use the plugin root or PATH")]
    [DefaultValue("")]
    public string FFmpegPath { get; set; } = "";

    /// <summary>Plex RefreshMetadataAsync delay.</summary>
    [Display(Name = "Plex Fixup Delay", Description = "The delay (in minutes) after the VFS adds a file to regenerate it and force refresh series metadata")]
    [Range(1, 60, ErrorMessage = "Plex Fixup Delay must be between 1 and 60")]
    [DefaultValue(2)]
    public int PlexFixupDelay { get; set; } = 2;

    /// <summary>Plex partial scan delay.</summary>
    [Display(Name = "Plex Scan Delay", Description = "The delay (in seconds) after the VFS adds a file to trigger a partial library scan in Plex")]
    [Range(1, 60, ErrorMessage = "Plex Scan Delay must be between 1 and 60")]
    [DefaultValue(5)]
    public int PlexScanDelay { get; set; } = 5;

    /// <summary>Task parallelism limit.</summary>
    [Display(Name = "Parallelism", Description = "The maximum number of concurrent operations *used by VFS and AnimeThemes batch operations")]
    [Range(1, 16, ErrorMessage = "Parallelism must be between 1 and 16")]
    [DefaultValue(4)]
    public int Parallelism { get; set; } = 4;
}

#endregion

#region Secrets

/// <summary>Container for sensitive authentication data.</summary>
public class RelaySecrets
{
    /// <summary>Plex authentication secrets.</summary>
    public PlexAuthSecrets PlexAuth { get; set; } = new();

    /// <summary>Whether the secrets container is empty.</summary>
    public bool IsEmpty => PlexAuth.IsEmpty;
}

/// <summary>Authentication data for Plex.tv.</summary>
public class PlexAuthSecrets
{
    /// <summary>Plex client identifier.</summary>
    public string ClientIdentifier { get; set; } = "";

    /// <summary>Whether the secrets are empty.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(ClientIdentifier);
}

#endregion
