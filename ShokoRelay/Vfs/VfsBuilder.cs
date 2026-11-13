using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
/// <param name="metadataService">Shoko metadata service.</param>
/// <param name="assetLinker">VFS asset linker.</param>
/// <param name="videoService">Shoko video service.</param>
/// <param name="logger">Logger instance.</param>
public partial class VfsBuilder(IMetadataService metadataService, VfsAssetLinker assetLinker, IVideoService videoService, ILogger<VfsBuilder> logger)
{
    #region Public Interface

    /// <summary>Build or clean VFS for a single series ID.</summary>
    /// <param name="seriesId">Optional single series ID to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(int? seriesId = null, bool cleanRoot = true) => BuildInternal(seriesId.HasValue ? [seriesId.Value] : null, cleanRoot);

    /// <summary>Build or clean VFS for multiple series IDs.</summary>
    /// <param name="seriesIds">Collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    public VfsBuildResult Build(IReadOnlyCollection<int> seriesIds, bool cleanRoot = true) => BuildInternal(seriesIds, cleanRoot);

    #endregion

    #region Core Build Logic

    /// <summary>Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot)
    {
        var rootName = VfsShared.ResolveRootFolderName();
        var overlapping = videoService.GetAllManagedFolders()?.Select(f => f.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p) && VfsHelper.IsInVfsRoot(p, out _));

        if (overlapping != null)
            throw new InvalidOperationException(
                $"VFS generation blocked: Shoko managed folder '{overlapping}' resides inside or matches the VFS directory. Remove this managed folder in Shoko's settings before generating."
            );

        // Refresh the override cache to catch any link changes made in Shoko since the last operation. This includes VFS Overrides and if MergeTmdbSeries is enabled auto merged TMDB series as well.
        OverrideHelper.Reload();

        var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
        var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
        var cleanupDetails = new List<RootCleanupDetails>();
        var skippedDetailsBag = new ConcurrentBag<string>();
        var ignoredFolders = VfsShared.GetIgnoredFolderNames(Settings);
        var errorsBag = new ConcurrentBag<string>();
        var session = new VfsBuildSession();
        bool isFiltered = seriesIds?.Count > 0;

        var blueprint = isFiltered ? VfsShared.LoadBlueprint() : new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(VfsShared.PathComparer);
        bool isCleanBuild = cleanRoot && !isFiltered;

        // Delete the entire VFS root folder across all managed locations
        if (isCleanBuild)
        {
            logger.LogInformation("VFS: Performing global root cleanup...");
            var allRoots = videoService.GetAllManagedFolders()?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct(VfsShared.PathComparer) ?? [];
            foreach (var root in allRoots)
            {
                CleanVfsRoot(Path.Combine(root, rootName), cleanupDetails, logger);
                bool doMovie = Settings.Advanced.MovieGenerationMode != MovieGenerationMode.Disabled;
                CleanVfsRoot(Path.Combine(root, VfsShared.ResolveMovieRootFolderName()), cleanupDetails, logger, true, doMovie);
            }
        }

        // Resolve series list to process
        IEnumerable<IShokoSeries> seriesList;
        if (isFiltered)
        {
            var resolved = seriesIds!.Distinct().Select(id => (Id: id, Series: metadataService.GetShokoSeriesByID(id))).ToList();

            // Prune series that were completely deleted from Shoko's database
            foreach (var (id, series) in resolved.Where(x => x.Series == null))
            {
                PruneSeries(rootName, id, errorsBag.Add);
                foreach (var rootDict in blueprint.Values)
                    rootDict.TryRemove(id, out _);
            }

            seriesList = [.. resolved.Where(x => x.Series != null).Select(x => x.Series!).OfType<IShokoSeries>()];
        }
        else
            seriesList = metadataService.GetAllShokoSeries() ?? [];

        int totalInScope = seriesList.Count();
        int consolidatedCount = 0;

