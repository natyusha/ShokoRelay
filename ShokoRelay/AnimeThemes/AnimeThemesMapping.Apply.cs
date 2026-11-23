using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.FileSystem;

namespace ShokoRelay.AnimeThemes;

public partial class AnimeThemesMapping
{
    #region VFS Application

    /// <summary>Applies the AnimeThemes VFS mapping when <see cref="ShokoRelayConstants.FileAtMapping"/> exists in the plugin configuration directory.</summary>
    /// <param name="seriesFilter">Optional collection of series IDs to limit processing to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the optional mapping application.</returns>
    public Task ApplyMappingIfExistsAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken cancellationToken = default) =>
        File.Exists(Path.Combine(configProvider.ConfigDirectory, ShokoRelayConstants.FileAtMapping)) ? ApplyMappingAsync(seriesFilter, cancellationToken) : Task.CompletedTask;

    /// <summary>Read a previously built mapping file and create/update VFS links for matching theme files.</summary>
    /// <remarks>
    ///   - Prioritizes BD sources: if a filename collision occurs and a BD source is available, non-BD sources are skipped
    ///   - If multiple BD sources collide (or no BD sources exist), they are de-duplicated with (2), (3), etc
    ///   - Optionally transfers missing cross-root theme files according to <see cref="AdvancedConfig.AnimeThemesCrossRootMode"/>
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
            var transferMode = Settings.Advanced.AnimeThemesCrossRootMode;

            // Pre-index entries by AniDB ID and Video ID for instant lookups
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

            // Pre-build cross-root index when transfer mode is enabled
            CrossRootContext? crossRootContext =
                transferMode != AnimeThemesTransferMode.Disabled ? BuildCrossRootContext(themeRootName, transferMode, seriesFilter, entries, entriesByAniDb, folderGroups, cancellationToken) : null;

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
                                    Settings.Advanced.AnimeThemesOverlapLevel == AnimeThemesOverlapLevel.All
                                    || entry.Overlap.Equals("None", StringComparison.Ordinal)
                                    || (Settings.Advanced.AnimeThemesOverlapLevel == AnimeThemesOverlapLevel.TransitionOnly && entry.Overlap.Equals("Transition", StringComparison.Ordinal))
                                )
                                .Select(entry =>
                                {
                                    string relativePath = entry.FilePath.TrimLeadingSlashes();
                                    string candidateSource = Path.Combine(root, themeRootName, VfsShared.NormalizeSeparators(relativePath));
                                    bool existsLocally = fileSystem.FileExists(candidateSource);
                                    if (!existsLocally && crossRootContext?.HasSourceFor(relativePath) != true)
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
                                        ExistsLocally = existsLocally,
                                    };
                                });
                        })
                        .Where(candidate => candidate != null)
                        .GroupBy(candidate => candidate!.Signature);

                    foreach (var signatureGroup in potentialLinks)
                    {
                        // If any BD source exists, discard non-BD sources. Otherwise, keep all (TV/WEB/etc).
                        bool hasBD = signatureGroup.Any(candidate => candidate!.Entry.Source == "BD");
                        var sourceSurvivors = hasBD ? [.. signatureGroup.Where(candidate => candidate!.Entry.Source == "BD")] : signatureGroup.ToList();

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
                                if (!item.ExistsLocally && crossRootContext != null && !EnsureThemeInTargetRoot(root, item.RelativePath, item.SourcePath, transferMode, crossRootContext, state))
                                {
                                    counter++;
                                    continue;
                                }

                                if (!Settings.Advanced.DisableVfsGeneration)
                                    fileSystem.CreateDirectory(shortsDir);
                                string relativeTarget = VfsShared.NormalizeSeparators(Path.Combine("..", "..", "..", themeRootName, item.RelativePath));
                                if (VfsShared.TryCreateLink(item.SourcePath, destinationPath, logger, fileSystem, targetOverride: relativeTarget))
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

                    if (fileSystem.DirectoryExists(shortsDir))
                        foreach (var file in fileSystem.GetFilePaths(shortsDir, cancellationToken: cancellationToken))
                        {
                            string fileName = Path.GetFileName(file);
                            if (AnimeThemesHelper.CreditsFileRegex().IsMatch(fileName) || plannedFilenames.Contains(fileName))
                                continue;

                            // Protection for Plex Local Extras: Only delete the file if it's a symlink pointing to the AnimeThemes repository.
                            if (fileSystem.ReadSymbolicLink(file) is not { } linkTarget || !linkTarget.Contains(themeRootName, StringComparison.OrdinalIgnoreCase))
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
                                    fileSystem.DeleteFile(file);
                                }
                                catch { }
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

                    newCacheLines.AddRange(state.CacheEntries.Select(entry => $"{entry.VfsPath.Replace('\\', '/')}|{entry.VideoId}|{entry.Bitmask}"));
                    await VfsShared.WriteLinesAtomicAsync(cachePath, newCacheLines, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "AnimeThemes VFS: Failed to save webm cache");
            }

            logger.LogInformation(
                "AnimeThemes VFS: Task finished -> {Count} links created (Cross-root: {Hardlinked} hardlinked, {Copied} copied, {Moved} moved) in {Elapsed}ms.",
                state.Created,
                state.Hardlinked,
                state.Copied,
                state.Moved,
                stopwatch.ElapsedMilliseconds
            );
            return new AnimeThemesMappingApplyResult(
                state.Created,
                state.Skipped,
                state.Matched,
                state.Hardlinked,
                state.Copied,
                state.Moved,
                [.. state.Errors],
                [.. state.CacheEntries],
                [.. state.TransferDetails],
                stopwatch.Elapsed
            );
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(ShokoRelayConstants.TaskAtVfsBuild);
        }
    }

    #endregion

    #region Cross-Root Transfer

    /// <summary>Maintains shared physical theme locations, per-file locks, and active root usage for cross-root transfers.</summary>
    private sealed class CrossRootContext
    {
        /// <summary>Normalized relative paths mapped to available (Root, FullPath) physical locations.</summary>
        public ConcurrentDictionary<string, List<(string Root, string FullPath)>> PhysicalThemes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-relative-path locks synchronizing source lookup and Move updates.</summary>
        public ConcurrentDictionary<string, Lock> RelativeLocks { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-destination-path semaphores preventing parallel threads from writing the same target path simultaneously.</summary>
        public ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks { get; } = new(VfsShared.PathComparer);

        /// <summary>Set of (Root, NormalizedRelativePath) pairs actively referenced by a series in that root so Move avoids breaking live symlinks.</summary>
        public HashSet<(string Root, string RelativePath)> NeededInRoots { get; } = [];

        /// <summary>Checks whether any managed folder holds the specified relative theme path.</summary>
        /// <param name="relativePath">The relative theme path inside the AnimeThemes root.</param>
        /// <returns><c>true</c> if at least one physical source file exists.</returns>
        public bool HasSourceFor(string relativePath) => PhysicalThemes.ContainsKey(NormalizeRelativeThemeKey(relativePath));
    }

    /// <summary>Scans all VFS-enabled managed folders for available theme files and indexes which roots actively use them.</summary>
    /// <param name="themeRootName">The configured AnimeThemes folder name.</param>
    /// <param name="transferMode">The active cross-root transfer mode.</param>
    /// <param name="seriesFilter">Optional active series filter for incremental runs.</param>
    /// <param name="entries">All parsed mapping entries.</param>
    /// <param name="entriesByAniDb">Mapping entries indexed by AniDB anime ID.</param>
    /// <param name="folderGroups">Series groups being processed in this run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An initialized <see cref="CrossRootContext"/>.</returns>
    private CrossRootContext BuildCrossRootContext(
        string themeRootName,
        AnimeThemesTransferMode transferMode,
        IReadOnlyCollection<int>? seriesFilter,
        List<AnimeThemesMappingEntry> entries,
        ILookup<int, AnimeThemesMappingEntry> entriesByAniDb,
        List<IGrouping<int, IShokoSeries?>> folderGroups,
        CancellationToken cancellationToken
    )
    {
        var context = new CrossRootContext();
        var managedRoots = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true, requireExists: true, fileSystem: fileSystem);
        var webmOptions = new FileSystemListingOptions { Recursive = true, Extensions = [".webm"] };

        foreach (var managedRoot in managedRoots)
        {
            string normalizedRoot = managedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string themeDir = Path.Combine(normalizedRoot, themeRootName);
            if (!fileSystem.DirectoryExists(themeDir))
                continue;

            foreach (var filePath in fileSystem.GetFilePaths(themeDir, webmOptions, cancellationToken))
            {
                string key = NormalizeRelativeThemeKey(Path.GetRelativePath(themeDir, filePath));
                context.PhysicalThemes.GetOrAdd(key, _ => []).Add((normalizedRoot, Path.GetFullPath(filePath)));
            }
        }

        if (transferMode == AnimeThemesTransferMode.Move)
        {
            foreach (var group in folderGroups)
            {
                var primarySeries = metadataService.GetShokoSeriesByID(group.Key);
                if (primarySeries == null)
                    continue;

                var roots = VfsShared.ResolveSeriesVfsPaths(primarySeries, metadataService).Select(vfsPath => Path.GetDirectoryName(Path.GetDirectoryName(vfsPath))!).Distinct(VfsShared.PathComparer).ToList();
                if (roots.Count == 0)
                    continue;

                foreach (int memberId in OverrideHelper.GetGroup(group.Key, metadataService))
                {
                    if (metadataService.GetShokoSeriesByID(memberId) is not { AnidbAnimeID: > 0 } memberSeries)
                        continue;
                    foreach (var entry in entriesByAniDb[memberSeries.AnidbAnimeID])
                    {
                        string key = NormalizeRelativeThemeKey(entry.FilePath);
                        foreach (var root in roots)
                            context.NeededInRoots.Add((root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), key));
                    }
                }
            }

            // Include existing blueprint and WebM cache Shorts references when running an incremental filtered update
            if (seriesFilter?.Count > 0)
            {
                var blueprint = VfsShared.LoadBlueprint();
                foreach (var (vfsRootPath, seriesDict) in blueprint)
                {
                    string? root = Path.GetDirectoryName(vfsRootPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrEmpty(root))
                        continue;

                    string themePrefix = Path.Combine(root, themeRootName);
                    foreach (var series in seriesDict.Values)
                    foreach (var season in series.Seasons)
                    foreach (var file in season.Files)
                        if (VfsShared.IsPathUnderDirectory(file.Source, themePrefix))
                            context.NeededInRoots.Add((root, NormalizeRelativeThemeKey(Path.GetRelativePath(themePrefix, file.Source))));
                }

                string cachePath = Path.Combine(configProvider.CacheDirectory, ShokoRelayConstants.FileAtWebmCache);
                if (File.Exists(cachePath))
                {
                    var entriesByVideoId = entries.ToLookup(entry => entry.VideoId);
                    var normalizedManagedRoots = managedRoots.Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).OrderByDescending(path => path.Length).ToList();
                    foreach (var line in VfsShared.ReadLinesShared(cachePath))
                    {
                        var parts = line.Split('|', 3);
                        if (parts.Length < 2 || !int.TryParse(parts[1], out int videoId))
                            continue;

                        string? matchedRoot = normalizedManagedRoots.FirstOrDefault(root => VfsShared.IsPathUnderDirectory(VfsShared.NormalizeSeparators(parts[0]), root));
                        if (matchedRoot != null)
                            foreach (var entry in entriesByVideoId[videoId])
                                context.NeededInRoots.Add((matchedRoot, NormalizeRelativeThemeKey(entry.FilePath)));
                    }
                }
            }
        }

        return context;
    }

    /// <summary>Transfers a missing cross-root theme file into the target root's AnimeThemes directory before creating a VFS symlink.</summary>
    /// <param name="targetRoot">The target managed folder root.</param>
    /// <param name="relativePath">The relative path inside the AnimeThemes folder.</param>
    /// <param name="destinationPath">The full destination path inside the target root's AnimeThemes folder.</param>
    /// <param name="transferMode">The configured cross-root transfer mode.</param>
    /// <param name="context">The active cross-root transfer context.</param>
    /// <param name="state">The shared mapping execution state.</param>
    /// <returns><c>true</c> if the destination file now exists (or transfer is simulated/not needed); otherwise, <c>false</c>.</returns>
    private bool EnsureThemeInTargetRoot(string targetRoot, string relativePath, string destinationPath, AnimeThemesTransferMode transferMode, CrossRootContext context, MappingState state)
    {
        if (Settings.Advanced.DisableVfsGeneration)
            return true;

        string normalizedTargetRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string key = NormalizeRelativeThemeKey(relativePath);
        var destinationLock = context.DestinationLocks.GetOrAdd(destinationPath, _ => new SemaphoreSlim(1, 1));
        destinationLock.Wait();
        try
        {
            if (fileSystem.FileExists(destinationPath))
                return true;

            var relativeLock = context.RelativeLocks.GetOrAdd(key, _ => new Lock());
            lock (relativeLock)
            {
                if (fileSystem.FileExists(destinationPath))
                    return true;

                if (!context.PhysicalThemes.TryGetValue(key, out var locations))
                    return false;

                var source = locations.FirstOrDefault(location => !string.Equals(location.Root, normalizedTargetRoot, VfsShared.PathComparison) && fileSystem.FileExists(location.FullPath));
                if (string.IsNullOrEmpty(source.FullPath))
                    return false;

                string destinationDir = Path.GetDirectoryName(destinationPath)!;
                fileSystem.CreateDirectory(destinationDir);

                bool canMove = transferMode == AnimeThemesTransferMode.Move && !context.NeededInRoots.Contains((source.Root, key));
                if (canMove)
                {
                    fileSystem.MoveFile(source.FullPath, destinationPath, overwrite: false);
                    locations.Remove(source);
                    locations.Add((normalizedTargetRoot, destinationPath));
                    Interlocked.Increment(ref state.Moved);
                    state.TransferDetails.Add($"MOVED: {source.FullPath} -> {destinationPath}");
                    logger.LogInformation("AnimeThemes VFS: Moved cross-root theme '{Source}' -> '{Destination}'", source.FullPath, destinationPath);
                    return true;
                }

                string? sourceVolume = fileSystem.GetFileStat(source.FullPath)?.Identity.VolumeID;
                string? destinationVolume = fileSystem.GetDirectoryStat(destinationDir)?.Identity.VolumeID;
                bool hardlinked = false;
                if (sourceVolume != null && string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        fileSystem.CreateHardLink(source.FullPath, destinationPath);
                        hardlinked = true;
                    }
                    catch (IOException)
                    {
                        hardlinked = false;
                    }

                if (hardlinked)
                {
                    locations.Add((normalizedTargetRoot, destinationPath));
                    Interlocked.Increment(ref state.Hardlinked);
                    state.TransferDetails.Add($"HARDLINKED: {source.FullPath} -> {destinationPath}");
                    logger.LogInformation("AnimeThemes VFS: Hardlinked cross-root theme '{Source}' -> '{Destination}'", source.FullPath, destinationPath);
                }
                else
                {
                    fileSystem.CopyFile(source.FullPath, destinationPath, overwrite: false);
                    locations.Add((normalizedTargetRoot, destinationPath));
                    Interlocked.Increment(ref state.Copied);
                    string fallbackNote = transferMode == AnimeThemesTransferMode.Move ? " (source still needed in origin root)" : "";
                    state.TransferDetails.Add($"COPIED: {source.FullPath} -> {destinationPath}{fallbackNote}");
                    logger.LogInformation("AnimeThemes VFS: Copied cross-root theme '{Source}' -> '{Destination}'{Note}", source.FullPath, destinationPath, fallbackNote);
                }
                return true;
            }
        }
        catch (Exception exception)
        {
            state.Errors.Add($"Cross-root transfer failed for '{destinationPath}': {exception.Message}");
            logger.LogWarning(exception, "AnimeThemes VFS: Cross-root transfer failed for '{Destination}'", destinationPath);
            return false;
        }
        finally
        {
            destinationLock.Release();
        }
    }

    /// <summary>Normalizes a relative theme path into a forward-slash dictionary lookup key.</summary>
    /// <param name="relativePath">The relative theme path.</param>
    /// <returns>A normalized lookup key without leading slashes.</returns>
    private static string NormalizeRelativeThemeKey(string relativePath) => relativePath.TrimLeadingSlashes().Replace('\\', '/');

    #endregion

    #region Internal Helpers

    /// <summary>Maintains intermediate progress and result counters for mapping tasks.</summary>
    private class MappingState
    {
        /// <summary>Total links successfully created.</summary>
        public int Created;

        /// <summary>Total files skipped.</summary>
        public int Skipped;

        /// <summary>Total series matched.</summary>
        public int Matched;

        /// <summary>Total cross-root theme files hardlinked.</summary>
        public int Hardlinked;

        /// <summary>Total cross-root theme files copied.</summary>
        public int Copied;

        /// <summary>Total cross-root theme files moved.</summary>
        public int Moved;

        /// <summary>Thread-safe collection of error messages.</summary>
        public ConcurrentBag<string> Errors = [];

        /// <summary>Thread-safe collection of generated cache entries.</summary>
        public ConcurrentBag<WebmCacheEntry> CacheEntries = [];

        /// <summary>Thread-safe collection of cross-root transfer details.</summary>
        public ConcurrentBag<string> TransferDetails = [];
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
