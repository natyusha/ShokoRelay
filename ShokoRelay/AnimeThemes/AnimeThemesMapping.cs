using System.Diagnostics;
using System.Text.Json.Serialization;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Represents a single anime theme mapping between a local file path and AniDB/video identifiers.
/// All boolean fields are written as 1 (true) or 0 (false).
/// </summary>
public sealed record AnimeThemesMappingEntry(
    [property: JsonPropertyName("filepath")] string FilePath,
    [property: JsonPropertyName("videoId")] int VideoId,
    [property: JsonPropertyName("anidbId")] int AniDbId,
    [property: JsonPropertyName("nc")] bool NC, // (Creditless)
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("songTitle")] string SongTitle,
    [property: JsonPropertyName("lyrics")] bool Lyrics,
    [property: JsonPropertyName("subbed")] bool Subbed,
    [property: JsonPropertyName("uncen")] bool Uncen,
    [property: JsonPropertyName("nsfw")] bool NSFW,
    [property: JsonPropertyName("spoiler")] bool Spoiler,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("resolution")] int Resolution,
    [property: JsonPropertyName("episodes")] string Episodes,
    [property: JsonPropertyName("overlap")] string Overlap // Transition, Over, or None
);

/// <summary>
/// Result returned by a mapping file build operation, including statistics and messages.
/// </summary>
public sealed record AnimeThemesMappingBuildResult(string MapPath, int EntriesWritten, int Reused, int Errors, IReadOnlyList<string> Messages);

/// <summary>
/// Outcome of applying a mapping file to create VFS links, detailing how many links were created or skipped.
/// </summary>
public sealed record AnimeThemesMappingApplyResult(int LinksCreated, int Skipped, int SeriesMatched, IReadOnlyList<string> Errors, IReadOnlyList<string> Planned, TimeSpan Elapsed);

/// <summary>
/// Internal helper record used when looking up theme metadata by video identifier.
/// </summary>
internal sealed record AnimeThemesVideoLookup(
    int VideoId,
    int ThemeId,
    int AniDbId,
    bool NC,
    string Slug,
    int Version,
    string ArtistName,
    string SongTitle,
    bool Lyrics,
    bool Subbed,
    bool Uncen,
    bool NSFW,
    bool Spoiler,
    string Source,
    int Resolution,
    string Episodes,
    string Overlap
);

/// <summary>
/// Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers. Includes helpers for importing mapping data and querying the AnimeThemes API.
/// </summary>
public class AnimeThemesMapping
{
    // helpers for reading/writing the mapping file in CSV form. The format uses simple comma-separated rows; commas inside values are escaped as "\u002C" by the generators.
    // CSV columns: filepath, videoId, anidbId, nc, slug, version, artistName, songTitle, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap
    private static List<AnimeThemesMappingEntry> ParseMappingContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new List<AnimeThemesMappingEntry>();

        var result = new List<AnimeThemesMappingEntry>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            var fields = TextHelper.SplitCsvLine(line);
            if (fields.Length < 17)
                continue;

            string filepath = fields[0];
            if (!int.TryParse(fields[1], out int vid))
                continue;
            if (!int.TryParse(fields[2], out int aid))
                continue;
            bool nc = ParseCsvBoolean(fields[3]);
            string slug = fields[4];
            if (!int.TryParse(fields[5], out int version))
                continue;
            string songTitle = TextHelper.UnescapeUnicode(fields[6]);
            string artistName = TextHelper.UnescapeUnicode(fields[7]);
            bool lyrics = ParseCsvBoolean(fields[8]);
            bool subbed = ParseCsvBoolean(fields[9]);
            bool uncen = ParseCsvBoolean(fields[10]);
            bool nsfw = ParseCsvBoolean(fields[11]);
            bool spoiler = ParseCsvBoolean(fields[12]);
            string source = fields[13];
            int resolution = 0;
            var resField = fields[14];
            if (!string.IsNullOrWhiteSpace(resField) && int.TryParse(resField, out var tmpRes))
                resolution = tmpRes;
            string episodes = fields[15];
            string overlap = fields[16];

