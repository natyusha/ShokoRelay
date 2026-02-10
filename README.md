![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.com/channels/96234011612958720/268484849419943936)
-
This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex. It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and intends to mirror all of its functionality. Scanning is much faster and it will be possible to add many new features in the future as well.

Due to the lack of a custom scanner this plugin leverages a VFS (Virtual File System) to ensure that varied folder structures are supported. This means that your anime can be organised with whatever file or folder structure you want. The only caveat is that a folder cannot contain more than one AniDB series at a time if you want it to correctly support [local media assets](https://support.plex.tv/articles/200220717-local-media-assets-tv-shows/) like `Theme.mp3`. The VFS will be automatically updated when a file move or rename is detected by Shoko.

## Installation
### Shoko
> [!IMPORTANT]
> The VFS is created inside each Shoko import folder under the folder name configured as `VFS Root Folder` (default `!ShokoRelayVFS`). To stop Shoko from scanning the generated links, add a regex entry to `settings-server.json` under `Exclude`:
> ```json
> "Exclude": [
>   "[\\\/]!AnimeThemes[\\\/]",
>   "[\\\/]!ShokoRelayVFS[\\\/]",
>   "[\\\/]\\$RECYCLE\\.BIN[\\\/]",
>   "[\\\/]\\.Recycle\\.Bin[\\\/]",
>   "[\\\/]\\.Trash-\\d+[\\\/]"
> ]
> ```
- After making sure the VFS is excluded in Shoko's settings, extract the plugin into Shoko Server's `plugins` directory
- Restart Shoko Server

#### Setup
- Once the Server has loaded navigate to Shoko Relay's dashboard: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay/dashboard`
- Mandatory
    - Click the `Generate VFS` button in the "Virtual File System" section to initialize your collection
    - First time generation may take a couple minutes to complete with a large library.
    - The VFS will automatically update when it detects files have been renamed or moved.
- Optional
    - Link the plugin to your Plex account to enable watched state syncing and enhanced collection support
    - Provide a Shoko API key if you would like season poster support as well
        - Note: This is a temporary measure until season posters are in the plugin abstractions
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
- Click `Add Provider` in the Metadata Providers header and supply the url: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay`
- Click `Add Agent` in the Metadata Agents header, name it `Shoko Relay` and select `Shoko Relay` as the primary provider
- Under `additional providers` select `Plex Local Media` then click the `+` and `Save`

#### Library
> [!TIP]
> If you previously used the legacy `ShokoRelay.bundle` you can simply convert your existing libraries to the new agent.
> This allows you to maintain watched states and video preview thumbnails.
- The Shoko Relay agent requires a `TV Shows` type library to be created (or an existing one to be used)
- Simply change the Scanner to `Plex TV Series` and the Agent to `Shoko Relay`
- When adding your import folders to plex be sure to point them to the `!ShokoRelayVFS` directory
- Under "Advanced" in the Library it is recommended to set these settings:
    - Use season titles
    - Use local assets
    - Collections: `Hide items which are in collections`
    - Seasons: `Hide for single-season series`

## AnimeThemes Integration
#### Themes as Video Extras
This plugin includes full integration for [AnimeThemes](https://animethemes.moe/). It will look for `.webm` themes files in a folder called `!AnimeThemes` which is located in the root of your anime library. These files must have the same filename as they do on the AnimeThemes website and then a mapping must be generated for them in what is essentially a 3 step process. Simply navigate to the "AnimeThemes: VFS" section of the dashboard page to get started.
1. Download anime theme videos and place them in the `!AnimeThemes` folder
    - There is a torrent available with over 19000+ themes
2. Generate a mapping for the the videos by clicking the `Build Mapping` button:
    - A mapping for the current torrent is available [here](https://gist.github.com/natyusha/4e29252d939d0f522d38732facf328c7) (mapping the whole torrent takes ~12 hours due to rate limits)
3. Apply the mapping to the VFS by clicking the `Apply Mapping to VFS` button

> [!IMPORTANT]
> Similar to the VFS you must exclude the `!AnimeThemes` folder from Shoko. You must also configure the basepath for where it is located (the default is `/animethemes/`).

#### Themes as Series BGM
There is also support for generating `Theme.mp3` files as local metadata. This will add them to the VFS automatically and can be run for either a single series or as a batch operation. This requires Shoko Server to have access to [FFmpeg](https://ffmpeg.org/download.html) (place system appropriate binaries in the ShokoRelay plugin folder or have it in the system PATH) as AnimeThemes does not provide `.mp3` files.

This is available under the "AnimeThemes: Theme.mp3" section of the 

## Relay Metadata Endpoints
Append paths to the base: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay`
```
Collection      : /collections/{ShokoGroupID} *not fully implemented*
Series          : /metadata/{ShokoSeriesID}
Series Seasons  : /metadata/{ShokoSeriesID}/children
Series Episodes : /metadata/{ShokoSeriesID}/grandchildren
Season          : /metadata/{ShokoSeriesID}s{SeasonNumber}
Episode         : /metadata/e{ShokoEpisodeID}
Episode Parts   : /metadata/e{ShokoEpisodeID}p{PartNumber}
```


## Information
Due to this plugin relying on Shoko's plugin abstractions as well as Plex still actively developing this feature some TMDB/AniDB features are currently missing.

#### Missing Info
> mostly things that aren't available in Shoko's plugin abstractions
- **TMDB**
    - networks
    - season descriptions
    - season names
    - season posters (currently fetched via v3api)
    - taglines (does anyone care?)
    - user score
    - country
    - episode groups (custom seasons)
    - tvdbid [from xrefs] (for default theme songs)
- **AniDB**
    - tag weights
    - similar anime

#### Missing Plex Provider Features
- collections for tv show libraries (currently implemented via plex http api)
- ratings that aren't from tmdb/imdb/rotten tomatoes

## TODO
- Once available in Shoko plugin abstractions:
    - Add weight based content indicators/ratings
    - Add the missing TMDB info listed above
    - switch season posters from Shoko's v3 API to plugin abstractions when available
- Once available in Plex metadata providers
    - Switch collection support from Plex HTTP API "Generate Collections" button to the provider
    - Add custom or generic series/episode ratings
- Explore plex [webhooks](https://support.plex.tv/articles/115002267687-webhooks/) for full scrobble support
    - Should now be possible due to Relay's unique GUID scheme which utilises Shoko IDs
- Implement automatic watched state syncing to shoko without needing a webhook
- Add some sort of indicator to the dashboard when tasks are running like vfs generation or animethemes mapping
- Add github automations and begin providing builds