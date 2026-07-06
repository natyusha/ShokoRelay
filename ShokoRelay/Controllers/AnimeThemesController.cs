using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Vfs;
using IoFile = System.IO.File;

namespace ShokoRelay.Controllers;

/// <summary>Provides operations for building AnimeThemes VFS mappings, generating MP3 series themes, and handling the standalone video player endpoints.</summary>
[ApiController]
[ApiVersion(ShokoRelayConstants.ApiVersion)]
[Route(ShokoRelayConstants.BasePath)]
public class AnimeThemesController(
    ConfigProvider configProvider,
    IMetadataService metadataService,
    PlexClient plexLibrary,
    AnimeThemesMp3Generator animeThemesMp3Generator,
    AnimeThemesMapping animeThemesMapping,
    AnimeThemesWebmDownloader webmDownloader,
    IVideoService videoService
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region Setup

    private static readonly SemaphoreSlim s_webmDownloadLock = new(1, 1);

    #endregion

    #region VFS Mapping & Build

    /// <summary>Applies the anime‑themes mapping file to the directory structure.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <returns>A task representing the result of the build outcome.</returns>
    [HttpGet("animethemes/vfs/build")]
    public Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? filter = null) =>
        ValidateFilterOrBadRequest(filter, out var filterIds) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskAtVfsBuild,
                (sb, r) => LogHelper.BuildAtVfsBuildReport(sb, r, filterIds ?? []),
                () => animeThemesMapping.ApplyMappingAsync(filterIds, CancellationToken.None),
                VfsShared.VfsLock
            );

    /// <summary>Generates the anime‑themes mapping CSV or tests a single filename mapping.</summary>
    /// <param name="testPath">Optional webm filename to test mapping logic against.</param>
    /// <returns>A task representing the result of the mapping or test.</returns>
    [HttpGet("animethemes/vfs/map")]
    public async Task<IActionResult> AnimeThemesVfsMap(string? testPath = null)
    {
        if (!string.IsNullOrWhiteSpace(testPath))
        {
            var (entry, error, gen) = await animeThemesMapping.TestMappingEntryAsync(testPath, CancellationToken.None).ConfigureAwait(false);
            return error != null
                ? Ok(new RelayResponse<object>(Status: "error", Message: error, Data: new { testPath }))
                : Ok(
                    new RelayResponse<object>(
                        Data: new
                        {
                            testPath,
                            generatedFilename = gen,
                            csvLine = entry != null ? AnimeThemesMapping.SerializeMappingEntry(entry) : null,
                            entry,
                        }
                    )
                );
        }

        return await ExecuteTrackedTaskAsync(ShokoRelayConstants.TaskAtMapBuild, LogHelper.BuildAtVfsMapReport, () => animeThemesMapping.BuildMappingFileAsync(CancellationToken.None), VfsShared.VfsLock)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads and imports the curated mapping CSV from GitHub.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries imported.</returns>
    [HttpPost("animethemes/vfs/import")]
    public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
    {
        Logger.Info("AnimeThemes: Fetching curated mapping file from GitHub...");
        var (count, _) = await animeThemesMapping.ImportMappingFromUrlAsync(AnimeThemesHelper.AtRawMapUrl + ShokoRelayConstants.FileAtMapping, cancellationToken).ConfigureAwait(false);
        Logger.Info("AnimeThemes: Import successful. {0} entries updated.", count);
        return Ok(new RelayResponse<object>(Data: new { count }));
    }

    #endregion

    #region MP3 Generation

    /// <summary>Generates Theme.mp3 files for anime series.</summary>
    /// <param name="query">Parameters for the MP3 generation request.</param>
    /// <returns>A task representing the result of the generation.</returns>
    [HttpGet("animethemes/mp3")]
    public async Task<IActionResult> AnimeThemesMp3([FromQuery] AnimeThemesMp3Query query)
    {
        if (string.IsNullOrWhiteSpace(query.Path))
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "path is required"));
        string reverse = PlexLibrary.MapPlexPathToShokoPath(query.Path);
        if (!string.Equals(reverse, query.Path, StringComparison.Ordinal))
            query = query with { Path = reverse };

        if (query.Batch)
            return await ExecuteTrackedTaskAsync(
                    ShokoRelayConstants.TaskAtMp3Build,
                    LogHelper.BuildAtMp3Report,
                    async () =>
                    {
                        var batch = await animeThemesMp3Generator.ProcessBatchAsync(query, CancellationToken.None).ConfigureAwait(false);
                        foreach (var item in batch.Items.Where(i => i.Status == "ok" && !string.IsNullOrWhiteSpace(i.Folder)))
                            animeThemesMp3Generator.AddToThemeMp3Cache(item.Folder, AnimeThemesHelper.StandardizeSlug(item.Slug));
                        return batch;
                    },
                    VfsShared.VfsLock
                )
                .ConfigureAwait(false);

        // Single generation does not require a persistent log file, but still uses the VfsLock for safety.
        if (!await VfsShared.VfsLock.WaitAsync(0).ConfigureAwait(false))
            return Conflict(new RelayResponse<object>(Status: "busy", Message: "A conflicting operation is already in progress. Please wait for it to complete."));

        try
        {
            var result = await animeThemesMp3Generator.ProcessSingleAsync(query, null, CancellationToken.None).ConfigureAwait(false);
            if (result.Status == "ok" && !string.IsNullOrWhiteSpace(result.Folder))
                animeThemesMp3Generator.AddToThemeMp3Cache(result.Folder, AnimeThemesHelper.StandardizeSlug(result.Slug));
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
    public IActionResult AnimeThemesMp3Random([FromQuery] bool refresh = false)
    {
        if (refresh)
            animeThemesMp3Generator.RefreshThemeMp3Cache();
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
    /// <returns>A task representing the result of the audit.</returns>
    [HttpGet("animethemes/mp3/audit")]
    public async Task<IActionResult> AuditAnimeThemesMp3() =>
        await ExecuteTrackedTaskAsync(ShokoRelayConstants.TaskAtMp3Audit, LogHelper.BuildAtMp3AuditReport, () => animeThemesMp3Generator.AuditAsync(CancellationToken.None), VfsShared.VfsLock)
            .ConfigureAwait(false);

    /// <summary>Streams an existing Theme.mp3 with ID3 tags embedded in response headers.</summary>
    /// <param name="path">The folder path containing the Theme.mp3.</param>
    /// <returns>A file stream result.</returns>
    [Route("animethemes/mp3/stream")]
    [HttpGet]
    [HttpHead]
    public IActionResult AnimeThemesMp3Stream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();
        string themePath = Path.Combine(Path.GetFullPath(PlexLibrary.MapPlexPathToShokoPath(path)), "Theme.mp3");

        if (!IoFile.Exists(themePath))
            return NotFound();

        try
        {
            var tags = AnimeThemesHelper.ReadId3v2Tags(themePath);
            foreach (var tag in tags)
                Response.Headers[$"X-Theme-{tag.Key}"] = tag.Value;
            Response.Headers["Access-Control-Expose-Headers"] = "X-Theme-Title, X-Theme-Slug, X-Theme-Artist, X-Theme-Album";
        }
        catch { }

        return File(new FileStream(themePath, FileMode.Open, FileAccess.Read, FileShare.Read), "audio/mpeg", enableRangeProcessing: true);
    }

    #endregion

    #region WebM & Player

    /// <summary>Returns the hierarchical tree of WebM files for the standalone player.</summary>
    /// <returns>A JSON object containing the hierarchical theme list.</returns>
    [HttpGet("animethemes/webm/tree")]
    public IActionResult AnimeThemesWebmTree()
    {
        var items = new List<object>();
        var seriesTitleCache = new Dictionary<int, (string GroupTitle, string SeriesTitle, int AniDbId)>();

        // Load VFS WebM Cache (Existing Anime)
        string cachePath = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtWebmCache);
        if (IoFile.Exists(cachePath))
        {
            try
            {
                var lines = IoFile.ReadAllLines(cachePath).Where(l => !string.IsNullOrWhiteSpace(l));
                foreach (var line in lines)
                {
                    var pipeParts = line.Split('|');
                    string pathRaw = pipeParts[0];
                    int videoId = (pipeParts.Length > 1 && int.TryParse(pipeParts[1], out var vid)) ? vid : 0;
                    int flags = (pipeParts.Length > 2 && int.TryParse(pipeParts[2], out var f)) ? f : 0;

                    var segments = pathRaw.Replace('\\', '/').Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
                    int? seriesId = null;
                    for (int i = 0; i < segments.Length; i++)
                        if (int.TryParse(segments[i], out int sid))
                        {
                            seriesId = sid;
                            break;
                        }

                    if (!seriesId.HasValue)
                        continue;

                    if (!seriesTitleCache.TryGetValue(seriesId.Value, out var info))
                    {
                        if (MetadataService.GetShokoSeriesByID(seriesId.Value) is { } series)
                        {
                            var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
                            var group = MetadataService.GetShokoGroupByID(series.TopLevelGroupID);
                            info = (group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle.Value : displayTitle, displayTitle, series.AnidbAnimeID);
                        }
                        else
                            info = ($"Series {seriesId.Value}", $"Series {seriesId.Value}", 0);
                        seriesTitleCache[seriesId.Value] = info;
                    }

                    items.Add(
                        new
                        {
                            group = info.GroupTitle,
                            series = info.SeriesTitle,
                            seriesId = seriesId.Value,
                            anidbId = info.AniDbId,
                            file = Path.GetFileNameWithoutExtension(segments[^1]),
                            path = pathRaw.Replace('\\', '/').Trim(),
                            videoId,
                            nc = (flags & 1) != 0,
                            lyrics = (flags & 2) != 0,
                            subs = (flags & 4) != 0,
                            uncen = (flags & 8) != 0,
                            nsfw = (flags & 16) != 0,
                            spoiler = (flags & 32) != 0,
                            trans = (flags & 64) != 0,
                            over = (flags & 128) != 0,
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "AnimeThemes: Failed to parse webm cache");
            }
        }

        // Load Mapping CSV (Missing Anime)
        string mapPath = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
        if (IoFile.Exists(mapPath))
        {
            try
            {
                var managedFolders = videoService.GetAllManagedFolders()?.Where(f => !VfsShared.IsSourceOnly(f)).Select(f => f.Path).ToList() ?? [];
                string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
                var entries = AnimeThemesHelper.ParseMappingContentWithComments(IoFile.ReadAllText(mapPath)).Entries;

                // Pre-calculate a fast lookup hashset for series that physically exist in the local collection
                var localSeriesWithFiles = new HashSet<int>(MetadataService.GetAllShokoSeries()?.Where(s => s.Episodes.Any(e => e.VideoList?.Count > 0)).Select(s => s.AnidbAnimeID) ?? []);

                foreach (var entry in entries)
                {
                    // Check if anime is NOT in Shoko, or if it is but has no physical files
                    if (localSeriesWithFiles.Contains(entry.AniDbId))
                        continue;

                    // Locate the physical file inside the managed !AnimeThemes folders
                    string? absolutePath = null;
                    foreach (var mf in managedFolders)
                    {
                        if (string.IsNullOrWhiteSpace(mf))
                            continue;
                        string candidate = Path.Combine(mf, themeRootName, entry.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                        if (IoFile.Exists(candidate))
                        {
                            absolutePath = candidate;
                            break;
                        }
                    }

                    if (absolutePath == null)
                        continue;

                    // Parse series name gracefully directly from the file basename
                    string pseudoTitle = $"{AnimeThemesHelper.GetTitleFromAnimeThemesFile(entry.FilePath)}";
                    var lookup = new AnimeThemesVideoLookup(entry);

                    items.Add(
                        new
                        {
                            group = "Missing from Collection",
                            series = pseudoTitle,
                            seriesId = 0,
                            anidbId = entry.AniDbId,
                            file = AnimeThemesHelper.BuildNewFileName(lookup, ""),
                            path = absolutePath.Replace('\\', '/').Trim(),
                            videoId = entry.VideoId,
                            nc = entry.NC,
                            lyrics = entry.Lyrics,
                            subs = entry.Subbed,
                            uncen = entry.Uncen,
                            nsfw = entry.NSFW,
                            spoiler = entry.Spoiler,
                            trans = entry.Overlap == "Transition",
                            over = entry.Overlap == "Over",
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "AnimeThemes: Failed to append missing themes to tree");
            }
        }

        return Ok(new { items });
    }

    /// <summary>Streams a WebM theme file from the VFS.</summary>
    /// <param name="path">The relative VFS path to the WebM file.</param>
    /// <returns>A file stream result.</returns>
    [Route("animethemes/webm/stream")]
    [HttpGet]
    [HttpHead]
    public IActionResult AnimeThemesWebmStream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();
        string fullPath = Path.GetFullPath(PlexLibrary.MapPlexPathToShokoPath(path));
        return !IoFile.Exists(fullPath) ? NotFound() : File(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read), "video/webm", enableRangeProcessing: true);
    }

    /// <summary>Returns the list of videoIds marked as favourites.</summary>
    /// <returns>An array of video IDs.</returns>
    [HttpGet("animethemes/webm/favourites")]
    public IActionResult GetAnimeThemesFavourites()
    {
        string path = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtFavsCache);
        if (!IoFile.Exists(path))
            return Ok(new RelayResponse<List<int>>(Data: []));
        try
        {
            return Ok(new RelayResponse<List<int>>(Data: [.. IoFile.ReadAllLines(path).Select(l => l.Trim()).Where(l => !l.StartsWith("#") && int.TryParse(l, out _)).Select(int.Parse)]));
        }
        catch
        {
            return Ok(new RelayResponse<List<int>>(Data: []));
        }
    }

    /// <summary>Toggles a VideoId in the favourites list.</summary>
    /// <param name="videoId">The AnimeThemes video ID to toggle.</param>
    /// <returns>A JSON object with the new favourite status.</returns>
    [HttpPost("animethemes/webm/favourites")]
    public IActionResult UpdateAnimeThemesFavourite([FromBody] int videoId)
    {
        if (videoId <= 0)
            return BadRequest(new RelayResponse<object>(Status: "error", Message: "Invalid videoId"));

        string path = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtFavsCache);
        var ids = new HashSet<int>();
        if (IoFile.Exists(path))
            foreach (var l in IoFile.ReadAllLines(path))
                if (int.TryParse(l.Trim(), out int id))
                    ids.Add(id);

        if (!ids.Remove(videoId))
            ids.Add(videoId);

        try
        {
            IoFile.WriteAllLines(path, ids.Select(i => i.ToString()));
            return Ok(new RelayResponse<object>(Data: new { videoId, isFavourite = ids.Contains(videoId) }));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new RelayResponse<object>(Status: "error", Message: ex.Message));
        }
    }

    /// <summary>Downloads AnimeThemes WebM files directly based on filters.</summary>
    /// <param name="query">Filter parameters for downloading.</param>
    /// <returns>A task representing the result of the download operation.</returns>
    [HttpPost("animethemes/webm/download")]
    public async Task<IActionResult> DownloadAnimeThemesWebm([FromQuery] AnimeThemesWebmQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Season) && !query.Year.HasValue)
            query = query with { Year = DateTime.Now.Year };

        return string.IsNullOrWhiteSpace(query.Name) && string.IsNullOrWhiteSpace(query.Season)
            ? BadRequest(new RelayResponse<object>(Status: "error", Message: "At least one filter (Name or Season) is required."))
            : await ExecuteTrackedTaskAsync(ShokoRelayConstants.TaskAtWebmDownload, LogHelper.BuildWebmDownloadReport, () => webmDownloader.DownloadAsync(query, CancellationToken.None), s_webmDownloadLock)
                .ConfigureAwait(false);
    }

    #endregion
}