        if (EnforceTmdbNumbering)
        {
            // Group by primary ID and count how many secondary series are being merged
            var grouped = seriesList.GroupBy(s => OverrideHelper.GetPrimary(s.LocalID, metadataService)).ToList();
            seriesList = [.. grouped.Select(g => metadataService.GetShokoSeriesByID(g.Key) ?? g.First())];
            consolidatedCount = totalInScope - seriesList.Count();
        }

        // Process series in parallel
        Parallel.ForEach(
            seriesList,
            DefaultParallelOptions(),
            series =>
            {
                try
                {
                    var seriesSw = Stopwatch.StartNew();

                    // SeriesNode structure: { id, anidbId, title, seasons: { "Season 1": [ { name, source } ] }, rootFiles: [ { name, source } ] }
                    var seasons = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    var rootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int folderId = series.GetPrimaryId(metadataService);

                    // Ensure stale entries for this series are removed across all roots before rebuilding its node.
                    foreach (var rootDict in blueprint.Values)
                        rootDict.TryRemove(folderId, out _);

                    var (sCreated, sSkipped, sSkippedList, sErrors, sPlanned) = BuildSeries(
                        series,
                        rootName,
                        isCleanBuild,
                        isFiltered,
                        ignoredFolders,
                        session,
                        (vfsRoot, season, fileName, source) =>
                        {
                            if (string.IsNullOrEmpty(season))
                                rootFiles.TryAdd(fileName, source ?? "Local Metadata");
                            else
                            {
                                if (!seasons.TryGetValue(season, out var sDict))
                                    seasons[season] = sDict = new(StringComparer.OrdinalIgnoreCase);
                                sDict.TryAdd(fileName, source ?? "Local Metadata");
                            }
                        }
                    );

                    if (sCreated > 0 || seasons.Count > 0 || rootFiles.Count > 0)
                    {
                        // Group files by root physically used during build to support multi-root series (tabs).
                        var rootsInvolved = metadataService.GetShokoSeriesByID(folderId) is { } fs
                            ? VfsShared.ResolveSeriesVfsPaths(fs, metadataService).Select(Path.GetDirectoryName).OfType<string>().Distinct(VfsShared.PathComparer)
                            : [];
                        foreach (var root in rootsInvolved)
                            blueprint
                                .GetOrAdd(root, _ => new())
                                .TryAdd(
                                    folderId,
                                    new VfsBlueprintSeries(
                                        folderId,
                                        series.AnidbAnimeID,
                                        TextHelper.ResolveFullSeriesTitles(series).DisplayTitle,
                                        rootFiles.OrderBy(f => f.Key).Select(f => new VfsBlueprintFile(f.Key, f.Value)),
                                        seasons
                                            .OrderBy(kvp => VfsHelper.GetSeasonSortKey(kvp.Key))
                                            .Select(kvp => new VfsBlueprintSeason(kvp.Key, VfsHelper.GetSeasonId(kvp.Key), kvp.Value.OrderBy(f => f.Key).Select(f => new VfsBlueprintFile(f.Key, f.Value))))
                                    )
                                );
                    }

                    // Capture individual series details for the log report
                    bool isMovie = MapHelper.IsMovie(series);
                    bool doMovie = isMovie && Settings.Advanced.MovieGenerationMode != MovieGenerationMode.Disabled;
                    seriesDetailsBag.Add(new SeriesProcessDetails($"{series.GetDisplayTitle() ?? series.LocalID.ToString()} [{series.LocalID}]", seriesSw.ElapsedMilliseconds, sCreated, doMovie));

                    if (sCreated > 0 || sErrors.Count > 0)
                        logger.LogDebug(
                            "VFS: Processed {Type} -> {DisplayTitle} [{LocalId}] ({CreatedCount} links created) in {ElapsedMs}ms",
                            doMovie ? "movie" : "series",
                            series.GetDisplayTitle(),
                            series.LocalID,
                            sCreated,
                            seriesSw.ElapsedMilliseconds
                        );

                    Interlocked.Add(ref created, sCreated);
                    Interlocked.Add(ref skipped, sSkipped);
                    Interlocked.Add(ref planned, sPlanned);
                    foreach (var s in sSkippedList)
                        skippedDetailsBag.Add(s);
                    foreach (var err in sErrors)
                        errorsBag.Add(err);
                    Interlocked.Increment(ref seriesProcessed);
                }
                catch (Exception ex)
                {
                    errorsBag.Add($"Failed series {series.GetDisplayTitle()} [{series.LocalID}]: {ex.Message}");
                    logger.LogError(ex, "VFS: Build failed for series -> {DisplayTitle} [{LocalId}]", series.GetDisplayTitle(), series.LocalID);
                }
            }
        );

