<!-- prettier-ignore-start -->

![Shoko Relay Logo](https://github.com/natyusha/ShokoRelay.bundle/assets/985941/23bfd7c2-eb89-46d5-a7cb-558c374393d6 "Shoko Relay")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.gg/shokoanime)
[![Shoko Docs](https://img.shields.io/badge/VitePress-Shoko_Docs-4E7CF5?logo=vitepress&logoColor=fff)](https://docs.shokoanime.com/)
[![GitHub Latest](https://img.shields.io/github/v/tag/natyusha/ShokoRelay?label=Latest&logo=github&logoColor=fff)](https://github.com/natyusha/ShokoRelay/releases/latest)
-

<!-- prettier-ignore-end -->

This is a plugin for Shoko Server that acts as a [Custom Metadata Provider](https://forums.plex.tv/t/announcement-custom-metadata-providers/934384) for Plex. It is a successor to the [ShokoRelay.bundle](https://github.com/natyusha/ShokoRelay.bundle) legacy agent/scanner and mirrors all of its functionality (including the automation scripts). Scanning is much faster and there are many new features included as well. Just like the old bundle this is intended to work with series of all types within a single "TV Shows" library. All you need to get started is a populated [Shoko Server](https://shokoanime.com/downloads/shoko-server) and [Plex Media Server](https://www.plex.tv/media-server-downloads/).

Due to the lack of a custom scanner this plugin leverages a VFS (Virtual File System) to ensure that varied folder structures are supported. This means that your anime can be organised with whatever file or folder structure you want. There is one caveat though. A folder cannot contain more than one AniDB series at a time if you want it to correctly support [local media assets](https://support.plex.tv/articles/200220717-local-media-assets-tv-shows/) (like posters or theme songs). The VFS will be automatically updated when a file move or rename is detected by Shoko.

## Installation

### Shoko

> [!IMPORTANT]
> The VFS is created inside each of Shoko's "destination" type folders under a subfolder named `!ShokoRelayVFS` (configurable under `Advanced Settings > VFS Root Path`). To stop Shoko from scanning the generated links, navigate to Shoko's installation directory and add the following regex entries to `settings-server.json` under `Exclude`:
>
> ```json
> "Exclude": [
>   "[\\\\\\/]!AnimeThemes[\\\\\\/]",
>   "[\\\\\\/]!ShokoRelayVFS[\\\\\\/]"
> ],
> ```

- Be sure to also exclude the `AnimeThemes Root Path` (default `!AnimeThemes`) if you plan on using AnimeThemes.
- After excluding the VFS in Shoko's settings, extract [the latest release](https://github.com/natyusha/ShokoRelay/releases/latest) into Shoko Server's `plugins` directory
- Restart Shoko Server

#### Setup

- Once the Server has loaded navigate to Shoko Relay's dashboard at the following URL:
  - `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/dashboard`
- **Mandatory:**
  - Click the `Generate VFS` button in the "Shoko: VFS" section to initialize your collection
    - First time generation may take several minutes to complete with a large library
  - A report of the run will be written to `logs/vfs-report.log` inside the plugin directory
    - You can download the latest report via a dashboard toast that will appear when the process completes
  - The VFS will automatically update when it detects files have been renamed or moved
- **Recommended:**
  - Link the plugin to your Plex account via the `Start Plex Auth` button in the "Plex: Authentication" section
    - Once clicked it will change to a `Login` link which will redirect you to `app.plex.tv/auth`
    - From there you can login to Plex as normal using your credentials and then close the tab
    - This will enable: Auto Scanning, Scrobbling (webhooks) and enhanced collection/ratings support
- There are additional options similar to what the legacy agent had at the bottom under "Provider Settings"

> [!TIP]
> If you are sharing the symlinks over an SMB share they may not appear depending on the [Samba Configuration](https://www.samba.org/samba/docs/current/man-html/smb.conf.5.html). An example entry for `smb.conf` that may help to mitigate this is listed below:

```ini
[global]
    follow symlinks = yes
```

<details>
<summary><b>Recommended Shoko Server Configuration</b></summary><br>

Enable the following options in Shoko to ensure that Plex has at least one source of metadata for everything:

- `Settings > AniDB > Download Options`
  - [x] Character Images
  - [x] Creator Images
- `Settings > TMDB > TMDB Options`
  - [x] Auto Link
  - [x] Auto Link Restricted
- `Settings > TMDB > TMDB Download Options`
  - [x] Download Alternate Ordering
  - [x] Download Backdrops
  - [x] Download Posters
  - [x] Download Logos
  - [x] Download Networks (available in `settings-server.json`)
- `Settings > Collection > Relation Options`
  - [x] Auto Group Series
  - [x] Determine Main Series Using Relation Weighing

</details>

### Plex

#### Metadata Agent

- Navigate to `Settings > Metadata Agents`
- Click `Add Provider` in the Metadata Providers header and supply the following URL:
  - `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay`
- Click `Add Agent` in the Metadata Agents header, name it `Shoko Relay` and select it as the primary provider
- Under `additional providers` select `Plex Local Media` then click the `+` and `Save`

#### Library

> [!TIP]
> If you previously used the legacy `ShokoRelay.bundle` you can simply convert your existing libraries to the new agent. This allows you to maintain watched states and video preview thumbnails. _A full metadata refresh is required after the first scan._

- The Shoko Relay agent requires a `TV Shows` type library to be created (or an existing one to be used)
- Under `Add Folders` be sure to only enter a `!ShokoRelayVFS` (or the configured `VFS Root Path`) as the directory
- Under `Advanced` simply change the Scanner of the library to `Plex TV Series` and the Agent to `Shoko Relay`
- Additionally it is highly recommended to set the following Advanced settings:
  - [x] Use season titles
  - [x] Use local assets
  - Collections: `Hide items which are in collections`
  - Seasons: `Hide for single-season series`

<details>
<summary><b>Legacy Agent Cleanup</b></summary><br>

Once you are happy with your new libraries you can safely delete all of the old data left behind from any anime related legacy agents you may have used. To do so simply navigate to your [Plex Media Server data directory](https://support.plex.tv/articles/202915258-where-is-the-plex-media-server-data-directory-located/) and search for the full agent name. You can then delete all of the files and folders found that match the search result. Some example search terms are listed below:

```
com.plexapp.agents.hama
com.plexapp.agents.shoko
com.plexapp.agents.shokorelay
```

</details>

## Plex: Automation

> [!NOTE]
> The Plex automation interval or "Int." (listed under the "Plex: Authentication" section) can be configured to control how often [Generate Collections](#collection-generation) and [Apply Critic Ratings](#critic-rating-application) runs. _An interval of 24-hours or above is recommended as Shoko rarely updates this information._

### Collection Generation

- Currently Plex's Provider Framework does not allow collections to be automatically assigned
  - They have to be injected manually via Plex's HTTP API instead
- Click the `Generate Collections` button in the "Plex: Automation" section to start this process
- _Requires Plex authentication_

**Notes:**

As a bonus this supports using the primary series poster as the collection poster (if configured under "Provider Settings"). Custom local posters can also be used by placing them in the configured `Collection Posters Root Path` (default `!CollectionPosters`) folder. These files are simply named after the Shoko group name (or ID) that you wish them to apply to. Empty collections will also be removed automatically during collection generation.

### Critic Rating Application

- The Provider Framework supports TMDB ratings but they are not visible outside of the "New Plex Experience"
- To mitigate this the `Apply Critic Ratings` button on the dashboard is available
  - This makes Plex for Web/Desktop show the ratings next to a generic grey star in the UI
- The rating source for this can be configured (or disabled) under `Critic Rating Mode` in the Provider Settings
- _Requires Plex authentication_

#### Force Partial Scans

- When `Force Partial Scans` is enabled Plex's HTTP API will be used to scan folders modified by the VFS watcher
- _Requires Plex authentication_

#### Auto Scrobble

- When `Auto Scrobble` is enabled Plex's [webhook](https://support.plex.tv/articles/115002267687-webhooks/) will be used to forward scrobble events to shoko
- This can be enabled in the Plex Web/Desktop App under `Settings > Webhooks`
  - Click `Add Webhook` and enter: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/plex/webhook`
  - Click `Save Changes` to complete the process
- The webhook respects the `Include Ratings` and `Exclude Admin` settings in the Sync Watched States Menu
- Manages users must be added to `Extra Plex Users` on the dashboard if you wish them to be included
- _Requires a Plex Pass subscription_

## Shoko: Automation

- `Remove Missing` A button which will remove files that are no longer present from Shoko
  - Entries will _always_ be removed from the AniDB MyList as well
- `Import` A button which will make shoko rescan "Source" type drop folders
- `Sync` A button which opens a modal allowing for watched state syncing from Plex to Shoko or Shoko to Plex
  - This includes any users configured under `Extra Plex Users` in the "Plex: Authentication" section
  - _Requires Plex authentication_
- `Schedule Offset` An input which controls the starting time in UTC for scheduled tasks
- `Import Int.` An input which will schedule imports from "Source" type drop folders every `N` hours
- `Sync Int.` An input which will schedule watched state syncing from Plex to Shoko every `N` hours
  - This includes ratings/votes if `Include Ratings` is enabled in the `Sync Watched States` modal
  - _Requires Plex authentication_

## AnimeThemes Integration

### Themes as Video Extras

This plugin includes full [AnimeThemes](https://animethemes.moe/) integration. It will look for '.webm' theme files in a folder called `!AnimeThemes` (or the configured `AnimeThemes Root Path`) which is located in the root of your anime library (this works for any "destination" type folder managed by Shoko). These files must have the same name as they do on the AnimeThemes website and then a mapping must be generated for them, in what is essentially a 3 step process. Simply navigate to the "AnimeThemes: VFS" section of the dashboard page to get started.

1. Download anime theme videos and place them in the `!AnimeThemes` folder
   - There is a torrent available with over 19000+ themes
2. Generate a mapping for the the videos by clicking the `Build` button:
   - If you have the torrent click the `Import` button to download the [current torrent mapping](https://gist.github.com/natyusha/bb33a3b3bc95bc7a3869633e23d522bb)
   - Mapping the torrent takes ~8 hours (due to rate limits) and generated mappings will be appended to it
3. Apply the mapping to the VFS by clicking the `Generate` button

> [!IMPORTANT]
> Similar to the VFS you must exclude the `!AnimeThemes` folder from Shoko scans using the `Exclude` server option. An example `settings-server.json` entry is shown [above](#shoko).

> [!TIP]
> By default, the plugin appends metadata attributes to the filename, such as `[SPOIL, SUBS]`. If you prefer a cleaner look, you can disable this by unchecking `Advanced Settings > Append AnimeThemes Tags` in the dashboard's Provider Settings. Note that a fresh "Generate" run is required to rename existing links after changing this setting.

### Themes as Series BGM

There is also support for generating `Theme.mp3` files as local metadata. This will add them to the VFS automatically and can be run for either a single series or as a batch operation. This process requires Shoko Server to have access to [FFmpeg/FFprobe](https://ffmpeg.org/download.html) (place system appropriate binaries in the ShokoRelay plugin folder or system PATH) as AnimeThemes does not provide the '.mp3' files that plex requires for this feature.

- This is available under the "AnimeThemes: MP3" section of the dashboard
- Input the path (relative to Plex or Shoko) to a folder containing an anime series and then click `Generate`
- The `Force Overwrite Toggle` will overwrite any `Theme.mp3` files found in the configured path (or during a batch)
- The `Recursive Batch Toggle` will enable batch operations on every subfolder of the configured path

**Notes:**

Any subfolder named after the configured `VFS Root Path`, `Collection Posters Root Path`, or `AnimeThemes Root Path` are ignored during batch operations. This is due to those directories only being used internally and never containing series data. There is also a little media player included which will play downloaded themes if enabled. You can set it to looped playback or even have it shuffle through all of your Theme.mp3 files if desired. The progress bar is fully functional and you can pause playback by middle clicking it.

### AnimeThemes Video Player

Shoko Relay includes a stand-alone, browser-based video player designed specifically for your AnimeThemes collection. It can be accessed via the `Open Video Player` icon (clap board) within the "AnimeThemes: VFS" section of the dashboard (or by a dedicated url: `http(s)://{ShokoHost}:{ShokoPort}/api/plugin/ShokoRelay/dashboard/player`). There is a included tree view which allows you to browse your themes by Group and Series as they would appear in Plex. Support for Loop, Shuffle, and Sequential playback is also available via a 4 stage toggle button.

#### Filter

There is a search box included which will filter the treeview based on series, group, or filename level queries. The filter supports tag-based filtering syntax using `+` (inclusion) and `-` (exclusion) operators. and the following metadata tags:

- `spoil` (Spoiler content)
- `nsfw` (Not Safe For Work)
- `lyrics` (Includes timed lyrics)
- `subs` (Includes subtitles)
- `uncen` (Uncensored version)
- `nc` (No Credits version)
- `trans` (Transition overlap)
- `over` (Full overlap)

#### Favourites

You can mark any theme as a favourite by clicking the heart icon `❤` next to its name in the tree view. Once marked as a favourite the icon for a given item will change to be red in colour and it will be persisted to a settings file based on its AnimeThemes VideoID. To quickly view only your marked themes, type `favs` into the search bar. This keyword can be combined with other search terms as well as tags (e.g., `+nc favs gundam`) to find specific favourites.

#### Keybinds

- `Up/Down` (Volume +/- 10%)
- `Ctrl-Up/Down` (Cycle Playback Mode)
- `Right/Left` (Seek +/- 5s)
- `Ctrl-Right/Left` (Play Next/Previous Theme)
- `L/J` (Seek +/- 10s)
- `Space/K` (Pause)
- `F` (Fullscreen)
- `/` (Focus Filter Box)
- `L` (Toggle Favourite)

## Information

### VFS Mapping

When building the VFS files are placed into folders which are named according to their Shoko SeriesID. Within those folders they will be split into subfolders depending on the type of episode. For regular episodes or specials this means placement into a `Season #` or `Specials` folder. Files placed into those folders are named with the following pattern: `S##E##(-pt#)(-v#) [{ShokoFileID}].ext` (the parts in parenthesis are conditional). Files with `-pt#` in their name will also have `[{ShokoFileID}]` stripped to fully follow the format described in [Combining Episodes](#combining-episodes). To avoid conflicts any file which is a cross over episode will not trigger local metadata/subtitle linking.\
_The ShokoFileID is unused by Plex and is there purely to help users visualise the file mappings._

Non standard episodes on the other hand, are placed into a local series level Extra folder. Due to Plex not having individual episode pages or metadata for files placed in said folders they will be named according to the episode name (with a prefix) `X# ❯ Title.ext`. More info on local extras is available [here](https://support.plex.tv/articles/local-files-for-tv-show-trailers-and-extras/) and the following table showcases the assignments.

| Prefix | Type    | Subfolder   |
| :----- | :------ | :---------- |
| S##E## | Episode | Season #    |
| S##E## | Special | Specials    |
| C# ❯   | Credits | Shorts      |
| T# ❯   | Trailer | Trailers    |
| P# ❯   | Parody  | Scenes      |
| O# ❯   | Other   | Featurettes |
| U# ❯   | Unknown | Other       |

> [!NOTE]
> `Other` type episodes have a special rule where they will attempt to place themselves in `Season 1` or `Specials` if either is empty. Otherwise, they will be placed in `Featurettes` and display as extras in Plex. This was implemented because these episodes are generally parts of a Movie and have a full set of metadata which would not appear if they were in Plex's local extras.

### Automatic Title Modification

**Common Prefixes for Series**

When a series starts with a common title prefix (and `Move Common Series Title Prefixes` is enabled in the Provider Settings) it will be moved to the end of the title. The prefixes considered common by the provider are governed by the following regex:

```regex
^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)
```

**Ambiguous Titles for Episodes**

In cases where AniDB uses ambiguous episode titles the series title will be used instead (with the original title appended to it as necessary). The titles considered ambiguous by the provider are governed by the following regex:

```regex
^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$
```

> [!NOTE]
> The appended titles for both series and episodes will appear after an em dash (**—**) making it easy to search for anything affected by this.

### TMDB Matching

If you have a TMDB link for a given series in Shoko, it will have access to several features not available otherwise:

- Plex's default theme song support (using the TheTVDB ID provided by TMDB cross references)
- Fallback for series/episode descriptions and titles (if AniDB is missing that information)
- Background/backdrop/logo image support as well as additional main series poster options (if available)

With `TMDB Episode Numbering` enabled in the Provider Settings the following will also be supported:

- Season support for long running anime including names, posters, titles and descriptions
- Combining multiple Shoko series into a single Plex entry
- Alternate episode ordering for seasons

**Curated TMDB Mappings**

If you don't have any TMDB links in Shoko it is recommended that you start off with a curated list before auto linking.\
[**Additional Info Here**](https://docs.shokoanime.com/shoko-server/tmdb-features#curated-tmdb-mappings)

**Alternate TMDB Episode Ordering**

If you aren't happy with TMDB's default episode/season structure for a series, you can change it to an alternate.\
[**Additional Info Here**](https://docs.shokoanime.com/shoko-server/tmdb-features#alternate-episode-ordering)

> [!NOTE]
> If you select an alternate order for a series TMDB season posters will no longer be automatically added to Plex.

**Combining Series**

This allows shows which are separated on AniDB but part of the same TMDB listing to be combined into a single entry in Plex. To achieve this click the `VFS Overrides Editor` button on the dashboard. It will open a modal which will create (or edit an existing) `anidb_vfs_overrides.csv` file in the plugin's config directory. Each line should contain a comma separated list of AniDB IDs you wish to merge. The first ID is the _primary series_ and the others will be merged into it (for both VFS builds and metadata lookups). Lines that are blank or start with a "#" are ignored.

An example `anidb_vfs_overrides.csv` is available [here](https://gist.github.com/natyusha/a9ad00a5c16276cfbe2553346c745f1c).

### Combining Episodes

Sometimes you may encounter a single episode which is split across multiple files. In order to ensure that all of the files are treated as a single entity you can follow Plex's [Naming Conventions](https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/#toc-6). The VFS will automatically respect this type of file naming in the background. For an ideal playback experience however, it is recommended to merge these types of files together.

### Minimum Tag Weights

Many tags on AniDB use a [3 Star Weight System](https://wiki.anidb.net/Tags#Star-rating_-_the_Weight_system) which represents a value from 0 (no stars) to 600 (3 stars) and determines how relevant the tag is to the series it is applied to. By setting this value under `Minimum Tag Weight` in the Provider Settings you can filter out tags below a certain star threshold.

### Assumed Content Ratings

If `Assumed Content Ratings` are enabled in the Provider Settings the [target audience](https://anidb.net/tag/2606/animetb) and [content indicator](https://anidb.net/tag/2604/animetb) tags from AniDB will be used to roughly match the [TV Parental Guidelines](https://www.tvguidelines.org/resources/TheRatings.pdf) system. The target audience tags will be checked for ratings from most restrictive to least, then the content indicators will be appended. If the tag weights for the content indicators are high enough (≥ 400 or ★★☆) the rating will be raised to compensate. A general overview is listed in the table below:

| Tag                        | Rating  |
| :------------------------- | :------ |
| Kodomo                     | TV-Y    |
| Mina                       | TV-G    |
| Shoujo, Shounen            | TV-PG   |
| Josei, Seinen              | TV-14   |
| Sexual Humour              | TV-\*-D |
| Nudity, Sex                | TV-\*-S |
| ★★☆ Violence               | TV-14-V |
| ★★☆ Nudity, ★⯪☆ Sex        | TV-14-S |
| Borderline Porn (override) | TV-MA   |
| ★★⯪ Nudity, ★★☆ Sex        | TV-MA-S |
| ★★⯪ Violence               | TV-MA-V |
| 18 Restricted (override)   | X       |

### Plugin API

Controlling this plugin directly is possible via HTTP GET/POST see [Controller.md](./Docs/Controller.md) for more information.

### Missing Info

Due to this plugin relying on Plex's metadata provider feature (which is still under development) some things may be missing or not work correctly.

#### Missing from Shoko's Abstractions

- **TMDB**: taglines
- **AniDB**: Similar Anime `api/v3/Series/{seriesID}/AniDB/Similar`

#### Missing Plex Provider Features

- Collections for TV Show libraries (currently implemented via Plex's HTTP API)
- Custom or generic rating icons

## TODO

- Populate the provider's similar Array with similar AniDB series
- Once available in Plex metadata providers:
  - Switch collection support from the Plex HTTP API "Generate Collections" button to the provider
  - Add custom or generic series/episode ratings directly through the provider
  - Add rich cast info (bios) for cast and crew
  - Include generic ratings for "old experience" Plex clients without using the HTTP API
- Add a way to distinguish between AnimeThemes of the same slug index that are grouped due to VFS overrides
