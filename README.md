![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.com/channels/96234011612958720/268484849419943936)
-
This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex.
It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and intends to mirror all of its functionality. Scanning is much faster and it will be possible to add many new features as well.

Due to the lack of a custom scanner this plugin leverages [.plexmatch](https://support.plex.tv/articles/plexmatch/) files to ensure that varied folder structures are supported.
This means that your anime can be organised with whatever file or folder structure you want. The only caveat is that a folder cannot contain more than one AniDB series at a time in order to support theme songs for each one (subfolders are fine).
The matching files will be automatically generated when a file move or rename is detected by Shoko. This means that the potentially lengthy first time generation should only be a one time deal.

## Installation
#### Shoko
- Extract into Shoko Server's `plugins` directory
- Restart Shoko Server
- Provider Settings are stored in `ShokoRelayConfig.json` in Shoko's Install Directory

#### Mapping
- Generate the initial `.plexmatch` files by utilizing the following endpoint in a browser:
- `http://ShokoHost:ShokoPort/api/v3/ShokoRelay/plexmatch?path=ShokoImportFolderRoot`
- `ShokoImportFolderRoot` is the "Location" shown for an import folder on Shoko's dashboard

> [!NOTE]
> Add any import folders that you intend to use in Plex and wait for the .plexmatch generation to complete before using the new Agent.

#### Plex
- Navigate to `Settings > Metadata Agents`
- Click `Add Provider` in the Metadata Providers header and supply the url: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay`
- Click `Add Agent` in the Metadata Agents header name it and select `Shoko Relay` as the primary provider
- Under `additional providers` select `Plex Local Media` then click the `+` and `Save`

Now simply change the Scanner to `Plex TV Series` and the Agent to `Shoko Relay` for any `TV Shows` type library that contains anime matched with shoko.

For the Library under "Advanced" it is recommended to set these settings:
- Use season titles
- Use local assets
- Collections: `Hide items which are in collections`
- Seasons: `Hide for single-season series`

> [!NOTE]
> You can convert existing "TV Show" libraries to the new Agent as long as they contain files matched to Shoko and the `.plexmatch` files have been generated.

## Relay API Endpoints
Append paths to the base: `http://ShokoHost:ShokoPort/api/v3/ShokoRelay`
```
Matching        : /match
Plexmatch       : /plexmatchmatch?path={ShokoFolderPath}
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
- `.plexmatch` for multi episode files (bugged)

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
- Switch from .plexmatch to a full VFS
    - Bypass inability to fully map extras (without putting them in seasons)
    - Need to account for local plex metadata like posters/themes
- Potentially auth to plex and use plex's api for features not yet present in metadata providers
    - Will only do this if Shoko's integrated Auth flow will allow it
- Explore plex [webhooks](https://support.plex.tv/articles/115002267687-webhooks/) for full scrobble support
    - Should now be possible due to Relay's unique GUID scheme which utilises Shoko IDs
- Fully integrate the animethemes video .torrent as extras in the VFS
    - This will be configured to only add themes to the VFS if the user has the series in Shoko
    - Make a mapping file via animethemes API so other users don't hammer it
    - No need to link anything to anidb/shoko can use Plex extras folders
- Potentially integrate [animethemes.py](https://github.com/natyusha/ShokoRelay.bundle/blob/master/Contents/Scripts/animethemes.py) into the plugin
    - This would require ffmpeg