        sw.Stop(); // Capture total elapsed time here
        var errors = errorsBag.ToList();

        logger.LogInformation(
            "VFS: Build completed in {Elapsed}ms -> processed={Processed}, consolidated={Consolidated}, created={Created}, skipped={Skipped}, errors={Errors}",
            sw.ElapsedMilliseconds,
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
            [.. seriesDetailsBag.OrderBy(x => x.Name)],
            cleanupDetails,
            sw.Elapsed
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
            : session.SeriesFileDataCache.GetOrAdd(
                folderId,
                _ =>
                {
                    var group = OverrideHelper.GetGroup(folderId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                    return group.Count <= 1
                        ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series, metadataService)
                        : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<ISeries>(), metadataService);
                }
            );

        if (!fileData.Mappings.Any())
        {
            var emptyErrors = new List<string>();
            PruneSeries(rootFolderName, folderId, emptyErrors.Add);
            return (0, 0, [], emptyErrors, 0);
        }

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(m => Path.GetFileNameWithoutExtension(m.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).ToDictionary(g => g.Key, g => g.Count());
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);
        bool skipCheck = cleanRoot && !isFiltered;

        var state = new SeriesBuildState(series, rootFolderName, movieRootName, displayTitle, folderId, skipCheck, ignoredFolders, session, fileData, videoBaseNames, coordCounts, epPad, extraPad, onLink);

        // Evaluates the categorization logic to determine if the series should be processed as a movie
        var (doStandard, doMovie) = MapHelper.GetGenerationModes(MapHelper.IsMovie(series), Settings.Advanced.MovieGenerationMode);

        if (!doStandard && !skipCheck)
            PruneSeries(rootFolderName, folderId, state.Errors.Add);

        if (doStandard)
            BuildVfs(state);

        if (doMovie)
            BuildMovieVfs(state);

        // Dynamically register physically present AnimeThemes mapping files inside the blueprint Shorts directory to protect them from cleanup
        var anidbIds = OverrideHelper.GetGroup(folderId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().Select(s => s.AnidbAnimeID).ToList();

        foreach (var seriesPath in state.ResolvedVfsSeriesPaths)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!;
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();
            bool isMoviePath = seriesPath.Contains(movieRootName, StringComparison.OrdinalIgnoreCase);
            string folderName = Path.GetFileName(seriesPath);

            foreach (var anidbId in anidbIds)
            {
                if (session.ThemeMappings.TryGetValue(anidbId, out var themes))
                {
                    foreach (var theme in themes)
                    {
                        if (VfsHelper.GetThemeSourcePath(theme.RelativePath, root, themeRootName, session) is string srcPath)
                        {
                            string destFilePath = Path.Combine(seriesPath, "Shorts", theme.FinalName);
                            state.ExpectedFiles.Add(destFilePath);

                            string seasonArg = isMoviePath ? $"Movie ❯ {folderName}" : "Shorts";
                            string fileArg = isMoviePath ? $"Shorts/{theme.FinalName}" : theme.FinalName;

                            onLink?.Invoke(root, seasonArg, fileArg, srcPath);
                        }
                    }
                }
            }
        }

