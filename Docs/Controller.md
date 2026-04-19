# Controller

All of the endpoints below are available under the plugin base path: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

They can be interacted with easily using **/swagger/** at: `http(s)://{ShokoHost}:{ShokoPort}/swagger`

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

GET  /tasks/active                                             -> GetActiveTasks
GET  /tasks/completed                                          -> GetCompletedTasks
POST /tasks/clear/{taskName}                                   -> ClearTaskResult

GET  /logs/{fileName}                                          -> GetLog
```

- `GetControllerPage` Serves the plugin's frontend components and static assets from the `dashboard` folder.
  - `/dashboard` (no path): Serves the main settings dashboard (`dashboard.cshtml`).
  - `/dashboard/player`: Serves the stand-alone AnimeThemes video player (`player.cshtml`).
  - `/dashboard/{assetPath}`: Serves static assets (JS, CSS, fonts, images).
  - The controller uses `FileExtensionContentTypeProvider` for MIME mapping and automatically injects a `<base>` tag.
- `GetConfig` returns the current configuration payload (JSON) used by the dashboard page.
  - The `ConfigProvider` handles serialization and sanitization. Structure is nested into `Automation`, `Playback`, and `Advanced`.
  - This also includes `anidb_vfs_overrides.csv` in the response as a separate entry from the main payload.
- `SaveConfig` persists automation/provider settings (tokens handled separately).
  - `/config` does not expose the Plex token. Instead the response includes `PlexLibrary.HasToken`.
- `GetConfigSchema` returns a JSON schema representation of `RelayConfig` properties.
  - Properties within the `AdvancedConfig` class are automatically flagged with `Advanced: true`.
- `GetActiveTasks` returns a list of unique task identifiers currently running on the server.
- `GetCompletedTasks` returns a dictionary of results for tasks that finished while the dashboard was disconnected or before a refresh.
- `ClearTaskResult` acknowledges and removes a stored result from the server's memory.
- `GetLog` serves report files created under the plugin's `logs` directory.
  - This endpoint serves the file as `text/plain` without a download name, allowing it to be viewed directly in a browser tab.

**Notes:**

All automation endpoints utilize the `LogAndReturn` helper to provide a direct `logUrl` in the response. Reports include:

```
/plex/collections/build                                        -> collections-report.log
/plex/ratings/apply                                            -> ratings-report.log
/vfs                                                           -> vfs-report.log
/shoko/remove-missing                                          -> remove-missing-report.log
/sync-watched                                                  -> sync-watched-report.log
/animethemes/vfs/build                                         -> at-vfs-report.log
/animethemes/vfs/map                                           -> at-map-report.log
/animethemes/mp3?batch=true                                    -> at-mp3-report.log
```

---

## Metadata Provider

Endpoints managed by `MetadataController`

---

```
GET  /                                                         -> GetMediaProvider
GET  /matches?filename={path}&title={id}&manual=1              -> Match                     (for preview/testing)
POST /matches?filename={path}&title={id}&manual=1              -> Match

GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}?t={ticks}                     -> GetCollectionPoster       (image)

GET  /metadata/{ratingKey}?includeChildren={0|1}               -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
GET  /metadata/{ratingKey}/images                              -> GetImages
```

- `GetMediaProvider` returns the agent descriptor describing supported types and features.
- `Match` looks up a series by filename or title. Priority is given to IDs found in the path.
  - `filename`: The file path provided by Plex.
  - `title`: The Shoko Series ID (used when `manual=1`).
  - `manual`: (default 0) set to 1 to force identification via the `title` parameter.
  - Testing the `GET` endpoint would use the following format: `/matches?title={ShokoSeriesID}&manual=1`
- `GetCollection` retrieves collection metadata for a given group ID.
- `GetCollectionPoster` returns the poster image from the `!CollectionPosters` directory.
  - `t`: (optional) timestamp ticks used for cache busting.
- `GetMetadata` returns full metadata for a ratingKey (series/season/episode).
  - `includeChildren`: (default 0) set to 1 to embed immediate children in the response.
- `GetChildren` / `GetGrandchildren` return only the immediate or second-level child items respectively.
- `GetImages` returns a `MediaContainer` with an `Image` array used by Plex when fetching all artwork.

**Notes:**

- TMDB episode-numbering is honoured when enabled (uses `IShokoEpisode.TmdbEpisodes`).
- Hidden episodes are excluded from all metadata results.
- RatingKey formats supported:
  - `123` (Series)
  - `123s4` (Season 4 of series 123)
  - `e56789` (Episode)
  - `e56789p2` (Episode 56789, Part 2)
  - `a123` (AniDB ID 123 alias, resolves to Shoko Series)
- Crossover episodes (files belonging to multiple series) are skipped for local metadata/subtitle linking to avoid conflicts.

---

## Plex

Endpoints are managed by `PlexController`

---

### Plex: Authentication

```
GET  /plex/auth                                                -> StartPlexAuth             (returns pin + authUrl)
GET  /plex/auth/status?pinId={id}                              -> GetPlexAuthStatus         (poll for pin completion)
POST /plex/auth/unlink                                         -> UnlinkPlex                (revoke & clear saved token)
POST /plex/auth/refresh                                        -> RefreshPlexLibraries      (re-discover Shoko libraries)
```

- `StartPlexAuth` begins the PIN-based pairing flow via Plex.tv.
- `GetPlexAuthStatus` and `RefreshPlexLibraries` automatically call `RefreshAdminUsername` to identify and persist the server owner's name.
- `UnlinkPlex` revokes the token and removes all persisted server/library metadata from `plex.token`.

---

### Plex: Automation

```
GET  /plex/collections/build?seriesId={id}&filter={csv}        -> BuildPlexCollections
GET  /plex/collections/posters?seriesId={id}&filter={csv}      -> ApplyCollectionPosters

GET  /plex/ratings/apply?seriesId={id}&filter={csv}            -> ApplyAudienceRatings

GET  /plex/automation/run                                      -> RunPlexAutomationNow
```

- `BuildPlexCollections` generate Plex collections for the specified series or filter.
- `ApplyCollectionPosters` upload or refresh posters for the same series set.
- `ApplyAudienceRatings` update series/episode ratings based on the configured source (TMDB/AniDB).
- `RunPlexAutomationNow` triggers collection building and rating application back-to-back for all series.

**Notes:**

- Each of the above (other than `RunPlexAutomationNow`) accepts either `seriesId` _or_ a comma separated `filter`.
  - The `seriesId` defaults to Shoko but can be an AniDB ID if prefixed with an 'a'.
- All operations respect the `Advanced.Parallelism` setting to prevent IO saturation.
- The scheduler is governed by `Automation.PlexAutomationFrequencyHours`.

---

### Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- `PluginPlexWebhook` handles Plex `media.scrobble` and `media.rate` events.

**Notes:**

- Strict Validation:
  - Validates the `Server.uuid` against known servers to prevent leaks from shared libraries.
  - Distinguishes between the actual Admin account and managed users.
  - Managed users are only permitted to scrobble if listed in `Automation.ExtraPlexUsers`.
- Success logs use the format: `user='Name', series='Title', episode='S01E01'`.
- Rating events update Shoko episode ratings via `IUserDataService.RateEpisode` if `Automation.ShokoSyncWatchedIncludeRatings` is enabled.

---

## Shoko

Endpoints are managed by `ShokoController`

### Virtual File System (VFS)

```
GET  /vfs?run={true|false}&clean={true|false}&filter={csv}     -> BuildVfs

POST /vfs/overrides                                            -> SaveVfsOverrides
```

- `BuildVfs` (all query parameters are optional)
  - `run`: (default false) set to true to execute the VFS construction.
  - `clean`: (default true) clear the existing root before building.
  - `filter`: (optional) comma separated Shoko or AniDB (prefixed with an 'a') series IDs.
- `SaveVfsOverrides` accepts raw text for `anidb_vfs_overrides.csv`.

**Notes:**

- When `Automation.ScanOnVfsRefresh` is enabled, the controller schedules library scans for affected series automatically.
- When importing local metadata images, files named `Specials.<ext>` are renamed to `Season-Specials-Poster.<ext>` in the VFS.
- Any folder name listed in the `Folder Exclusions` setting (plus system folders like !AnimeThemes) is ignored during VFS generation.
- Overrides allow grouping multiple AniDB IDs under a single primary Shoko Series ID for Plex.
- `VfsWatcher` automatically triggers batch VFS builds when file events are detected.

---

### Shoko: Automation

```
GET  /shoko/remove-missing?dryRun={true|false}                 -> RemoveMissingFiles       (for preview/testing)
POST /shoko/remove-missing?dryRun={true|false}                 -> RemoveMissingFiles

POST /shoko/import                                             -> RunShokoImport
GET  /shoko/import/start                                       -> StartShokoImportNow

GET  /sync-watched                                             -> SyncPlexWatched           (for preview/testing)
POST /sync-watched                                             -> SyncPlexWatched
     [?dryRun={true|false}&sinceHours={int}&ratings={true|false}&import={true|false}&excludeAdmin={true|false}]

GET  /sync-watched/start                                       -> StartWatchedSyncNow
```

- `RemoveMissingFiles` removes missing files from Shoko and the AniDB MyList (physical files are never touched).
  - `dryRun`: (default true) set to false to actually remove records from Shoko and AniDB MyList.
- `RunShokoImport` triggers a scan of managed folders marked as "Source".
- `SyncPlexWatched` synchronizes watched state between Plex and Shoko (Bi-directional).
  - `dryRun`: (default true) set to false to write watched states/ratings to databases.
  - `sinceHours`: (optional) limit processing to items viewed within this window.
  - `ratings`: (default to configuration) set to true/false to override the `Automation.ShokoSyncWatchedIncludeRatings` setting.
  - `import`: (default false) set to true for `Plex←Shoko`. Default is `Plex→Shoko`.
  - `excludeAdmin`: (default to configuration) set to true/false to override the `Automation.ShokoSyncWatchedExcludeAdmin` setting.
  - Direction and exclusion settings are read from `AutomationConfig`.

**Notes:**

- Scheduled automations are anchored to UTC midnight using `Automation.UtcOffsetHours`.
- Managed user tokens are obtained transiently via Plex Home switching and are never persisted.

---

### Shoko Extras

```
POST /map-symlinks?mapFile={path}&purgeLinks={true|false}      -> ProcessSourceLinks
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

### WebM (Player Support)

```
GET  /animethemes/webm/tree                                    -> AnimeThemesWebmTree
GET  /animethemes/webm/stream?path={path}                      -> AnimeThemesWebmStream
HEAD /animethemes/webm/stream?path={path}                      -> AnimeThemesWebmStream
GET  /animethemes/webm/favourites                              -> GetAnimeThemesFavourites
POST /animethemes/webm/favourites                              -> UpdateAnimeThemesFavourite
```

- `AnimeThemesWebmTree` returns the hierarchical tree including bitmask flags and `videoId`.
- `AnimeThemesWebmStream` supports HTTP range requests for seekable browser playback.
- `GetAnimeThemesFavourites` returns a list of `videoId` favourites from `favs_animethemes.cache`.
- `UpdateAnimeThemesFavourite` toggles a `videoId` in the favourites list using a raw integer body.

---

### MP3

```
GET  /animethemes/mp3                                          -> AnimeThemesMp3
     [?path={path}&slug={slug}&offset={int}&batch={true|false}&force={true|false}]
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
