# Endpoints

All of the endpoints used by the Shoko Relay plugin are available under the plugin base path: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

## Table of contents

- [Dashboard / Config](#dashboard-config)
- [Metadata Provider](#metadata-provider)
- [Plex: Authentication](#plex-authentication)
- [Plex: Webhook](#plex-webhook)
- [Plex: Collections](#plex-collections)
- [Virtual File System (VFS)](#virtual-file-system-vfs)
- [Shoko: Automations](#shoko-automations)
- [Sync Watched](#sync-watched)
- [AnimeThemes](#animethemes)

---

## Dashboard / Config

```
GET  /dashboard/{*path}                                        -> GetControllerPage (serve dashboard index & assets)
GET  /config                                                   -> GetConfig
POST /config                                                   -> SaveConfig
GET  /config/schema                                            -> GetConfigSchema
```

- Serves the dashboard UI and static assets (fonts, images, JS/CSS) from the plugin `dashboard` folder.
- `{*path}` is an optional catch-all for dashboard assets.
- `SaveConfig` persists provider settings (path mappings, tokens handled separately).

---

## Metadata Provider

```
GET  /                                                         -> GetMediaProvider (agent descriptor / supported types)
GET  /matches?name={name}                                      -> Match (also accepts POST body `{ Filename }`)
POST /matches                                                  -> Match
GET  /collections/{groupId}                                    -> GetCollection
GET  /collections/user/{groupId}                               -> GetCollectionPoster (image)
GET  /metadata/{ratingKey}?includeChildren=0|1                 -> GetMetadata
GET  /metadata/{ratingKey}/children                            -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                       -> GetGrandchildren
```

- Purpose: agent discovery, match flows and metadata serving for Plex-compatible GUIDs.
- `Match` accepts `name` query OR POST body `{ Filename: string }` and extracts a Shoko file id when present (VFS-style `[ShokoFileId]` token).
- `GetCollection` / `GetCollectionPoster` return collection metadata and poster image for a Shoko group.
- `GetMetadata` supports `episode`, `season` and `series` ratingKey formats (see notes below).

---

## Plex: Authentication

```
GET  /plexauth                                                 -> StartPlexAuth (returns pin + authUrl + statusUrl)
GET  /plexauth/status?pinId={id}                               -> GetPlexAuthStatus (poll for pin completion)
POST /plex/unlink                                              -> UnlinkPlex (revoke & clear saved token)
POST /plex/libraries/refresh                                   -> RefreshPlexLibraries (re-discover Shoko libraries)
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
GET /vfs?run={true|false}&clean={true|false}&filter={filter}   -> BuildVfs
```

Parameters:

- `run` (bool) : actually builds the VFS
- `clean` (bool) : remove/clean root first (default: true)
- `filter` (csv) : comma-separated ShokoSeriesIDs to restrict processing

- When `run=true` and the configured Plex client has `ScanOnVfsRefresh`, the controller will schedule Plex scans for affected series.

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
- Parameters:
  - `dryRun` — omitted => dry-run (no writes). Only `?dryRun=false` performs writes.
  - `sinceHours` — restrict to recent activity.
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
GET /animethemes/vfs?mapping={true|false}&applyMapping={true|false}&mapPath={map.json}&torrentRoot={root}&filter={csv}  -> AnimeThemesVfs
```

- `mapping=true` builds mapping JSON from a torrent root.
- `applyMapping=true` reads a mapping file and applies mappings to create VFS links for series (accepts optional `filter`).
- If neither `mapping` nor `applyMapping` is provided, the endpoint returns HTTP 400.

### AnimeThemes MP3

```
GET /animethemes/mp3?path={path}&slug={slug}&offset={n}&batch={true|false}&force={true|false}  -> AnimeThemesMp3
```

- Creates `Theme.mp3` files for folders. `batch=true` processes all subfolders under `path`.
- `path` may be a Plex-style path; controller maps it back to Shoko path mappings when necessary.

---

## Notes & Behaviors

- TMDB episode-numbering: when enabled, the controller/mapper prefers per-episode TMDB links (`IShokoEpisode.TmdbEpisodes`) for coordinate assignment.
- Hidden episodes (`IShokoEpisode.IsHidden`) are excluded from VFS and metadata lists.
- 'Other' type episodes without a TMDB match will attempt to place themselves into 'Season 1' or 'Season 0' (Specials) if either is empty. Otherwise, they will be placed in 'Featurettes' and display as extras in Plex.
- `MapHelper` produces `FileMapping` objects consumed by `VfsHelper` and `PlexMetadata` (see `MapHelper.GetSeriesFileData`).
- Crossover episodes: a crossover is a file/video that is cross-referenced to episodes in more than one distinct AniDB/Shoko series.
  - The VFS build will **not** copy/link local metadata (art, Theme.mp3) or subtitles for crossover files to prevent metadata from one series overwriting another.
- Sync Watched automation: controlled by `RelayConfig.ShokoSyncWatchedFrequencyHours` (interval) and `RelayConfig.ShokoSyncWatchedIncludeRatings` (whether scheduled runs include ratings). The automation is enabled when the interval is > 0 and disabled when it is 0. The dashboard's **Include Ratings** checkbox persists to `ShokoSyncWatchedIncludeRatings` so scheduled automation follows the dashboard preference.
  - Use the API `dryRun=true` to preview changes. The `dryRun` query accepts only `true` or `false`; omitting it defaults to a safe dry-run and invalid values return HTTP 400.
  - The plugin fetches watched states for the configured Plex token **and** any usernames configured in `RelayConfig.ExtraPlexUsers` and applies changes to the first Shoko user returned by `IUserService` (the main Shoko user).
  - Real runs only apply writes for candidates where `WouldMark` is true. Note: the dashboard's Sync button performs a real run (use the API with `?dryRun=true` to preview).
