using System.Collections.Concurrent;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.AnimeThemes;

/// <summary>Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.</summary>
/// <param name="apiClient">AnimeThemes API client.</param>
/// <param name="httpClient">HTTP client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public partial class AnimeThemesMapping(
    AnimeThemesApi apiClient,
    HttpClient httpClient,
    IMetadataService metadataService,
    IVideoService videoService,
    IFileSystem fileSystem,
    ConfigProvider configProvider,
    ILogger<AnimeThemesMapping> logger
)
{
    #region Setup

    private readonly AnimeThemesApi _apiClient = apiClient;

    #endregion

    #region Public API

    /// <summary>Loads and groups the AnimeThemes mapping entries, resolving their expected VFS filenames.</summary>
    /// <param name="configDirectory">The plugin configuration directory path.</param>
    /// <returns>A dictionary mapping AniDB ID to their expected VFS theme items.</returns>
    public static Dictionary<int, List<ThemeMapItem>> LoadThemeMappings(string configDirectory)
    {
        var mappings = new Dictionary<int, List<ThemeMapItem>>();
        string mapPath = Path.Combine(configDirectory, ShokoRelayConstants.FileAtMapping);
        if (!File.Exists(mapPath))
            return mappings;

        try
        {
            var entries = AnimeThemesHelper.ParseMappingContentWithComments(VfsShared.ReadAllTextShared(mapPath)).Entries;
            foreach (var entry in entries)
            {
                if (entry.AniDbId <= 0)
                    continue;

                if (!mappings.TryGetValue(entry.AniDbId, out var list))
                    mappings[entry.AniDbId] = list = [];

                list.Add(new ThemeMapItem(AnimeThemesHelper.BuildNewFileName(new AnimeThemesVideoLookup(entry), ".webm"), entry.FilePath));
            }
        }
        catch { }

        return mappings;
    }

    /// <summary>Download the mapping file from a direct raw URL and save it.</summary>
    /// <param name="rawUrl">Raw URL to download from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple of entry count and a log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await httpClient.GetStringAsync(rawUrl, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (0, "Downloaded content empty");

            await VfsShared.VfsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await VfsShared.WriteTextAtomicAsync(Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping), content, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                VfsShared.VfsLock.Release();
            }

            int count = AnimeThemesHelper.ParseMappingContentWithComments(content).Entries.Count;
            return (count, $"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nUrl: {rawUrl}\nEntries: {count}");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AnimeThemes Map: Failed to import mapping from URL");
            return (0, "Import failed: " + exception.Message);
        }
    }

    /// <summary>Scan configured import roots for AnimeThemes files and write a mapping CSV.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A build result with statistics.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("AnimeThemes Map: Starting mapping task...");

        bool startedTask = TaskHelper.StartTask(ShokoRelayConstants.TaskAtMapBuild);
        try
        {
            string themeFolder = VfsShared.ResolveAnimeThemesFolderName();
            var roots = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true).Select(path => Path.Combine(path, themeFolder)).Where(fileSystem.DirectoryExists).ToList();
            if (roots.Count == 0)
                throw new DirectoryNotFoundException("AnimeThemes root folder not found");

            string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
            var (existingComments, parsedEntries) = await ReadMappingFileAsync(mapPath, cancellationToken).ConfigureAwait(false);
            var entries = new List<AnimeThemesMappingEntry>();
            var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);
            var existingByFilename = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in parsedEntries)
            {
                existing.TryAdd(entry.FilePath, entry);
                existingByFilename.TryAdd(Path.GetFileName(entry.FilePath), entry);
            }

            var filesBag = new ConcurrentBag<(string File, string RelativePath)>();
            var listingOptions = new FileSystemListingOptions
            {
                Recursive = true,
                Extensions = [".webm"],
                Filter = entry => !entry.IsDirectory || !entry.Name.Equals("misc", StringComparison.OrdinalIgnoreCase),
            };

            Parallel.ForEach(roots, DefaultParallelOptions(cancellationToken), root =>
            {
                foreach (var file in fileSystem.GetFilePaths(root, listingOptions, cancellationToken))
                    filesBag.Add((file, "/" + Path.GetRelativePath(root, file).Replace('\\', '/')));
            });

            var uniqueFiles = filesBag.DistinctBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var toProcess = new List<(string File, string RelativePath)>();

            foreach (var (file, relativePath) in uniqueFiles)
            {
                if (existing.TryGetValue(relativePath, out var oldEntry) && oldEntry.AniDbId > 0)
                    entries.Add(oldEntry);
                else if (existingByFilename.TryGetValue(Path.GetFileName(file), out var oldByName) && oldByName.AniDbId > 0)
                    entries.Add(oldByName with { FilePath = relativePath });
                else
                    toProcess.Add((file, relativePath));
            }

            logger.LogInformation("AnimeThemes Map: Found {TotalCount} total files ({CachedCount} cached, {PendingCount} pending mapping resolution)...", uniqueFiles.Count, entries.Count, toProcess.Count);

            int reusedCount = entries.Count;
            var errorsBag = new ConcurrentBag<(int Year, string FilePath, string Message)>();
            var newMappingsBag = new ConcurrentBag<(int Year, string FilePath, string Message)>();
            int errors = 0;
            await Parallel
                .ForEachAsync(toProcess, DefaultParallelOptions(cancellationToken), async (item, token) =>
                {
                    try
                    {
                        int year = AnimeThemesHelper.GetYearForSort(item.RelativePath);
                        var (lookup, idMissing) = await FetchMetadataAsync(Path.GetFileName(item.File), null, token).ConfigureAwait(false);
                        if (lookup == null)
                        {
                            string errorMessage = $"Missing metadata for {item.RelativePath}";
                            errorsBag.Add((year, item.RelativePath, errorMessage));
                            logger.LogWarning("AnimeThemes Map: Failed to map '{Path}' -> Metadata missing", item.RelativePath);
                            Interlocked.Increment(ref errors);
                            return;
                        }

                        lock (entries)
                            entries.Add(new AnimeThemesMappingEntry(item.RelativePath, lookup));

                        if (idMissing)
                        {
                            errorsBag.Add((year, item.RelativePath, $"AniDB ID still missing on AnimeThemes for {item.RelativePath}"));
                            logger.LogWarning("AnimeThemes Map: Recorded '{Path}' with missing AniDB ID", item.RelativePath);
                        }
                        else
                        {
                            string mapMessage = $"Mapped: {item.RelativePath} -> VideoID: {lookup.VideoId}, AniDB ID: {lookup.AniDbId}";
                            newMappingsBag.Add((year, item.RelativePath, mapMessage));
                            logger.LogDebug("AnimeThemes Map: Mapped '{Path}' -> VideoID: {VideoId}, AniDB ID: {AnidbId}", item.RelativePath, lookup.VideoId, lookup.AniDbId);
                        }
                    }
                    catch (Exception exception)
                    {
                        int year = AnimeThemesHelper.GetYearForSort(item.RelativePath);
                        string errorMessage = $"{item.RelativePath}: {exception.Message}";
                        errorsBag.Add((year, item.RelativePath, errorMessage));
                        logger.LogWarning(exception, "AnimeThemes Map: Exception mapping '{Path}' -> {Error}", item.RelativePath, exception.Message);
                        Interlocked.Increment(ref errors);
                    }
                })
                .ConfigureAwait(false);

            var finalEntries = await SaveMappingFileAsync(mapPath, existingComments, entries, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("AnimeThemes Map: Finished mapping task -> {Count} entries written.", finalEntries.Count);
            List<string> finalMessages =
            [
                .. errorsBag.OrderBy(errorItem => errorItem.Year).ThenBy(errorItem => errorItem.FilePath).Select(errorItem => errorItem.Message),
                .. newMappingsBag.OrderBy(mappingItem => mappingItem.Year).ThenBy(mappingItem => mappingItem.FilePath).Select(mappingItem => mappingItem.Message),
            ];
            return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, reusedCount, errors, finalMessages);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(ShokoRelayConstants.TaskAtMapBuild);
        }
    }

    /// <summary>Resolves metadata and appends newly downloaded theme entries to the mapping CSV file.</summary>
    /// <param name="newFiles">Collection of relative file paths and filenames to map.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Count of new mapping entries successfully written.</returns>
    public async Task<int> AppendEntriesToMappingFileAsync(IReadOnlyCollection<(string FilePath, string FileName)> newFiles, CancellationToken cancellationToken = default)
    {
        if (newFiles.Count == 0)
            return 0;

        await VfsShared.VfsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
            var (existingComments, entries) = await ReadMappingFileAsync(mapPath, cancellationToken).ConfigureAwait(false);
            var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
                existing.TryAdd(entry.FilePath, entry);

            int addedCount = 0;
            foreach (var (relativePath, fileName) in newFiles)
            {
                if (existing.TryGetValue(relativePath, out var oldEntry) && oldEntry.AniDbId > 0)
                    continue;

                try
                {
                    var (lookup, idMissing) = await FetchMetadataAsync(fileName, null, cancellationToken).ConfigureAwait(false);
                    if (lookup != null)
                    {
                        var entry = new AnimeThemesMappingEntry(relativePath, lookup);
                        if (oldEntry != null)
                            entries.Remove(oldEntry);

                        entries.Add(entry);
                        existing[relativePath] = entry;
                        addedCount++;
                        if (lookup.AniDbId > 0)
                            logger.LogInformation("AnimeThemes Map: Auto-mapped '{Path}' -> VideoID: {VideoId}, AniDB ID: {AnidbId}", relativePath, lookup.VideoId, lookup.AniDbId);
                        else
                            logger.LogWarning("AnimeThemes Map: Recorded '{Path}' with missing AniDB ID", relativePath);
                    }
                    else
                        logger.LogWarning("AnimeThemes Map: Failed to auto-map '{Path}' -> Metadata missing", relativePath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "AnimeThemes Map: Exception auto-mapping '{Path}'", relativePath);
                }
            }

            if (addedCount > 0)
            {
                await SaveMappingFileAsync(mapPath, existingComments, entries, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("AnimeThemes Map: Appended {Count} new entries to mapping file.", addedCount);
            }

            return addedCount;
        }
        finally
        {
            VfsShared.VfsLock.Release();
        }
    }

    /// <summary>Tests, resolves, or updates mapping fields for a specific webm filename without redownloading, with optional persistence and AniDB ID override.</summary>
    /// <param name="webmFileName">The webm filename or relative path.</param>
    /// <param name="save">Whether to save the resolved mapping to anidb_animethemes_xrefs.csv.</param>
    /// <param name="overrideAnidbId">Optional AniDB ID override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the mapping entry, error message, generated filename, save status, and list of updated fields.</returns>
    public async Task<(AnimeThemesMappingEntry? entry, string? error, string filename, bool wasSaved, List<string> changedFields)> ProcessSingleMappingAsync(
        string webmFileName,
        bool save = false,
        int? overrideAnidbId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(webmFileName)
            || webmFileName.IndexOfAny(['\r', '\n', ',']) >= 0
            || webmFileName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal)
        )
            return (null, "Invalid filename or path.", webmFileName, false, []);

        string fileName = Path.GetFileName(webmFileName);
        var (lookup, idMissing) = await FetchMetadataAsync(fileName, overrideAnidbId, cancellationToken).ConfigureAwait(false);
        if (lookup == null)
            return (null, "Missing metadata on AnimeThemes", webmFileName, false, []);

        string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
        await VfsShared.VfsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (existingComments, entries) = await ReadMappingFileAsync(mapPath, cancellationToken).ConfigureAwait(false);
            string relativePath = webmFileName.StartsWith('/') ? webmFileName : "";
            AnimeThemesMappingEntry? match = entries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(relativePath) && string.Equals(entry.FilePath, relativePath, StringComparison.OrdinalIgnoreCase))
            );
            if (string.IsNullOrEmpty(relativePath) && match != null)
                relativePath = match.FilePath;

            if (string.IsNullOrEmpty(relativePath))
            {
                string themeFolder = VfsShared.ResolveAnimeThemesFolderName();
                var matchedFile = VfsShared
                    .GetManagedFolderPaths(videoService, onlyVfsEnabled: true)
                    .Select(root => Path.Combine(root, themeFolder))
                    .Where(fileSystem.DirectoryExists)
                    .Select(dir =>
                        (
                            Root: dir,
                            File: fileSystem
                                .GetFilePaths(
                                    dir,
                                    new FileSystemListingOptions { Recursive = true, Filter = entry => entry.IsDirectory || entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) },
                                    cancellationToken
                                )
                                .FirstOrDefault()
                        )
                    )
                    .FirstOrDefault(candidate => candidate.File != null);

                if (matchedFile.File != null)
                    relativePath = "/" + Path.GetRelativePath(matchedFile.Root, matchedFile.File).Replace('\\', '/');
            }

            if (string.IsNullOrEmpty(relativePath))
                relativePath = "/unknown/" + fileName;

            // Preserve existing AniDB ID if already mapped and AnimeThemes still lacks the resource link
            int effectiveAnidbId = overrideAnidbId ?? (lookup.AniDbId > 0 ? lookup.AniDbId : (match?.AniDbId ?? 0));
            var effectiveLookup = lookup with { AniDbId = effectiveAnidbId };
            var entry = new AnimeThemesMappingEntry(relativePath, effectiveLookup);
            string generatedFileName = AnimeThemesHelper.BuildNewFileName(effectiveLookup, Path.GetExtension(fileName));

            var changedFields = new List<string>();
            if (match != null)
            {
                if (match.AniDbId != entry.AniDbId)
                    changedFields.Add($"AniDbId: {match.AniDbId} -> {entry.AniDbId}");
                if (!string.Equals(match.SongTitle, entry.SongTitle, StringComparison.Ordinal))
                    changedFields.Add($"SongTitle: \"{match.SongTitle}\" -> \"{entry.SongTitle}\"");
                if (!string.Equals(match.ArtistName, entry.ArtistName, StringComparison.Ordinal))
                    changedFields.Add($"ArtistName: \"{match.ArtistName}\" -> \"{entry.ArtistName}\"");
                if (match.Nc != entry.Nc)
                    changedFields.Add($"NC: {match.Nc} -> {entry.Nc}");
                if (!string.Equals(match.Slug, entry.Slug, StringComparison.Ordinal))
                    changedFields.Add($"Slug: {match.Slug} -> {entry.Slug}");
                if (match.Version != entry.Version)
                    changedFields.Add($"Version: {match.Version} -> {entry.Version}");
                if (match.Lyrics != entry.Lyrics)
                    changedFields.Add($"Lyrics: {match.Lyrics} -> {entry.Lyrics}");
                if (match.Subbed != entry.Subbed)
                    changedFields.Add($"Subbed: {match.Subbed} -> {entry.Subbed}");
                if (match.Uncen != entry.Uncen)
                    changedFields.Add($"Uncen: {match.Uncen} -> {entry.Uncen}");
                if (match.NSFW != entry.NSFW)
                    changedFields.Add($"NSFW: {match.NSFW} -> {entry.NSFW}");
                if (match.Spoiler != entry.Spoiler)
                    changedFields.Add($"Spoiler: {match.Spoiler} -> {entry.Spoiler}");
                if (!string.Equals(match.Source, entry.Source, StringComparison.Ordinal))
                    changedFields.Add($"Source: {match.Source} -> {entry.Source}");
                if (match.Resolution != entry.Resolution)
                    changedFields.Add($"Resolution: {match.Resolution} -> {entry.Resolution}");
                if (!string.Equals(match.Episodes, entry.Episodes, StringComparison.Ordinal))
                    changedFields.Add($"Episodes: \"{match.Episodes}\" -> \"{entry.Episodes}\"");
                if (!string.Equals(match.Overlap, entry.Overlap, StringComparison.Ordinal))
                    changedFields.Add($"Overlap: {match.Overlap} -> {entry.Overlap}");
            }

            if (!save)
                return (entry, idMissing && effectiveAnidbId <= 0 ? "AniDB ID missing on AnimeThemes" : null, generatedFileName, false, changedFields);

            entries.RemoveAll(existingEntry =>
                string.Equals(existingEntry.FilePath, relativePath, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(existingEntry.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
            );
            entries.Add(entry);

            await SaveMappingFileAsync(mapPath, existingComments, entries, cancellationToken).ConfigureAwait(false);

            if (changedFields.Count > 0)
                logger.LogInformation("AnimeThemes Map: Updated {Count} field(s) for '{Path}': {Fields}", changedFields.Count, relativePath, string.Join(", ", changedFields));
            else
                logger.LogInformation("AnimeThemes Map: Saved mapping for '{Path}' -> VideoID: {VideoId}, AniDB ID: {AnidbId}", relativePath, effectiveLookup.VideoId, effectiveLookup.AniDbId);

            // If the entry has a valid AniDB ID, refresh the VFS symlink immediately so the renamed file is visible in Plex
            if (entry.AniDbId > 0 && metadataService.GetShokoSeriesByAnidbID(entry.AniDbId) is { } shokoSeries)
                await ApplyMappingAsync([shokoSeries.LocalID], CancellationToken.None).ConfigureAwait(false);

            return (entry, null, generatedFileName, true, changedFields);
        }
        finally
        {
            VfsShared.VfsLock.Release();
        }
    }

    #endregion

    #region Metadata Fetching

    /// <summary>Fetches the theme metadata for a specific video filename.</summary>
    /// <param name="fileName">The basename of the video file.</param>
    /// <param name="overrideAnidbId">Optional manual AniDB ID override.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the mapped metadata lookup and a boolean indicating if the AniDB ID was missing.</returns>
    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string fileName, int? overrideAnidbId = null, CancellationToken cancellationToken = default)
    {
        var videoEntry = await _apiClient.FetchVideoWithArtistsAsync(fileName, cancellationToken).ConfigureAwait(false);
        if (videoEntry == null)
            return (null, false);
        var first = videoEntry.Animethemeentries?.Nodes?.FirstOrDefault();
        if (first?.Animetheme == null)
            return (null, false);

        string[] seasonOrder = ["Winter", "Spring", "Summer", "Fall"];
        var allAnime = videoEntry.Animethemeentries?.Nodes?.Select(entry => entry.Animetheme?.Anime).Where(anime => anime != null).ToList() ?? [];

        var best = allAnime
            .Select(entry => new
            {
                Entry = entry,
                Year = entry!.Year ?? 9999,
                SeasonOrder = entry.Season != null ? Array.IndexOf(seasonOrder, entry.Season) : 9999,
                Ids = entry
                    .Resources?.Nodes?.Where(resource => string.Equals(resource.Site, "aniDB", StringComparison.OrdinalIgnoreCase) && resource.ExternalId.HasValue)
                    .Select(resource => resource.ExternalId!.Value)
                    .OrderBy(id => id)
                    .ToList(),
            })
            .Where(candidate => candidate.Ids?.Count > 0)
            .OrderBy(candidate => candidate.Year)
            .ThenBy(candidate => candidate.SeasonOrder)
            .ThenBy(candidate => candidate.Ids?.FirstOrDefault() ?? 9999)
            .FirstOrDefault();

        int anidbId = overrideAnidbId ?? best?.Ids?[0] ?? 0;
        bool idMissing = anidbId <= 0;
        var theme = first.Animetheme;

        return (
            new AnimeThemesVideoLookup(
                videoEntry.Id,
                anidbId,
                videoEntry.Nc,
                theme.Slug ?? "",
                first.Version,
                string.Join(" / ", theme.Song?.Performances?.Select(performance => performance.Artist?.Name?.Main).Where(name => !string.IsNullOrEmpty(name)) ?? []),
                theme.Song?.Title?.Romaji ?? "",
                videoEntry.Lyrics,
                videoEntry.Subbed,
                videoEntry.Uncen,
                first.Nsfw,
                first.Spoiler,
                videoEntry.Source ?? "",
                videoEntry.Resolution ?? 0,
                first.Episodes ?? "",
                videoEntry.Overlap ?? ""
            ),
            idMissing
        );
    }

    #endregion

    #region Internal Helpers

    /// <summary>Reads comments and mapping entries from an AnimeThemes mapping CSV file if it exists.</summary>
    /// <param name="mapPath">The full path to the mapping CSV file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing preserved comments and parsed mapping entries.</returns>
    private static async Task<(List<string> Comments, List<AnimeThemesMappingEntry> Entries)> ReadMappingFileAsync(string mapPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(mapPath))
            return ([], []);

        try
        {
            return AnimeThemesHelper.ParseMappingContentWithComments(await VfsShared.ReadAllTextSharedAsync(mapPath, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            return ([], []);
        }
    }

    /// <summary>Deduplicates, sorts, and atomically writes mapping entries to disk.</summary>
    /// <param name="mapPath">The full path to the mapping CSV file.</param>
    /// <param name="comments">Preserved header comment lines.</param>
    /// <param name="entries">The mapping entries to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deduplicated and sorted list of entries written to disk.</returns>
    private static async Task<List<AnimeThemesMappingEntry>> SaveMappingFileAsync(string mapPath, List<string> comments, IEnumerable<AnimeThemesMappingEntry> entries, CancellationToken cancellationToken)
    {
        var finalEntries = entries.DistinctBy(entry => entry.FilePath).OrderBy(entry => AnimeThemesHelper.GetYearForSort(entry.FilePath)).ThenBy(entry => entry.FilePath).ToList();
        await VfsShared.WriteTextAtomicAsync(mapPath, AnimeThemesHelper.SerializeMapping(comments, finalEntries), cancellationToken).ConfigureAwait(false);
        return finalEntries;
    }

    #endregion
}
