using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Services;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Controllers;

/// <summary>
/// Provides operations for building AnimeThemes VFS mappings, generating MP3 series themes,
/// and handling the standalone video player endpoints including favourites.
/// </summary>
public class AnimeThemesController : ShokoRelayBaseController
{
    private readonly AnimeThemesMp3Generator _animeThemesMp3Generator;
    private readonly AnimeThemesMapping _animeThemesMapping;

    public AnimeThemesController(ConfigProvider configProvider, IMetadataService metadataService, PlexClient plexLibrary, AnimeThemesMp3Generator animeThemesMp3Generator, AnimeThemesMapping animeThemesMapping)
        : base(configProvider, metadataService, plexLibrary)
    {
        _animeThemesMp3Generator = animeThemesMp3Generator;
        _animeThemesMapping = animeThemesMapping;
    }

    #region VFS Mapping & Build

    /// <summary>
    /// Applies the anime‑themes mapping file to the directory structure.
    /// </summary>
    /// <param name="filter">Optional comma-separated Shoko Series IDs to restrict the build.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Statistics on links created and errors encountered.</returns>
    [HttpGet("animethemes/vfs/build")]
    public async Task<IActionResult> AnimeThemesVfsBuild([FromQuery] string? filter = null, CancellationToken cancellationToken = default)
    {
        var validation = ValidateFilterOrBadRequest(filter, out var filterIds);
        if (validation != null)
            return validation;

        string resolvedMapPath = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtMapFileName);
        if (!System.IO.File.Exists(resolvedMapPath))
            return BadRequest(new { status = "error", message = "Mapping file not found" });

        var result = await _animeThemesMapping.ApplyMappingAsync(filterIds, cancellationToken).ConfigureAwait(false);

        // Save the WebM cache for the standalone player during unfiltered builds
        if (filterIds == null || filterIds.Count == 0)
        {
            try
            {
                string themeRoot = Vfs.VfsShared.ResolveAnimeThemesFolderName();
                var xrefsRaw = System
                    .IO.File.ReadAllLines(resolvedMapPath)
                    .Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l))
                    .Select(l => TextHelper.SplitCsvLine(l))
                    .Where(f => f.Length > 1)
                    .ToDictionary(f => f[0], f => f, StringComparer.OrdinalIgnoreCase);

