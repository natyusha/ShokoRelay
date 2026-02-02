![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.com/channels/96234011612958720/268484849419943936)
-
This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex.
It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and intends to mirror all of its features. Scanning is much faster now and it will be possible to add many new features as well.

Due to the lack of a custom scanner this plugin leverages [.plexmatch](https://support.plex.tv/articles/plexmatch/) files to ensure that varied folder structures are supported.
This means that your anime can be organised with whatever file or folder structure you want. The only caveat is that a folder cannot contain more than one AniDB series at a time (subfolders are fine).
These matching files will be automatically generated when a file move or rename is detected by Shoko. This means that the potentially lengthy first time generation should only be a one time deal.

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

## Information
Due to this plugin relying on Shoko's plugin abstractions as well as Plex still actively developing this feature some TMDB/AniDB features are currently missing.

#### Missing TMDB Info
- networks
- season descriptions
- season names (not in shoko plugin abstractions)
- season posters (not in shoko plugin abstractions)
- taglines (does anyone care?)
- user score
- episode groups (custom seasons)
- tvdbid [from xrefs] (for default theme songs)

#### Missing AniDB Info
- tag weights (not in shoko plugin abstractions)
- similar anime

#### Missing Plex Features
- collections from shoko groups (not implemented)
- `.plexmatch` for multi episode files (bugged)

## TODO
- Add the missing TMDB info listed above
- Add a different way to configure the plugin as it seems broken
- Add weight based content ratings
- Add collection support
- Integrate animethemes
