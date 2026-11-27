using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Jobs;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Provides operations for building AnimeThemes VFS mappings, generating MP3 series themes, and handling the standalone video player endpoints.</summary>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="plexClient">Plex client.</param>
/// <param name="animeThemesMp3Generator">AnimeThemes MP3 generator.</param>
/// <param name="animeThemesMapping">AnimeThemes mapping service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="logger">Logger instance.</param>
[ApiController]
[RelayAdminAuthorize]
[Route(ShokoRelayConstants.BasePath)]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
public class AnimeThemesController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexClient,
    AnimeThemesMp3Generator animeThemesMp3Generator,
    AnimeThemesMapping animeThemesMapping,
    IVideoService videoService,
    IFileSystem fileSystem,
    ILogger<AnimeThemesController> logger
) : ShokoRelayBaseController(configProvider, metadataService, plexClient, logger)
{
    #region Setup

    /// <summary>Display group label for themes whose series are not in the local Shoko collection.</summary>
    private const string MissingCollectionLabel = "Missing from Collection";

    /// <summary>Lock object synchronizing reads and writes to the AnimeThemes favourites file.</summary>
    private static readonly Lock s_favsLock = new();

    /// <summary>Normalized player tree item payload pre-sorted and pre-indexed for the standalone video player.</summary>
    /// <param name="Group">Shoko group title or missing-collection label.</param>
    /// <param name="Series">Resolved series display title.</param>
    /// <param name="SeriesId">Shoko Series ID, or <c>0</c> if missing from collection.</param>
    /// <param name="AnidbId">AniDB anime ID.</param>
    /// <param name="File">Unescaped display filename without extension.</param>
    /// <param name="RelPath">Relative source path inside the AnimeThemes root.</param>
    /// <param name="FolderSegments">Ordered folder path segments for source-folder view.</param>
    /// <param name="FolderSortKey">Chronological and alphabetical sort key for source-folder view.</param>
    /// <param name="SearchIndex">Pre-computed lowercase search text stripped of zero-width formatting characters.</param>
    /// <param name="Path">Normalized filesystem path used by the streaming endpoint.</param>
    /// <param name="VideoId">AnimeThemes video identifier.</param>
    /// <param name="Tags">Active attribute tag keywords (<c>nc</c>, <c>lyrics</c>, <c>subs</c>, <c>uncen</c>, <c>nsfw</c>, <c>spoil</c>, <c>trans</c>, <c>over</c>).</param>
    /// <param name="Nc">No Credits flag.</param>
    /// <param name="Lyrics">Karaoke lyrics flag.</param>
    /// <param name="Subs">Subtitles flag.</param>
    /// <param name="Uncen">Uncensored flag.</param>
    /// <param name="Nsfw">NSFW flag.</param>
    /// <param name="Spoiler">Spoiler flag.</param>
    /// <param name="Trans">Transitional overlap flag.</param>
    /// <param name="Over">Full overlap flag.</param>
    private sealed record WebmTreeItem(
        string Group,
        string Series,
        int SeriesId,
        int AnidbId,
        string File,
        string? RelPath,
        string[] FolderSegments,
        string FolderSortKey,
        string SearchIndex,
        string Path,
        int VideoId,
        string[] Tags,
        bool Nc,
        bool Lyrics,
        bool Subs,
        bool Uncen,
        bool Nsfw,
        bool Spoiler,
        bool Trans,
        bool Over
    );

    #endregion

    #region VFS Mapping & Build

    /// <summary>Applies the anime‑themes mapping file to the directory structure.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <param name="queue">When <c>true</c>, enqueues the job in Shoko's queue and returns immediately.</param>
    /// <returns>A task representing the result of the build outcome.</returns>
    [HttpGet("animethemes/vfs/build")]
    public Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? filter = null, [FromQuery] bool queue = false)
    {
        if (ValidateFilterOrBadRequest(filter, out var filterIds) is { } guard)
            return Task.FromResult(guard);

        string normalizedFilter = string.Join(",", filterIds.Order());
        string filterKey = RelayJobBase.ComputeFilterKey(normalizedFilter);
        return ExecuteQueuedJobAsync<AtVfsBuildJob>(ShokoRelayConstants.TaskAtVfsBuild, queue, job =>
        {
            job.Filter = normalizedFilter;
            job.FilterKey = filterKey;
        });
    }

    /// <summary>Generates the anime‑themes mapping CSV, tests a single filename mapping, or updates/saves a mapping.</summary>
    /// <param name="path">Optional webm filename or relative path to test or update mapping against.</param>
    /// <param name="save">Whether to save or update the mapping in the CSV file.</param>
    /// <param name="anidbId">Optional manual AniDB ID override.</param>
    /// <param name="queue">When <c>true</c>, enqueues the mapping build job in Shoko's queue and returns immediately.</param>
    /// <returns>A task representing the result of the mapping, test, or update.</returns>
    [HttpGet("animethemes/vfs/map")]
    public async Task<IActionResult> AnimeThemesVfsMap([FromQuery] string? path = null, [FromQuery] bool save = false, [FromQuery] int? anidbId = null, [FromQuery] bool queue = false)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var (entry, error, generatedFilename, wasSaved, changedFields) = await animeThemesMapping.ProcessSingleMappingAsync(path, save, anidbId, HttpContext.RequestAborted).ConfigureAwait(false);
            return error != null
                ? Ok(new RelayResponse<object>(Status: "error", Message: error, Data: new { path }))
                : Ok(
                    new RelayResponse<object>(
                        Data: new
                        {
                            path,
                            saved = wasSaved,
                            generatedFilename,
                            changedFields,
                            csvLine = entry != null ? AnimeThemesHelper.SerializeEntry(entry) : null,
                            entry,
                        }
                    )
                );
        }

        return await ExecuteQueuedJobAsync<AtMapBuildJob>(ShokoRelayConstants.TaskAtMapBuild, queue).ConfigureAwait(false);
    }

    /// <summary>Downloads and imports the curated mapping CSV from GitHub.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries imported.</returns>
    [HttpPost("animethemes/vfs/import")]
    public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("AnimeThemes Map: Fetching curated mapping file from GitHub...");
        var (count, _) = await animeThemesMapping.ImportMappingFromUrlAsync(AnimeThemesHelper.AtRawMapUrl, cancellationToken).ConfigureAwait(false);
        Logger.LogInformation("AnimeThemes Map: Import successful. {Count} entries updated.", count);
        return Ok(new RelayResponse<object>(Data: new { count }));
    }

    #endregion

    #region MP3 Generation

    /// <summary>Generates Theme.mp3 files for anime series.</summary>
    /// <param name="query">Parameters for the MP3 generation request.</param>
    /// <param name="queue">When <c>true</c>, enqueues batch MP3 generation in Shoko's queue and returns immediately.</param>
    /// <returns>A task representing the result of the generation.</returns>
    [HttpGet("animethemes/mp3")]
    public async Task<IActionResult> AnimeThemesMp3([FromQuery] AnimeThemesMp3Query query, [FromQuery] bool queue = false)
    {
        if (string.IsNullOrWhiteSpace(query.Path))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "path is required"));

        string mappedPath = PlexClient.MapPlexPathToShokoPath(query.Path);
        string? fullFolderPath = null;
        try
        {
            if (Path.IsPathRooted(mappedPath))
                fullFolderPath = Path.GetFullPath(mappedPath);
            else
            {
                string relativeCandidate = VfsShared.NormalizeSeparators(mappedPath).TrimLeadingSlashes();
                foreach (var folderPath in VfsShared.GetManagedFolderPaths(videoService))
                {
                    string candidate = Path.GetFullPath(Path.Combine(folderPath, relativeCandidate));
                    if (VfsShared.IsPathUnderDirectory(candidate, folderPath) && fileSystem.DirectoryExists(candidate))
                    {
                        fullFolderPath = candidate;
                        break;
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Invalid path."));
        }

        if (string.IsNullOrEmpty(fullFolderPath) || !IsInsideManagedFolders(fullFolderPath, allowRoot: true))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Path must be inside a configured Shoko managed folder."));

        query = query with { Path = fullFolderPath };

        if (query.Batch)
            return await ExecuteQueuedJobAsync<AtMp3BatchJob>(ShokoRelayConstants.TaskAtMp3Build, queue, job =>
                {
                    job.FolderPath = fullFolderPath;
                    job.Slug = query.Slug;
                    job.Offset = query.Offset;
                    job.Force = query.Force;
                    job.Seasonal = query.Seasonal;
                })
                .ConfigureAwait(false);

        // Single generation does not require a persistent log file, but still uses the VfsLock for safety.
        if (!await VfsShared.VfsLock.WaitAsync(0).ConfigureAwait(false))
            return Conflict(new RelayResponse<object>(Status: "busy", Message: "A conflicting operation is already in progress. Please wait for item to complete."));

        try
        {
            var result = await animeThemesMp3Generator.ProcessSingleAsync(query, null, CancellationToken.None).ConfigureAwait(false);
            return result.Status == "error" ? BadRequest(new RelayResponse<object>(Status: "error", Message: result.Message, Data: result)) : Ok(new RelayResponse<ThemeMp3OperationResult>(Data: result));
        }
        finally
        {
            VfsShared.VfsLock.Release();
        }
    }

    /// <summary>Returns a random Theme.mp3 path from the current cache.</summary>
    /// <param name="refresh">If true, forces a cache refresh before selecting.</param>
    /// <returns>A JSON object containing the random path.</returns>
    [HttpGet("animethemes/mp3/random")]
    public async Task<IActionResult> AnimeThemesMp3Random([FromQuery] bool refresh = false)
    {
        if (refresh)
        {
            await VfsShared.VfsLock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            try
            {
                animeThemesMp3Generator.RefreshThemeMp3Cache();
            }
            finally
            {
                VfsShared.VfsLock.Release();
            }
        }
        var folders = animeThemesMp3Generator.GetCachedThemeMp3s().Keys.ToList();
        return folders.Count == 0
            ? NotFound(new RelayResponse<object>(Status: "error", Message: "No themes found"))
            : Ok(new RelayResponse<object>(Data: new { path = folders[Random.Shared.Next(folders.Count)] }));
    }

    /// <summary>Returns the dictionary of cached Theme.mp3 folder paths mapped to their respective theme slugs.</summary>
    /// <returns>A dictionary of paths and slugs.</returns>
    [HttpGet("animethemes/mp3/cache")]
    public IActionResult GetAnimeThemesMp3Cache() => Ok(new RelayResponse<IReadOnlyDictionary<string, string>>(Data: animeThemesMp3Generator.GetCachedThemeMp3s()));

    /// <summary>Audits existing non-OP Theme.mp3 files to check if an OP upgrade is now available on AnimeThemes.</summary>
    /// <param name="queue">When <c>true</c>, enqueues the job in Shoko's queue and returns immediately.</param>
    /// <returns>A task representing the result of the audit.</returns>
    [HttpGet("animethemes/mp3/audit")]
    public Task<IActionResult> AuditAnimeThemesMp3([FromQuery] bool queue = false) => ExecuteQueuedJobAsync<AtMp3AuditJob>(ShokoRelayConstants.TaskAtMp3Audit, queue);

    /// <summary>Streams an existing Theme.mp3 with ID3 tags embedded in response headers.</summary>
    /// <param name="path">The folder path containing the Theme.mp3.</param>
    /// <returns>A file stream result.</returns>
    [HttpGet]
    [HttpHead]
    [Route("animethemes/mp3/stream")]
    public IActionResult AnimeThemesMp3Stream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();

        string themePath;
        try
        {
            themePath = Path.Combine(Path.GetFullPath(PlexClient.MapPlexPathToShokoPath(path)), "Theme.mp3");
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        if (!IsInsideManagedFolders(themePath) || !fileSystem.FileExists(themePath))
            return NotFound();

        try
        {
            var tags = AnimeThemesHelper.ReadId3v2Tags(themePath, fileSystem);
            foreach (var tag in tags)
                Response.Headers[$"X-Theme-{tag.Key}"] = Uri.EscapeDataString(tag.Value);
            Response.Headers["Access-Control-Expose-Headers"] = "X-Theme-Title, X-Theme-Slug, X-Theme-Artist, X-Theme-Album";
        }
        catch { }

        // Stream via FileStream rather than PhysicalFile to prevent browsers from receiving physical file paths or triggering security errors
        return File(fileSystem.OpenFile(themePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), "audio/mpeg", enableRangeProcessing: true);
    }

    #endregion

    #region WebM & Player

    /// <summary>Returns the pre-sorted, pre-indexed tree of WebM files and favourites for the standalone player.</summary>
    /// <remarks>Restricted-tag visibility (<c>IUser.IsAllowedToSee</c>) is intentionally not enforced here so <see cref="AllowPlayerAccessAttribute"/> remains usable without a user session.</remarks>
    /// <returns>A JSON object containing the sorted theme list, favourites, and configured playback mode.</returns>
    [AllowPlayerAccess]
    [HttpGet("animethemes/webm/tree")]
    public IActionResult AnimeThemesWebmTree()
    {
        var items = new List<WebmTreeItem>();
        var seriesTitleCache = new Dictionary<int, (string GroupTitle, string SeriesTitle, int AniDbId)>();
        var seenSeriesVideoKeys = new HashSet<(int SeriesId, int VideoId)>();

        // Load Mapping CSV early to extract original relative source paths
        string mapPath = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
        var videoIdToRelativePath = new Dictionary<int, string>();
        List<AnimeThemesMappingEntry> entries = [];
        if (IoFile.Exists(mapPath))
            try
            {
                entries = AnimeThemesHelper.ParseMappingContentWithComments(VfsShared.ReadAllTextShared(mapPath)).Entries;
                foreach (var entry in entries)
                    videoIdToRelativePath.TryAdd(entry.VideoId, entry.FilePath);
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "AnimeThemes Player: Failed to parse mapping CSV for webm tree");
            }

        // Load VFS WebM Cache (Existing Anime)
        string cachePath = Path.Combine(ConfigProvider.CacheDirectory, ShokoRelayConstants.FileAtWebmCache);
        if (IoFile.Exists(cachePath))
            try
            {
                foreach (var line in VfsShared.ReadLinesShared(cachePath))
                {
                    var pipeParts = line.Split('|');
                    string rawPath = pipeParts[0];
                    int videoId = (pipeParts.Length > 1 && int.TryParse(pipeParts[1], out var parsedVideoId)) ? parsedVideoId : 0;
                    int flags = (pipeParts.Length > 2 && int.TryParse(pipeParts[2], out var parsedFlags)) ? parsedFlags : 0;

                    var segments = rawPath.Replace('\\', '/').Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
                    int? seriesId = ResolveWebmTreeSeriesId(segments);

                    if (!seriesId.HasValue || (videoId > 0 && !seenSeriesVideoKeys.Add((seriesId.Value, videoId))))
                        continue;

                    if (!seriesTitleCache.TryGetValue(seriesId.Value, out var info))
                    {
                        if (MetadataService.GetShokoSeriesByID(seriesId.Value) is { } series)
                        {
                            var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
                            var group = MetadataService.GetShokoGroupByID(series.TopLevelGroupID);
                            info = (!string.IsNullOrWhiteSpace(group?.PreferredTitle?.Value) ? group.PreferredTitle.Value : displayTitle, displayTitle, series.AnidbAnimeID);
                        }
                        else
                            info = ($"Series {seriesId.Value}", $"Series {seriesId.Value}", 0);
                        seriesTitleCache[seriesId.Value] = info;
                    }

                    items.Add(
                        BuildWebmTreeItem(
                            info.GroupTitle,
                            info.SeriesTitle,
                            seriesId.Value,
                            info.AniDbId,
                            Path.GetFileNameWithoutExtension(segments[^1]),
                            videoIdToRelativePath.GetValueOrDefault(videoId),
                            rawPath.Replace('\\', '/').Trim(),
                            videoId,
                            (flags & 1) != 0,
                            (flags & 2) != 0,
                            (flags & 4) != 0,
                            (flags & 8) != 0,
                            (flags & 16) != 0,
                            (flags & 32) != 0,
                            (flags & 64) != 0,
                            (flags & 128) != 0
                        )
                    );
                }
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "AnimeThemes Player: Failed to parse webm cache");
            }

        // Process Mapping CSV (Missing Anime)
        if (entries.Count > 0)
            AppendMissingMappingWebms(entries, items);

        var sortedItems = items
            .OrderBy(item => item.Group == MissingCollectionLabel ? 0 : 1)
            .ThenBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Series, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .Select(item => new
            {
                group = item.Group,
                series = item.Series,
                seriesId = item.SeriesId,
                anidbId = item.AnidbId,
                file = item.File,
                relPath = item.RelPath,
                folderSegments = item.FolderSegments,
                folderSortKey = item.FolderSortKey,
                searchIndex = item.SearchIndex,
                path = item.Path,
                videoId = item.VideoId,
                tags = item.Tags,
                nc = item.Nc,
                lyrics = item.Lyrics,
                subs = item.Subs,
                uncen = item.Uncen,
                nsfw = item.Nsfw,
                spoiler = item.Spoiler,
                trans = item.Trans,
                over = item.Over,
            })
            .ToList();

        return Ok(
            new
            {
                items = sortedItems,
                favourites = ReadFavouriteIds(),
                playbackMode = Settings.Playback.AnimeThemesWebmMode,
            }
        );
    }

    /// <summary>Streams a WebM theme file from the VFS.</summary>
    /// <remarks>Restricted-tag visibility (<c>IUser.IsAllowedToSee</c>) is intentionally not enforced here so <see cref="AllowPlayerAccessAttribute"/> remains usable without a user session.</remarks>
    /// <param name="path">The relative VFS path to the WebM file.</param>
    /// <returns>A file stream result.</returns>
    [HttpGet]
    [HttpHead]
    [AllowPlayerAccess]
    [Route("animethemes/webm/stream")]
    public IActionResult AnimeThemesWebmStream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(PlexClient.MapPlexPathToShokoPath(path));
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        // Stream via FileStream rather than PhysicalFile to prevent browsers from receiving physical file paths or triggering security errors
        return !IsInsideManagedFolders(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".webm", StringComparison.OrdinalIgnoreCase) || !fileSystem.FileExists(fullPath)
            ? NotFound()
            : File(fileSystem.OpenFile(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), "video/webm", enableRangeProcessing: true);
    }

    /// <summary>Returns the list of videoIds marked as favourites.</summary>
    /// <returns>An array of video IDs.</returns>
    [AllowPlayerAccess]
    [HttpGet("animethemes/webm/favourites")]
    public IActionResult GetAnimeThemesFavourites() => Ok(new RelayResponse<List<int>>(Data: ReadFavouriteIds()));

    /// <summary>Toggles a VideoId in the favourites list.</summary>
    /// <param name="videoId">The AnimeThemes video ID to toggle.</param>
    /// <returns>A JSON object with the new favourite status.</returns>
    [HttpPost("animethemes/webm/favourites")]
    public IActionResult UpdateAnimeThemesFavourite([FromBody] int videoId)
    {
        if (videoId <= 0)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Invalid videoId"));

        string path = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtFavs);
        lock (s_favsLock)
        {
            var ids = new HashSet<int>();
            if (IoFile.Exists(path))
                foreach (var line in IoFile.ReadAllLines(path))
                    if (int.TryParse(line.Trim(), out int id))
                        ids.Add(id);

            if (!ids.Remove(videoId))
                ids.Add(videoId);

            try
            {
                VfsShared.WriteLinesAtomic(path, ids.Order().Select(id => id.ToString()));
                return Ok(new RelayResponse<object>(Data: new { videoId, isFavourite = ids.Contains(videoId) }));
            }
            catch (Exception exception)
            {
                return StatusCode(500, new RelayResponse<object>(Status: "error", Message: exception.Message));
            }
        }
    }

    /// <summary>Downloads AnimeThemes WebM files directly based on filters or recent additions.</summary>
    /// <param name="query">Filter parameters for downloading.</param>
    /// <param name="queue">When <c>true</c>, enqueues the job in Shoko's queue and returns immediately.</param>
    /// <returns>A task representing the result of the download operation.</returns>
    [HttpPost("animethemes/webm/download")]
    public Task<IActionResult> DownloadAnimeThemesWebm([FromQuery] AnimeThemesWebmQuery query, [FromQuery] bool queue = false)
    {
        if (query.Recent.HasValue)
        {
            if (query.Recent.Value is < 1 or > 100)
                return Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Recent count must be between 1 and 100.")));
            if (!string.IsNullOrWhiteSpace(query.Name) || query.Year.HasValue || !string.IsNullOrWhiteSpace(query.Season))
                return Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "Cannot combine 'recent' with Name, Year, or Season filters.")));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(query.Season) && !query.Year.HasValue)
                query = query with { Year = DateTime.Now.Year };

            if (string.IsNullOrWhiteSpace(query.Name) && !query.Year.HasValue && string.IsNullOrWhiteSpace(query.Season))
                return Task.FromResult<IActionResult>(BadRequest(new RelayResponse<object>(Status: "error", Message: "At least one filter (Name, Year, Season, or Recent) is required.")));
        }

        return ExecuteQueuedJobAsync<AtWebmDownloadJob>(ShokoRelayConstants.TaskAtWebmDownload, queue, job =>
        {
            job.Name = query.Name;
            job.Year = query.Year;
            job.Season = query.Season;
            job.Recent = query.Recent;
            job.Force = query.Force;
            job.Destination = query.Destination;
        });
    }

    #endregion

    #region Internal Helpers

    /// <summary>Checks whether a normalized full path resides within any configured Shoko managed folder.</summary>
    /// <param name="fullPath">The full filesystem path to validate.</param>
    /// <param name="allowRoot">When <c>true</c>, also permits the managed folder root directory itself.</param>
    /// <returns><c>true</c> if the path is inside a managed folder; otherwise, <c>false</c>.</returns>
    private bool IsInsideManagedFolders(string fullPath, bool allowRoot = false) => VfsShared.GetManagedFolderPaths(videoService).Any(root => VfsShared.IsPathUnderDirectory(fullPath, root, allowRoot));

    /// <summary>Resolves the Shoko Series ID from the path segments of a cached VFS WebM entry.</summary>
    /// <param name="segments">The normalized path segments of the VFS entry.</param>
    /// <returns>The resolved primary Shoko Series ID, or <c>null</c> if it could not be determined.</returns>
    private int? ResolveWebmTreeSeriesId(string[] segments)
    {
        // Fast path: determine whether the ID preceding Shorts belongs to a Movie VFS (EpisodeID) or TV VFS (SeriesID)
        int shortsIndex = Array.FindIndex(segments, segment => string.Equals(segment, "Shorts", StringComparison.OrdinalIgnoreCase));
        if (shortsIndex > 0 && int.TryParse(segments[shortsIndex - 1], out int directId))
        {
            bool isMovieVfs = shortsIndex > 1 && string.Equals(segments[shortsIndex - 2], VfsShared.ResolveMovieRootFolderName(), StringComparison.OrdinalIgnoreCase);
            return isMovieVfs
                ? (MetadataService.GetShokoEpisodeByID(directId) is { ShokoSeriesID: var episodeSeriesId } ? OverrideHelper.GetPrimary(episodeSeriesId, MetadataService) : null)
                : MetadataService.GetShokoSeriesByID(directId)?.GetPrimaryId(MetadataService);
        }

        if (shortsIndex < 0)
            for (int segmentIndex = segments.Length - 1; segmentIndex >= 0; segmentIndex--)
                if (int.TryParse(segments[segmentIndex], out int candidateId))
                {
                    if (MetadataService.GetShokoSeriesByID(candidateId) is { } series)
                        return series.GetPrimaryId(MetadataService);
                    if (MetadataService.GetShokoEpisodeByID(candidateId) is { ShokoSeriesID: var episodeSeriesId })
                        return OverrideHelper.GetPrimary(episodeSeriesId, MetadataService);
                }

        return null;
    }

    /// <summary>Constructs a pre-indexed player tree item including normalized tags, folder path segments, and search text.</summary>
    /// <param name="group">Shoko group title or missing-collection label.</param>
    /// <param name="series">Resolved series display title.</param>
    /// <param name="seriesId">Shoko Series ID, or <c>0</c> when missing.</param>
    /// <param name="anidbId">AniDB anime ID.</param>
    /// <param name="rawFile">Raw display filename without extension.</param>
    /// <param name="relPath">Relative source path inside the AnimeThemes root.</param>
    /// <param name="path">Normalized filesystem path used for streaming.</param>
    /// <param name="videoId">AnimeThemes video ID.</param>
    /// <param name="nc">No Credits flag.</param>
    /// <param name="lyrics">Karaoke lyrics flag.</param>
    /// <param name="subs">Subtitles flag.</param>
    /// <param name="uncen">Uncensored flag.</param>
    /// <param name="nsfw">NSFW flag.</param>
    /// <param name="spoiler">Spoiler flag.</param>
    /// <param name="trans">Transitional overlap flag.</param>
    /// <param name="over">Full overlap flag.</param>
    /// <returns>A populated <see cref="WebmTreeItem"/>.</returns>
    private static WebmTreeItem BuildWebmTreeItem(
        string group,
        string series,
        int seriesId,
        int anidbId,
        string rawFile,
        string? relPath,
        string path,
        int videoId,
        bool nc,
        bool lyrics,
        bool subs,
        bool uncen,
        bool nsfw,
        bool spoiler,
        bool trans,
        bool over
    )
    {
        string file = TextHelper.UnescapeUnicode(rawFile);
        var rawParts = (string.IsNullOrWhiteSpace(relPath) ? $"Unknown/{file}" : relPath).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] folderSegments = [.. rawParts.Take(Math.Max(0, rawParts.Length - 1)), series];
        string folderSortKey =
            string.Join(
                '/',
                folderSegments.Select(segment =>
                    segment.ToLowerInvariant() switch
                    {
                        "winter" => "0001",
                        "spring" => "0002",
                        "summer" => "0003",
                        "fall" => "0004",
                        _ when segment.EndsWith('s') && int.TryParse(segment[..^1], out int decade) => (1900 + decade).ToString("D4"),
                        _ when int.TryParse(segment, out int year) => year.ToString("D4"),
                        _ => segment.ToLowerInvariant(),
                    }
                )
            )
            + "/"
            + file.ToLowerInvariant();
        string searchIndex = $"{group} {series} {file} {relPath ?? ""}"
            .ToLowerInvariant()
            .Replace(TextHelper.ZeroWidthSpace, "", StringComparison.Ordinal)
            .Replace(TextHelper.HairSpace, "", StringComparison.Ordinal);

        var tags = new List<string>(8);
        if (nc)
            tags.Add("nc");
        if (lyrics)
            tags.Add("lyrics");
        if (subs)
            tags.Add("subs");
        if (uncen)
            tags.Add("uncen");
        if (nsfw)
            tags.Add("nsfw");
        if (spoiler)
            tags.Add("spoil");
        if (trans)
            tags.Add("trans");
        if (over)
            tags.Add("over");

        return new WebmTreeItem(group, series, seriesId, anidbId, file, relPath, folderSegments, folderSortKey, searchIndex, path, videoId, [.. tags], nc, lyrics, subs, uncen, nsfw, spoiler, trans, over);
    }

    /// <summary>Appends physically present WebM files from the AnimeThemes mapping CSV whose series are missing or have no active videos in Shoko.</summary>
    /// <param name="entries">Parsed entries from the AnimeThemes mapping CSV.</param>
    /// <param name="items">Target list receiving player tree item payloads.</param>
    private void AppendMissingMappingWebms(List<AnimeThemesMappingEntry> entries, List<WebmTreeItem> items)
    {
        try
        {
            var managedFolders = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true);
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();

            // Scan physically present .webm files once into an in-memory dictionary
            var physicalThemes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var webmOptions = new FileSystemListingOptions { Recursive = true, Extensions = [".webm"] };
            foreach (var managedFolder in managedFolders)
            {
                string themeDir = Path.Combine(managedFolder, themeRootName);
                if (fileSystem.DirectoryExists(themeDir))
                    foreach (var file in fileSystem.GetFilePaths(themeDir, webmOptions))
                        physicalThemes.TryAdd(Path.GetRelativePath(themeDir, file).Replace('\\', '/').TrimStart('/'), file);
            }

            if (physicalThemes.Count == 0)
                return;

            // Lazy lookup cache for series that physically exist in the local collection
            var localSeriesHasFiles = new Dictionary<int, bool>();

            foreach (var entry in entries)
            {
                string normalizedRelativePath = entry.FilePath.TrimLeadingSlashes().Replace('\\', '/');
                if (!physicalThemes.TryGetValue(normalizedRelativePath, out var absolutePath))
                    continue;

                // Check if anime is in Shoko and has active physical files without querying the entire database upfront
                if (entry.AniDbId > 0)
                {
                    if (!localSeriesHasFiles.TryGetValue(entry.AniDbId, out bool hasFiles))
                        localSeriesHasFiles[entry.AniDbId] = hasFiles =
                            MetadataService.GetShokoSeriesByAnidbID(entry.AniDbId) is { } localSeries && MapHelper.GetActiveVideos(localSeries, MetadataService).Any();
                    if (hasFiles)
                        continue;
                }

                // Parse series name gracefully directly from the file basename
                string rawFileName = Path.GetFileNameWithoutExtension(entry.FilePath);
                int dashIndex = rawFileName.IndexOf('-');
                string pseudoTitle = AnimeThemesHelper.PascalCaseRegex().Replace(dashIndex > 0 ? rawFileName[..dashIndex] : rawFileName, " ");
                var lookup = new AnimeThemesVideoLookup(entry);

                items.Add(
                    BuildWebmTreeItem(
                        MissingCollectionLabel,
                        pseudoTitle,
                        0,
                        entry.AniDbId,
                        AnimeThemesHelper.BuildNewFileName(lookup, ""),
                        entry.FilePath,
                        absolutePath.Replace('\\', '/').Trim(),
                        entry.VideoId,
                        entry.Nc,
                        entry.Lyrics,
                        entry.Subbed,
                        entry.Uncen,
                        entry.NSFW,
                        entry.Spoiler,
                        entry.Overlap == "Transition",
                        entry.Overlap == "Over"
                    )
                );
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "AnimeThemes Player: Failed to append missing themes to tree");
        }
    }

    /// <summary>Reads the list of favourited AnimeThemes video IDs from disk under synchronization.</summary>
    /// <returns>The list of favourited video IDs.</returns>
    private List<int> ReadFavouriteIds()
    {
        string path = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtFavs);
        lock (s_favsLock)
        {
            if (!IoFile.Exists(path))
                return [];
            try
            {
                return [.. IoFile.ReadAllLines(path).Select(line => line.Trim()).Where(line => !line.StartsWith('#') && int.TryParse(line, out _)).Select(int.Parse)];
            }
            catch
            {
                return [];
            }
        }
    }

    #endregion
}
