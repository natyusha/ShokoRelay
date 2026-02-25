# Controller

All of the endpoints below are available under the plugin base path: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`

## Table of Contents

- [Dashboard / Config](#dashboard--config)
- [Metadata Provider](#metadata-provider)
- [Plex: Authentication](#plex-authentication)
- [Plex: Automation](#plex-automation)
- [Plex: Webhook](#plex-webhook)
- [Virtual File System (VFS)](#virtual-file-system-vfs)
- [Shoko: Automation](#shoko-automation)
- [Sync Watched](#sync-watched)
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

- `GetConfig` returns the current plugin configuration payload (JSON) used by the dashboard page.
  - The `ConfigProvider` handles serialization, sanitization and omits any sensitive fields.
- `SaveConfig` persists automation/provider settings (tokens handled separately).
  - `/config` does not expose the Plex token. Instead the response includes `PlexLibrary.HasToken` which indicates token validity.
  - The actual secret lives only in `plex.token`.
- `GetConfigSchema` returns a JSON schema representation of `RelayConfig` properties.
  - Used by the dashboard to dynamically render the settings form with correct field names/types.

- `GetLog` many manual actions also produce plain-text reports saved under a `logs` subfolder
  - download them via the `logUrl` property or the generic `/logs/{fileName}` endpoint.

---

## Metadata Provider

```
GET  /                                                         -> GetMediaProvider (agent descriptor / supported types)
GET  /matches?name={name}&title={id}?manual=1                  -> Match (title is a ShokoSeriesID)
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

- `GetCollection` retrieves collection metadata for a given group ID.
- `GetCollectionPoster` returns the poster image.

- `GetMetadata` returns full metadata for a ratingKey (series/season/episode).
  - `includeChildren` (optional, 0/1) controls whether nested items are included.
- `GetChildren` / `GetGrandchildren` return only the immediate or second-level child items respectively.
- `GetImages` returns a `MediaContainer` with an `Image` array.
  - Used by Plex when fetching all artwork for an item.

**Notes:**

- TMDB episode‑numbering is honoured when enabled (uses `IShokoEpisode.TmdbEpisodes`).
- Hidden episodes are excluded.
- Episodes of type "Other" without a TMDB match are placed in Season 1/0 or treated as extras.
- RatingKey formats supported: `123` (series), `123s4` (season 4), `e56789` (episode).

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

## Plex Automation

```
GET  /plex/collections/build?seriesId={id}&filter={filter}     -> BuildPlexCollections
GET  /plex/collections/posters?seriesId={id}&filter={filter}   -> ApplyCollectionPosters

GET  /plex/ratings/apply?seriesId={id}&filter={filter}         -> ApplyCriticRatings

GET  /plex/automation/run                                      -> RunPlexAutomationNow
```

- `BuildPlexCollections` generate Plex collections for the specified series or filter.
- `ApplyCollectionPosters` upload or refresh posters for the same series set.

- `ApplyCriticRatings` update series/episode ratings based on the configured source (TMDB/AniDB).

- `RunPlexAutomationNow` triggers the two operations above back-to-back.
  - Useful if you want to force automation without waiting for the scheduled interval.
  - The scheduler itself is governed by `RelayConfig.PlexAutomationFrequencyHours` (0 disables it).

**Notes:**

- Each of the above (other than `RunPlexAutomationNow`) accepts either `seriesId` _or_ a comma-separated `filter`; not both.
  - All work is performed per-configured Plex target and return counts/summary information.

---

## Plex: Webhook

```
POST /plex/webhook                                             -> PluginPlexWebhook
```

- `PluginPlexWebhook` handles Plex `media.scrobble` callbacks and synchronizes watched status to Shoko.
  - Supports both form‑encoded `payload` fields and raw JSON bodies.
  - Only events originating from the admin or users listed in `RelayConfig.ExtraPlexUsers` are considered. Others are ignored.
  - The service extracts the ShokoEpisodeID from the `Metadata.guid` value `tv.plex.agents.custom.shoko://episode/{ShokoEpisodeID}`.
  - Events without a GUID are dropped `reason: no_shoko_guid`.

---

## Virtual File System (VFS)

```
GET /vfs                                                       -> BuildVfs
    [?run={true|false}&clean={true|false}&cleanOnly={true|false}&filter={filter}]
```

- `BuildVfs` (all query parameters are optional)
  - `run` (default false) if true the VFS is constructed; when false the call just returns metadata.
  - `clean` (default true) clear the existing root before building.
  - `cleanOnly` (default false) perform only the cleanup stage (ignores `run`).
  - `filter` comma-separated Shoko series IDs to restrict processing (the value `0` forces `cleanOnly`).

**Notes:**

- When the Plex configuration has `ScanOnVfsRefresh`, the controller schedules library scans for affected series automatically.
- `MapHelper.GetSeriesFileData` generates `FileMapping` objects consumed by `VfsHelper`/`PlexMetadata` to translate Shoko to Plex paths.
- Crossover episodes (files belonging to multiple AniDB/Shoko series) are skipped for metadata/subtitle copying to avoid conflicts.
- Each build writes a plain-text report to `vfs-report.log` in the plugin directory; the UI exposes a `logUrl` property to download it.
- When importing local metadata images, files named `Specials.<ext>` will be renamed to `Season-Specials-Poster.<ext>` in the VFS
  - This is purely for the aesthetics of the original file structure (where `<ext>` is one of the supported image extensions)

