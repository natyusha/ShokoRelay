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

GET  /logs/{fileName}                                          -> GetLog

GET  /tasks/active                                             -> GetActiveTasks
GET  /tasks/completed                                          -> GetCompletedTasks
POST /tasks/clear/{taskName}                                   -> ClearTaskResult
```

- `GetControllerPage` Serves the plugin's frontend components and static assets from the `dashboard` folder.
  - `/dashboard` (no path): Serves the main settings dashboard (`dashboard.cshtml`).
  - `/dashboard/player`: Serves the stand-alone AnimeThemes video player (`player.cshtml`).
  - `/dashboard/{assetPath}`: Serves static assets (JS, CSS, fonts, images).
  - The controller uses `FileExtensionContentTypeProvider` for MIME mapping and automatically injects a `<base>` tag.

---

- `GetConfig` returns the current configuration payload (JSON) used by the dashboard page.
  - The `ConfigProvider` handles serialization and sanitization. Structure is nested into `Automation`, `Playback`, and `Advanced`.
  - This also includes `anidb_vfs_overrides.csv` in the response as a separate entry from the main payload.
- `SaveConfig` persists automation/provider settings (tokens handled separately).
  - `/config` does not expose the Plex token. Instead the response includes `PlexLibrary.HasToken`.
- `GetConfigSchema` returns a JSON schema representation of `RelayConfig` properties.
  - Properties within the `AdvancedConfig` class are automatically flagged with `Advanced: true`.

---

- `GetLog` serves report files created under the plugin's `logs` directory.
  - This endpoint serves the file as `text/plain` without a download name, allowing it to be viewed directly in a browser tab.
- All automation endpoints utilize the `LogAndReturn` helper to provide a direct `logUrl` in the response. Reports include:

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

- `GetActiveTasks` returns a list of unique task identifiers currently running on the server.
- `GetCompletedTasks` returns a dictionary of results for tasks that finished while the dashboard was disconnected or before a refresh.
- `ClearTaskResult` acknowledges and removes a stored result from the server's memory.

---

## Metadata Provider

Endpoints managed by `MetadataController`

---

```
GET  /                                                         -> GetMediaProvider
GET  /matches?filename={path}&title={id}&manual=1              -> Match
POST /matches                                                  -> Match

GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}                               -> GetCollectionPoster (image)

GET  /metadata/{ratingKey}?includeChildren=0|1                 -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
GET  /metadata/{ratingKey}/images                              -> GetImages
```

- `GetMediaProvider` returns the agent descriptor describing supported types and features.
- `Match` looks up a series by filename or title. Priority is given to IDs found in the path.
  - Testing the `GET` endpoint would use the following format: `/matches?title={ShokoSeriesID}&manual=1`

---

- `GetCollection` retrieves collection metadata for a given group ID.
- `GetCollectionPoster` returns the poster image from the `!CollectionPosters` directory.
  - Supports an optional `?t={ticks}` query parameter for cache busting.

---

- `GetMetadata` returns full metadata for a ratingKey (series/season/episode).
  - Logic for context resolution and series merging is delegated to the `PlexMetadata` service.
- `GetChildren` / `GetGrandchildren` return only the immediate or second-level child items respectively.
- `GetImages` returns a `MediaContainer` with an `Image` array used by Plex when fetching all artwork.

---

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
GET  /plex/collections/build?seriesId={id}&filter={filter}     -> BuildPlexCollections
GET  /plex/collections/posters?seriesId={id}&filter={filter}   -> ApplyCollectionPosters

GET  /plex/ratings/apply?seriesId={id}&filter={filter}         -> ApplyAudienceRatings

GET  /plex/automation/run                                      -> RunPlexAutomationNow
```

- `BuildPlexCollections` generate Plex collections for the specified series or filter.
- `ApplyCollectionPosters` upload or refresh posters for the same series set.

---

- `ApplyAudienceRatings` update series/episode ratings based on the configured source (TMDB/AniDB).

---

- `RunPlexAutomationNow` triggers collection building and rating application back-to-back for all series.
  - The scheduler is governed by `Automation.PlexAutomationFrequencyHours`.

---

**Notes:**

- Each of the above (other than `RunPlexAutomationNow`) accepts either `seriesId` _or_ a comma separated `filter`.
- All operations respect the `Advanced.Parallelism` setting to prevent IO saturation.

---

### Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- `PluginPlexWebhook` handles Plex `media.scrobble` and `media.rate` events.
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
GET  /vfs?run={true|false}&clean={true|false}&filter={filter}  -> BuildVfs

POST /vfs/overrides                                            -> SaveVfsOverrides
```

- `BuildVfs` (all query parameters are optional)
  - `run` (default false): if true the VFS is constructed.
  - `clean` (default true): clear the existing root before building.
- `SaveVfsOverrides` accepts raw text for `anidb_vfs_overrides.csv`.

---

**Notes:**

- When `Automation.ScanOnVfsRefresh` is enabled, the controller schedules library scans for affected series automatically.
- When importing local metadata images, files named `Specials.<ext>` are renamed to `Season-Specials-Poster.<ext>` in the VFS.
- Overrides allow grouping multiple AniDB IDs under a single primary Shoko Series ID for Plex.

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
- `RunShokoImport` triggers a scan of managed folders marked as "Source".
- `SyncPlexWatched` synchronizes watched state between Plex and Shoko (Bi-directional).
  - Default direction is `Plex→Shoko`. Set `import=true` for `Plex←Shoko`.
  - Direction and exclusion settings are read from `Automation` config.

---

**Notes:**

- Scheduled automations are anchored to UTC midnight using `Automation.UtcOffsetHours`.
- Managed user tokens are obtained transiently via Plex Home switching and are never persisted.

---

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
  - Cache Format: `VfsPath|VideoId|Bitmask`.
  - Bitmask flags: `1:NC, 2:Lyrics, 4:Subs, 8:Uncen, 16:NSFW, 32:Spoil, 64:Trans, 128:Over`.
- `AnimeThemesVfsMap` generates the mapping CSV or tests a single filename mapping.
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
- `AnimeThemesMp3Stream` embeds ID3v2 tags in response headers.
  - Headers: `X-Theme-Title`, `X-Theme-Slug`, `X-Theme-Artist`, `X-Theme-Album`.
- `AnimeThemesMp3Random` uses a startup cache persisted in `mp3_animethemes.cache`.

---

**Notes:**

- All paths may be Plex or Shoko relative; the controller translates them via `Advanced.PathMappings`.
- Mapping de-duplication logic:
  - BD Prioritization: If metadata for multiple video files results in the same filename, BD (Blu-ray) sources are prioritized.
    - If a BD source is available, all non-BD sources (TV, Web, etc.) for that specific filename are skipped.
  - Numbering: If multiple sources of the same priority (e.g., multiple BDs or multiple TV rips) result in the same filename, they are de-duplicated by appending ` (2)`, ` (3)`, etc.
  - Overrides: Themes originating from secondary series in an override group receive a prefix (e.g., `P2 ❯ `, `P3 ❯ `) to distinguish them from the primary series themes.

---
