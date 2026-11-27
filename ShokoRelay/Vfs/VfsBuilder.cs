using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Utilities;
using Shoko.Abstractions.Video.Services;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Vfs;

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="assetLinker">VFS asset linker.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="fileSystem">Shoko file system service.</param>
/// <param name="logger">Logger instance.</param>
public partial class VfsBuilder(IMetadataService metadataService, VfsAssetLinker assetLinker, IVideoService videoService, IFileSystem fileSystem, ILogger<VfsBuilder> logger)
{
    #region Public Interface

    /// <summary>Build or clean VFS for a single series ID.</summary>
    /// <param name="seriesId">Optional single series ID to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true, CancellationToken cancellationToken = default, IProgress<decimal>? progress = null) =>
        BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot, cancellationToken, progress);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    /// <param name="seriesIds">Collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true, CancellationToken cancellationToken = default, IProgress<decimal>? progress = null) =>
        BuildInternal(seriesIds, cleanRoot, cancellationToken, progress);

    #endregion

    #region Core Build Logic

    /// <summary>Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0 to 100).</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot, CancellationToken cancellationToken = default, IProgress<decimal>? progress = null)
    {
        var rootName = VfsShared.ResolveRootFolderName();
        var overlapping = videoService.GetAllManagedFolders()?.Select(folder => folder.Path).FirstOrDefault(path => !string.IsNullOrEmpty(path) && VfsHelper.IsInVfsRoot(path, out _));

        if (overlapping != null)
            throw new InvalidOperationException(
                $"VFS generation blocked: Shoko managed folder '{overlapping}' resides inside or matches the VFS directory. Remove this managed folder in Shoko's settings before generating."
            );

        // Refresh the override cache to catch any link changes made in Shoko since the last operation. This includes VFS Overrides and if MergeTmdbSeries is enabled auto merged TMDB series as well.
        OverrideHelper.Reload();

        var (stopwatch, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
        var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
        var cleanupDetails = new List<RootCleanupDetails>();
        var skippedDetailsBag = new ConcurrentBag<string>();
        var ignoredFolders = VfsShared.GetIgnoredFolderNames(Settings);
        var errorsBag = new ConcurrentBag<string>();
        var session = new VfsBuildSession(videoService);
        bool isFiltered = seriesIds?.Count > 0;

        var blueprint = isFiltered ? VfsShared.LoadBlueprint() : new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(VfsShared.PathComparer);
        bool isCleanBuild = cleanRoot && !isFiltered;

        // Delete the entire VFS root folder across all managed locations
        if (isCleanBuild)
        {
            logger.LogInformation("VFS: Performing global root cleanup...");
            var allRoots = VfsShared.GetManagedFolderPaths(videoService);
            foreach (var root in allRoots)
            {
                CleanVfsRoot(Path.Combine(root, rootName), cleanupDetails);
                bool doMovie = Settings.Advanced.MovieGenerationMode != MovieGenerationMode.Disabled;
                CleanVfsRoot(Path.Combine(root, VfsShared.ResolveMovieRootFolderName()), cleanupDetails, true, doMovie);
            }
        }

        // Resolve series list to process
        IEnumerable<IShokoSeries> seriesList;
        if (isFiltered)
        {
            var resolved = seriesIds!.Distinct().Select(id => (Id: id, Series: metadataService.GetShokoSeriesByID(id))).ToList();

            // Prune series that were completely deleted from Shoko's database
            foreach (var (id, series) in resolved.Where(item => item.Series == null))
            {
                var blueprintMovieIds = blueprint
                    .Values.SelectMany(rootDict => rootDict.TryGetValue(id, out var entry) ? entry.Seasons : [])
                    .Select(season => season.Name.StartsWith("Movie ❯ ", StringComparison.Ordinal) && int.TryParse(season.Name.AsSpan(8), out int episodeId) ? episodeId : 0)
                    .Where(episodeId => episodeId > 0)
                    .ToHashSet();
                PruneSeries(rootName, id, errorsBag.Add, blueprintMovieIds);
                foreach (var rootDict in blueprint.Values)
                    rootDict.TryRemove(id, out _);
            }

            seriesList = [.. resolved.Where(item => item.Series != null).Select(item => item.Series!).OfType<IShokoSeries>()];
        }
        else
            seriesList = metadataService.GetAllShokoSeries() ?? [];

        int consolidatedCount = 0;

        if (EnforceTmdbNumbering)
        {
            // Group by primary ID and count how many secondary series are being merged
            var grouped = seriesList.GroupBy(series => OverrideHelper.GetPrimary(series.LocalID, metadataService)).ToList();
            seriesList = [.. grouped.Select(group => metadataService.GetShokoSeriesByID(group.Key) ?? group.First())];
            consolidatedCount = seriesList.Sum(series => Math.Max(0, OverrideHelper.GetGroup(series.LocalID, metadataService).Count - 1));

            // Remove stale secondary series entries from the blueprint (and disk on filtered builds) when series are merged via overrides
            foreach (var primarySeries in seriesList)
            {
                int primaryId = primarySeries.LocalID;
                bool doMovie = MapHelper.IsMovie(primarySeries) && Settings.Advanced.MovieGenerationMode != MovieGenerationMode.Disabled;
                foreach (int secondaryId in OverrideHelper.GetGroup(primaryId, metadataService).Where(id => id != primaryId))
                {
                    foreach (var rootDict in blueprint.Values)
                        rootDict.TryRemove(secondaryId, out _);
                    if (isFiltered)
                        PruneSeries(rootName, secondaryId, errorsBag.Add, pruneStandard: true, pruneMovies: !doMovie);
                }
            }
        }

        var seriesItems = seriesList.ToList();
        var itemProgress = progress.Throttle().ForItems(seriesItems.Count);
        itemProgress.Report(0);

        // Process series in parallel
        try
        {
            Parallel.ForEach(seriesItems, DefaultParallelOptions(cancellationToken), series =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var seriesStopwatch = Stopwatch.StartNew();

                    // SeriesNode structure: { id, anidbId, title, seasons: { "Season 1": [ { name, source } ] }, rootFiles: [ { name, source } ] }
                    var seasonsByRoot = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(VfsShared.PathComparer);
                    var rootFilesByRoot = new Dictionary<string, Dictionary<string, string>>(VfsShared.PathComparer);
                    int folderId = series.GetPrimaryId(metadataService);

                    // Ensure stale entries for this series are removed across all roots before rebuilding its node.
                    foreach (var rootDict in blueprint.Values)
                        rootDict.TryRemove(folderId, out _);

                    var (seriesCreated, seriesSkipped, seriesSkippedList, seriesErrors, seriesPlanned) = BuildSeries(
                        series,
                        rootName,
                        isCleanBuild,
                        isFiltered,
                        ignoredFolders,
                        session,
                        (vfsRoot, season, fileName, source) =>
                        {
                            if (string.IsNullOrEmpty(season))
                            {
                                if (!rootFilesByRoot.TryGetValue(vfsRoot, out var rootFiles))
                                    rootFilesByRoot[vfsRoot] = rootFiles = new(StringComparer.OrdinalIgnoreCase);
                                rootFiles.TryAdd(fileName, source ?? "Local Metadata");
                            }
                            else
                            {
                                if (!seasonsByRoot.TryGetValue(vfsRoot, out var rootSeasons))
                                    seasonsByRoot[vfsRoot] = rootSeasons = new(StringComparer.OrdinalIgnoreCase);
                                if (!rootSeasons.TryGetValue(season, out var seasonDict))
                                    rootSeasons[season] = seasonDict = new(StringComparer.OrdinalIgnoreCase);
                                seasonDict.TryAdd(fileName, source ?? "Local Metadata");
                            }
                        }
                    );

                    var rootsInvolved = rootFilesByRoot.Keys.Concat(seasonsByRoot.Keys).Distinct(VfsShared.PathComparer).ToList();
                    if (seriesCreated > 0 || rootsInvolved.Count > 0)
                        foreach (var root in rootsInvolved)
                        {
                            var rootFiles = rootFilesByRoot.GetValueOrDefault(root) ?? [];
                            var seasonMap = seasonsByRoot.GetValueOrDefault(root) ?? [];
                            blueprint
                                .GetOrAdd(root, _ => new())
                                .TryAdd(
                                    folderId,
                                    new VfsBlueprintSeries(
                                        folderId,
                                        series.AnidbAnimeID,
                                        TextHelper.ResolveFullSeriesTitles(series).DisplayTitle,
                                        rootFiles.OrderBy(file => file.Key).Select(file => new VfsBlueprintFile(file.Key, file.Value)),
                                        seasonMap
                                            .OrderBy(seasonEntry => VfsHelper.GetSeasonSortKey(seasonEntry.Key))
                                            .Select(seasonEntry => new VfsBlueprintSeason(
                                                seasonEntry.Key,
                                                VfsHelper.GetSeasonId(seasonEntry.Key),
                                                seasonEntry.Value.OrderBy(file => file.Key).Select(file => new VfsBlueprintFile(file.Key, file.Value))
                                            ))
                                    )
                                );
                        }

                    // Capture individual series details for the log report
                    bool isMovie = MapHelper.IsMovie(series);
                    bool doMovie = isMovie && Settings.Advanced.MovieGenerationMode != MovieGenerationMode.Disabled;
                    seriesDetailsBag.Add(new SeriesProcessDetails($"{series.GetDisplayTitle() ?? series.LocalID.ToString()} [{series.LocalID}]", seriesStopwatch.ElapsedMilliseconds, seriesCreated, doMovie));

                    if (seriesCreated > 0 || seriesErrors.Count > 0)
                        logger.LogDebug(
                            "VFS: Processed {Type} -> {DisplayTitle} [{LocalId}] ({CreatedCount} links created) in {ElapsedMs}ms",
                            doMovie ? "movie" : "series",
                            series.GetDisplayTitle(),
                            series.LocalID,
                            seriesCreated,
                            seriesStopwatch.ElapsedMilliseconds
                        );

                    Interlocked.Add(ref created, seriesCreated);
                    Interlocked.Add(ref skipped, seriesSkipped);
                    Interlocked.Add(ref planned, seriesPlanned);
                    foreach (var skippedItem in seriesSkippedList)
                        skippedDetailsBag.Add(skippedItem);
                    foreach (var error in seriesErrors)
                        errorsBag.Add(error);
                    Interlocked.Increment(ref seriesProcessed);
                    itemProgress.Increment();
                }
                catch (Exception exception)
                {
                    errorsBag.Add($"Failed series {series.GetDisplayTitle()} [{series.LocalID}]: {exception.Message}");
                    logger.LogError(exception, "VFS: Build failed for series -> {DisplayTitle} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                    itemProgress.Increment();
                }
            });
        }
        catch (OperationCanceledException)
        {
            if (seriesProcessed > 0)
                VfsShared.SaveBlueprint(blueprint);
            throw;
        }

        stopwatch.Stop(); // Capture total elapsed time here
        var errors = errorsBag.ToList();

        logger.LogInformation(
            "VFS: Build completed in {Elapsed}ms -> processed={Processed}, consolidated={Consolidated}, created={Created}, skipped={Skipped}, errors={Errors}",
            stopwatch.ElapsedMilliseconds,
            seriesProcessed,
            consolidatedCount,
            created,
            skipped,
            errors.Count
        );

        VfsShared.SaveBlueprint(blueprint);

        return new VfsBuildResult(
            rootName,
            seriesProcessed,
            consolidatedCount,
            created,
            skipped,
            [.. skippedDetailsBag],
            errors,
            planned,
            [.. seriesDetailsBag.OrderBy(item => item.Name)],
            cleanupDetails,
            stopwatch.Elapsed
        );
    }

    /// <summary>Builds the VFS structure for a specific series, synchronizing files with the expected layout.</summary>
    /// <param name="series">Shoko series metadata.</param>
    /// <param name="rootFolderName">Name of the VFS root folder.</param>
    /// <param name="cleanRoot">Whether to skip existence checks because the root was wiped.</param>
    /// <param name="isFiltered">Whether this is a filtered incremental build.</param>
    /// <param name="ignoredFolders">List of folders in Shoko destinations to exclude from processing.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <param name="onLink">Callback to invoke when a link is confirmed as expected.</param>
    /// <returns>A tuple of counts: Created, Skipped, SkippedDetails, Errors, Planned.</returns>
    private (int Created, int Skipped, List<string> SkippedDetails, List<string> Errors, int Planned) BuildSeries(
        IShokoSeries series,
        string rootFolderName,
        bool cleanRoot,
        bool isFiltered,
        HashSet<string> ignoredFolders,
        VfsBuildSession session,
        Action<string, string, string, string?>? onLink = null
    )
    {
        var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
        int folderId = series.GetPrimaryId(metadataService);
        string movieRootName = VfsShared.ResolveMovieRootFolderName();

        // Retrieve the files and episode mappings for a series, leveraging the build-session cache
        var fileData = !EnforceTmdbNumbering
            ? session.SeriesFileDataCache.GetOrAdd(series.LocalID, _ => MapHelper.GetSeriesFileData(series, metadataService))
            : session.SeriesFileDataCache.GetOrAdd(folderId, _ =>
            {
                var group = OverrideHelper.GetGroup(folderId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                return group.Count <= 1
                    ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series, metadataService)
                    : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<ISeries>(), metadataService);
            });

        if (!fileData.Mappings.Any())
        {
            var emptyErrors = new List<string>();
            PruneSeries(rootFolderName, folderId, emptyErrors.Add);
            return (0, 0, [], emptyErrors, 0);
        }

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(mapping => Path.GetFileNameWithoutExtension(mapping.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(mapping => (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation)).ToDictionary(group => group.Key, group => group.Count());
        int episodePad = Math.Max(
            2,
            fileData.Mappings.Where(mapping => mapping.Coords.Season >= 0).DefaultIfEmpty().Max(mapping => mapping?.Coords.EndEpisode ?? mapping?.Coords.Episode ?? 1).ToString().Length
        );
        var extraPad = fileData.Mappings.Where(mapping => mapping.Coords.Season < 0).GroupBy(mapping => mapping.Coords.Season).ToDictionary(group => group.Key, group => group.Count() > 9 ? 2 : 1);
        bool skipCheck = cleanRoot && !isFiltered;

        var state = new SeriesBuildState(series, rootFolderName, movieRootName, displayTitle, folderId, skipCheck, ignoredFolders, session, fileData, videoBaseNames, coordCounts, episodePad, extraPad, onLink);

        // Evaluates the categorization logic to determine if the series should be processed as a movie
        var (doStandard, doMovie) = MapHelper.GetGenerationModes(MapHelper.IsMovie(series), Settings.Advanced.MovieGenerationMode);

        // Prune stale virtual folders: If movie mode was disabled or a series lost its movie link, clean up leftover movie folders
        // If a series was switched to movie-only mode, clean up its standard TV show folder
        if (!skipCheck && (!doStandard || !doMovie))
        {
            HashSet<int>? staleMovieIds = !doMovie
                ?
                [
                    .. fileData
                        .Mappings.Where(mapping => mapping.PrimaryEpisode.Type == EpisodeType.Episode)
                        .Select(mapping => mapping.PrimaryEpisode.ID.TryGetNumericID(out int episodeId) ? episodeId : 0)
                        .Where(episodeId => episodeId > 0),
                ]
                : null;
            PruneSeries(rootFolderName, folderId, state.Errors.Add, staleMovieIds, pruneStandard: !doStandard, pruneMovies: !doMovie);
        }

        if (doStandard)
            BuildVfs(state);

        if (doMovie)
            BuildMovieVfs(state);

        // Dynamically register physically present AnimeThemes mapping files inside the blueprint Shorts directory to protect them from cleanup
        var overrideSeries = OverrideHelper.GetGroup(folderId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();

        foreach (var seriesPath in state.ResolvedVfsSeriesPaths)
        {
            string vfsRootDir = Path.GetDirectoryName(seriesPath)!;
            string root = Path.GetDirectoryName(vfsRootDir)!;
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
            bool isMoviePath = string.Equals(Path.GetFileName(vfsRootDir), movieRootName, VfsShared.PathComparison);
            string folderName = Path.GetFileName(seriesPath);

            var resolvedThemes = AnimeThemesHelper.ResolveSeriesThemeLinks(overrideSeries, session.ThemeMappings, relativePath =>
            {
                string? sourcePath = VfsHelper.GetThemeSourcePath(relativePath, root, themeRootName, session, fileSystem);
                return (sourcePath != null, sourcePath);
            });

            foreach (var theme in resolvedThemes)
            {
                string destinationFilePath = Path.Combine(seriesPath, "Shorts", theme.FinalName);
                state.ExpectedFiles.Add(destinationFilePath);

                string seasonArg = isMoviePath ? $"Movie ❯ {folderName}" : "Shorts";
                string fileArg = isMoviePath ? $"Shorts/{theme.FinalName}" : theme.FinalName;

                onLink?.Invoke(vfsRootDir, seasonArg, fileArg, theme.SourcePath);
            }
        }

        VfsHelper.CleanupOrphanedFilesAndFolders(state.ResolvedVfsSeriesPaths, state.ExpectedFiles, fileSystem);

        return (state.Created, state.Skipped, state.SkippedDetails, state.Errors, state.Planned);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Resolves the physical source location for a mapping and validates that it is not ignored by exclusions.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="mapping">The file mapping to resolve.</param>
    /// <param name="locationInfo">Outputs the resolved import root and physical source path.</param>
    /// <returns>True if the physical file exists and is eligible for linking; otherwise, false.</returns>
    private bool TryResolveAndValidate(SeriesBuildState state, MapHelper.FileMapping mapping, out (string ImportRoot, string SourcePath) locationInfo)
    {
        (string ImportRoot, string SourcePath)? resolvedLocation = null;
        foreach (var file in mapping.Video.Files ?? [])
        {
            if (!VfsShared.IsVfsEnabledFolder(file.ManagedFolder))
                continue;
            var importRoot = VfsShared.ResolveImportRootPath(file);
            if (importRoot != null && VfsShared.ResolveSourcePath(file, importRoot, fileSystem) is { } sourcePath)
            {
                resolvedLocation = (importRoot, sourcePath);
                break;
            }
        }

        if (resolvedLocation == null)
        {
            state.Skipped++;
            state.SkippedDetails.Add($"[Missing/Source-Only] {state.Series.GetDisplayTitle()} [{state.Series.LocalID}] S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
            locationInfo = default;
            return false;
        }
        if (VfsShared.IsPathIgnored(resolvedLocation.Value.SourcePath, videoService, Settings, state.IgnoredFolders, relativeTo: resolvedLocation.Value.ImportRoot, fileSystem: fileSystem))
        {
            state.Skipped++;
            state.SkippedDetails.Add($"[Excluded Path] {state.Series.GetDisplayTitle()} [{state.Series.LocalID}] S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
            locationInfo = default;
            return false;
        }
        locationInfo = resolvedLocation.Value;
        return true;
    }

    /// <summary>Queues series-level metadata directories (including season-subfolder parents) and links episode-level sidecar metadata for a newly created video symlink.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="sourcePath">Absolute path to the physical source video file.</param>
    /// <param name="destinationSeriesDir">Target VFS series or movie root folder.</param>
    /// <param name="destinationEpisodeDir">Target VFS season or movie folder for episode sidecars.</param>
    /// <param name="destinationFilePath">Target VFS video symlink path.</param>
    /// <param name="importRoot">The physical import root path.</param>
    /// <param name="physicalImportRoot">The physical managed folder root used to guard parent-folder traversal.</param>
    /// <param name="seriesSeasonLabel">Blueprint season label for series-level metadata.</param>
    /// <param name="episodeSeasonLabel">Blueprint season label for episode-level sidecars.</param>
    /// <param name="isPrimary">Whether this file belongs to the primary series in the override group.</param>
    private void LinkLocalMetadataForFile(
        SeriesBuildState state,
        string sourcePath,
        string destinationSeriesDir,
        string destinationEpisodeDir,
        string destinationFilePath,
        string importRoot,
        string physicalImportRoot,
        string seriesSeasonLabel,
        string episodeSeasonLabel,
        bool isPrimary
    )
    {
        string sourceDir = Path.GetDirectoryName(sourcePath)!;
        if (state.LinkedSeriesMetaDirs.Add($"{sourceDir}\0{destinationSeriesDir}"))
            state.PendingSeriesMetaDirs.Add((sourceDir, destinationSeriesDir, importRoot, seriesSeasonLabel, isPrimary));

        // When episodes sit inside a Season/Specials subfolder, also queue the series root folder one level above
        if (
            VfsHelper.IsSeasonSubfolder(Path.GetFileName(sourceDir))
            && Path.GetDirectoryName(sourceDir) is { Length: > 0 } parentDir
            && !string.Equals(
                parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                physicalImportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                VfsShared.PathComparison
            )
            && state.LinkedSeriesMetaDirs.Add($"{parentDir}\0{destinationSeriesDir}")
        )
            state.PendingSeriesMetaDirs.Add((parentDir, destinationSeriesDir, importRoot, seriesSeasonLabel, isPrimary));

        assetLinker.LinkEpisodeMetadata(
            sourcePath,
            sourceDir,
            Path.GetFileNameWithoutExtension(destinationFilePath),
            destinationEpisodeDir,
            state.Session.SubtitleFileCache,
            state.Session.AttachmentDirCache,
            ref state.Planned,
            ref state.Skipped,
            state.Errors,
            ref state.Created,
            (name, source) =>
            {
                state.ExpectedFiles.Add(Path.Combine(destinationEpisodeDir, name));
                state.OnLink?.Invoke(importRoot, episodeSeasonLabel, name, source);
            },
            state.SkipCheck
        );
    }

    /// <summary>Links queued series-level metadata directories, prioritizing primary series folders ahead of secondary override folders.</summary>
    /// <param name="state">The active series build state.</param>
    private void FlushSeriesMetadataLinks(SeriesBuildState state)
    {
        foreach (var (sourceDir, destinationSeriesDir, importRoot, seriesSeasonLabel, _) in state.PendingSeriesMetaDirs.OrderByDescending(item => item.IsPrimary))
        {
            assetLinker.LinkSeriesMetadata(
                sourceDir,
                destinationSeriesDir,
                state.VideoBaseNames,
                state.Session.MetadataFileCache,
                ref state.Planned,
                ref state.Created,
                state.ExpectedFiles,
                (name, source) =>
                {
                    state.ExpectedFiles.Add(Path.Combine(destinationSeriesDir, name));
                    state.OnLink?.Invoke(importRoot, seriesSeasonLabel, name, source);
                },
                state.SkipCheck
            );
        }
        state.PendingSeriesMetaDirs.Clear();
    }

    /// <summary>Safely purges the contents and removes a root VFS directory.</summary>
    /// <param name="path">The absolute path of the root directory to clean.</param>
    /// <param name="cleanupDetails">The tracking list for cleanups.</param>
    /// <param name="isMovieRoot">Whether this root is the standalone movies directory.</param>
    /// <param name="recreate">Whether to recreate the empty directory and .ignore file after cleanup.</param>
    private void CleanVfsRoot(string path, List<RootCleanupDetails> cleanupDetails, bool isMovieRoot = false, bool recreate = true)
    {
        if (Settings.Advanced.DisableVfsGeneration || !fileSystem.DirectoryExists(path))
            return;

        if (!VfsShared.IsSafeToDelete(path))
        {
            logger.LogWarning("VFS: Refusing to delete unsafe path -> {Path}", path);
            return;
        }

        // Safety verification: Prevent wiping user media if VfsRootPath was accidentally set to an existing non-VFS media directory
        string marker = Path.Combine(path, ".ignore");
        if (!fileSystem.FileExists(marker) && fileSystem.GetFileSystemEntries(path).Any(entry => !entry.IsSymbolicLink))
        {
            logger.LogError("VFS: Aborting cleanup! Path '{Path}' contains non-symlink data and lacks a .ignore marker. Verify VFS Root Path settings.", path);
            return;
        }

        try
        {
            var cleanStopwatch = Stopwatch.StartNew();
            Parallel.ForEach(fileSystem.GetDirectoryEntries(path), DefaultParallelOptions(), entry =>
            {
                try
                {
                    if (entry.IsSymbolicLink)
                        VfsShared.DeleteSymbolicLink(fileSystem, entry.Path);
                    else
                        fileSystem.DeleteDirectory(entry.Path, true);
                }
                catch { }
            });
            fileSystem.DeleteDirectory(path, true);
            cleanupDetails.Add(new RootCleanupDetails(path, cleanStopwatch.ElapsedMilliseconds));
            logger.LogInformation("VFS: Cleaned {Type}root folder -> '{Path}' in {Elapsed}ms", isMovieRoot ? "movie " : "", path, cleanStopwatch.ElapsedMilliseconds);
            if (recreate)
            {
                fileSystem.CreateDirectory(path);
                WriteIgnoreMarker(path);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "VFS: Failed to clean {Type}root -> {Path}", isMovieRoot ? "movie " : "", path);
        }
    }

    /// <summary>Safely ensures the existence of a target directory and marks it as ignored by Shoko if it is a VFS root.</summary>
    /// <param name="path">The absolute path of the directory to ensure.</param>
    /// <param name="session">The active build session tracking created directories.</param>
    /// <param name="isRoot">Whether this directory is a top-level VFS root.</param>
    private void EnsureDirectory(string path, VfsBuildSession session, bool isRoot = false)
    {
        if (session.CreatedDirs.TryAdd(path, 0) && !Settings.Advanced.DisableVfsGeneration)
        {
            fileSystem.CreateDirectory(path);
            if (isRoot)
                WriteIgnoreMarker(path);
        }
    }

    /// <summary>Writes an empty <c>.ignore</c> marker inside a VFS root directory.</summary>
    /// <param name="directoryPath">The VFS root directory path.</param>
    private void WriteIgnoreMarker(string directoryPath)
    {
        try
        {
            using var markerStream = fileSystem.OpenFile(Path.Combine(directoryPath, ".ignore"), FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch { }
    }

    /// <summary>Recursively deletes the virtual directory for a series across all active VFS roots.</summary>
    /// <param name="rootFolderName">The name of the VFS root folder.</param>
    /// <param name="folderId">The folder ID representation of the series to prune.</param>
    /// <param name="onError">Callback to invoke if an exception occurs during pruning.</param>
    /// <param name="extraMovieEpisodeIds">Optional pre-resolved movie episode IDs (e.g. from the blueprint cache when the series is already deleted).</param>
    /// <param name="pruneStandard">Whether to remove the standard TV VFS folder for this series.</param>
    /// <param name="pruneMovies">Whether to remove standalone Movie VFS folders for this series.</param>
    private void PruneSeries(string rootFolderName, int folderId, Action<string> onError, HashSet<int>? extraMovieEpisodeIds = null, bool pruneStandard = true, bool pruneMovies = true)
    {
        if (Settings.Advanced.DisableVfsGeneration)
            return;

        List<string> roots = VfsShared.GetManagedFolderPaths(videoService);
        string movieRoot = VfsShared.ResolveMovieRootFolderName();
        var movieEpisodeIds = new HashSet<int>(extraMovieEpisodeIds ?? []);
        if (pruneMovies)
            foreach (int groupSeriesId in OverrideHelper.GetGroup(folderId, metadataService))
                if (metadataService.GetShokoSeriesByID(groupSeriesId) is { Episodes: { } episodes })
                    foreach (var episode in episodes.Where(episode => episode.Type == EpisodeType.Episode))
                        movieEpisodeIds.Add(episode.LocalID);

        foreach (var root in roots)
        {
            if (pruneStandard)
            {
                string path = Path.Combine(root, rootFolderName, folderId.ToString());
                try
                {
                    if (fileSystem.DirectoryExists(path))
                    {
                        fileSystem.DeleteDirectory(path, true);
                        logger.LogInformation("VFS: Pruned empty/orphaned series folder -> {Path}", path);
                    }
                }
                catch (Exception exception)
                {
                    onError($"Prune failed {path}: {exception.Message}");
                    logger.LogWarning(exception, "VFS: Failed to prune series path -> {Path}", path);
                }
            }

            if (!pruneMovies)
                continue;

            foreach (var episodeId in movieEpisodeIds)
            {
                string moviePath = Path.Combine(root, movieRoot, episodeId.ToString());
                try
                {
                    if (fileSystem.DirectoryExists(moviePath))
                    {
                        fileSystem.DeleteDirectory(moviePath, true);
                        logger.LogInformation("VFS: Pruned empty/orphaned movie folder -> {Path}", moviePath);
                    }
                }
                catch (Exception exception)
                {
                    onError($"Prune failed {moviePath}: {exception.Message}");
                    logger.LogWarning(exception, "VFS: Failed to prune movie path -> {Path}", moviePath);
                }
            }
        }
    }

    /// <summary>Computes the collision version index and peer flag for a file mapping within its coordinate/variation bucket.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="versionCounters">Mutable version counter dictionary keyed by coordinate and variation state.</param>
    /// <param name="mapping">The file mapping being evaluated.</param>
    /// <param name="hasPeer">Outputs <c>true</c> if multiple mappings share the same coordinate and variation bucket.</param>
    /// <returns>The 1-based duplicate version index, or <c>null</c> if not applicable.</returns>
    private static int? GetNextVersionIndex(SeriesBuildState state, Dictionary<(int Season, int Episode, bool IsVariation), int> versionCounters, MapHelper.FileMapping mapping, out bool hasPeer)
    {
        var key = (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation);
        hasPeer = state.CoordCounts.TryGetValue(key, out var count) && count > 1;
        return (hasPeer && !mapping.PartIndex.HasValue && versionCounters.TryGetValue(key, out var version)) ? (versionCounters[key] = version + 1) - 1 : null;
    }

    /// <summary>Validates destination uniqueness, creates the VFS symlink, and updates build counters and blueprint callbacks.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="sourcePath">Physical source video path.</param>
    /// <param name="destinationFilePath">Target VFS symlink path.</param>
    /// <param name="rootPath">The VFS root path for blueprint registration.</param>
    /// <param name="seasonLabel">The blueprint season label.</param>
    /// <param name="fileName">The symlink filename.</param>
    /// <returns><c>true</c> if the link exists or was created successfully; otherwise, <c>false</c>.</returns>
    private bool TryCreateMappedVideoLink(SeriesBuildState state, string sourcePath, string destinationFilePath, string rootPath, string seasonLabel, string fileName)
    {
        if (state.ExpectedFiles.Contains(destinationFilePath))
        {
            state.Skipped++;
            state.SkippedDetails.Add($"[Duplicate Destination] {state.Series.GetDisplayTitle()} [{state.Series.LocalID}] -> {destinationFilePath}");
            return false;
        }

        if (VfsShared.TryCreateLink(sourcePath, destinationFilePath, logger, fileSystem, skipExistenceCheck: state.SkipCheck))
        {
            state.Created++;
            state.Planned++;
            state.ExpectedFiles.Add(destinationFilePath);
            state.OnLink?.Invoke(rootPath, seasonLabel, fileName, sourcePath);
            return true;
        }

        state.Skipped++;
        state.Errors.Add($"Link failed: {sourcePath} -> {destinationFilePath}");
        return false;
    }

    #endregion
}