                var cacheLines = new List<string>();
                foreach (var plan in result.Planned)
                {
                    var parts = plan.Split(" <- ");
                    if (parts.Length < 2)
                        continue;

                    string vfsPath = parts[0];
                    string symlinkTarget = parts[1].Replace('\\', '/');

                    string lookupKey = string.Empty;
                    int rootIdx = symlinkTarget.IndexOf("/" + themeRoot + "/", StringComparison.OrdinalIgnoreCase);
                    if (rootIdx != -1)
                        lookupKey = symlinkTarget.Substring(rootIdx + themeRoot.Length + 1);
                    else if (symlinkTarget.Contains(themeRoot + "/"))
                        lookupKey = symlinkTarget.Substring(symlinkTarget.IndexOf(themeRoot + "/") + themeRoot.Length);

                    if (!lookupKey.StartsWith("/"))
                        lookupKey = "/" + lookupKey;

                    if (xrefsRaw.TryGetValue(lookupKey, out var fields))
                    {
                        int flags = 0;
                        string videoId = fields.Length > 1 ? fields[1] : "0";
                        if (fields.Length > 12)
                        {
                            if (fields[3] == "1")
                                flags |= 1; // nc
                            if (fields[8] == "1")
                                flags |= 2; // lyrics
                            if (fields[9] == "1")
                                flags |= 4; // subs
                            if (fields[10] == "1")
                                flags |= 8; // uncen
                            if (fields[11] == "1")
                                flags |= 16; // nsfw
                            if (fields[12] == "1")
                                flags |= 32; // spoiler
                        }
                        cacheLines.Add($"{vfsPath}|{videoId}|{flags}");
                    }
                    else
                        cacheLines.Add($"{vfsPath}|0|0");
                }
                System.IO.File.WriteAllLines(Path.Combine(_configProvider.ConfigDirectory, "webm_animethemes.cache"), cacheLines);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to save webm cache");
            }
        }

        return await LogAndReturn("at-vfs-build-report.log", result, (sb, r) => LogHelper.BuildAtVfsBuildReport(sb, r, filterIds ?? new List<int>()));
    }

    /// <summary>
    /// Generates the anime‑themes mapping CSV or tests a single filename.
    /// </summary>
    /// <param name="testPath">Optional filename to test against the API without writing to the CSV.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("animethemes/vfs/map")]
    public async Task<IActionResult> AnimeThemesVfsMap(string? testPath = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(testPath))
        {
            var (entry, error, generatedFilename) = await _animeThemesMapping.TestMappingEntryAsync(testPath, cancellationToken).ConfigureAwait(false);
            if (error != null)
                return Ok(
                    new
                    {
                        status = "error",
                        testPath,
                        error,
                    }
                );

            return Ok(
                new
                {
                    status = "ok",
                    testPath,
                    generatedFilename,
                    csvLine = entry != null ? AnimeThemesMapping.SerializeMappingEntry(entry) : null,
                    entry = entry,
                }
            );
        }

        var result = await _animeThemesMapping.BuildMappingFileAsync(cancellationToken).ConfigureAwait(false);
        return await LogAndReturn("at-vfs-map-report.log", result, (sb, r) => LogHelper.BuildAtVfsMapReport(sb, r));
    }

    /// <summary>
    /// Downloads and imports the curated mapping CSV from GitHub.
    /// </summary>
    [HttpPost("animethemes/vfs/import")]
    public async Task<IActionResult> ImportAnimeThemesMapping(CancellationToken cancellationToken = default)
    {
        const string rawUrl = AnimeThemesHelper.AtRawMapUrl + AnimeThemesHelper.AtMapFileName;
        var (count, log) = await _animeThemesMapping.ImportMappingFromUrlAsync(rawUrl, cancellationToken).ConfigureAwait(false);
        return Ok(new { status = "ok", count });
    }

    #endregion

    #region MP3 Generation

    /// <summary>
    /// Generates Theme.mp3 files for anime series.
    /// </summary>
    [HttpGet("animethemes/mp3")]
    public async Task<IActionResult> AnimeThemesMp3([FromQuery] AnimeThemesMp3Query query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Path))
            return BadRequest(new { status = "error", message = "path is required" });

        string reverse = _plexLibrary.MapPlexPathToShokoPath(query.Path);
        if (!string.Equals(reverse, query.Path, StringComparison.Ordinal))
            query = query with { Path = reverse };

        if (query.Batch)
        {
            var batch = await _animeThemesMp3Generator.ProcessBatchAsync(query, cancellationToken);
            foreach (var item in batch.Items.Where(i => i.Status == "ok" && !string.IsNullOrWhiteSpace(i.Folder)))
                _animeThemesMp3Generator.AddToThemeMp3Cache(item.Folder);

            return await LogAndReturn("at-mp3-report.log", batch, (sb, r) => LogHelper.BuildAtMp3Report(sb, r));
        }

        var single = await _animeThemesMp3Generator.ProcessSingleAsync(query, cancellationToken);
        if (single.Status == "error")
            return BadRequest(single);
        if (single.Status == "ok" && !string.IsNullOrWhiteSpace(single.Folder))
            _animeThemesMp3Generator.AddToThemeMp3Cache(single.Folder);

        return Ok(single);
    }

    /// <summary>
    /// Returns a random Theme.mp3 path from the current cache.
    /// </summary>
    [HttpGet("animethemes/mp3/random")]
    public IActionResult AnimeThemesMp3Random([FromQuery] bool refresh = false)
    {
        if (refresh)
            _animeThemesMp3Generator.RefreshThemeMp3Cache();
        var folders = _animeThemesMp3Generator.GetCachedThemeMp3Folders();
        if (folders.Count == 0)
            return NotFound();
        return Ok(new { status = "ok", path = folders[Random.Shared.Next(folders.Count)] });
    }

    /// <summary>
    /// Streams an existing Theme.mp3 with ID3 tags embedded in response headers.
    /// </summary>
    [HttpGet("animethemes/mp3/stream")]
    [HttpHead("animethemes/mp3/stream")]
    public IActionResult AnimeThemesMp3Stream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();
        string resolved = _plexLibrary.MapPlexPathToShokoPath(path);
        string themePath = Path.Combine(Path.GetFullPath(resolved), "Theme.mp3");

        if (!System.IO.File.Exists(themePath))
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

    #region WebM & Favourites

    /// <summary>
    /// Returns the hierarchical tree of WebM files for the standalone player.
    /// </summary>
    [HttpGet("animethemes/webm/tree")]
    public IActionResult AnimeThemesWebmTree()
    {
        string cachePath = Path.Combine(_configProvider.ConfigDirectory, "webm_animethemes.cache");
        if (!System.IO.File.Exists(cachePath))
            return Ok(new { status = "empty" });

        string[] lines;
        try
        {
            lines = System.IO.File.ReadAllLines(cachePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }
        catch
        {
            return Ok(new { status = "empty" });
        }

        var items = new List<object>();
        var seriesTitleCache = new Dictionary<int, (string GroupTitle, string SeriesTitle)>();

        foreach (var line in lines)
        {
            var pipeParts = line.Split('|');
            string pathRaw = pipeParts[0];
            int videoId = (pipeParts.Length > 1 && int.TryParse(pipeParts[1], out var vid)) ? vid : 0;
            int flags = (pipeParts.Length > 2 && int.TryParse(pipeParts[2], out var f)) ? f : 0;

            string normalized = pathRaw.Replace('\\', '/').Trim();
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int? seriesId = null;
            for (int i = 0; i < segments.Length; i++)
                if (int.TryParse(segments[i], out int sid))
                {
                    seriesId = sid;
                    break;
                }

            if (!seriesId.HasValue)
                continue;

            if (!seriesTitleCache.TryGetValue(seriesId.Value, out var titles))
            {
                var series = _metadataService.GetShokoSeriesByID(seriesId.Value);
                if (series != null)
                {
                    var res = TextHelper.ResolveFullSeriesTitles(series);
                    var group = _metadataService.GetShokoGroupByID(series.TopLevelGroupID);
                    titles = (group is IWithTitles titled && !string.IsNullOrWhiteSpace(titled.PreferredTitle?.Value) ? titled.PreferredTitle!.Value : res.DisplayTitle, res.DisplayTitle);
                }
                else
                    titles = ($"Series {seriesId.Value}", $"Series {seriesId.Value}");
                seriesTitleCache[seriesId.Value] = titles;
            }

            items.Add(
                new
                {
                    group = titles.GroupTitle,
                    series = titles.SeriesTitle,
                    file = Path.GetFileNameWithoutExtension(segments[^1]),
                    path = pathRaw.Replace('\\', '/').Trim(),
                    videoId,
                    nc = (flags & 1) != 0,
                    lyrics = (flags & 2) != 0,
                    subs = (flags & 4) != 0,
                    uncen = (flags & 8) != 0,
                    nsfw = (flags & 16) != 0,
                    spoiler = (flags & 32) != 0,
                }
            );
        }
        return Ok(new { status = "ok", items });
    }

    /// <summary>
    /// Streams a WebM theme file from the VFS.
    /// </summary>
    [HttpGet("animethemes/webm/stream")]
    [HttpHead("animethemes/webm/stream")]
    public IActionResult AnimeThemesWebmStream([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();
        string fullPath = Path.GetFullPath(_plexLibrary.MapPlexPathToShokoPath(path));
        if (!System.IO.File.Exists(fullPath))
            return NotFound();
        return File(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read), "video/webm", enableRangeProcessing: true);
    }

    /// <summary>
    /// Returns the list of videoIds marked as favourites.
    /// </summary>
    [HttpGet("animethemes/webm/favourites")]
    public IActionResult GetAnimeThemesFavourites()
    {
        string path = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtFavsFileName);
        if (!System.IO.File.Exists(path))
            return Ok(Array.Empty<int>());
        try
        {
            return Ok(System.IO.File.ReadAllLines(path).Select(l => l.Trim()).Where(l => !l.StartsWith("#") && int.TryParse(l, out _)).Select(int.Parse).ToList());
        }
        catch
        {
            return Ok(Array.Empty<int>());
        }
    }

    /// <summary>
    /// Toggles a VideoId in the favourites list.
    /// </summary>
    [HttpPost("animethemes/webm/favourites")]
    public IActionResult UpdateAnimeThemesFavourite([FromBody] int videoId)
    {
        if (videoId <= 0)
            return BadRequest();
        string path = Path.Combine(_configProvider.ConfigDirectory, AnimeThemesHelper.AtFavsFileName);
        var ids = new HashSet<int>();
        if (System.IO.File.Exists(path))
            foreach (var l in System.IO.File.ReadAllLines(path))
                if (int.TryParse(l.Trim(), out int id))
                    ids.Add(id);

        bool isFav = !ids.Remove(videoId);
        if (isFav)
            ids.Add(videoId);

        try
        {
            System.IO.File.WriteAllLines(path, ids.Select(i => i.ToString()));
            return Ok(new { videoId, isFavourite = isFav });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    #endregion

    #region Private Helpers

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
            string id = System.Text.Encoding.ASCII.GetString(d, p, 4);
            if (id[0] == '\0')
                break;
            int sz = (d[p + 4] << 24) | (d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7];
            if (sz <= 0 || p + 10 + sz > r)
                break;
            if (id.StartsWith('T') && id != "TXXX" && sz > 1)
            {
                string val = d[p + 10] switch
                {
                    1 => System.Text.Encoding.Unicode.GetString(d, p + 11, sz - 1),
                    2 => System.Text.Encoding.BigEndianUnicode.GetString(d, p + 11, sz - 1),
                    3 => System.Text.Encoding.UTF8.GetString(d, p + 11, sz - 1),
                    _ => System.Text.Encoding.Latin1.GetString(d, p + 11, sz - 1),
                };
                tags[id.Replace("TIT2", "Title").Replace("TIT3", "Slug").Replace("TPE1", "Artist").Replace("TALB", "Album")] = val.TrimEnd('\0').Replace("\uFEFF", "");
            }
            p += 10 + sz;
        }
        return tags;
    }

    #endregion
}
