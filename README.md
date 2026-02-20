<!-- prettier-ignore-start -->

![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.com/channels/96234011612958720/268484849419943936)
[![GitHub Latest](https://img.shields.io/github/v/tag/natyusha/ShokoRelay?label=Latest&logo=github&logoColor=fff)](https://github.com/natyusha/ShokoRelay/releases/latest)
-

<!-- prettier-ignore-end -->

This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex. It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and intends to mirror all of its functionality (including the automation scripts). Scanning is much faster and it will be possible to add many new features in the future as well.

Due to the lack of a custom scanner this plugin leverages a VFS (Virtual File System) to ensure that varied folder structures are supported. This means that your anime can be organised with whatever file or folder structure you want. The only caveat is that a folder cannot contain more than one AniDB series at a time if you want it to correctly support [local media assets](https://support.plex.tv/articles/200220717-local-media-assets-tv-shows/) like `Theme.mp3`. The VFS will be automatically updated when a file move or rename is detected by Shoko.

## Installation

### Shoko

> [!IMPORTANT]
> The VFS is created inside each Shoko import folder under the folder name configured as `VFS Root Path` (default `!ShokoRelayVFS`). To stop Shoko from scanning the generated links, add the following regex entries to `settings-server.json` under `Exclude`:
>
> ```json
> "Exclude": [
>   "[\\\\\\/]!AnimeThemes[\\\\\\/]",
>   "[\\\\\\/]!ShokoRelayVFS[\\\\\\/]"
> ],
> ```

- Be sure to also exclude the `AnimeThemes Root Path` (default `!AnimeThemes`) if you plan on utilising AnimeThemes `.webms`
- After making sure the VFS is excluded in Shoko's settings, extract [the latest release](https://github.com/natyusha/ShokoRelay/releases/latest) into Shoko Server's `plugins` directory
- Restart Shoko Server

#### Setup

- Once the Server has loaded navigate to Shoko Relay's dashboard: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/dashboard`
- Mandatory
  - Click the `Generate VFS` button in the "Shoko: VFS" section to initialize your collection
  - First time generation may take several minutes to complete with a large library
  - A report of the run will be written to `vfs-report.log` inside the plugin directory
    - You can download the latest report via the dashboard toast that appears when the process finishes
  - The VFS will automatically update when it detects files have been renamed or moved
- Optional
  - Link the plugin to your Plex account to enable auto scanning, scrobbling (webhooks) and enhanced collection support
  - Add a Shoko API Key from `http://{ShokoHost}:{ShokoPort}/webui/settings/api-keys` to enable watch sync and import tasks
- There are additional options similar to what the legacy agent had at the bottom under "Provider Configuration"

> [!TIP]
> If you are sharing the symlinks over an SMB share they may not appear depending on the [Samba Configuration](https://www.samba.org/samba/docs/current/man-html/smb.conf.5.html). An example entry for `smb.conf` that may help is listed below:

```ini
[global]
    follow symlinks = yes
```

### Plex

#### Metadata Agent

- Navigate to `Settings > Metadata Agents`
- Click `Add Provider` in the Metadata Providers header and supply the URL: `http://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`
- Click `Add Agent` in the Metadata Agents header, name it `Shoko Relay` and select `Shoko Relay` as the primary provider
- Under `additional providers` select `Plex Local Media` then click the `+` and `Save`

#### Library

> [!TIP]
> If you previously used the legacy `ShokoRelay.bundle` you can simply convert your existing libraries to the new agent.
> This allows you to maintain watched states and video preview thumbnails. This requires a full metadata refresh after the first scan.

- The Shoko Relay agent requires a `TV Shows` type library to be created (or an existing one to be used)
- Simply change the Scanner to `Plex TV Series` and the Agent to `Shoko Relay`
- When adding your import folders to plex be sure to point them to the `!ShokoRelayVFS` directory
- Under "Advanced" in the Library it is recommended to set these settings:
  - Use season titles
  - Use local assets
  - Collections: `Hide items which are in collections`
  - Seasons: `Hide for single-season series`

## AnimeThemes Integration

### Themes as Video Extras

This plugin includes full integration for [AnimeThemes](https://animethemes.moe/). It will look for `.webm` theme files in a folder called `!AnimeThemes` which is located in the root of your anime library. These files must have the same filename as they do on the AnimeThemes website and then a mapping must be generated for them in what is essentially a 3 step process. Simply navigate to the "AnimeThemes: VFS" section of the dashboard page to get started.

1. Download anime theme videos and place them in the `!AnimeThemes` folder
   - There is a torrent available with over 19000+ themes
2. Generate a mapping for the the videos by clicking the `Build Mapping` button:
   - A mapping for the current torrent is available [here](https://gist.github.com/natyusha/4e29252d939d0f522d38732facf328c7) which you can place in the Shoko Relay's plugin directory (mapping the whole torrent takes ~12 hours due to rate limits)
3. Apply the mapping to the VFS by clicking the `Apply Mapping to VFS` button

> [!IMPORTANT]
> Similar to the VFS you must exclude the `!AnimeThemes` folder from Shoko using the `Exclude` server option. You must also configure the basepath for where the original files are located (the default is `/animethemes/`).

### Themes as Series BGM

There is also support for generating `Theme.mp3` files as local metadata. This will add them to the VFS automatically and can be run for either a single series or as a batch operation. This requires Shoko Server to have access to [FFmpeg](https://ffmpeg.org/download.html) (place system appropriate binaries in the ShokoRelay plugin folder or have it in the system PATH) as AnimeThemes does not provide `.mp3` files.

This is available under the "AnimeThemes: Theme.mp3" section of the dashboard.

## Plex: Automation

### Collection Generation

- Currently Plex's Provider Framework does not allow collections to be assigned so they have to be assigned separately
  - This is done by injecting them via Plex's HTTP API which requires authentication to use
- To do this navigate to the dashboard and authenticate wth Plex
- Once Authenticated select the libraries you want to apply collections to then click the `Generate Collections` button
- As a bonus this supports using the primary series poster as the collection poster (if configured under "Provider Configuration")
- Local collection posters can also be used by placing them in the configured `Collection Posters Root Path` (default `!CollectionPosters`) folder
- These files are simply named after the Shoko group name (or ID) that you wish them to apply to

### Scan on VFS Change

- When `Scan on VFS Change` is enabled and you are authenticated Plex's HTTP api will be used to instantly scan folder modified by the VFS watcher

### Auto Scrobble (Plex Pass)

- Enable `Auto Scrobble` in the Shoko Relay dashboard's "Plex: Automation" section.
- In the Plex Web App go to `Settings > Webhooks` and add the URL: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/plex/webhook`

## Shoko: Automation

Most of the options in this section require a Shoko API key to fully function (as mentioned in the setup instructions).

- `Remove Missing`: A button which will remove files that are no longer present from Shoko and your AniDB MyList
- `Import`: A button which will make shoko rescan "Source" type drop folders
- `Sync`: A button which opens a modal allowing for watched state syncing from Plex to Shoko or Shoko to Plex
  - Note: This includes any users configured under "Extra Plex Users" in the "Plex: Automation" section
- `Import Int.`: An input which will schedule imports from "Source" type drop folders every `N` hours
- `Sync Int.`: An input which will schedule watched state syncing from Plex to Shoko every `N` hours
  - Note: this includes ratings/votes if `Include Ratings` is enabled in the `Sync Watched States` modal

## Information

Info on controlling this plugin directly is available in [Endpoints.md](./ShokoRelay/Docs/Endpoints.md)

#### Missing Info

> Due to this plugin relying on Shoko's plugin abstractions as well as Plex still actively developing the metadata provider feature some things may be missing or not work correctly.

- **TMDB**
  - taglines
- **AniDB**
  - similar anime

#### Missing Plex Provider Features

- collections for tv show libraries (currently implemented via plex http api)
- rating icons that aren't from tmdb/imdb/rotten tomatoes

## TODO

- Fix audience ratings not applying to episodes or series (may be a Plex issue)
- Once available in Plex metadata providers
  - Switch collection support from Plex HTTP API "Generate Collections" button to the provider
  - Add custom or generic series/episode ratings directly through the provider
- Refactor and comment code for legibility
- Expand documentation
