using System.Text;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Containers;
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
    AnimeThemesWebmDownloader webmDownloader
) : ShokoRelayBaseController(configProvider, metadataService, plexLibrary)
{
    #region VFS Mapping / Build

    /// <summary>Applies the anime‑themes mapping file to the directory structure.</summary>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    /// <returns>A task representing the result of the build outcome.</returns>
    [HttpGet("animethemes/vfs/build")]
    public Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? filter = null) =>
        ValidateFilterOrBadRequest(filter, out var filterIds) is { } guard
            ? Task.FromResult(guard)
            : ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskAtVfsBuild,
                ShokoRelayConstants.LogAtVfs,
                (sb, r) => LogHelper.BuildAtVfsBuildReport(sb, r, filterIds ?? []),
                async () =>
                {
                    var result = await animeThemesMapping.ApplyMappingAsync(filterIds, CancellationToken.None).ConfigureAwait(false);
                    if (filterIds == null || filterIds.Count == 0)
                    {
                        try
                        {
                            var cacheLines = result.CacheEntries.Select(ce => $"{ce.VfsPath.Replace('\\', '/')}|{ce.VideoId}|{ce.Bitmask}");
                            IoFile.WriteAllLines(Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtWebmCache), cacheLines);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "AnimeThemes: Failed to save webm cache");
                        }
                    }
                    return result;
                },
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

        return await ExecuteTrackedTaskAsync(
                ShokoRelayConstants.TaskAtMapBuild,
                ShokoRelayConstants.LogAtMap,
                LogHelper.BuildAtVfsMapReport,
                () => animeThemesMapping.BuildMappingFileAsync(CancellationToken.None),
                VfsShared.VfsLock
            )
            .ConfigureAwait(false);
    }

    /// <summary>Downloads and imports the curated mapping CSV from GitHub.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries imported.</returns>
    [HttpPost("animethemes/vfs/import")]
    public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
    {
        Logger.Info("AnimeThemes: Fetching curated mapping file from GitHub...");
        const string RawUrl = AnimeThemesHelper.AtRawMapUrl + ShokoRelayConstants.FileAtMapping;
        var (count, _) = await animeThemesMapping.ImportMappingFromUrlAsync(RawUrl, cancellationToken).ConfigureAwait(false);
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
        {
            return await ExecuteTrackedTaskAsync(
                    ShokoRelayConstants.TaskAtMp3Build,
                    ShokoRelayConstants.LogAtMp3,
                    LogHelper.BuildAtMp3Report,
                    async () =>
                    {
                        var batch = await animeThemesMp3Generator.ProcessBatchAsync(query, CancellationToken.None).ConfigureAwait(false);
                        foreach (var item in batch.Items.Where(i => i.Status == "ok" && !string.IsNullOrWhiteSpace(i.Folder)))
                            animeThemesMp3Generator.AddToThemeMp3Cache(item.Folder);
                        return batch;
                    },
                    VfsShared.VfsLock
                )
                .ConfigureAwait(false);
        }

        // Single generation does not require a persistent log file, but still uses the VfsLock for safety.
        if (!await VfsShared.VfsLock.WaitAsync(0).ConfigureAwait(false))
            return Conflict(new RelayResponse<object>(Status: "busy", Message: "A conflicting VFS operation is already in progress."));

        try
        {
            var result = await animeThemesMp3Generator.ProcessSingleAsync(query, null, CancellationToken.None).ConfigureAwait(false);
            if (result.Status == "ok" && !string.IsNullOrWhiteSpace(result.Folder))
                animeThemesMp3Generator.AddToThemeMp3Cache(result.Folder);
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
        var folders = animeThemesMp3Generator.GetCachedThemeMp3Folders();
        return folders.Count == 0
            ? NotFound(new RelayResponse<object>(Status: "error", Message: "No themes found"))
            : Ok(new RelayResponse<object>(Data: new { path = folders[Random.Shared.Next(folders.Count)] }));
    }

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
        string resolved = PlexLibrary.MapPlexPathToShokoPath(path);
        string themePath = Path.Combine(Path.GetFullPath(resolved), "Theme.mp3");

        if (!IoFile.Exists(themePath))
            return NotFound();

        try
        {
            var tags = ReadId3v2Tags(themePath);
            foreach (var tag in tags)
                Response.Headers[$"X-Theme-{tag.Key}"] = tag.Value;
            Response.Headers["Access-Control-Expose-Headers"] = "X-Theme-Title, X-Theme-Slug, X-Theme-Artist, X-Theme-Album";
        }
        catch { }

        return File(new FileStream(themePath, FileMode.Open, FileAccess.Read, FileShare.Read), "audio/mpeg", enableRangeProcessing: true);
    }

    #endregion

    #region WebM Player

    /// <summary>Returns the hierarchical tree of WebM files for the standalone player.</summary>
    /// <returns>A JSON object containing the hierarchical theme list.</returns>
    [HttpGet("animethemes/webm/tree")]
    public IActionResult AnimeThemesWebmTree()
    {
        string cachePath = Path.Combine(ConfigProvider.ConfigDirectory, ShokoRelayConstants.FileAtWebmCache);
        if (!IoFile.Exists(cachePath))
            return Ok(new { items = Array.Empty<object>() });

        string[] lines;
        try
        {
            lines = [.. IoFile.ReadAllLines(cachePath).Where(l => !string.IsNullOrWhiteSpace(l))];
        }
        catch
        {
            return Ok(new { items = Array.Empty<object>() });
        }

        var items = new List<object>();
        var seriesTitleCache = new Dictionary<int, (string GroupTitle, string SeriesTitle, int AniDbId)>();

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
                var series = MetadataService.GetShokoSeriesByID(seriesId.Value);
                if (series != null)
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
            var favs = IoFile.ReadAllLines(path).Select(l => l.Trim()).Where(l => !l.StartsWith("#") && int.TryParse(l, out _)).Select(int.Parse).ToList();
            return Ok(new RelayResponse<List<int>>(Data: favs));
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
    /// <remarks>Please use the official AnimeThemes archive torrent first before using this endpoint to fill out missing entries.</remarks>
    /// <param name="query">Filter parameters for downloading.</param>
    /// <returns>A task representing the result of the download operation.</returns>
    [HttpPost("animethemes/webm/download")]
    public async Task<IActionResult> DownloadAnimeThemesWebm([FromQuery] AnimeThemesWebmQuery query)
    {
        bool hasYear = query.Year.HasValue;
        bool hasSeason = !string.IsNullOrWhiteSpace(query.Season);

        return hasYear != hasSeason ? BadRequest(new RelayResponse<object>(Status: "error", Message: "Year and Season must both be provided together when filtering by date."))
            : string.IsNullOrWhiteSpace(query.Name) && !hasYear ? BadRequest(new RelayResponse<object>(Status: "error", Message: "At least one filter (Name or Year + Season) is required."))
            : await ExecuteTrackedTaskAsync(
                    ShokoRelayConstants.TaskAtWebmDownload,
                    ShokoRelayConstants.LogAtWebmDownload,
                    LogHelper.BuildWebmDownloadReport,
                    () => webmDownloader.DownloadAsync(query, CancellationToken.None)
                )
                .ConfigureAwait(false);
    }

    #endregion

    #region Private Helpers

    /// <summary>Reads specific ID3v2 tags from an MP3 file without external dependencies.</summary>
    /// <param name="filePath">Absolute path to the MP3 file.</param>
    /// <returns>A dictionary of tag names and values.</returns>
    private static Dictionary<string, string> ReadId3v2Tags(string filePath)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> h = stackalloc byte[10];
        if (fs.Read(h) < 10 || h[0] != 'I' || h[1] != 'D' || h[2] != '3')
            return tags;

        int tagSize = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
        int maxRead = Math.Min(tagSize, 65536);
        byte[] d = new byte[maxRead];
        int r = fs.Read(d, 0, maxRead);
        int p = 0;
        while (p + 10 <= r)
        {
            string id = Encoding.ASCII.GetString(d, p, 4);
            if (id[0] == '\0')
                break;
            int sz = (d[p + 4] << 24) | (d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7];
            if (sz <= 0 || p + 10 + sz > r)
                break;
            if (id.StartsWith('T') && id != "TXXX" && sz > 1)
            {
                string val = d[p + 10] switch
                {
                    1 => Encoding.Unicode.GetString(d, p + 11, sz - 1),
                    2 => Encoding.BigEndianUnicode.GetString(d, p + 11, sz - 1),
                    3 => Encoding.UTF8.GetString(d, p + 11, sz - 1),
                    _ => Encoding.Latin1.GetString(d, p + 11, sz - 1),
                };
                tags[id.Replace("TIT2", "Title").Replace("TIT3", "Slug").Replace("TPE1", "Artist").Replace("TALB", "Album")] = val.TrimEnd('\0').Replace("\uFEFF", "");
            }
            p += 10 + sz;
        }
        return tags;
    }

    #endregion
}
