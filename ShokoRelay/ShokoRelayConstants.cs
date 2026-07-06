namespace ShokoRelay;

/// <summary>Centralized constants for plugin identity, task identifiers, logging, and system filenames.</summary>
public static class ShokoRelayConstants
{
    #region Plugin Identity

    /// <summary>Display name of the plugin.</summary>
    public const string Name = "Shoko Relay";

    /// <summary>Description of the plugin.</summary>
    public const string Description = "A custom metadata provider and automation toolset for integrating Plex and AnimeThemes with Shoko Server.";

    /// <summary>Current version string.</summary>
    public const string Version = "0.16.2";

    /// <summary>Internal API version.</summary>
    public const string ApiVersion = "1";

    /// <summary>Unique plugin ID used for configuration storage.</summary>
    public const string PluginId = "2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a";

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

    /// <summary>Default folder name for the AnimeThemes local repository.</summary>
    public const string FolderAnimeThemesDefault = "!AnimeThemes";

    /// <summary>Default folder name for local collection posters.</summary>
    public const string FolderCollectionImagesDefault = "!CollectionImages";

    /// <summary>Default folder name for the virtual filesystem root.</summary>
    public const string FolderVfsDefault = "!ShokoRelayVFS";

    #endregion

    #region System Filenames

    /// <summary>Filename for user preferences.</summary>
    public const string FilePreferences = "preferences.json";

    /// <summary>Filename for the AnimeThemes cross-reference mapping CSV.</summary>
    public const string FileAtMapping = "anidb_animethemes_xrefs.csv";

    /// <summary>Filename for the Theme.mp3 folder cache.</summary>
    public const string FileAtMp3Cache = "mp3_animethemes.cache";

    /// <summary>Filename for the WebM VFS metadata cache.</summary>
    public const string FileAtWebmCache = "webm_animethemes.cache";

    /// <summary>Filename for the AnimeThemes favourites list.</summary>
    public const string FileAtFavsCache = "favs_animethemes.cache";

    /// <summary>Filename for the Plex token.</summary>
    public const string FilePlexToken = "plex.token";

    /// <summary>Filename for the Plex-generated episode image sync cache.</summary>
    public const string FilePlexImagesCache = "images_shokorelay.cache";

    /// <summary>Filename for the VFS series overrides CSV.</summary>
    public const string FileVfsOverrides = "anidb_vfs_overrides.csv";

    /// <summary>Filename for the VFS structure blueprint cache.</summary>
    public const string FileVfsBlueprintCache = "vfs_blueprint.cache";

    #endregion

    #region Task Names

    /// <summary>Task name for applying AnimeThemes mappings to the VFS.</summary>
    public const string TaskAtVfsBuild = "at-vfs-build";

    /// <summary>Task name for scanning files and building the AnimeThemes mapping CSV.</summary>
    public const string TaskAtMapBuild = "at-map-build";

    /// <summary>Task name for generating and applying series Theme.mp3 files.</summary>
    public const string TaskAtMp3Build = "at-mp3-build";

    /// <summary>Task name for auditing existing Theme.mp3 files for available OP upgrades.</summary>
    public const string TaskAtMp3Audit = "at-mp3-audit";

    /// <summary>Task name for downloading WebM files directly.</summary>
    public const string TaskAtWebmDownload = "at-webm-download";

    /// <summary>Task name for refreshing Plex library discovery.</summary>
    public const string TaskPlexAuthRefresh = "plex-auth-refresh";

    /// <summary>Task name for creating and assigning Plex collections.</summary>
    public const string TaskPlexCollectionsBuild = "plex-collections-build";

    /// <summary>Task name for applying audience/critic ratings to Plex items.</summary>
    public const string TaskPlexRatingsApply = "plex-ratings-apply";

    /// <summary>Task name for synchronizing Plex-generated episode screenshots back to Shoko.</summary>
    public const string TaskPlexImagesSync = "plex-images-sync";

    /// <summary>Task name for running full Plex metadata automation.</summary>
    public const string TaskPlexAutomationRun = "plex-automation-run";

    /// <summary>Task name for building the standard Shoko VFS.</summary>
    public const string TaskVfsBuild = "shoko-vfs-build";

    /// <summary>Task name for auditing the VFS for broken symlinks and orphaned folders.</summary>
    public const string TaskVfsAudit = "shoko-vfs-audit";

    /// <summary>Task name for identifying and removing missing files from the database.</summary>
    public const string TaskShokoPurgeMissing = "shoko-purge-missing";

    /// <summary>Task name for synchronizing watched state between Plex and Shoko.</summary>
    public const string TaskShokoSyncWatched = "shoko-sync-watched";

    /// <summary>Task name for processing manual source folder symlinks.</summary>
    public const string TaskMapSymlinks = "shoko-map-symlinks";

    #endregion
}
