using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.AnimeThemes;

/// <summary>Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.</summary>
/// <param name="apiClient">AnimeThemes API client.</param>
/// <param name="httpClient">HTTP client.</param>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="configProvider">Configuration provider.</param>
/// <param name="logger">Logger instance.</param>
public class AnimeThemesMapping(AnimeThemesApi apiClient, HttpClient httpClient, IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider, ILogger<AnimeThemesMapping> logger)
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
            var entries = AnimeThemesHelper.ParseMappingContentWithComments(File.ReadAllText(mapPath)).Entries;
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
            var roots = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true).Select(path => Path.Combine(path, themeFolder)).Where(Directory.Exists).ToList();
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
            var miscDirectoryToken = $"{Path.DirectorySeparatorChar}misc{Path.DirectorySeparatorChar}";

            Parallel.ForEach(roots, DefaultParallelOptions(cancellationToken), root =>
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.webm", SearchOption.AllDirectories))
                    if (!file.Contains(miscDirectoryToken, StringComparison.OrdinalIgnoreCase))
                        filesBag.Add((file, "/" + Path.GetRelativePath(root, file).Replace('\\', '/')));
            });

            var toProcess = new List<(string File, string RelativePath)>();

            foreach (var (file, relativePath) in filesBag)
            {
                if (existing.TryGetValue(relativePath, out var oldEntry) && oldEntry.AniDbId > 0)
                    entries.Add(oldEntry);
                else if (existingByFilename.TryGetValue(Path.GetFileName(file), out var oldByName) && oldByName.AniDbId > 0)
                    entries.Add(oldByName with { FilePath = relativePath });
                else
                    toProcess.Add((file, relativePath));
            }

            logger.LogInformation("AnimeThemes Map: Found {TotalCount} total files ({CachedCount} cached, {PendingCount} pending mapping resolution)...", filesBag.Count, entries.Count, toProcess.Count);

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
                            string mapMsg = $"Mapped: {item.RelativePath} -> VideoID: {lookup.VideoId}, AniDB ID: {lookup.AniDbId}";
                            newMappingsBag.Add((year, item.RelativePath, mapMsg));
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
                    .Where(Directory.Exists)
                    .Select(dir => (Root: dir, File: Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault()))
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

    /// <summary>Read a previously built mapping file and create/update VFS links for matching theme files.</summary>
    /// <remarks>
    ///   Prioritizes BD sources: if a filename collision occurs and a BD source is available, non-BD sources are skipped.
    ///   If multiple BD sources collide (or no BD sources exist), they are de-duplicated with (2), (3), etc.
    /// </remarks>
    /// <param name="seriesFilter">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An <see cref="AnimeThemesMappingApplyResult"/> with counts and results.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken cancellationToken = default)
    {
        bool startedTask = TaskHelper.StartTask(ShokoRelayConstants.TaskAtVfsBuild);
        try
        {
            logger.LogInformation("AnimeThemes VFS: Starting task...");
            string mapPath = Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping);
            if (!File.Exists(mapPath))
                throw new FileNotFoundException("Mapping file not found");

            var entries = AnimeThemesHelper.ParseMappingContentWithComments(await File.ReadAllTextAsync(mapPath, cancellationToken).ConfigureAwait(false)).Entries;
            var (stopwatch, state) = (Stopwatch.StartNew(), new MappingState());
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
            string vfsRoot = VfsShared.ResolveRootFolderName();

            // Pre-index entries by AniDB ID for instant bucket lookups per series
            var entriesByAniDb = entries.Where(entry => entry.AniDbId > 0).ToLookup(entry => entry.AniDbId);

            var seriesToQuery =
                seriesFilter?.Count > 0
                    ? seriesFilter
                        .Distinct()
                        .Select(id => OverrideHelper.GetPrimary(id, metadataService))
                        .Distinct()
                        .SelectMany(primaryId => OverrideHelper.GetGroup(primaryId, metadataService))
                        .Distinct()
                        .Select(metadataService.GetShokoSeriesByID)
                    : metadataService.GetAllShokoSeries();

            var folderGroups = seriesToQuery.Where(series => series?.AnidbAnimeID > 0).GroupBy(series => OverrideHelper.GetPrimary(series!.LocalID, metadataService)).ToList();

            Parallel.ForEach(folderGroups, DefaultParallelOptions(cancellationToken), folderGroup =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int primaryId = folderGroup.Key;
                var overrideOrder = OverrideHelper.GetGroup(primaryId, metadataService).ToList();

                var primarySeries = metadataService.GetShokoSeriesByID(primaryId);
                if (primarySeries == null)
                    return;

                var vfsPaths = VfsShared.ResolveSeriesVfsPaths(primarySeries, metadataService).ToList();
                if (vfsPaths.Count == 0)
                {
                    Interlocked.Increment(ref state.Skipped);
                    return;
                }

                Interlocked.Increment(ref state.Matched);
                var isFilteredRun = seriesFilter?.Any() == true;
                var myPrefixes = folderGroup.Select(series => overrideOrder.IndexOf(series!.LocalID) is var index && index > 0 ? $"P{index + 1} ❯ " : null).ToHashSet();

                foreach (var vfsPath in vfsPaths)
                {
                    string shortsDir = Path.Combine(vfsPath, "Shorts");
                    string root = Path.GetDirectoryName(Path.GetDirectoryName(vfsPath))!;
                    var plannedFilenames = new HashSet<string>(VfsShared.PathComparer);

                    var potentialLinks = overrideOrder
                        .Select(metadataService.GetShokoSeriesByID)
                        .OfType<IShokoSeries>()
                        .SelectMany(series =>
                        {
                            int seriesIndex = overrideOrder.IndexOf(series.LocalID);
                            return entriesByAniDb[series.AnidbAnimeID]
                                // Determine if a theme mapping's overlap level is allowed based on the configured overlap restriction
                                .Where(entry =>
                                    Settings.Advanced.AnimeThemesOverlapLevel == OverlapLevel.All
                                    || entry.Overlap.Equals("None", StringComparison.Ordinal)
                                    || (Settings.Advanced.AnimeThemesOverlapLevel == OverlapLevel.TransitionOnly && entry.Overlap.Equals("Transition", StringComparison.Ordinal))
                                )
                                .Select(entry =>
                                {
                                    string relativePath = entry.FilePath.TrimLeadingSlashes();
                                    string candidateSource = Path.Combine(root, themeRootName, VfsShared.NormalizeSeparators(relativePath));
                                    if (!File.Exists(candidateSource))
                                        return null;
                                    string sourcePath = Path.GetFullPath(candidateSource);
                                    var lookup = new AnimeThemesVideoLookup(entry);
                                    return new
                                    {
                                        Entry = entry,
                                        Lookup = lookup,
                                        Signature = Path.GetFileNameWithoutExtension(AnimeThemesHelper.BuildNewFileName(lookup with { Nc = false }, "")),
                                        SourcePath = sourcePath,
                                        Extension = Path.GetExtension(sourcePath),
                                        RelativePath = relativePath,
                                        SeriesIndex = seriesIndex,
                                        OriginalId = series.LocalID,
                                    };
                                });
                        })
                        .Where(candidate => candidate != null)
                        .GroupBy(candidate => candidate!.Signature);

                    foreach (var sigGroup in potentialLinks)
                    {
                        // If any BD source exists, discard non-BD sources. Otherwise, keep all (TV/WEB/etc).
                        bool hasBD = sigGroup.Any(candidate => candidate!.Entry.Source == "BD");
                        var sourceSurvivors = hasBD ? [.. sigGroup.Where(candidate => candidate!.Entry.Source == "BD")] : sigGroup.ToList();

                        // NC Filtering: If enabled, prefer versions without credits over those with credits
                        var finalSurvivors =
                            (Settings.Advanced.AnimeThemesPreferNc && sourceSurvivors.Any(candidate => candidate!.Entry.Nc)) ? [.. sourceSurvivors.Where(candidate => candidate!.Entry.Nc)] : sourceSurvivors;

                        int counter = 1;
                        foreach (var item in finalSurvivors)
                        {
                            string finalName = AnimeThemesHelper.BuildNewFileName(item!.Lookup, (counter > 1 ? $" ({counter})" : "") + item.Extension, item.SeriesIndex);
                            string destinationPath = VfsShared.NormalizeSeparators(Path.Combine(shortsDir, finalName));
                            plannedFilenames.Add(finalName);

                            if (!isFilteredRun || folderGroup.Any(series => series!.LocalID == item.OriginalId))
                            {
                                if (!Settings.Advanced.DisableVfsGeneration)
                                    Directory.CreateDirectory(shortsDir);
                                string relativeTarget = VfsShared.NormalizeSeparators(Path.Combine("..", "..", "..", themeRootName, item.RelativePath));
                                if (VfsShared.TryCreateLink(item.SourcePath, destinationPath, logger, targetOverride: relativeTarget))
                                {
                                    int bitmask =
                                        (item.Entry.Nc ? 1 : 0)
                                        | (item.Entry.Lyrics ? 2 : 0)
                                        | (item.Entry.Subbed ? 4 : 0)
                                        | (item.Entry.Uncen ? 8 : 0)
                                        | (item.Entry.NSFW ? 16 : 0)
                                        | (item.Entry.Spoiler ? 32 : 0)
                                        | (
                                            item.Entry.Overlap == "Transition" ? 64
                                            : item.Entry.Overlap == "Over" ? 128
                                            : 0
                                        );
                                    Interlocked.Increment(ref state.Created);
                                    state.CacheEntries.Add(new WebmCacheEntry(destinationPath, item.Entry.VideoId, bitmask));
                                }
                                else
                                    state.Errors.Add($"Link failed: {destinationPath}");
                            }
                            counter++;
                        }
                    }

                    if (Directory.Exists(shortsDir))
                    {
                        foreach (var file in Directory.EnumerateFiles(shortsDir))
                        {
                            string fileName = Path.GetFileName(file);
                            if (AnimeThemesHelper.CreditsFileRegex().IsMatch(fileName) || plannedFilenames.Contains(fileName))
                                continue;

                            // Protection for Plex Local Extras: Only delete the file if it's a symlink pointing to the AnimeThemes repository.
                            var info = new FileInfo(file);
                            if (info.LinkTarget == null || !info.LinkTarget.Contains(themeRootName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (isFilteredRun)
                            {
                                bool isMyFile = false;
                                if (AnimeThemesHelper.OverrideThemeFileRegex().IsMatch(fileName))
                                {
                                    foreach (var prefix in myPrefixes)
                                        if (prefix != null && fileName.StartsWith(prefix))
                                            isMyFile = true;
                                }
                                else if (myPrefixes.Contains(null))
                                    isMyFile = true;

                                if (!isMyFile)
                                    continue;
                            }

                            if (!Settings.Advanced.DisableVfsGeneration)
                                try
                                {
                                    File.Delete(file);
                                }
                                catch { }
                        }
                    }
                }
            });

            // Handle WebM Cache Updates (Global vs Incremental)
            try
            {
                string cachePath = Path.Combine(configProvider.CacheDirectory, ShokoRelayConstants.FileAtWebmCache);
                if (seriesFilter == null || seriesFilter.Count == 0)
                {
                    var cacheLines = state.CacheEntries.Select(entry => $"{entry.VfsPath.Replace('\\', '/')}|{entry.VideoId}|{entry.Bitmask}");
                    await VfsShared.WriteLinesAtomicAsync(cachePath, cacheLines, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var newCacheLines = new List<string>();
                    var primaryIdsToPurge = new HashSet<string>(
                        seriesFilter.SelectMany(id => EnforceTmdbNumbering ? OverrideHelper.GetGroup(OverrideHelper.GetPrimary(id, metadataService), metadataService) : [id]).Select(id => id.ToString())
                    );
                    string searchToken = $"/{vfsRoot}/";
                    string movieSearchToken = $"/{VfsShared.ResolveMovieRootFolderName()}/";

                    if (File.Exists(cachePath))
                    {
                        foreach (var line in VfsShared.ReadLinesShared(cachePath))
                        {
                            var parts = line.Split('|', 2);
                            if (parts.Length > 0)
                            {
                                if (TryExtractSegmentAfterToken(parts[0], searchToken) is { } seriesIdText)
                                {
                                    if (primaryIdsToPurge.Contains(seriesIdText) || (int.TryParse(seriesIdText, out int cachedSeriesId) && metadataService.GetShokoSeriesByID(cachedSeriesId) is null))
                                        continue;
                                }
                                else if (TryExtractSegmentAfterToken(parts[0], movieSearchToken) is { } episodeIdText)
                                {
                                    if (
                                        int.TryParse(episodeIdText, out int episodeId)
                                        && (
                                            metadataService.GetShokoEpisodeByID(episodeId) is not { ShokoSeriesID: var seriesId }
                                            || primaryIdsToPurge.Contains((EnforceTmdbNumbering ? OverrideHelper.GetPrimary(seriesId, metadataService) : seriesId).ToString())
                                        )
                                    )
                                        continue;
                                }
                            }
                            newCacheLines.Add(line);
                        }
                    }

                    newCacheLines.AddRange(state.CacheEntries.Select(entry => $"{entry.VfsPath.Replace('\\', '/')}|{entry.VideoId}|{entry.Bitmask}"));
                    await VfsShared.WriteLinesAtomicAsync(cachePath, newCacheLines, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "AnimeThemes VFS: Failed to save webm cache");
            }

            logger.LogInformation("AnimeThemes VFS: Task finished -> {Count} links created in {Elapsed}ms.", state.Created, stopwatch.ElapsedMilliseconds);
            return new AnimeThemesMappingApplyResult(state.Created, state.Skipped, state.Matched, [.. state.Errors], [.. state.CacheEntries], stopwatch.Elapsed);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(ShokoRelayConstants.TaskAtVfsBuild);
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

    #region Internal Classes

    /// <summary>Maintains intermediate progress and result counters for mapping tasks.</summary>
    private class MappingState
    {
        /// <summary>Total links successfully created.</summary>
        public int Created;

        /// <summary>Total files skipped.</summary>
        public int Skipped;

        /// <summary>Total series matched.</summary>
        public int Matched;

        /// <summary>Thread-safe collection of error messages.</summary>
        public ConcurrentBag<string> Errors = [];

        /// <summary>Thread-safe collection of generated cache entries.</summary>
        public ConcurrentBag<WebmCacheEntry> CacheEntries = [];
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
            return AnimeThemesHelper.ParseMappingContentWithComments(await File.ReadAllTextAsync(mapPath, cancellationToken).ConfigureAwait(false));
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

    /// <summary>Extracts the immediate path segment following a known VFS root token without allocating split arrays.</summary>
    /// <param name="path">The cached VFS path string.</param>
    /// <param name="token">The root delimiter token (e.g. <c>/!ShokoRelayVFS/</c>).</param>
    /// <returns>The extracted segment string, or <c>null</c> if the token was not found.</returns>
    private static string? TryExtractSegmentAfterToken(string path, string token)
    {
        int rootIndex = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (rootIndex < 0)
            return null;

        int startIndex = rootIndex + token.Length;
        int endIndex = path.IndexOf('/', startIndex);
        return endIndex > startIndex ? path[startIndex..endIndex] : null;
    }

    #endregion
}
