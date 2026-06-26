# Controller

- All of the endpoints below are available under the plugin base path: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`
- They can be interacted with easily using **/swagger/** at: `http(s)://{ShokoHost}:{ShokoPort}/swagger/index.html?urls.primaryName=Shoko+Relay+V1`
  - There is a small `Relay API` button in the top right of the dashboard's "Quick Actions" panel that will go straight there

## Table of Contents

- [Dashboard / Config](#dashboard--config)
- [Metadata](#metadata-provider)
- [Plex](#plex)
- [Shoko](#shoko)
- [AnimeThemes](#animethemes)

---

## Dashboard / Config

Endpoints managed by `DashboardController`

---

```
GET  /dashboard/{*path}                                        -> GetControllerPage

GET  /config                                                   -> GetConfig
POST /config                                                   -> SaveConfig
GET  /config/schema                                            -> GetConfigSchema

GET /theme.css                                                 -> GetDynamicThemeCss

GET  /tasks/active                                             -> GetActiveTasks
GET  /tasks/completed                                          -> GetCompletedTasks
POST /tasks/clear/{taskName}                                   -> ClearTaskResult

GET /logs/list                                                 -> GetLogsList
GET /logs/{fileName}                                           -> GetLog
```

- `GetControllerPage` Serves the plugin's frontend components and static assets from the `dashboard` folder.
  - `/dashboard` (no path): Serves the main settings dashboard (`dashboard.cshtml`).
  - `/player`: Serves the stand-alone AnimeThemes video player (`player.cshtml`).
  - `/browser`: Serves the stand-alone VFS browser (`browser.cshtml`).
  - `/dashboard/{assetPath}`: Serves static assets (JS, CSS, fonts, images).
  - The controller uses `FileExtensionContentTypeProvider` for MIME mapping and automatically injects a `<base>` tag.
- `GetConfig` returns the current configuration payload (JSON) used by the dashboard page.
  - The `ConfigProvider` handles serialization and sanitization. Structure is nested into `Automation`, `Playback`, and `Advanced`.
  - This also includes `anidb_vfs_overrides.csv` in the response as a separate entry from the main payload.
- `SaveConfig` persists automation/provider settings (tokens handled separately).
  - `/config` does not expose the Plex token. Instead the response includes `PlexLibrary.HasToken`.
- `GetConfigSchema` returns a JSON schema representation of `RelayConfig` properties.
  - Properties within the `AdvancedConfig` class are automatically flagged with `Advanced: true`.
- `GetDynamicThemeCss` generates and serves a dynamically mapped CSS stylesheet.
  - It reads the selected Shoko WebUI theme (configured under `Advanced.SelectedTheme`) from Shoko's `themes/` directory via `IApplicationPaths`.
  - For custom WebUI themes, it appends a translation block that bridges Shoko's native CSS variables onto the plugin's custom layout variables.
- `GetActiveTasks` returns a list of unique task identifiers currently running on the server.
- `GetCompletedTasks` returns a dictionary of results for tasks that finished while the dashboard was disconnected or before a refresh.
- `ClearTaskResult` acknowledges and removes a stored result from the server's memory.
- `GetLogsList` returns a JSON list of all currently generated task logs that exist on disk.
- `GetLog` serves report files created under the plugin's `logs` directory.
  - This endpoint serves the file as `text/plain` without a download name, allowing it to be viewed directly in a browser tab.

**Notes:**

All automation endpoints utilize the `LogAndReturn` helper (via `ExecuteTrackedTaskAsync`) to provide a direct `logUrl` in the response. Reports include:

```
/animethemes/vfs/build                                         -> at-vfs-build-report.log
/animethemes/vfs/map                                           -> at-map-build-report.log
/animethemes/mp3?batch=true                                    -> at-mp3-build-report.log
/animethemes/mp3/audit                                         -> at-mp3-audit-report.log
/animethemes/webm/download                                     -> at-webm-download-report.log
/plex/auth/refresh                                             -> plex-auth-refresh-report.log
/plex/collections/build                                        -> plex-collections-build-report.log
/plex/ratings/apply                                            -> plex-ratings-apply-report.log
/plex/images/sync                                              -> plex-images-sync-report.log
/plex/automation/run                                           -> plex-automation-run-report.log
/vfs                                                           -> shoko-vfs-build-report.log
/shoko/purge-missing                                           -> shoko-purge-missing-report.log
/sync-watched                                                  -> shoko-sync-watched-report.log
/map-symlinks                                                  -> shoko-map-symlinks-report.log
```

---

## Metadata Provider

Endpoints managed by `MetadataController`

---

```
GET  /                                                         -> GetMediaProvider
GET  /matches?filename={path}&title={id}&manual=1              -> Match                      (for preview/testing)
POST /matches?filename={path}&title={id}&manual=1              -> Match

GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}                               -> GetCollectionImage         (image)
     [?name={string}&suffix={string}&t={ticks}]

GET  /metadata/{ratingKey}?includeChildren={0|1}               -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
GET  /metadata/{ratingKey}/images                              -> GetMetadataImages
GET  /metadata/{ratingKey}/extras                              -> GetMetadataExtras
```

- `GetMediaProvider` returns the agent descriptor describing supported types and features.
- `Match` looks up a series by filename or title. Priority is given to IDs found in the path.
  - `filename`: The file path provided by Plex.
  - `title`: The Shoko Series ID (used when `manual=1`).
  - `manual`: (default 0) set to 1 to force identification via the `title` parameter.
  - Testing the `GET` endpoint would use the following format: `/matches?title={ShokoSeriesID}&manual=1`
- `GetCollection` retrieves collection metadata for a given group ID.
- `GetCollectionImage` returns the image from the `!CollectionImages` directory matching the given Shoko group name/ID or a Plex smart collection rating key prefixed with 'sc'.
  - `t`: (optional) timestamp ticks used for cache busting.
  - `name`: (optional) the collection name for smart collections since they have no mapping to a group ID.
  - `suffix`: (optional) the artwork type suffix (e.g. -logo, -backdrop, -square)
- `GetMetadata` returns full metadata for a ratingKey (series/season/episode).
  - `includeChildren`: (default 0) set to 1 to embed immediate children in the response.
- `GetChildren` / `GetGrandchildren` return only the immediate or second-level child items respectively.
- `GetMetadataImages` returns a `MediaContainer` with an `Image` array used by Plex when fetching all artwork.
- `GetMetadataExtras` returns an empty `MediaContainer` to satisfy Plex requirements and prevent 404 errors during metadata refreshes.

**Notes:**

- Advanced Provider Overrides: Users can specify provider options directly via the base URL in Plex by appending an `options` segment.
  - Example: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/options/SeriesTitleLanguage=EN;TmdbImageLanguage=EN`
  - Any setting under `#region Provider Config` in `RelayConfig.cs` can be overridden per-request. Semicolons are used as delimiters.
  - Settings primarily utilized by background automations or Quick Actions (e.g., `CollectionImages`, `CriticRatingMode`) fall outside the scope of Plex's active metadata requests.
  - These settings will fallback to the globally configured dashboard values.
- TMDB episode-numbering is honoured when enabled (uses `IShokoEpisode.TmdbEpisodes`).
- Hidden episodes are excluded from all metadata results.
- Supported `RatingKey` formats:
  - `123` (Shoko Series ID) / `a890` (AniDB Series ID)
  - `123s4` (Shoko Series Season 4) / `a123s4` (AniDB Series Season 4)
  - `e567` (Shoko Episode ID) / `ae567` (AniDB Episode ID)
  - `e567p2` (Shoko Episode Part 2) / `ae567p2` (AniDB Episode Part 2)
  - _AniDB IDs resolve to Shoko IDs and must be known to Shoko_
- Part suffixes support both physical multi-part files and virtual TMDB episode segments.
- Crossover episodes (files belonging to multiple series) are skipped for local metadata/subtitle linking to avoid conflicts.

---

## Plex

Endpoints are managed by `PlexController`

---

### Plex: Authentication

```
GET  /plex/auth                                                -> StartPlexAuth
GET  /plex/auth/status?pinId={id}                              -> GetPlexAuthStatus
POST /plex/auth/refresh                                        -> RefreshPlexLibraries
POST /plex/auth/unlink                                         -> UnlinkPlex
```

- `StartPlexAuth` initiates the PIN-based OAuth flow by requesting a unique pairing code and authorization URL from Plex.tv.
- `GetPlexAuthStatus` polls for PIN completion; upon success, it saves the authentication token and triggers the initial discovery of servers and Shoko Relay libraries.
- `RefreshPlexLibraries` uses the saved token to force re-discovery of all accessible Plex servers then updates server URIs and the list of Shoko Relay libraries.
- `UnlinkPlex` revokes the token at Plex.tv and deletes the `plex.token` file.

---

### Plex: Automation

```
GET  /plex/library/refresh?filter={csv}                        -> RefreshPlexSeries

GET  /plex/collections/build                                   -> BuildPlexCollections
     [?filter={csv}&assignment={true|false}&clean={true|false}]

GET  /plex/ratings/apply?filter={csv}                          -> ApplyAudienceRatings

GET  /plex/images/sync                                         -> SyncPlexImages

GET  /plex/automation/run                                      -> RunPlexAutomationNow
```

- `RefreshPlexSeries` triggers a partial library scan in Plex for a comma-separated list of series IDs.
- `BuildPlexCollections` generates Plex collections for a comma-separated list of series IDs (or all series if omitted).
  - `assignment`: (default true) if false, skips assigning series to collections and only applies posters.
  - `clean`: (default true) if true and `PlexMetadataPath` is configured, prunes old cached custom posters from Plex's local metadata directory
  - This will also scan Plex for smart collection names allowing their posters to be set.
- `ApplyAudienceRatings` updates ratings for a comma-separated list of series IDs based on the configured source (TMDB/AniDB).
- `SyncPlexImages` queries Plex for generated episode thumbnails and scans VFS/collection paths for all local images, uploading and marking them as preferred in Shoko.
  - The generated episode thumbnails will not be uploaded if `TmdbThumbnails` is enabled or a local thumbnail is present.
  - Tracks processed images via `images_shokorelay.cache` to avoid redundant uploads.
- `RunPlexAutomationNow` triggers collection building, rating application, and image sync (if enabled) back-to-back for all series.

**Notes:**

- Each of the above (other than `RunPlexAutomationNow`) accepts a comma separated `filter`.
  - The filter is comprised of Shoko or AniDB (prefixed with an 'a') series IDs.
- All operations respect the `Advanced.Parallelism` setting to prevent IO saturation.
- The scheduler is governed by `Automation.PlexAutomationFrequencyHours`.

---

### Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- `PluginPlexWebhook` handles Plex `media.scrobble`, `media.rate`, `media.stop`, and `media.pause` events.

**Notes:**

- Strict Validation:
  - Validates the `Server.uuid` against known servers to prevent leaks from shared libraries.
  - Distinguishes between the actual Admin account and managed users.
  - Managed users are only permitted to scrobble if listed in `Automation.ExtraPlexUsers`.
- Success logs use the format: `user='Name', series='Title', episode='S01E01'`.
- Rating events update Shoko episode ratings via `IUserDataService.RateEpisode` if `Automation.ShokoSyncWatchedIncludeRatings` is enabled.
- Progress events (`media.stop` / `media.pause`) update the VideoUserData ProgressPosition via `IUserDataService.SaveVideoUserData`.

---

## Shoko

Endpoints are managed by `ShokoController`

### Virtual File System (VFS)

```
GET  /vfs?run={true|false}&clean={true|false}&filter={csv}     -> BuildVfs
GET /vfs/audit                                                 -> AuditVfs

POST /vfs/overrides                                            -> SaveVfsOverrides

GET /vfs/tree                                                  -> GetVfsTree
```

- `BuildVfs` (all query parameters are optional)
  - `run`: (default false) set to true to execute the VFS construction.
  - `clean`: (default true) clear the existing root before building.
  - `filter`: (optional) comma separated Shoko or AniDB (prefixed with an 'a') series IDs.
- `AuditVfs` scans physical VFS directories against the database to find and remove orphaned folders and broken symlinks.
- `SaveVfsOverrides` accepts raw text for `anidb_vfs_overrides.csv`.
- `GetVfsTree` returns a hierarchical representation of the VFS structure by reading `vfs_blueprint.cache`.

**Notes:**

- When `Automation.ScanOnVfsRefresh` is enabled, the controller schedules library scans for affected series automatically.
- When importing local metadata images, files named `Specials.<ext>` are renamed to `Season-Specials-Poster.<ext>` in the VFS.
- Any folder name listed in the `Folder Exclusions` setting (plus system folders like !AnimeThemes) is ignored during VFS generation.
  - If `PlexLocalExtras` is enabled all folders and files matching Plex local extra formatting will be ignored as well.
- Overrides allow grouping multiple AniDB IDs under a single primary Shoko Series ID for Plex.
- `VfsWatcher` automatically triggers batch VFS builds when file events are detected.
- Executing `BuildVfs` also generates or updates `vfs_blueprint.cache`.

---

### Shoko: Automation

```
GET  /shoko/purge-missing?dryRun={true|false}                  -> PurgeMissingFiles           (for preview/testing)
POST /shoko/purge-missing?dryRun={true|false}                  -> PurgeMissingFiles

POST /shoko/import                                             -> RunShokoImport
GET  /shoko/import/start                                       -> StartShokoImportNow

GET  /sync-watched                                             -> SyncPlexWatched            (for preview/testing)
POST /sync-watched                                             -> SyncPlexWatched
     [?dryRun={true|false}&sinceHours={int}&ratings={true|false}&progress={true|false}&import={true|false}&users={All|Admin|Extra|None}&libraryName={name}]

GET  /sync-watched/start                                       -> StartWatchedSyncNow
```

- `PurgeMissingFiles` completely removes missing files from Shoko's DB and the AniDB MyList (physical files are never touched).
  - `dryRun`: (default true) set to false to actually remove records from Shoko and AniDB MyList.
- `RunShokoImport` triggers a scan of managed folders.
- `SyncPlexWatched` synchronizes watched state between Plex and Shoko (Bi-directional).
  - `dryRun`: (default true) If true, skip database and Plex server writes.
  - `sinceHours`: (optional) limit processing to items viewed within this window.
  - `ratings`: (default to configuration) set to true/false to override the `Automation.ShokoSyncWatchedIncludeRatings` setting.
  - `progress`: (default to configuration) set to true/false to override the `Automation.ShokoSyncWatchedIncludeProgress` setting.
  - `import`: (default false) Direction: true for Plex←Shoko, false for Plex→Shoko.
  - `users`: (default to configuration) Restrict sync to specific user groups.
  - `libraryName`: (optional) restrict processing to a specific Plex library name (e.g. `Anime`).
  - Direction and exclusion settings are read from `AutomationConfig`.

**Notes:**

- The response for `PurgeMissingFiles` includes a Processed property containing the count of records removed.
- Scheduled automations are anchored to UTC midnight using `Automation.UtcOffsetHours`.
- Managed user tokens are obtained transiently via Plex Home switching and are never persisted.

---

### Shoko Extras

```
POST /map-symlinks?mapFile={path}&purgeLinks={true|false}      -> ProcessSourceLinks

POST /shoko/purge-custom-images                                -> PurgeLocalImages
POST /shoko/purge-episode-images                               -> PurgeEpisodeImages
```

- `ProcessSourceLinks` manages relative symlinks from protected source folders to the library based on a text-based mapping file, or purges existing links.
  - `mapFile`: (required if not purging) path to the `.txt` file relative to the Import Root (e.g., `!Source/symlinks.txt`).
  - `purgeLinks`: (default false) set to true to recursively remove all symlinks and `_attach` folders in the import roots.

**Notes:**

- If `purgeLinks` is `true`:
  - It explicitly excludes the configured VFS / AnimeThemes / Posters root folders and never deletes physical media files.
- If `purgeLinks` is `false` (default):
  - The `mapFile` parameter is the path to the `.txt` file relative to the Import Root (e.g., `!Source/symlinks.txt`).
  - Source paths are resolved relative to the directory containing the mapping file.
  - Destination paths are resolved relative to the Import Root.
  - Sidecar files (any file starting with `{baseName}`) and attachment folders (directories named `{baseName}_attachments`) are automatically identified and renamed to match the destination.
    - The `_attachments` folders are renamed to `_attach` at the destination to allow the `purgeLinks` operation to delete them without touching the originals.
- `PurgeLocalImages` removes and purges all custom user-submitted posters and Plex-generated episode screenshots from Shoko.
- `PurgeEpisodeImages` removes and purges all default non-locally-generated episode backdrops from Shoko.

---

**Mapping File Format:**

The mapping file uses a pipe-delimited (`|`) and semicolon-delimited (`;`) structure. While designed for multiple scripts, this plugin specifically extracts the source path, destination path, and tags.

`"original_path";comment;tag1,tag2|"symlink_path";comment;ext|title|subgroup|"audio_path";comment;lang;name`

**Plugin Logic:**

- Segment 1 (Source):
  - `Path`: Extracted from the first double-quoted string.
  - `Tags`: Extracted from the 3rd semicolon-separated group. Tags are split by commas and appended to the destination filename in brackets (e.g., `[tag1] [tag2]`).
- Segment 2 (Destination):
  - `Path`: Extracted from the second double-quoted string. Used as the base for the symlink and sidecar renaming.
- Segments 3+ (Optional):
  - Used by external scripts for title, subgroup, or external audio track info; ignored by this plugin.

**Notes:**

- Sidecar Renaming: Sidecars are renamed by replacing the source base name with the tagged destination base name.
- Attachment Handling:
  - Source Convention: The original attachment folders must end in `_attachments` (e.g., `Anime_attachments`).
  - Destination Result: The plugin creates a physical folder at the destination ending in `_attach` (e.g., `Anime_attach`).
  - Internal contents (fonts) are linked as individual relative file symlinks.
  - This distinction ensures the `purgeLinks` command can safely remove generated links without touching original source files.
- Relative Pathing: Links are created with relative targets. They remain valid as long as the relative depth between the source and destination remains consistent.
- Bookkeeping: Lines that are successfully processed are automatically prefixed with `#` to prevent redundant processing in future runs.
- `PurgeLocalImages` / `PurgeEpisodeImages` will be removed once Shoko's v3 API has similar functionality

## AnimeThemes

Endpoints managed by `AnimeThemesController`

---

### VFS & Mapping

```
GET  /animethemes/vfs/build?filter={csv}                       -> AnimeThemesVfsBuild
GET  /animethemes/vfs/map?testPath={filename}                  -> AnimeThemesVfsMap
POST /animethemes/vfs/import                                   -> ImportAnimeThemesMapping
```

- `AnimeThemesVfsBuild` applies the mapping and generates `webm_animethemes.cache`.
  - `filter`: (optional) comma separated Shoko or AniDB (prefixed with an 'a') series IDs.
  - Cache Format: `VfsPath|VideoId|Bitmask`.
  - Bitmask flags: `1:NC, 2:Lyrics, 4:Subs, 8:Uncen, 16:NSFW, 32:Spoil, 64:Trans, 128:Over`.
- `AnimeThemesVfsMap` generates the mapping CSV or tests a single filename mapping.
  - `testPath`: (optional) provide a filename to preview its mapping result and generated name.
  - The filename generation respects the `Advanced.AnimeThemesAppendTags` setting.

---

### WebM (Player Support & Download)

```
GET  /animethemes/webm/tree                                    -> AnimeThemesWebmTree
GET  /animethemes/webm/stream?path={path}                      -> AnimeThemesWebmStream
HEAD /animethemes/webm/stream?path={path}                      -> AnimeThemesWebmStream
GET  /animethemes/webm/favourites                              -> GetAnimeThemesFavourites
POST /animethemes/webm/favourites                              -> UpdateAnimeThemesFavourite
POST /animethemes/webm/download                                -> DownloadAnimeThemesWebm
     [?name={string}&year={int}&season={string}&force={true|false}&destination={path}]
```

- `AnimeThemesWebmTree` returns the hierarchical tree including bitmask flags and `videoId`.
- `AnimeThemesWebmStream` supports HTTP range requests for seekable browser playback.
- `GetAnimeThemesFavourites` returns a list of `videoId` favourites from `favs_animethemes.cache`.
- `UpdateAnimeThemesFavourite` toggles a `videoId` in the favourites list using a raw integer body.
- `DownloadAnimeThemesWebm` fetches WebM files from the AnimeThemes API and organizes them by Year/Season inside the configured `!AnimeThemes` folder.
  - At least one filter (`name` or `season`) is required to prevent accidentally downloading the entire database.
  - If a `season` is provided without a `year`, it automatically defaults to the current year.
  - `force`: (default false) set to true to overwrite existing `.webm` files.
  - `destination`: (optional) absolute path to a specific managed folder to force downloads into.
  - Files from before the year 2000 will sort by Decade/Season

---

### MP3

```
GET  /animethemes/mp3                                          -> AnimeThemesMp3
     [?path={path}&slug={slug}&offset={int}&batch={true|false}&force={true|false}&seasonal={true|false}]
GET  /animethemes/mp3/cache                                    -> GetAnimeThemesMp3Cache
GET  /animethemes/mp3/audit                                    -> AuditAnimeThemesMp3
GET  /animethemes/mp3/stream?path={path}                       -> AnimeThemesMp3Stream
HEAD /animethemes/mp3/stream?path={path}                       -> AnimeThemesMp3Stream
GET  /animethemes/mp3/random?refresh={true|false}              -> AnimeThemesMp3Random
```

- `AnimeThemesMp3` generates or batches Theme.mp3 files using parallelism.
  - `path`: (required) relative or absolute Shoko/Plex path.
  - `slug`: (optional) specific theme identifier (e.g., OP1).
  - `offset`: (default 0) selection index when multiple themes match.
  - `batch`: (default false) set to true to recursively process subfolders.
  - `force`: (default false) set to true to overwrite existing `Theme.mp3` files.
  - `seasonal`: (default false) if `batch=true`, filter processing to the current anime season (with a one-month early buffer).
- `GetAnimeThemesMp3Cache` returns the dictionary of cached `Theme.mp3` folder paths mapped to their respective theme slugs.
- `AuditAnimeThemesMp3` audits existing non-OP `Theme.mp3` files to check if an OP is available on AnimeThemes. It also repairs missing local cache slugs via ID3 tags.
- `AnimeThemesMp3Stream` embeds ID3v2 tags in response headers.
  - Headers: `X-Theme-Title`, `X-Theme-Slug`, `X-Theme-Artist`, `X-Theme-Album`.
- `AnimeThemesMp3Random` uses a startup cache persisted in `mp3_animethemes.cache`.
  - `refresh`: (default false) set to true to force a re-scan of managed roots for existing themes.

**Notes:**

- All paths may be Plex or Shoko relative; the controller translates them via `Advanced.PathMappings`.
- Mapping de-duplication logic:
  - BD Prioritization: If metadata for multiple video files results in the same filename, BD (Blu-ray) sources are prioritized.
    - If a BD source is available, all non-BD sources (TV, Web, etc.) for that specific filename are skipped.
  - Numbering: If multiple sources of the same priority (e.g., multiple BDs or multiple TV rips) result in the same filename, they are de-duplicated by appending ` (2)`, ` (3)`, etc.
  - Overrides: Themes originating from secondary series in an override group receive a prefix (e.g., `P2 ❯ `, `P3 ❯ `) to distinguish them from the primary series themes.

---
