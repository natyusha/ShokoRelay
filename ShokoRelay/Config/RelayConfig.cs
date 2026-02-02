using System.ComponentModel.DataAnnotations;
using System.ComponentModel;

namespace ShokoRelay.Config
{
    public class RelayConfig
    {
        [Display(Name = "Series Title Language", Description = "Languages separated by comma (e.g. SHOKO, X-JAT, EN).")]
        [DefaultValue("SHOKO, X-JAT, EN")]
        public string SeriesTitleLanguage { get; set; } = "SHOKO, X-JAT, EN";

        [Display(Name = "Series Alt Title Language", Description = "Languages separated by comma.")]
        [DefaultValue("EN, X-JAT, SHOKO")]
        public string SeriesAltTitleLanguage { get; set; } = "EN, X-JAT, SHOKO";

        [Display(Name = "Episode Title Language", Description = "Languages separated by comma.")]
        [DefaultValue("SHOKO, EN, X-JAT")]
        public string EpisodeTitleLanguage { get; set; } = "SHOKO, EN, X-JAT";

        [Display(Name = "TMDB Structure", Description = "If you want to use TMDB or AniDB episode numbering")]
        [DefaultValue(true)]
        public bool TMDBStructure { get; set; } = true;

        [Display(Name = "TMDB Episode Group Names", Description = "Prefer TMDB titles for grouped episodes.")]
        [DefaultValue(true)]
        public bool TMDBEpGroupNames { get; set; } = true;

        //[Display(Name = "TMDB Season Posters", Description = "Use TMDB posters for multi-season series.")]
        //[DefaultValue(true)]
        //public bool TMDBSeasonPosters { get; set; } = true;

        [Display(Name = "TMDB Thumbnails", Description = "Use TMDB episode thumbnails.")]
        [DefaultValue(false)]
        public bool TMDBThumbnails { get; set; } = false;

        [Display(Name = "Add Every Image", Description = "Add all images instead of just the preferred one.")]
        [DefaultValue(false)]
        public bool AddEveryImage { get; set; } = false;

        [Display(Name = "Content Ratings", Description = "Enable using assumed content ratings.")]
        [DefaultValue(true)]
        public bool ContentRatings { get; set; } = true;

        [Display(Name = "Crew Listings", Description = "Enable staff listings in Cast & Crew.")]
        [DefaultValue(true)]
        public bool CrewListings { get; set; } = true;

        [Display(Name = "Move Common Series Title Prefixes", Description = "Move 'Gekijouban' etc. to the end of the title.")]
        [DefaultValue(true)]
        public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

        //[Display(Name = "Theme Music", Description = "Grab theme music files for Plex.")]
        //[DefaultValue(true)]
        //public bool ThemeMusic { get; set; } = true;

        //[Display(Name = "Critic Ratings", Description = "Predefined values: AniDB, TMDB, Disabled")]
        //[DefaultValue("AniDB")]
        //public string CriticRatings { get; set; } = "AniDB";

        [Display(Name = "Sanitize Summary", Description = "1: Fully Sanitize, 2: Allow Info Lines, 3: Allow Misc. Lines, 4: Allow Both Types")]
        [DefaultValue(1)]
        public int SanitizeSummary { get; set; } = 1;

        //[Display(Name = "Minimum Tag Weight", Description = "Predefined values: 600, 500, 400, 300, 200, 100, 0")]
        //[DefaultValue("0")]
        //public int MinimumTagWeight { get; set; } = 0;

        [Display(Name = "Tag Blacklist", Description = "Tags to exclude, separated by a comma.")]
        [DefaultValue("")]
        public string TagBlacklist { get; set; } = "";
    }
}