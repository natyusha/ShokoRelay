# Endpoints

All of the endpoints used by the Shoko Relay plugin are available under the plugin base path: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

---

## Provider & Matching

```
GET  /                                                        -> GetMediaProvider (agent descriptor / supported types)
GET  /matches?name={name}                                     -> Match (also accepts POST body { Filename })
POST /matches                                                 -> Match
```

- Purpose: agent discovery & file->series quick-match used by Plex / agent match flows.
- Match params: `name` query OR POST body `{ Filename: string }` (will attempt PathEndsWith lookup).

---

## Dashboard (UI assets)

```
GET /dashboard/{*path}                                        -> Serve plugin dashboard index and static assets (fonts/images)
```

- This
- `{*path}` is an optional catch-all for dashboard assets; the index is served from the plugin `dashboard` folder.

---

## Plex authentication & discovery

```
GET  /plexauth                                                -> StartPlexAuth (returns pin + authUrl + statusUrl)
GET  /plexauth/status?pinId={id}                              -> GetPlexAuthStatus (poll for pin completion)
POST /plex/unlink                                             -> UnlinkPlex (revoke & clear saved token)
POST /plex/libraries/refresh                                  -> RefreshPlexLibraries (re-discover Shoko libraries)
```

- `StartPlexAuth` returns a PIN code and an `authUrl` for the user to complete Plex pairing.
- `GetPlexAuthStatus` saves the token to preferences when pairing completes and discovers Plex servers/libraries.

---

## Configuration

```
GET  /config                                                  -> GetConfig (returns current RelayConfig)
POST /config                                                  -> SaveConfig (body: RelayConfig)
GET  /config/schema                                           -> GetConfigSchema (JSON schema for UI)
```

- `SaveConfig` normalizes and persists settings (path mappings, CSV fields, tokens stored separately).

---

## Plex collections & posters

```
GET  /plex/collections/build?seriesId={id}&filter={filter}    -> BuildPlexCollections
GET  /plex/collections/posters?seriesId={id}&filter={filter}  -> ApplyCollectionPosters
GET  /collections/{groupId}                                   -> GetCollection
GET  /collections/user/{groupId}                              -> GetCollectionPoster (image)
```

- `build` and `posters` accept `seriesId` or a `filter` (comma-separated filter) — not both.
- Responses contain process counts and optional error lists.

---

## Plex: Sync Watched

```
GET|POST /plex/syncwatched[?dryRun={true|false}&sinceHours={int}]
```

- Purpose: synchronize watched-state reported by Plex into Shoko user data.
- Query parameters:
  - `dryRun` (optional, bool) — when `true` the endpoint will **not** write any changes to Shoko; instead it returns a detailed audit of every change it would have made (developer/testing only).

Behavior

- The plugin fetches watched-state for:
  - the configured Plex token (the Plex "admin"/owner account), and
  - any managed Plex usernames listed in `RelayConfig.ExtraPlexUsers`. These are configured in the dashboard UI (comma separated).

- Behavior for managed/home users:
  - Extra Plex usernames may optionally include a 4‑digit PIN using the format `username;1234`. Example: `alice;1234, bob`.
    - If a semicolon is present and the value after it is exactly four digits, that four‑digit value is treated as the user's PIN and will be supplied to the Plex `/switch` call as `&pin=1234`.
    - If the semicolon is present but the part after it is not a 4‑digit integer (for example `user;1`), the entire string is treated as the username (the semicolon is preserved in the username).
  - If an extra username is configured the plugin will automatically attempt to obtain a transient per-user token from Plex (Plex Home "switch" API) using the configured Admin token. Managed/home user tokens are session-scoped; the plugin fetches them at runtime and does **NOT** persist them.

- Mapping strategy: **GUID-only** — the endpoint requires a Shoko GUID embedded in Plex metadata and will **skip** items without a Shoko GUID (`Reason: no_shoko_guid`).
- Real runs only write Shoko watched-state for candidates where `WouldMark` would be true; dry-run uses the same eligibility logic and reports `WouldMark` without applying changes.
- All watched states are applied to the first Shoko user returned by `IUserService` (the "main" Shoko user). The plugin does **not** support separate Shoko users.
- Duplicate episodes (same Shoko episode reported by multiple Plex users) are de-duplicated so each Shoko episode is written at most once per sync.