---

## Shoko: Automation

```
POST /shoko/remove-missing?removeFromMyList={true|false}       -> RemoveMissingFiles

POST /shoko/import                                             -> RunShokoImport
GET  /shoko/import/start                                       -> StartShokoImportNow
```

- `RemoveMissingFiles` calls Shoko's RemoveMissingFiles action.
  - `removeFromMyList` (default=true) controls whether items are also removed from MyList.

- `RunShokoImport` triggers a Shoko source import and replies with `{ status:"ok", scanned:[...], scannedCount:n }`.
- `StartShokoImportNow` forces an immediate import and updates the scheduler's last-run time.

**Notes:**

- Scheduled imports/syncs are anchored to UTC midnight (with an optional offset) rather than relying on the previous run time.
- This means a 24‑hour interval will always fire at midnight (plus offset) and server restarts do not reset the schedule.
- Missed runs are executed on the next interval.

---

## Sync Watched

```
GET  /syncwatched                                              -> SyncPlexWatched (for preview/testing)
POST /syncwatched                                              -> SyncPlexWatched
     [?dryRun={true|false}&sinceHours={int}&ratings={true|false}&import={true|false}&excludeAdmin={true|false}]

GET  /syncwatched/start                                        -> StartWatchedSyncNow
```

- `SyncPlexWatched` (all query parameters are optional)
  - `dryRun` (default true) perform a dry run (no writes). Specify `false` to make actual changes.
  - `sinceHours` – limit syncing to items changed in the last N hours (automation uses interval+1).
  - `ratings` (default false) include user ratings when true.
  - `import` (default false) run direction Plex←Shoko instead of Plex→Shoko.
  - `excludeAdmin` (default false) when exporting skip the admin Plex user, useful with configured ExtraPlexUsers.

- `StartWatchedSyncNow` triggers a one-off sync and marks the last-run time for scheduling.

**Notes:**

- Synchronizes watched state between Plex and Shoko.
- Default direction is **Plex→Shoko**. Set `import=true` for **Plex←Shoko**.
- Scheduled automations use `RelayConfig.ShokoSyncWatchedFrequencyHours` and `RelayConfig.ShokoSyncWatchedIncludeRatings`.
- An interval of 0 disables the scheduler (the dashboard checkbox persists the ratings choice).
- Matching is GUID-based, items lacking a Shoko GUID are skipped.
- The service considers the admin token's user plus any configured ExtraPlexUsers, it obtains per-user tokens via Plex Home switching.
- Export operations skip users without access to a library/section.
- Response object `PlexWatchedSyncResult` includes status, direction, processed counts, per-user summaries, errors, and optional diagnostics.

---

## AnimeThemes

### AnimeThemes VFS

```
GET /animethemes/vfs/build?mapPath={map.json}&filter={csv}     -> AnimeThemesVfsBuild

GET /animethemes/vfs/map?mapPath={map.json}                    -> AnimeThemesVfsMap

POST /animethemes/vfs/import                                   -> ImportAnimeThemesMapping
```

- `AnimeThemesVfsBuild` applies the mapping file to the AnimeThemes directory structure.
  - `mapPath` (optional) lets you specify a custom JSON file instead of the default `anidb_animethemes_xrefs.json`.
  - `filter` restricts the mapping to the given comma-separated AniDB IDs.

- `AnimeThemesVfsMap` generates the mapping JSON from the current raw source.
  - `mapPath` (optional) overrides the default output file path.

- `ImportAnimeThemesMapping` downloads the latest mapping JSON from the hardcoded Gist URL.
  - This is written to `anidb_animethemes_xrefs.json` in the plugin folder.

### AnimeThemes MP3

```
GET /animethemes/mp3                                           -> AnimeThemesMp3
    [?path={path}&slug={slug}&offset={n}&batch={true|false}&force={true|false}]
```

- `AnimeThemesMp3` generates or batches MP3 files for theme folders.
  - `path` (required) the filesystem path to a series folder. Can be in Plex form which will be translated.
  - `slug` (optional) override the default selection with a specified OP#/ED#
  - `offset` (optional) when AnimeThemes matches to multiple anime, start at this index (1‑based).
  - `batch` (optional) if true the service will recurse down the directory tree and process every valid subfolder in sequence.
  - `force` (optional) regenerate an MP3 even if one already exists.

**Notes:**

- Skips any subfolder whose name matches the configured VFS/CollectionPosters/AnimeThemes root.
  - subfolders named `misc` are also skipepd as the AnimeThemes torrent puts files in there which will always fail to map
- If no mapping entry exists for the specified series/slug the endpoint returns a `skipped` status instead of failing.
- `path` may be a Plex or Shoko relative path; the controller translates them via configured path mappings.

---