        VfsHelper.CleanupOrphanedFilesAndFolders(state.ResolvedVfsSeriesPaths, state.ExpectedFiles);

        return (state.Created, state.Skipped, state.SkippedDetails, state.Errors, state.Planned);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Resolves the physical source location for a mapping and validates that it is not ignored by exclusions.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="mapping">The file mapping to resolve.</param>
    /// <param name="locInfoValue">Outputs the resolved import root and physical source path.</param>
    /// <returns>True if the physical file exists and is eligible for linking; otherwise, false.</returns>
    private bool TryResolveAndValidate(SeriesBuildState state, MapHelper.FileMapping mapping, out (string ImportRoot, string Src) locInfoValue)
    {
        (string ImportRoot, string Src)? loc = null;
        foreach (var file in mapping.Video.Files ?? [])
        {
            if (!VfsShared.IsVfsEnabledFolder(file.ManagedFolder))
                continue;
            var importRoot = VfsShared.ResolveImportRootPath(file);
            if (importRoot != null && VfsShared.ResolveSourcePath(file, importRoot) is { } src)
            {
                loc = (importRoot, src);
                break;
            }
        }

        if (loc == null)
        {
            state.Skipped++;
            state.SkippedDetails.Add($"[Missing/Source-Only] {state.Series.GetDisplayTitle()} [{state.Series.LocalID}] S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
            locInfoValue = default;
            return false;
        }
        if (VfsShared.IsPathIgnored(loc.Value.Src, videoService, Settings, state.IgnoredFolders))
        {
            state.Skipped++;
            state.SkippedDetails.Add($"[Excluded Path] {state.Series.GetDisplayTitle()} [{state.Series.LocalID}] S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
            locInfoValue = default;
            return false;
        }
        locInfoValue = loc.Value;
        return true;
    }

    /// <summary>Links series-level and episode-level sidecar metadata for a newly created video symlink.</summary>
    /// <param name="state">The active series build state.</param>
    /// <param name="srcPath">Absolute path to the physical source video file.</param>
    /// <param name="destSeriesDir">Target VFS series or movie root folder.</param>
    /// <param name="destEpDir">Target VFS season or movie folder for episode sidecars.</param>
    /// <param name="destFilePath">Target VFS video symlink path.</param>
    /// <param name="importRoot">The physical import root path.</param>
    /// <param name="seriesSeasonLabel">Blueprint season label for series-level metadata.</param>
    /// <param name="epSeasonLabel">Blueprint season label for episode-level sidecars.</param>
    private void LinkLocalMetadataForFile(SeriesBuildState state, string srcPath, string destSeriesDir, string destEpDir, string destFilePath, string importRoot, string seriesSeasonLabel, string epSeasonLabel)
    {
        string srcDir = Path.GetDirectoryName(srcPath)!;
        if (state.LinkedSeriesMetaDirs.Add(Path.Combine(srcDir, destSeriesDir)))
        {
            assetLinker.LinkSeriesMetadata(
                srcDir,
                destSeriesDir,
                state.VideoBaseNames,
                state.Session.MetadataFileCache,
                (name, s) =>
                {
                    state.ExpectedFiles.Add(Path.Combine(destSeriesDir, name));
                    state.OnLink?.Invoke(importRoot, seriesSeasonLabel, name, s);
                },
                state.SkipCheck
            );
        }

        assetLinker.LinkEpisodeMetadata(
            srcPath,
            srcDir,
            Path.GetFileNameWithoutExtension(destFilePath),
            destEpDir,
            state.Session.SubtitleFileCache,
            state.Session.AttachmentDirCache,
            ref state.Planned,
            ref state.Skipped,
            state.Errors,
            ref state.Created,
            (name, s) =>
            {
                state.ExpectedFiles.Add(Path.Combine(destEpDir, name));
                state.OnLink?.Invoke(importRoot, epSeasonLabel, name, s);
            },
            state.SkipCheck
        );
    }

    /// <summary>Safely purges the contents and removes a root VFS directory.</summary>
    /// <param name="path">The absolute path of the root directory to clean.</param>
    /// <param name="cleanupDetails">The tracking list for cleanups.</param>
    /// <param name="logger">The logger to use for logging messages.</param>
    /// <param name="isMovieRoot">Whether this root is the standalone movies directory.</param>
    /// <param name="recreate">Whether to recreate the empty directory and .ignore file after cleanup.</param>
    private static void CleanVfsRoot(string path, List<RootCleanupDetails> cleanupDetails, ILogger logger, bool isMovieRoot = false, bool recreate = true)
    {
        if (Settings.Advanced.DisableVfsGeneration || !Directory.Exists(path))
            return;

        if (!VfsShared.IsSafeToDelete(path))
        {
            logger.LogWarning("VFS: Refusing to delete unsafe path -> {Path}", path);
            return;
        }

        // Safety verification: Prevent wiping user media if VfsRootPath was accidentally set to an existing non-VFS media directory
        string marker = Path.Combine(path, ".ignore");
        if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(path).Any(e => !File.GetAttributes(e).HasFlag(FileAttributes.ReparsePoint)))
        {
            logger.LogError("VFS: Aborting cleanup! Path '{Path}' contains non-symlink data and lacks a .ignore marker. Verify VFS Root Path settings.", path);
            return;
        }

        try
        {
            var cleanSw = Stopwatch.StartNew();
            Parallel.ForEach(
                Directory.GetDirectories(path),
                DefaultParallelOptions(),
                dir =>
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            );
            Directory.Delete(path, true);
            cleanupDetails.Add(new RootCleanupDetails(path, cleanSw.ElapsedMilliseconds));
            logger.LogInformation("VFS: Cleaned {Type}root folder -> '{Path}' in {Elapsed}ms", isMovieRoot ? "movie " : "", path, cleanSw.ElapsedMilliseconds);
            if (recreate)
            {
                Directory.CreateDirectory(path);
                try
                {
                    File.WriteAllText(Path.Combine(path, ".ignore"), "");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VFS: Failed to clean {Type}root -> {Path}", isMovieRoot ? "movie " : "", path);
        }
    }

    /// <summary>Safely ensures the existence of a target directory and marks it as ignored by Shoko if it is a VFS root.</summary>
    /// <param name="path">The absolute path of the directory to ensure.</param>
    /// <param name="session">The active build session tracking created directories.</param>
    /// <param name="isRoot">Whether this directory is a top-level VFS root.</param>
    private static void EnsureDirectory(string path, VfsBuildSession session, bool isRoot = false)
    {
        if (session.CreatedDirs.TryAdd(path, 0) && !Settings.Advanced.DisableVfsGeneration)
        {
            Directory.CreateDirectory(path);
            if (isRoot)
            {
                try
                {
                    File.WriteAllText(Path.Combine(path, ".ignore"), "");
                }
                catch { }
            }
        }
    }

    /// <summary>Recursively deletes the virtual directory for a series across all active VFS roots.</summary>
    /// <param name="rootFolderName">The name of the VFS root folder.</param>
    /// <param name="folderId">The folder ID representation of the series to prune.</param>
    /// <param name="onError">Callback to invoke if an exception occurs during pruning.</param>
    private void PruneSeries(string rootFolderName, int folderId, Action<string> onError)
    {
        if (Settings.Advanced.DisableVfsGeneration)
            return;

        List<string> roots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct(VfsShared.PathComparer)];

        foreach (var root in roots)
        {
            string path = Path.Combine(root, rootFolderName, folderId.ToString());
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    logger.LogInformation("VFS: Pruned empty/orphaned series folder -> {Path}", path);
                }
            }
            catch (Exception ex)
            {
                onError($"Prune failed {path}: {ex.Message}");
                logger.LogWarning(ex, "VFS: Failed to prune series path -> {Path}", path);
            }
        }
    }

    #endregion
}
