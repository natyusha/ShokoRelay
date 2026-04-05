namespace ShokoRelay;

/// <summary>Centralized constants for plugin identity, task identifiers, logging, and system filenames.</summary>
public static class ShokoRelayConstants
{
    #region Plugin Identity

    /// <summary>Display name of the plugin.</summary>
    public const string Name = "Shoko Relay";

    /// <summary>Current version string.</summary>
    public const string Version = "0.10.4";

    /// <summary>Internal API version.</summary>
    public const string ApiVersion = "1";

    /// <summary>Plex agent URI scheme identifier.</summary>
    public const string AgentScheme = "tv.plex.agents.custom.shoko";

    /// <summary>Base HTTP path for plugin endpoints.</summary>
    public const string BasePath = "/api/plugin/ShokoRelay";

    #endregion

    #region Paths & Folders

    /// <summary>Plugin identifier used in filesystem paths.</summary>
    public const string FolderPluginSubfolder = "ShokoRelay";

    /// <summary>Directory name for configuration files.</summary>
    public const string FolderConfigSubfolder = "config";

    /// <summary>Default folder name for local collection posters.</summary>
    public const string FolderCollectionPostersDefault = "!CollectionPosters";

    /// <summary>Default folder name for the virtual filesystem root.</summary>
    public const string FolderVfsDefault = "!ShokoRelayVFS";

    /// <summary>Default folder name for the AnimeThemes local repository.</summary>
    public const string FolderAnimeThemesDefault = "!AnimeThemes";

    #endregion

    #region System Filenames

    /// <summary>Filename for user preferences.</summary>
    public const string FilePreferences = "preferences.json";

    /// <summary>Filename for the Plex token.</summary>
    public const string FilePlexToken = "plex.token";

    /// <summary>Filename for the VFS series overrides CSV.</summary>
    public const string FileVfsOverrides = "anidb_vfs_overrides.csv";

    /// <summary>Filename for the AnimeThemes cross-reference mapping CSV.</summary>
    public const string FileAtMapping = "anidb_animethemes_xrefs.csv";

    /// <summary>Filename for the Theme.mp3 folder cache.</summary>
    public const string FileAtMp3Cache = "mp3_animethemes.cache";

    /// <summary>Filename for the WebM VFS metadata cache.</summary>
    public const string FileAtWebmCache = "webm_animethemes.cache";

    /// <summary>Filename for the AnimeThemes favourites list.</summary>
    public const string FileAtFavsCache = "favs_animethemes.cache";

    #endregion

    #region Task Names

    /// <summary>Task name for creating and assigning Plex collections.</summary>
    public const string TaskPlexCollectionsBuild = "plex-collections-build";

    /// <summary>Task name for applying audience/critic ratings to Plex items.</summary>
    public const string TaskPlexRatingsApply = "plex-ratings-apply";

    /// <summary>Task name for building the standard Shoko VFS.</summary>
    public const string TaskVfsBuild = "vfs-build";

    /// <summary>Task name for identifying and removing missing files from the database.</summary>
    public const string TaskShokoRemoveMissing = "shoko-remove-missing";

    /// <summary>Task name for applying AnimeThemes mappings to the VFS.</summary>
    public const string TaskAtVfsBuild = "at-vfs-build";

    /// <summary>Task name for scanning files and building the AnimeThemes mapping CSV.</summary>
    public const string TaskAtMapBuild = "at-map-build";

    /// <summary>Task name for generating and applying series Theme.mp3 files.</summary>
    public const string TaskAtMp3Build = "at-mp3-build";

    #endregion

    #region Log Filenames

    /// <summary>Log filename for Plex collection build reports.</summary>
    public const string LogCollections = "collections-report.log";

    /// <summary>Log filename for Plex rating application reports.</summary>
    public const string LogRatings = "ratings-report.log";

    /// <summary>Log filename for VFS generation reports.</summary>
    public const string LogVfs = "vfs-report.log";

    /// <summary>Log filename for missing file removal reports.</summary>
    public const string LogRemoveMissing = "remove-missing-report.log";

    /// <summary>Log filename for watched state synchronization reports.</summary>
    public const string LogSyncWatched = "sync-watched-report.log";

    /// <summary>Log filename for AnimeThemes VFS application reports.</summary>
    public const string LogAtVfs = "at-vfs-report.log";

    /// <summary>Log filename for AnimeThemes mapping generation reports.</summary>
    public const string LogAtMap = "at-map-report.log";

    /// <summary>Log filename for AnimeThemes MP3 generation reports.</summary>
    public const string LogAtMp3 = "at-mp3-report.log";

    #endregion
}