Request

- No body required. Use `?dryRun=true` to preview changes. Omitting `dryRun` defaults to a safe dry-run (no writes).
- Optionally pass `sinceHours` to limit results to recently-viewed items — this is only applied when provided.
  - The scheduled automation (enabled by `ShokoSyncWatchedFrequencyHours`) automatically applies the same time-window to speed up processing.

Response (JSON)

```
{
  status: "ok",
  processed: <int>,            // total Plex items examined
  marked: <int>,               // total Shoko episodes marked watched
  skipped: <int>,
  scheduled: <int>,            // server jobs scheduled (if any)
  perUser: {                   // per-Plex-user summary
    "admin": { processed, markedWatched, skipped, errors },
    "otherUser": { ... }
  },
  perUserChanges: {            // audit list of changes (present in dry-run and real runs)
    "admin": [
      { PlexUser, ShokoEpisodeId, SeriesTitle, EpisodeTitle, SeasonNumber, EpisodeNumber, RatingKey, Guid, FilePath, LastViewedAt, WouldMark, AlreadyWatchedInShoko, Reason }
    ],
    "otherUser": [ ... ]
  },
  errors: <int>,
  errorsList: ["...", ...]
}
```

- `perUserChanges` details every candidate change. Fields:
  - `PlexUser`: the Plex account whose view produced this record
  - `ShokoEpisodeId`: the target Shoko episode id
  - `SeriesTitle` / `EpisodeTitle` / `SeasonNumber` / `EpisodeNumber`: human-readable metadata when available
  - `RatingKey` / `Guid` / `FilePath`: Plex metadata used for mapping
  - `LastViewedAt`: Plex's epoch-derived last-view timestamp (UTC)
  - `WouldMark`: whether this run would mark the episode watched (false in dry-run when already watched or no files)
  - `AlreadyWatchedInShoko`: whether Shoko already considers the episode watched for the target user
  - `Reason`: when not applying, a short reason (e.g. `already_watched`, `no_files`, `duplicate`)

Security / usage notes

- `dryRun` is meant for developer/testing use — the dashboard UI does **not** expose a dry-run toggle.
  - The dashboard's **Sync** button performs a real run (equivalent to calling the API with `?dryRun=false`); call the endpoint directly with `?dryRun=true` to preview changes.

Examples

- Dry run (developer):
  - `POST /api/plugin/ShokoRelay/plex/syncwatched?dryRun=true`
- Real run (admin/automation):
  - `POST /api/plugin/ShokoRelay/plex/syncwatched?dryRun=false`

---

## Plex metadata endpoints

```
GET  /metadata/{ratingKey}?includeChildren=0|1                -> GetMetadata
GET  /metadata/{ratingKey}/children                           -> GetChildren
GET  /metadata/{ratingKey}/grandchildren                      -> GetGrandchildren
```

- `ratingKey` formats:
  - Episode: `e{ShokoEpisodeID}` optionally `e{ShokoEpisodeID}p{PartNumber}` for multi-part files
  - Season: `{ShokoSeriesID}s{SeasonNumber}`
  - Series: `{ShokoSeriesID}`
  - Note: `ratingKey` formats used by the metadata endpoints use the `PlexConstants` prefixes: `s` = season, `e` = episode, `p` = part.
- `GetMetadata` behavior:
  - Episode requests return episode metadata (uses TMDB per-episode overrides when available).
  - Season requests return a season metadata object (optionally include episode children).
  - Series requests return series metadata (optionally include all seasons as children).

---

## Debug endpoints

```
GET  /debug/file/{shokoFileId}                                -> DebugFile (returns MapHelper diagnostics for a specific Shoko file/video id)
GET  /debug/series/{shokoSeriesId}                            -> DebugSeries (returns MapHelper diagnostics for every file in the series; `files` array contains the same payloads produced by `/debug/file/{shokoFileId}`)
```