            result.Add(new AnimeThemesMappingEntry(filepath, vid, aid, nc, slug, version, artistName, songTitle, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap));
        }
        return result;
    }

    private static string SerializeEntry(AnimeThemesMappingEntry e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(e.FilePath).Append(',');
        sb.Append(e.VideoId).Append(',');
        sb.Append(e.AniDbId).Append(',');
        sb.Append(e.NC ? "1" : "0").Append(',');
        sb.Append(e.Slug).Append(',');
        sb.Append(e.Version).Append(',');
        sb.Append(TextHelper.EscapeCsvCommas(e.SongTitle)).Append(',');
        sb.Append(TextHelper.EscapeCsvCommas(e.ArtistName)).Append(',');
        sb.Append(e.Lyrics ? "1" : "0").Append(',');
        sb.Append(e.Subbed ? "1" : "0").Append(',');
        sb.Append(e.Uncen ? "1" : "0").Append(',');
        sb.Append(e.NSFW ? "1" : "0").Append(',');
        sb.Append(e.Spoiler ? "1" : "0").Append(',');
        sb.Append(e.Source).Append(',');
        sb.Append(e.Resolution).Append(',');
        sb.Append(TextHelper.EscapeCsvCommas(e.Episodes)).Append(',');
        sb.Append(e.Overlap);
        return sb.ToString();
    }

    private static string SerializeMapping(List<AnimeThemesMappingEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Shoko Relay AniDB AnimeThemes Xrefs ##");
        sb.AppendLine();
        sb.AppendLine("# filepath, videoId, anidbId, nc, slug, version, songTitle, artistName, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap"); // see Controller.md for details
        foreach (var e in entries)
            sb.AppendLine(SerializeEntry(e));
        return sb.ToString();
    }

    /// <summary>
    /// Serialize a single AnimeThemesMappingEntry to a CSV line.
    /// </summary>
    internal static string SerializeMappingEntry(AnimeThemesMappingEntry entry)
    {
        return SerializeEntry(entry);
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IMetadataService _metadataService;
    private readonly IVideoService _videoService;
    private readonly AnimeThemesApi _apiClient;

    private readonly string _configDirectory;

    public AnimeThemesMapping(IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
    {
        _metadataService = metadataService;
        _videoService = videoService;
        _apiClient = new AnimeThemesApi();

        _configDirectory = configProvider.ConfigDirectory;
    }

    /// <summary>
    /// Download the mapping file from a direct raw URL (e.g. gist raw link) and save it.
    /// </summary>
    /// <param name="rawUrl">Raw URL to download the mapping content from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (Count, Log) with the number of entries parsed and a human-readable log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        int count = 0;
        string logMsg;

        try
        {
            using var client = new HttpClient();
            AnimeThemesConstants.EnsureUserAgent(client);
            var content = await client.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                logMsg = "Downloaded content empty";
                return (0, logMsg);
            }

            string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
            await File.WriteAllTextAsync(mapPath, content, ct).ConfigureAwait(false);

            try
            {
                count = ParseMappingContent(content).Count;
            }
            catch { }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Url: {rawUrl}");
            sb.AppendLine($"Entries: {count}");
            logMsg = sb.ToString();
            return (count, logMsg);
        }
        catch (Exception ex)
        {
            logMsg = "Import failed: " + ex.Message;
            Logger.Warn(ex, "Failed to import mapping from URL");
            return (0, logMsg);
        }
    }

    /// <summary>
    /// Scan configured import roots for AnimeThemes files and write a mapping CSV.
    /// The resulting file is always written to the standard location in the configuration directory and any existing file will be read to perform incremental updates.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingBuildResult"/> with statistics and the output file path.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken ct = default)
    {
        // build list of candidate root folders containing the AnimeThemes files
        var rootPaths = new List<string>();

        // auto-detect by looking for the configured root folder under every destination import root
        {
            var themeFolder = GetThemeRootFolderName();
            var candidates = _videoService
                .GetAllVideoFiles()
                .Select(v => v.ManagedFolder?.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.Combine(p!, themeFolder))
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .ToList();

            if (candidates.Count > 0)
                rootPaths.AddRange(candidates);
        }

        // discard any paths that no longer exist
        rootPaths = rootPaths.Where(Directory.Exists).ToList();
        if (rootPaths.Count == 0)
            throw new DirectoryNotFoundException("AnimeThemes root folder not found (no valid root paths)");

        Logger.Info("AnimeThemes mapping build started (roots={RootList})", string.Join(';', rootPaths));
        var sw = Stopwatch.StartNew();

        string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
        // map file path (CSV) for output. existing CSV contents will be read when performing incremental updates.
        var messages = new List<string>();
        var entries = new List<AnimeThemesMappingEntry>();
        int errors = 0;
        int reusedCount = 0; // number of files already present in existing map

        // load existing map to avoid re-querying the API for entries we already have
        var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(mapPath))
        {
            try
            {
                string oldContent = await File.ReadAllTextAsync(mapPath, ct).ConfigureAwait(false);
                foreach (var e in ParseMappingContent(oldContent))
                    existing.TryAdd(e.FilePath, e);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read existing AnimeThemes mapping; regenerating all entries");
            }
        }

        // gather all webm files from every detected root, but ignore any that live inside a "misc" subfolder
        var files = rootPaths
            .Where(Directory.Exists)
            .SelectMany(root =>
                Directory
                    .EnumerateFiles(root, "*.webm", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var rel = Path.GetRelativePath(root, f);
                        // split on both separators to support inconsistent paths
                        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return !parts.Any(p => string.Equals(p, "misc", StringComparison.OrdinalIgnoreCase));
                    })
            )
            .ToList();

        // phase 1: partition files into reused (already in existing map) and toProcess (need API calls)
        var toProcess = new List<(string File, string Rel)>();
        foreach (string file in files)
        {
            string? root = rootPaths.FirstOrDefault(r =>
                file.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || file.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            );
            string rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/').TrimStart('/') : file;

            if (existing.TryGetValue(rel, out var oldEntry))
            {
                entries.Add(oldEntry);
                reusedCount++;
            }
            else
            {
                toProcess.Add((file, rel));
            }
        }

        // phase 2: fetch metadata for new files in parallel; the rate limiter serialises API calls
        Logger.Info("AnimeThemes mapping has {ToProcessCount} new files to query", toProcess.Count);
        int maxDop = Math.Max(1, ShokoRelay.Settings.Parallelism);
        await Parallel
            .ForEachAsync(
                toProcess,
                new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = ct },
                async (item, token) =>
                {
                    Logger.Info("Fetching metadata for {Rel}", item.Rel);
                    try
                    {
                        string baseName = Path.GetFileName(item.File);

                        var (lookup, idMissing) = await FetchMetadataAsync(baseName, token);
                        if (lookup == null)
                        {
                            lock (messages)
                            {
                                Interlocked.Increment(ref errors);
                                messages.Add(idMissing ? $"AniDB ID missing for {item.Rel}" : $"Missing metadata for {item.Rel}");
                            }
                            return;
                        }

                        lock (entries)
                        {
                            entries.Add(
                                new AnimeThemesMappingEntry(
                                    item.Rel,
                                    lookup.VideoId,
                                    lookup.AniDbId,
                                    lookup.NC,
                                    lookup.Slug,
                                    lookup.Version,
                                    lookup.ArtistName,
                                    lookup.SongTitle,
                                    lookup.Lyrics,
                                    lookup.Subbed,
                                    lookup.Uncen,
                                    lookup.NSFW,
                                    lookup.Spoiler,
                                    lookup.Source,
                                    lookup.Resolution,
                                    lookup.Episodes,
                                    lookup.Overlap
                                )
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        Logger.Warn(ex, "Failed to build mapping for {File}", item.File);
                        lock (messages)
                        {
                            messages.Add($"{item.Rel}: {ex.Message}");
                        }
                    }
                }
            )
            .ConfigureAwait(false);

        // Deduplicate entries by FilePath (keeping first occurrence) to handle cases where users have multiple copies of the same themes
        var deduplicatedEntries = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            deduplicatedEntries.TryAdd(entry.FilePath, entry);
        }
        var finalEntries = deduplicatedEntries.Values.ToList();
        int dedupedCount = entries.Count - finalEntries.Count;

        var fileContent = SerializeMapping(finalEntries);
        await File.WriteAllTextAsync(mapPath, fileContent, ct);

        sw.Stop();
        Logger.Info(
            "AnimeThemes mapping build completed in {Elapsed}ms: entries={Count}, deduplicated={Deduped}, reused={Reused}, errors={Errors}",
            sw.ElapsedMilliseconds,
            finalEntries.Count,
            dedupedCount,
            reusedCount,
            errors
        );
        Logger.Info("AnimeThemes mapping written to {Path} with {Count} entries", mapPath, finalEntries.Count);
        return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, reusedCount, errors, messages);
    }

    /// <summary>
    /// Test the mapping process for a single webm filename without adding it to the CSV.
    /// Returns the metadata that would be created for the file, useful for verifying API integration and filename generation.
    /// </summary>
    /// <param name="webbmFileName">The webm filename to test (e.g., "OP1.webm" or "ED2v3.webm").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result object containing the generated entry, any errors, and status information.</returns>
    public async Task<(AnimeThemesMappingEntry? entry, string? error, string filename)> TestMappingEntryAsync(string webmFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webmFileName))
            return (null, "Filename is empty or null", webmFileName);

        try
        {
            Logger.Info("Testing mapping for {FileName}", webmFileName);

            var (lookup, idMissing) = await FetchMetadataAsync(webmFileName, ct);
            if (lookup == null)
            {
                string error = idMissing ? $"AniDB ID missing for {webmFileName}" : $"Missing metadata for {webmFileName}";
                Logger.Warn("Test failed: {Error}", error);
                return (null, error, webmFileName);
            }

            // Create a test entry with a dummy relative path
            string testPath = $"/test/{webmFileName}";
            var entry = new AnimeThemesMappingEntry(
                testPath,
                lookup.VideoId,
                lookup.AniDbId,
                lookup.NC,
                lookup.Slug,
                lookup.Version,
                lookup.ArtistName,
                lookup.SongTitle,
                lookup.Lyrics,
                lookup.Subbed,
                lookup.Uncen,
                lookup.NSFW,
                lookup.Spoiler,
                lookup.Source,
                lookup.Resolution,
                lookup.Episodes,
                lookup.Overlap
            );

            // Generate the filename that would be created
            string extension = Path.GetExtension(webmFileName);
            string generatedFilename = BuildNewFileName(lookup, extension);

            Logger.Info("Test succeeded for {FileName}: videoId={VideoId}, anidbId={AniDbId}, generated={Generated}", webmFileName, lookup.VideoId, lookup.AniDbId, generatedFilename);

            return (entry, null, generatedFilename);
        }
        catch (Exception ex)
        {
            string error = $"Test failed with exception: {ex.Message}";
            Logger.Warn(ex, "Test failed for {FileName}", webmFileName);
            return (null, error, webmFileName);
        }
    }

    /// <summary>
    /// Read a previously built mapping file and create/update VFS links for matching theme files.
    /// Optionally restrict to a set of series via <paramref name="seriesFilter"/>.
    /// </summary>
    /// <param name="seriesFilter">Optional set of Shoko series IDs to limit processing to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts of operations performed and any errors encountered.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        string mapPath = Path.Combine(_configDirectory, AnimeThemesConstants.AtMapFileName);
        if (!File.Exists(mapPath))
            throw new FileNotFoundException("Mapping file not found", mapPath);

        string content = await File.ReadAllTextAsync(mapPath, ct);
        var entries = ParseMappingContent(content);
        Logger.Info("Applying AnimeThemes mapping: {EntryCount} entries parsed", entries.Count);

        Logger.Info("AnimeThemes apply mapping started (map={MapPath}, entries={EntryCount}, filter={FilterCount})", mapPath, entries.Count, seriesFilter?.Count ?? 0);
        var sw = Stopwatch.StartNew();
        string themeRootFolder = GetThemeRootFolderName();

        List<IShokoSeries?> seriesList;
        if (seriesFilter != null && seriesFilter.Count > 0)
            seriesList = seriesFilter.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)).ToList();
        else
            seriesList = _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>().ToList();

        seriesList = seriesList.Where(s => s != null && s.AnidbAnimeID > 0).ToList();
        var byAniDb = seriesList.GroupBy(s => s!.AnidbAnimeID).ToDictionary(g => g.Key, g => g.ToList());

        string rootName = VfsShared.ResolveRootFolderName();
        var destSeen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var createdDirs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // cache import roots per series ID to avoid repeated episode/file enumeration
        var importRootsCache = new Dictionary<int, List<string>>();

        int created = 0;
        int skipped = 0;
        int matchedSeries = 0;
        var errors = new List<string>();
        var planned = new List<string>();
        var plannedDests = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (seriesFilter != null && seriesFilter.Count > 0)
        {
            foreach (var id in seriesFilter)
            {
                if (seriesList.All(s => s?.ID != id))
                    errors.Add($"Series {id} not found or missing AniDB id");
            }
        }

        // pre-group entries by AniDB ID so we can process per-series with timing; also filter by configured overlap level
        var entriesByAniDb = new Dictionary<int, List<AnimeThemesMappingEntry>>();
        var overlapLevel = ShokoRelay.Settings.AnimeThemesOverlapLevel;
        foreach (var entry in entries)
        {
            string relativePath = entry.FilePath.TrimStart('/', '\\');
            var segments = relativePath.Split('/', '\\');
            if (string.IsNullOrWhiteSpace(relativePath) || segments.Any(s => string.Equals(s, "misc", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            // Filter by overlap level: None (2) blocks Transition and Over; TransitionOnly (1) blocks Over; All (0) allows everything
            if (overlapLevel > OverlapLevel.All)
            {
                var overlap = entry.Overlap ?? "None";
                if (overlapLevel == OverlapLevel.TransitionOnly && string.Equals(overlap, "Over", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                if (overlapLevel == OverlapLevel.None && (string.Equals(overlap, "Transition", StringComparison.OrdinalIgnoreCase) || string.Equals(overlap, "Over", StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
            }

            if (!byAniDb.ContainsKey(entry.AniDbId))
            {
                skipped++;
                continue;
            }

            if (!entriesByAniDb.TryGetValue(entry.AniDbId, out var list))
            {
                list = new List<AnimeThemesMappingEntry>();
                entriesByAniDb[entry.AniDbId] = list;
            }
            list.Add(entry);
        }

        // process per-series (grouped by AniDB ID) to enable per-series logging
        foreach (var (anidbId, matchedEntries) in entriesByAniDb)
        {
            Logger.Info("Processing {Count} mappings for AniDB {AniDbId}", matchedEntries.Count, anidbId);
            ct.ThrowIfCancellationRequested();
            if (!byAniDb.TryGetValue(anidbId, out var seriesMatches))
                continue;

            foreach (var series in seriesMatches)
            {
                if (series == null)
                {
                    skipped++;
                    continue;
                }

                int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);

                if (!importRootsCache.TryGetValue(series.ID, out var roots))
                {
                    roots = GetImportRoots(series);
                    importRootsCache[series.ID] = roots;
                }
                if (roots.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var seriesSw = Stopwatch.StartNew();
                int seriesCreated = 0,
                    seriesSkipped = 0,
                    seriesErrors = 0,
                    seriesPlanned = 0;

                matchedSeries++;
                foreach (var entry in matchedEntries)
                {
                    string relativePath = entry.FilePath.TrimStart('/', '\\');
                    foreach (string importRoot in roots)
                    {
                        string? source = ResolveThemeSourcePath(relativePath, importRoot, themeRootFolder);
                        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                        {
                            skipped++;
                            seriesSkipped++;
                            errors.Add($"Source missing for {entry.FilePath} under {importRoot}");
                            continue;
                        }

                        string shortsDir = Path.Combine(importRoot, rootName, primaryId.ToString(), "Shorts");
                        string destName = BuildNewFileName(
                            new AnimeThemesVideoLookup(
                                entry.VideoId,
                                0,
                                entry.AniDbId,
                                entry.NC,
                                entry.Slug,
                                entry.Version,
                                entry.ArtistName,
                                entry.SongTitle,
                                entry.Lyrics,
                                entry.Subbed,
                                entry.Uncen,
                                entry.NSFW,
                                entry.Spoiler,
                                entry.Source,
                                entry.Resolution,
                                entry.Episodes,
                                entry.Overlap
                            ),
                            Path.GetExtension(source)
                        );
                        destName = EnsureExtension(destName, Path.GetExtension(source));
                        string destPath = Path.Combine(shortsDir, destName);

                        string relativeTarget = BuildThemeRelativeTarget(relativePath, themeRootFolder);

                        if (plannedDests.Add(destPath))
                        {
                            planned.Add($"{destPath} <- {relativeTarget}");
                            seriesPlanned++;
                        }

                        // only issue the syscall the first time we encounter this directory
                        if (createdDirs.Add(shortsDir))
                        {
                            try
                            {
                                Directory.CreateDirectory(shortsDir);
                            }
                            catch (Exception ex)
                            {
                                skipped++;
                                seriesSkipped++;
                                seriesErrors++;
                                Logger.Warn(ex, "Failed to create directory {Dir}", shortsDir);
                                errors.Add($"Failed to create directory {shortsDir}: {ex.Message}");
                                continue;
                            }
                        }

                        // ensure destPath is unique; if already seen, append numeric suffix until free
                        string originalDest = destPath;
                        int dupIdx = 1;
                        while (!destSeen.Add(destPath))
                        {
                            dupIdx++;
                            string baseNoExt = Path.GetFileNameWithoutExtension(originalDest);
                            string ext = Path.GetExtension(originalDest);
                            destPath = Path.Combine(shortsDir, $"{baseNoExt} ({dupIdx}){ext}");
                            for (int pi = 0; pi < planned.Count; pi++)
                            {
                                if (planned[pi].StartsWith(originalDest, StringComparison.OrdinalIgnoreCase))
                                    planned[pi] = planned[pi].Replace(originalDest, destPath);
                            }
                        }

                        if (VfsShared.TryCreateLink(source, destPath, Logger, targetOverride: relativeTarget))
                        {
                            created++;
                            seriesCreated++;
                        }
                        else
                        {
                            skipped++;
                            seriesSkipped++;
                            seriesErrors++;
                            Logger.Warn("Failed to link {Src} -> {Dest}", source, destPath);
                            errors.Add($"Failed to link {source} -> {destPath}");
                        }
                    }
                }

                seriesSw.Stop();
                Logger.Debug(
                    "AnimeThemesMapping --- BuildSeries {SeriesId} completed in {Elapsed}ms: mappings={Mappings} created={Created} planned={Planned} skipped={Skipped} errors={Errors}",
                    series.ID,
                    seriesSw.ElapsedMilliseconds,
                    matchedEntries.Count,
                    seriesCreated,
                    seriesPlanned,
                    seriesSkipped,
                    seriesErrors
                );
            }
        }

        sw.Stop();
        Logger.Info(
            "AnimeThemes apply mapping completed in {Elapsed}ms: created={Created}, skipped={Skipped}, matchedSeries={Matched}, errors={Errors}",
            sw.ElapsedMilliseconds,
            created,
            skipped,
            matchedSeries,
            errors.Count
        );
        return new AnimeThemesMappingApplyResult(created, skipped, matchedSeries, errors, planned, sw.Elapsed);
    }

    private static string EnsureExtension(string fileName, string ext)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "Unknown" + ext;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) && !string.IsNullOrWhiteSpace(ext))
            return fileName + ext;
        return fileName;
    }

    private static List<string> GetImportRoots(IShokoSeries series)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return series.Episodes.SelectMany(ep => ep.VideoList).SelectMany(v => v.Files).Select(VfsShared.ResolveImportRootPath).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(comparer).ToList()!;
    }

    // returns tuple containing lookup and flag if AniDB id was missing
    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string baseName, CancellationToken ct)
    {
        bool idMissing = false;

        // Optimized single call: fetch video with audio, theme info, anime, and song/artists in one request
        var videoResp = await _apiClient.FetchVideoWithAudioAsync(baseName, ct);
        if (videoResp?.Video == null)
            return (null, idMissing);

        // Extract video level values (in animethemes api order)
        int videoId = videoResp.Video.Id;
        bool lyrics = videoResp.Video.Lyrics;
        bool nc = videoResp.Video.NC;
        string overlap = videoResp.Video.Overlap ?? "";
        int resolution = videoResp.Video.Resolution ?? 0;
        string source = videoResp.Video.Source ?? "";
        bool subbed = videoResp.Video.Subbed;
        bool uncen = videoResp.Video.Uncen;

        var firstEntry = videoResp.Video.Animethemeentries?.FirstOrDefault();
        if (firstEntry?.Animetheme == null)
            return (null, idMissing);

        // Extra animethemeentries level values (prefer the first entry / in animethemes api order)
        int themeId = firstEntry.Animetheme.Id;
        string episodes = firstEntry.Episodes ?? "";
        bool nsfw = firstEntry.NSFW;
        bool spoiler = firstEntry.Spoiler;
        int version = firstEntry.Version;

        // Extract animetheme level values
        var song = firstEntry.Animetheme.Song;
        string slug = firstEntry.Animetheme.Slug ?? "";
        string songTitle = song?.Title ?? "";

        // Capture artist(s)
        string artist = string.Empty;
        var artistsList = song?.Artists;
        if (artistsList != null && artistsList.Count > 0)
        {
            artist = string.Join(" / ", artistsList.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name!));
        }

        // Lookup AniDB ID from anime query (need second call for this)
        var animeResp = await _apiClient.FetchAnimeResourcesAsync(videoId, ct);
        int anidbId = 0;
        // choose the anime entry with the earliest release date (year, then season); tie-breaker by smallest external AniDB id
        var animeList = animeResp?.Anime;
        if (animeList != null && animeList.Count > 0)
        {
            // determine season order
            string[] seasonOrder = { "Winter", "Spring", "Summer", "Fall" };
            var best = animeList
                .Select(e => new
                {
                    Entry = e,
                    Year = e.Year ?? int.MaxValue,
                    SeasonIdx = e.Season != null ? Array.IndexOf(seasonOrder, e.Season) : int.MaxValue,
                    AniDbIds = e.Resources?.Where(r => string.Equals(r.Site, "AniDB", StringComparison.OrdinalIgnoreCase) && r.ExternalId.HasValue).Select(r => r.ExternalId!.Value).OrderBy(id => id).ToList()
                        ?? new List<int>(),
                })
                .OrderBy(x => x.Year)
                .ThenBy(x => x.SeasonIdx)
                .ThenBy(x => x.AniDbIds.FirstOrDefault(int.MaxValue))
                .FirstOrDefault();
            if (best != null && best.AniDbIds.Count > 0)
                anidbId = best.AniDbIds.First();
        }
        if (anidbId == 0)
        {
            // video exists but the AniDB id was absent/nullable
            idMissing = true;
            return (null, idMissing);
        }

        return (new AnimeThemesVideoLookup(videoId, themeId, anidbId, nc, slug, version, artist, songTitle, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap), idMissing);
    }

    /// <summary>
    /// Filename format: {nc}{slug}{version} ❯ {songTitle} ❯ {artist} [{attributes}]
    /// Boolean attributes (Lyrics, Subbed, Uncensored, NSFW, Spoiler) are appended in brackets if true.
    /// Example: NCO‍P1v2 - Title · Artist [Lyrics, Subbed].webm
    /// </summary>
    private static string BuildNewFileName(AnimeThemesVideoLookup lookup, string extension)
    {
        // Insert a zero-width space to prevent Plex from renaming OP/ED-prefixed files. Prefix Hair space to OP to sort it before ED. Remove the numeric suffix if it is "1"
        string slug = string.IsNullOrWhiteSpace(lookup.Slug) ? "Theme" : lookup.Slug;
        const string zwsp = "\u200B"; // Zero-width space
        const string hsp = "\u200A"; // Hair space
        slug = slug switch
        {
            var s when s.StartsWith("OP") => $"{hsp}O{zwsp}P{(s[2..] == "1" ? "" : s[2..])}",
            var s when s.StartsWith("ED") => $"E{zwsp}D{(s[2..] == "1" ? "" : s[2..])}",
            _ => slug,
        };

        string ncPrefix = lookup.NC ? $"NC" : "";
        string versionStr = lookup.Version > 1 ? $"v{lookup.Version}" : ""; // Only show if version > 1
        string titleStr = string.IsNullOrWhiteSpace(lookup.SongTitle) ? "" : " ❯ " + lookup.SongTitle;

        // If 4+ artists, use "Various Artists" to prevent extremely long filenames
        string artistDisplay = (!string.IsNullOrWhiteSpace(lookup.ArtistName) && lookup.ArtistName.Split(" / ").Length >= 4) ? "Various Artists" : lookup.ArtistName;
        string ArtistStr = string.IsNullOrWhiteSpace(artistDisplay) ? "" : " ❯ " + artistDisplay;

        // Append boolean attributes if any are true
        var attributes = new List<string>(5);
        if (lookup.Lyrics)
            attributes.Add("LYRICS");
        if (lookup.Subbed)
            attributes.Add("SUBS");
        if (lookup.Uncen)
            attributes.Add("UNCEN");
        if (lookup.NSFW)
            attributes.Add("NSFW");
        if (lookup.Spoiler)
            attributes.Add("SPOIL");
        string attributeSuffix = attributes.Count > 0 ? $" [{string.Join(", ", attributes)}]" : "";

        string baseName = $"{ncPrefix}{slug}{versionStr}{titleStr}{ArtistStr}{attributeSuffix}";
        string full = baseName + extension;
        return VfsHelper.CleanEpisodeTitleForFilename(full);
    }

    /// <summary>
    /// Parse a CSV boolean field ("1" = true, anything else = false).
    /// </summary>
    private static bool ParseCsvBoolean(string field)
    {
        return field == "1";
    }

    private static string GetThemeRootFolderName() => VfsShared.ResolveAnimeThemesFolderName();

    private static string? ResolveThemeSourcePath(string relativeFilePath, string importRoot, string themeRootFolder)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(rel))
            return null;

        string basePath = Path.Combine(importRoot, themeRootFolder);

        string normalizedBase = Path.GetFullPath(basePath);
        string relativePart = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string candidate = Path.Combine(normalizedBase, relativePart);
        string fullCandidate = Path.GetFullPath(candidate);

        return File.Exists(fullCandidate) ? fullCandidate : null;
    }

    private static string BuildThemeRelativeTarget(string relativeFilePath, string themeRootFolder)
    {
        string rel = relativeFilePath.TrimStart('/', '\\');
        string normalized = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        string basePath = Path.Combine("..", "..", "..", themeRootFolder);
        string target = Path.Combine(basePath, normalized);

        return target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }
}
