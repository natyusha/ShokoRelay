![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.com/channels/96234011612958720/268484849419943936)
-
This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex.
It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and intends to mirror all of its functionality. Scanning is much faster and it will be possible to add many new features as well.

Due to the lack of a custom scanner this plugin leverages a VFS (Virtual File Sytem) to ensure that varied folder structures are supported.
This means that your anime can be organised with whatever file or folder structure you want. The only caveat is that a folder cannot contain more than one AniDB series at a time if you want it to correctly support local metadata like `Theme.mp3`. The VFS will be automatically updated when a file move or rename is detected by Shoko.

## Installation
### Shoko
> [!IMPORTANT]
> The VFS is created inside each Shoko import folder under the folder name configured as `VFS Root Folder` (default `!ShokoRelayVFS`). To stop Shoko from scanning the generated links, add a regex entry to `settings-server.json` under `Exclude`:
> ```json
> "Exclude": [
>   "[\\\/]!ShokoRelayVFS[\\\/]",
>   "[\\\/]\\$RECYCLE\\.BIN[\\\/]",
>   "[\\\/]\\.Recycle\\.Bin[\\\/]",
>   "[\\\/]\\.Trash-\\d+[\\\/]"
> ]
> ```
- After making sure the VFS is excluded in Shoko's settings, extract the plugin into Shoko Server's `plugins` directory
- Provider Settings are stored in `ShokoRelayConfig.json` in Shoko's installation directory..
- Once complete restart Shoko Server

#### VFS
- Once the Server has loaded navigate to `http://ShokoHost:ShokoPort/api/v3/ShokoRelay/vfs?run=true` to generate the VFS.
- It will automatically update when it detects files have been renamed or moved.

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
- Click `Add Agent` in the Metadata Agents header name it and select `Shoko Relay` as the primary provider
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

## Relay API Endpoints
Append paths to the base: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay`
```
VFS             : /vfs
Matching        : /match
Collection      : /collection/{ShokoGroupID} (not fully implemented)
Series          : /metadata/{ShokoSeriesID}
Series Seasons  : /metadata/{ShokoSeriesID}/children
Series Episodes : /metadata/{ShokoSeriesID}/grandchildren
Season          : /metadata/{ShokoSeriesID}s{SeasonNumber}
Episode         : /metadata/e{ShokoEpisodeID}
Episode Parts   : /metadata/e{ShokoEpisodeID}p{PartNumber}
```

## Information
Due to this plugin relying on Shoko's plugin abstractions as well as Plex still actively developing this feature some TMDB/AniDB features are currently missing.

#### Missing TMDB Info
- networks
- season descriptions
- season names (not in shoko plugin abstractions)
- season posters (not in shoko plugin abstractions)
- taglines (does anyone care?)
- user score
- country
- episode groups (custom seasons)
- tvdbid [from xrefs] (for default theme songs)

#### Missing AniDB Info
- tag weights (not in shoko plugin abstractions)
- similar anime

#### Missing Plex Provider Features
- collections from shoko groups (not implemented)
- ratings that aren't from tmdb/imdb/rotten tomatoes

## TODO
- Once available in Shoko plugin abstractions:
    - Add a different way to configure the plugin as it seems broken/clunky
        - Full Web UI integration will be possible
    - Add weight based content indicators/ratings
    - Add the missing TMDB info listed above
- Once available in Plex metadata providers
    - Add collection support
    - Add custom series/episode rating sources
- Fully replace [collection-posters.py](https://github.com/natyusha/ShokoRelay.bundle/blob/master/Contents/Scripts/collection-posters.py)
    - Users will simply put posters with the same name as a collection into a configurable folder
    - Collection posters from the primary series in a Shoko group will already work
- Potentially auth to plex and use plex's api for features not yet present in metadata providers
    - Will only do this if Shoko's integrated Auth flow will allow it to simplify the setup
- Explore plex [webhooks](https://support.plex.tv/articles/115002267687-webhooks/) for full scrobble support
    - Should now be possible due to Relay's unique GUID scheme which utilises Shoko IDs
- Fully integrate the animethemes video .torrent as extras in the VFS
    - This will be configured to only add themes to the VFS if the user has the series in Shoko
    - Make a mapping file via animethemes API so other users don't hammer it
    - No need to link anything to anidb/shoko can use Plex extras folders
- Potentially integrate [animethemes.py](https://github.com/natyusha/ShokoRelay.bundle/blob/master/Contents/Scripts/animethemes.py) into the plugin
    - This would require ffmpeg
