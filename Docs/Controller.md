# Controller

All of the endpoints below are available under the plugin base path: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

They can be interacted with easily using **/swagger/** at: `http(s)://{ShokoHost}:{ShokoPort}/swagger`

## Table of Contents

- [Dashboard / Config](#dashboard--config)
- [Metadata Provider](#metadata-provider)
- [Plex: Authentication](#plex-authentication)
- [Plex: Automation](#plex-automation)
- [Plex: Webhook](#plex-webhook)
- [Virtual File System (VFS)](#virtual-file-system-vfs)
- [Shoko: Automation](#shoko-automation)
- [AnimeThemes](#animethemes)

---

## Dashboard / Config

```
GET  /dashboard/{*path}                                        -> GetControllerPage (serve dashboard index & assets)

GET  /config                                                   -> GetConfig
POST /config                                                   -> SaveConfig
GET  /config/schema                                            -> GetConfigSchema

GET  /logs/{fileName}                                          -> GetLog (download any report from logs folder)
```

- `GetControllerPage` Serves the dashboard UI and static assets (fonts, images, JS/CSS) from the plugin `dashboard` folder.
  - `{*path}` is an optional catch-all for dashboard assets.

---

- `GetConfig` returns the current plugin configuration payload (JSON) used by the dashboard page.
  - The `ConfigProvider` handles serialization, sanitization and omits any sensitive fields.
  - This also includes `anidb_vfs_overrides.csv` in the response as a separate entry from the main payload.
- `SaveConfig` persists automation/provider settings (tokens handled separately).
  - `/config` does not expose the Plex token. Instead the response includes `PlexLibrary.HasToken` which indicates token validity.
  - The actual secret lives only in `plex.token`.
- `GetConfigSchema` returns a JSON schema representation of `RelayConfig` properties.
  - Used by the dashboard to dynamically render the settings form with correct field names/types.

---

- `GetLog` serves any report file created under the plugin's `logs` directory.
- Request `/logs/{fileName}` to download the desired report, operations which generate logs are listed below:

```
/plex/collections/build                                        -> collections-report.log
/plex/ratings/apply                                            -> ratings-report.log

/vfs                                                           -> vfs-report.log

/shoko/remove-missing                                          -> remove-missing-report.log
/sync-watched                                                  -> sync-watched-report.log

/animethemes/vfs/build                                         -> at-vfs-build-report.log
/animethemes/vfs/map                                           -> at-vfs-map-report.log
/animethemes/mp3?batch=true                                    -> at-mp3-report.log
```

---

## Metadata Provider

```
GET  /                                                         -> GetMediaProvider (agent descriptor / supported types)
GET  /matches?name={name}&title={id}&manual=1                  -> Match (title is a ShokoSeriesID)
POST /matches                                                  -> Match

GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}                               -> GetCollectionPoster (image)

GET  /metadata/{ratingKey}?includeChildren=0|1                 -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
GET  /metadata/{ratingKey}/images                              -> GetImages (all image assets for the item)
```

- `GetMediaProvider` returns the agent descriptor describing supported types and features.
- `Match` looks up a series by filename or title.
  - Plex uses `title` + `manual=1` for manual matches and all titles are treated as ShokoSeriesIDs.
  - When invoked directly you may also POST a JSON body with `Filename`, `Title`, and `Manual` properties.

---

- `GetCollection` retrieves collection metadata for a given group ID.
- `GetCollectionPoster` returns the poster image.

---

- `GetMetadata` returns full metadata for a ratingKey (series/season/episode).
  - `includeChildren` (optional, 0/1) controls whether nested items are included.
- `GetChildren` / `GetGrandchildren` return only the immediate or second-level child items respectively.
- `GetImages` returns a `MediaContainer` with an `Image` array.
  - Used by Plex when fetching all artwork for an item.

---

**Notes:**

- TMDB episode‑numbering is honoured when enabled (uses `IShokoEpisode.TmdbEpisodes`).
- Hidden episodes are excluded.
- Episodes of type "Other" are initially placed in Featurettes (Season -4); a fallback rule moves them to Season 1 if it has no files, or Season 0 (Specials) if Season 1 is occupied, otherwise they remain in Featurettes.
- RatingKey formats supported using ShokoID: `123` (series), `123s4` (season 4), `e56789` (episode).
- A special alias exists if you want to lookup series metadata by AniDB ID instead of ShokoSeriesID: `a123` (series).
  - This supports children/grandchildren at the series level but not: `a123s#` or `ae56789`

---

## Plex: Authentication

```
GET  /plex/auth                                                -> StartPlexAuth (returns pin + authUrl)
GET  /plex/auth/status?pinId={id}                              -> GetPlexAuthStatus (poll for pin completion)
POST /plex/auth/unlink                                         -> UnlinkPlex (revoke & clear saved token)
POST /plex/auth/refresh                                        -> RefreshPlexLibraries (re-discover Shoko libraries)
```

- `StartPlexAuth` begins the PIN-based pairing flow and returns both the PIN and the authorization URL that the user must visit.
- `GetPlexAuthStatus` polls the Plex API to determine whether the PIN has been validated.
  - once completed the plugin stores the token and enumerates available servers/libraries.
- `UnlinkPlex` revokes the current Plex token and clears stored library information.
- `RefreshPlexLibraries` forces a rediscovery of servers and sections without reauthenticating.

---

## Plex: Automation

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
  - Useful if you want to force automation without waiting for the scheduled interval.
  - The scheduler itself is governed by `RelayConfig.PlexAutomationFrequencyHours` (0 disables it).

---

**Notes:**

- Each of the above (other than `RunPlexAutomationNow`) accepts either `seriesId` _or_ a comma separated `filter`; not both.
  - All work is performed per-configured Plex target and return counts/summary information.

---

## Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- `PluginPlexWebhook` handles Plex `media.scrobble` callbacks and, when the "Include Ratings" option is enabled, `media.rate` events.
  - The endpoint is gated by `RelayConfig.AutoScrobble`; when disabled all payloads are ignored with `reason: auto_scrobble_disabled`.
  - Supports both form‑encoded `payload` fields and raw JSON bodies.
  - Only events originating from the admin or users listed in `RelayConfig.ExtraPlexUsers` are considered; owner events may also be ignored if the "Exclude Admin" flag is set.
  - The service extracts the ShokoEpisodeID from the `Metadata.guid` value `tv.plex.agents.custom.shoko://episode/{ShokoEpisodeID}`.
  - Events without a GUID are dropped `reason: no_shoko_guid`.
  - Rating events update Shoko episode ratings via `IUserDataService.RateEpisode`.

---

## Virtual File System (VFS)

```
GET  /vfs?run={true|false}&clean={true|false}&filter={filter}  -> BuildVfs

POST /vfs/overrides                                            -> SaveVfsOverrides
```

- `BuildVfs` (all query parameters are optional)
  - `run` (default false) if true the VFS is constructed; when false the call just returns metadata.
  - `clean` (default true) clear the existing root before building.
  - `filter` comma separated Shoko series IDs to restrict processing.

---

- `SaveVfsOverrides` accepts the raw text of an `anidb_vfs_overrides.csv` file in the request body and overwrites (or clears) the file in the plugin's config directory.
  - This is the endpoint targeted by the dashboard editor; a successful save will reload the override groups for any subsequent VFS/metadata ops.

---

**Notes:**

- When the Plex configuration has `ScanOnVfsRefresh`, the controller schedules library scans for affected series automatically.
- `MapHelper.GetSeriesFileData` generates `FileMapping` objects consumed by `VfsHelper`/`PlexMetadata` to translate Shoko to Plex paths.
- Crossover episodes (files belonging to multiple AniDB/Shoko series) are skipped for metadata/subtitle copying to avoid conflicts.
- Each build writes a plain-text report to `vfs-report.log` in the plugin directory; the UI exposes a `logUrl` property to download it.
- When importing local metadata images, files named `Specials.<ext>` will be renamed to `Season-Specials-Poster.<ext>` in the VFS
  - This is purely for the aesthetics of the original file structure (where `<ext>` is one of the supported image extensions)
- A `anidb_vfs_overrides.csv` file may be placed in the plugin's _config_ directory to group multiple Shoko series IDs under a single primary series.
  - When present the first ID on each line becomes the canonical series and VFS/metadata operations merge the children of all listed IDs.
  - This requires all series that are being merged to have the same TMDB series match.
  - Blank lines and lines starting with `#` are ignored.
  - The dashboard provides an editor modal (accessible via the Paths section) which posts edits to `/vfs/overrides`.
    - If no override file exists when the editor is opened it will still appear empty and you may type new entries.
    - Clicking **Save** will create the file in the config directory.

---

## Shoko: Automation

```
GET  /shoko/remove-missing?dryRun={true|false}                 -> RemoveMissingFiles (for preview/testing)
POST /shoko/remove-missing?dryRun={true|false}                 -> RemoveMissingFiles

POST /shoko/import?onlyUnrecognized={true|false}               -> RunShokoImport
GET  /shoko/import/start                                       -> StartShokoImportNow

GET  /sync-watched                                             -> SyncPlexWatched (for preview/testing)
POST /sync-watched                                             -> SyncPlexWatched
     [?dryRun={true|false}&sinceHours={int}&ratings={true|false}&import={true|false}&excludeAdmin={true|false}]

GET  /sync-watched/start                                       -> StartWatchedSyncNow
```

- `RemoveMissingFiles` removes missing files from Shoko and the AniDB MyList.
  - by default (no query parameter) the endpoint performs a dry run and returns the list of would-be deletions
  - specify `dryRun=false` explicitly to actually execute the removals.

---

- `RunShokoImport` triggers a Shoko source import and replies with `{ status:"ok", scanned:[...], scannedCount:n }`.
- `StartShokoImportNow` forces an immediate import and updates the scheduler's last-run time.

---

- `SyncPlexWatched` (all query parameters are optional)
  - `dryRun` (default true) perform a dry run (no writes). Specify `false` to make actual changes.
  - `sinceHours` – limit syncing to items changed in the last N hours (automation uses interval+1).
  - `ratings` (default false) include user ratings when true.
  - `import` (default false) run direction Plex←Shoko instead of Plex→Shoko.
  - `excludeAdmin` (default false) when exporting skip the admin Plex user, useful with configured ExtraPlexUsers.

- `StartWatchedSyncNow` triggers a one-off sync and marks the last-run time for scheduling.

---

**Notes:**

- Synchronizes watched state between Plex and Shoko.
- Default direction is **Plex→Shoko**. Set `import=true` for **Plex←Shoko**.
- Scheduled automations use `RelayConfig.ShokoSyncWatchedFrequencyHours`, `RelayConfig.ShokoSyncWatchedIncludeRatings`, and `RelayConfig.ShokoSyncWatchedExcludeAdmin`.
- Scheduled imports/syncs are anchored to UTC midnight (with an optional offset) rather than relying on the previous run time.
- This means a 24‑hour interval will always fire at midnight (plus offset) and server restarts do not reset the schedule.
- Missed runs are executed on the next interval.
- An interval of 0 disables the scheduler (the dashboard checkbox persists the ratings choice).
- Matching is GUID-based, items lacking a Shoko GUID are skipped.
- The service considers the admin token's user plus any configured ExtraPlexUsers, it obtains per-user tokens via Plex Home switching.
- Export operations skip users without access to a library/section.
- Response object `PlexWatchedSyncResult` includes status, direction, processed counts, per-user summaries, errors, and optional diagnostics.

---

## AnimeThemes

### VFS

```
GET  /animethemes/vfs/build?filter={csv}                       -> AnimeThemesVfsBuild

GET  /animethemes/vfs/map                                      -> AnimeThemesVfsMap

POST /animethemes/vfs/import                                   -> ImportAnimeThemesMapping
```

- `AnimeThemesVfsBuild` applies the mapping file (located in the config directory) to the AnimeThemes directory structure.
  - When `anidb_vfs_overrides.csv` is present, all links for grouped series will be routed into the primary series folder.
  - `filter` restricts the mapping to the given comma separated AniDB IDs.

- `AnimeThemesVfsMap` generates the mapping csv from the current raw source.
  - The resulting file is written to the config directory at `anidb_animethemes_xrefs.csv` in the plugin's _config_ directory.

- `ImportAnimeThemesMapping` downloads the latest mapping csv from the hardcoded Gist URL.

---

**Notes:**

- Example `anidb_animethemes_xrefs.csv` contents (note commas in filenames are encoded as `\u002C`):

```csv
# filepath, videoId, anidbId, newFilename
/60s/ExampleSeries-OP1.webm,12345,6789,OP1 - Hello\u002C World.webm
```

---

### MP3

```
GET  /animethemes/mp3                                          -> AnimeThemesMp3
     [?path={path}&slug={slug}&offset={n}&batch={true|false}&force={true|false}]

GET  /animethemes/mp3/stream?path={path}                       -> AnimeThemesMp3Stream

GET  /animethemes/mp3/random?refresh={true|false}              -> AnimeThemesMp3Random
```

- `AnimeThemesMp3` generates or batches MP3 files for theme folders.
  - `path` (required) the filesystem path to a series folder. Plex-style paths are reverse-mapped automatically.
  - `slug` (optional) override the default selection with a specified OP#/ED#
  - `offset` (optional) when AnimeThemes matches to multiple anime, start at this index (1‑based).
  - `batch` (optional) if true the service will recurse down the directory tree and process every valid subfolder in sequence.
  - `force` (optional) regenerate an MP3 even if one already exists.

---

- `AnimeThemesMp3Stream` streams an existing `Theme.mp3` from the specified folder for in-browser playback.
  - `path` (required) the filesystem path to the folder containing the `Theme.mp3`. Plex-style paths are reverse-mapped automatically.
  - Responds with `audio/mpeg` content type and supports HTTP range requests for seekable playback.
  - Returns `404` if no `Theme.mp3` exists at the resolved path.
  - Used by the dashboard to play theme audio in the background when the `AnimeThemesMp3Playback` setting is enabled.

---

- `AnimeThemesMp3Random` returns the folder path of a randomly selected `Theme.mp3` from the cache.
  - `refresh` (optional, default false) when true forces a full re-scan of all managed import folders and rebuilds the cache.
  - Returns `404` when the cache is empty (no `Theme.mp3` files found).
  - Used by the dashboard shuffle button.

---

**Notes:**

- Skips any subfolder whose name matches the configured VFS/CollectionPosters/AnimeThemes root.
  - subfolders named `misc` are also skipped as the AnimeThemes torrent puts files in there which will always fail to map
- If no mapping entry exists for the specified series/slug the endpoint returns a `skipped` status instead of failing.
- `path` may be a Plex or Shoko relative path; the controller translates them via configured path mappings.
- The `theme.cache` file (located in the plugin's _config_ directory) persists the list of known `Theme.mp3` folder paths across plugin restarts.
  - On first access the cache is loaded from this file. If the file does not exist an empty cache is used.
  - After a full re-scan (`?refresh=true`) or when new MP3s are generated, the file is updated automatically.
  - Each successful single MP3 generation additively appends to the cache without rescanning.

---