- Purpose: inspect the exact inputs, intermediate steps, and outputs that `MapHelper.BuildFileMappings` uses when assigning Plex coordinates (`PlexCoords`), part/index decisions, TMDB overrides, and deduplication.
- Returns: `FileMappingDebugInfo` / `EpisodeDebugInfo` payloads containing per-file filename/path, `FileIndex`/`FileCount`, `HasPlexSplitTag`, `AllowPartSuffix`, `FileIndexParam`, `FinalCoords`, selected TMDB episode (if any), sorted/filtered/deduped lists, per-episode `CrossRefPercentage` and `CrossRefOrder` (when available), and a **final `FileMapping` summary** (primitive fields only) when available.
- Usage: helpful for debugging why a file is mapped to the season/episode it is, or why it is excluded.

---

## Virtual File System (VFS)

```
GET /vfs?run={true|false}&clean={true|false}&filter={filter}
```

_Parameters_

```
- run (bool)           : actually builds the VFS
- clean (bool)         : controls whether VFS root is cleaned first (default 'true').
- filter (string csv)  : comma-separated 'ShokoSeriesID' values to restrict processing. To target a single series pass a single value (e.g. 'filter=123').
```

- If `run=true` and the configured Plex client has `ScanOnVfsRefresh`, the controller automatically triggers Plex scans for affected series paths.

---

## AnimeThemes VFS

```
GET /animethemes/vfs?mapping={true|false}&applyMapping={true|false}&mapPath={map.json}&torrentRoot={root}&filter={csv}
```

Purpose: build or apply AnimeThemes mapping files `AniDB-AnimeThemes-xrefs.json` which are placed in the plugin's root.

_Parameters_

```
mapping (bool)         : scan a torrent root and generate an AnimeThemes mapping JSON ('AnimeThemesMappingBuildResult')
applyMapping (bool)    : read mapping file and apply the mappings, creating VFS links ('AnimeThemesMappingApplyResult)
mapPath (string)       : mapping file path to read/write
torrentRoot (string)   : optional scan root for mapping/build (overrides configured AnimeThemesPathMapping)
filter (string csv)    : comma-separated ShokoSeriesIDs to restrict 'applyMapping'
```

---

## AnimeThemes MP3

```
GET /animethemes/mp3?path={path}&slug={slug}&offset={n}&batch={true|false}&force={true|false}
```

Purpose: create `Theme.mp3` files for folders. `batch=true` processes every subfolder under `path` and returns a `ThemeMp3BatchResult`.

_Parameters_

```
path (string)          : the path to check for a series relative to plex or shoko via 'PathMappings' (required for single and batch operations).
slug (string)          : theme selector (e.g., 'op', 'ed', 'op1-tv')
offset (int)           : index into AnimeThemes API results
batch (bool)           : recursively process all subfolders under 'path' (returns 'ThemeMp3BatchResult')
force (bool)           : overwrite existing 'Theme.mp3' (default 'false').
```

---

## Notes & behaviors

- TMDB episode-numbering: when enabled, the controller/mapper prefers per-episode TMDB links (`IShokoEpisode.TmdbEpisodes`) for coordinate assignment.
- Hidden episodes (`IShokoEpisode.IsHidden`) are excluded from VFS and metadata lists.
- `MapHelper` produces `FileMapping` objects consumed by `VfsHelper` and `PlexMetadata` (see `MapHelper.GetSeriesFileData`).
- Crossover episodes: a crossover is a file/video that is cross-referenced to episodes in more than one distinct AniDB/Shoko series.
  - The VFS build will **not** copy/link local metadata (art, Theme.mp3) or subtitles for crossover files to prevent metadata from one series overwriting another.
- Sync Watched automation: controlled by `RelayConfig.SyncPlexWatched` (enable/disable) and `RelayConfig.ShokoSyncWatchedFrequencyHours` (interval).
  - Use the API `dryRun=true` to preview changes. The `dryRun` query accepts only `true` or `false`; omitting it defaults to `true` (safe dry-run) and invalid values return HTTP 400.
  - The plugin fetches watched-state for the configured Plex token **and** any usernames configured in `RelayConfig.ExtraPlexUsers` and applies changes to the first Shoko user returned by `IUserService` (the main Shoko user).
  - Real runs only apply writes for candidates where `WouldMark` is true. Note: the dashboard's Sync button performs a real run (use the API with `?dryRun=true` to preview).
