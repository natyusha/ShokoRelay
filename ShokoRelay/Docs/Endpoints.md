# Endpoints

All of the endpoints used by the Shoko Relay plugin are available under the plugin base path: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

## Table of contents

- [Dashboard / Config](#dashboard-config)
- [Metadata Provider](#metadata-provider)
- [Plex: Authentication](#plex-authentication)
- [Plex: Webhook](#plex-webhook)
- [Plex: Collections](#plex-collections)
- [Virtual File System (VFS)](#virtual-file-system-vfs)
- [Sync Watched](#sync-watched)
- [Shoko: Automations](#shoko-automations)
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

- Serves the dashboard UI and static assets (fonts, images, JS/CSS) from the plugin `dashboard` folder.
- `{*path}` is an optional catch-all for dashboard assets.
- `SaveConfig` persists provider settings (path mappings, tokens handled separately).
- Many manual actions also produce plain-text reports saved under a `logs` subfolder
  - download them via the `logUrl` property or the generic `/logs/{fileName}` endpoint.

---

## Metadata Provider

```
GET  /                                                         -> GetMediaProvider (agent descriptor / supported types)
GET  /matches?name={name}&title={id}?manual=1                  -> Match (title may be numeric series ID)
POST /matches                                                  -> Match (body may include `{ Filename, Title, Manual }` for manual searches)

GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}                               -> GetCollectionPoster (image)
GET  /metadata/{ratingKey}?includeChildren=0|1                 -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
GET  /metadata/{ratingKey}/images                              -> GetImages (all image assets for the item)
```

- Purpose: agent discovery, match flows and metadata serving for Plex-compatible GUIDs.
- TMDB episode-numbering: when enabled, the controller/mapper prefers per-episode TMDB links (`IShokoEpisode.TmdbEpisodes`) for coordinate assignment.
- Hidden episodes (`IShokoEpisode.IsHidden`) are excluded from VFS and metadata lists.
- 'Other' type episodes without a TMDB match will attempt to place themselves into 'Season 1' or 'Season 0' (Specials) if either is empty; otherwise they land in Featurettes as extras.
- `Match` accepts either the `name` query (file path) or a JSON body. For manual searches Plex sends `title` (either in query or body) and `manual=1`;
  - when `title` is numeric it is treated as a Shoko series ID. The filename fallback uses the same path-based series extraction as the VFS builder.
- The optional `/metadata/{ratingKey}/images` endpoint returns a `MediaContainer` with an `Image` array of all available assets for the given item.
- `GetCollection` / `GetCollectionPoster` return collection metadata and poster image for a Shoko group.
- `GetMetadata` supports `episode`, `season` and `series` ratingKey formats (see notes below).

---

## Plex: Authentication

```
GET  /plex/auth                                                -> StartPlexAuth (returns pin + authUrl + statusUrl)
GET  /plex/auth/status?pinId={id}                              -> GetPlexAuthStatus (poll for pin completion)
POST /plex/auth/unlink                                         -> UnlinkPlex (revoke & clear saved token)
POST /plex/auth/refresh                                        -> RefreshPlexLibraries (re-discover Shoko libraries)
```

- `StartPlexAuth` returns a PIN and `authUrl` to complete Plex pairing.
- `GetPlexAuthStatus` saves the token and discovers available PMS servers & libraries.

---

## Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- Receives Plex `media.scrobble` events and marks corresponding Shoko episodes watched (when `RelayConfig.AutoScrobble` is enabled).
- Accepts either form-encoded `payload` or raw JSON.
- Only scrobbles from the admin (token owner) and usernames configured in `RelayConfig.ExtraPlexUsers` are processed.
- Extracts the Shoko episode id from `Metadata.guid` (agent GUID); events without a Shoko GUID are ignored (`reason: no_shoko_guid`).

---

## Plex: Collections

```
GET  /plex/collections/build?seriesId={id}&filter={filter}     -> BuildPlexCollections
GET  /plex/collections/posters?seriesId={id}&filter={filter}   -> ApplyCollectionPosters
```

- `build` and `posters` accept either `seriesId` or a `filter` (comma-separated list) — not both.
- Operations run per-configured Plex target; responses include processed/created/uploaded counts and errors.

---

## Virtual File System (VFS)

```
GET /vfs                                                       -> BuildVfs (see note below)
    [?run={true|false}&clean={true|false}&cleanOnly={true|false}&filter={filter}]
```

Parameters:

- `run` (bool) : actually builds the VFS
- `clean` (bool) : remove/clean root first (default: true)
- `cleanOnly` (bool) : perform only the cleanup stage (ignores `run`); returns once deletion is finished
- `filter` (csv) : comma-separated ShokoSeriesIDs to restrict processing; enter 0 to initiate a cleanOnly

- When `run=true` and the configured Plex client has `ScanOnVfsRefresh`, the controller will schedule Plex scans for affected series.
- `MapHelper` produces `FileMapping` objects consumed by `VfsHelper` and `PlexMetadata` (see `MapHelper.GetSeriesFileData`).
- Crossover episodes (videos referenced by multiple AniDB/Shoko series) are handled specially: the VFS build will **not** copy/link local metadata or subtitles for crossover files to avoid conflicts.
- A plain‑text report of each VFS build is saved to `vfs-report.log` in the plugin directory
- You can download the most recent report via the `logUrl` property returned in the JSON response when `run=true` (logs now live under `/logs/`).

---

## Sync Watched

```
GET  /syncwatched                                              -> SyncPlexWatched (for preview and testing usage)
POST /syncwatched                                              -> SyncPlexWatched
     [?dryRun={true|false}&sinceHours={int}&ratings={true|false}&import={true|false}&excludeAdmin={true|false}]

GET  /syncwatched/start                                        -> StartWatchedSyncNow
```

- Synchronizes watched-state between Plex and Shoko.
  - Default: **Plex -> Shoko**. Use `import=true` for **Shoko -> Plex**.
  - Automation is governed by `RelayConfig.ShokoSyncWatchedFrequencyHours` (interval) and
    `RelayConfig.ShokoSyncWatchedIncludeRatings` (include ratings); interval 0 disables
    the scheduler and the dashboard checkbox persists the ratings flag.
- Parameters:
  - `dryRun` — omitted => dry-run (no writes). Only `?dryRun=false` performs writes.
  - `sinceHours` — restrict to recent activity. (Scheduled automation automatically adds this based in the interval +1 hour)
  - `ratings` / `votes` — include user ratings when `true`.
  - `import` — direction toggle (Shoko→Plex when `true`).
  - `excludeAdmin` — when exporting, skip applying changes for the admin account (useful with ExtraPlexUsers).

- Manual trigger: `GET /syncwatched/start` triggers a one-off watched-state sync and marks the automation's last-run time so the scheduler calculates the next run from "now".

Behavior highlights:

- Mapping strategy: **GUID-only** — items without a Shoko GUID are skipped.
- The endpoint examines the admin token's user and any `RelayConfig.ExtraPlexUsers` entries; per-user tokens are obtained transiently via Plex Home switch.
- Export (Shoko→Plex) skips users who don't have access to the target library/section.
- Results include `perUser` summaries and `perUserChanges` audit lists (candidates where `WouldMark` is true).

Response includes (`PlexWatchedSyncResult`):

```
{ status, direction, processed, marked, skipped, scheduled, votesFound, votesUpdated, votesSkipped, matched, missingMappings, perUser, perUserChanges, errors, errorsList }
```

> **Log note:** when a real sync runs (dryRun=false) the `sync.log` file will list the number of missing mappings and, if any, the specific Shoko episode IDs that could not be matched. This prevents the previous `System.Collections.Generic.List`1[...]` output.

---

## Shoko: Automations

```
POST /shoko/remove-missing                                     -> RemoveMissingFiles
POST /shoko/import                                             -> RunShokoImport
GET  /shoko/import/start                                       -> StartShokoImportNow
```

- `POST /shoko/import` triggers a Shoko import and returns `{ status: "ok", scanned: [...], scannedCount: n }`.
- `POST /shoko/remove-missing` calls Shoko's RemoveMissingFiles action; optional `removeFromMyList` query param (default `true`).

- Manual trigger: `GET /shoko/import/start` triggers a one-off import and marks the automation's last-run time so the scheduler calculates the next run from "now".

---

## AnimeThemes

### AnimeThemes VFS

```
GET /animethemes/vfs/build                                     -> Apply mapping to VFS
    [?mapPath={map.json}&torrentRoot={root}&filter={csv}]

GET /animethemes/vfs/map                                       -> Build animethemes mapping JSON
    [?mapPath={map.json}&torrentRoot={root}]

POST /animethemes/vfs/import                                   -> Import mapping from hardcoded raw URL
```

- `build` and `map` perform the operations described above.
- `import` will download the latest mapping JSON from a hardcoded raw URL (Gist raw link) and write it to `AniDB-AnimeThemes-xrefs.json` in the plugin directory.

### AnimeThemes MP3

```
GET /animethemes/mp3                                           -> AnimeThemesMp3
    [?path={path}&slug={slug}&offset={n}&batch={true|false}&force={true|false}]
```

- Creates `Theme.mp3` files for folders. `batch=true` processes all subfolders under `path`.
- The batch operation automatically skips any child folder whose name matches the configured **VFS Root Path**, **Collection Posters Root Path**, or **AnimeThemes Root Path** (case‑insensitive). This prevents attempts to process system directories used by the plugin.
- If AnimeThemes has no entry for a particular series (or for a specific slug when provided), the request will return a `skipped` status with an explanatory message instead of an error. Callers such as the dashboard will display this as a warning toast.
- `path` may be a Plex-style path; controller maps it back to Shoko path mappings when necessary.

---
