using System.Collections.Concurrent;
using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoRelay.Vfs;

/// <summary>Builds a virtual filesystem tree for Plex mapping metadata to conventions.</summary>
/// <param name="metadataService">Metadata service used for series and episode resolution.</param>
/// <param name="assetLinker">Service for linking local media assets and Plex extras.</param>
/// <param name="videoService">Shoko video and import folder service.</param>
public class VfsBuilder(IMetadataService metadataService, VfsAssetLinker assetLinker, IVideoService videoService)
{
    #region Setup & State

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    #endregion

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

    /// <summary>Audits the VFS to find and remove orphaned series folders and broken symlinks.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result object containing audit statistics.</returns>
    public VfsAuditResult Audit(CancellationToken ct = default)
    {
        s_logger.Info("VFS Audit: Starting task...");
        var sw = Stopwatch.StartNew();

        var removed = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();
        int brokenLinks = 0,
            orphanedFolders = 0,
            seriesChecked = 0;
        int blueprintUpdated = 0;

        string rootName = VfsShared.ResolveRootFolderName();
        var allRoots = videoService.GetAllManagedFolders()?.Where(f => !VfsShared.IsSourceOnly(f)).Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList() ?? [];

        var validFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSeries = metadataService.GetAllShokoSeries();
        if (allSeries != null)
        {
            foreach (var s in allSeries)
            {
                int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(s.ID, metadataService) : s.ID;
                validFolderIds.Add(folderId.ToString());
            }
        }

        var blueprint = VfsShared.LoadBlueprint();

        foreach (var root in allRoots)
        {
            ct.ThrowIfCancellationRequested();
            string vfsRoot = Path.Combine(root, rootName);
            if (!Directory.Exists(vfsRoot))
                continue;

            Parallel.ForEach(
                Directory.GetDirectories(vfsRoot),
                DefaultParallelOptions(ct),
                seriesFolder =>
                {
                    string folderName = Path.GetFileName(seriesFolder);

                    // Ignore special files/folders like .ignore
                    if (folderName.StartsWith('.'))
                        return;

                    if (!validFolderIds.Contains(folderName))
                    {
                        try
                        {
                            Directory.Delete(seriesFolder, true);
                            Interlocked.Increment(ref orphanedFolders);
                            removed.Add($"[Orphaned Series] {seriesFolder}");

                            if (int.TryParse(folderName, out int parsedFolderId))
                            {
                                foreach (var rootDict in blueprint.Values)
                                    if (rootDict.TryRemove(parsedFolderId, out _))
                                        Interlocked.Exchange(ref blueprintUpdated, 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to delete orphaned folder {seriesFolder}: {ex.Message}");
                        }
                        return;
                    }

                    Interlocked.Increment(ref seriesChecked);

                    foreach (var file in Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.LinkTarget != null && !info.Exists)
                            {
                                File.Delete(file);
                                Interlocked.Increment(ref brokenLinks);
                                removed.Add($"[Broken Link] {file}");
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to process file {file}: {ex.Message}");
                        }
                    }

                    foreach (var subDir in Directory.GetDirectories(seriesFolder))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                            {
                                Directory.Delete(subDir, false);
                                Interlocked.Increment(ref orphanedFolders);
                                removed.Add($"[Empty Folder] {subDir}");
                            }
                        }
                        catch { }
                    }
                }
            );
        }

        if (blueprintUpdated > 0)
            VfsShared.SaveBlueprint(blueprint);

        sw.Stop();
        s_logger.Info("VFS Audit: Task finished -> seriesChecked={0}, brokenLinksRemoved={1}, orphanedFoldersRemoved={2} in {3}ms.", seriesChecked, brokenLinks, orphanedFolders, sw.ElapsedMilliseconds);
        return new VfsAuditResult(seriesChecked, brokenLinks, orphanedFolders, [.. removed.OrderBy(x => x)], [.. errors.OrderBy(x => x)]);
    }

    #endregion

    #region Core Build Logic

    /// <summary>Internal core logic for orchestrating a VFS build or clean run.</summary>
    /// <param name="seriesIds">Optional collection of series IDs to process.</param>
    /// <param name="cleanRoot">Whether to delete existing VFS folders before building.</param>
    /// <returns>A result object containing statistics and details for the log report.</returns>
    private VfsBuildResult BuildInternal(IReadOnlyCollection<int>? seriesIds, bool cleanRoot)
    {
        var rootName = VfsShared.ResolveRootFolderName();
        var overlapping =
            videoService
                .GetAllManagedFolders()
                ?.Where(f => !string.IsNullOrEmpty(f.Path) && f.Path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Contains(rootName))
                .Select(f => f.Path)
                .ToList()
            ?? [];

        if (overlapping.Count > 0)
            throw new InvalidOperationException(
                $"VFS generation blocked: Shoko import folder '{overlapping[0]}' resides inside or matches the VFS directory. Remove this import folder in Shoko's settings before generating."
            );

        // Refresh the override cache to catch any link changes made in Shoko since the last operation. This includes VFS Overrides and if MergeTmdbSeries is enabled auto merged TMDB series as well.
        OverrideHelper.Reload(metadataService);

        var (sw, created, skipped, seriesProcessed, planned) = (Stopwatch.StartNew(), 0, 0, 0, 0);
        var seriesDetailsBag = new ConcurrentBag<SeriesProcessDetails>();
        var cleanupDetails = new List<RootCleanupDetails>();
        var skippedDetailsBag = new ConcurrentBag<string>();
        var ignoredFolders = VfsShared.GetIgnoredFolderNames(Settings);
        var errorsBag = new ConcurrentBag<string>();
        var session = new VfsBuildSession();
        bool isFiltered = seriesIds?.Count > 0;

        var blueprint = isFiltered ? VfsShared.LoadBlueprint() : new ConcurrentDictionary<string, ConcurrentDictionary<int, VfsBlueprintSeries>>(VfsShared.PathComparer);

        if (cleanRoot && !isFiltered)
        {
            PerformGlobalRootCleanup(rootName, cleanupDetails);
            cleanRoot = false;
        }

        // Resolve series list to process
        IEnumerable<IShokoSeries> seriesList;
        if (isFiltered)
        {
            var resolved = seriesIds!.Distinct().Select(id => new { Id = id, Series = metadataService.GetShokoSeriesByID(id) }).ToList();

            // Prune series that were completely deleted from Shoko's database
            foreach (var item in resolved.Where(x => x.Series == null))
            {
                PruneSeries(rootName, item.Id, errorsBag.Add);
                foreach (var rootDict in blueprint.Values)
                    rootDict.TryRemove(item.Id, out _);
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
            var grouped = seriesList.GroupBy(s => OverrideHelper.GetPrimary(s.ID, metadataService)).ToList();
            seriesList = [.. grouped.Select(g => g.FirstOrDefault(s => s.ID == g.Key) ?? g.First())];
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
                    var seasons = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    var rootFiles = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;

                    // Ensure stale entries for this series are removed across all roots before rebuilding its node.
                    foreach (var rootDict in blueprint.Values)
                        rootDict.TryRemove(folderId, out _);

                    var (sCreated, sSkipped, sSkippedList, sErrors, sPlanned) = BuildSeries(
                        series,
                        rootName,
                        cleanRoot,
                        isFiltered,
                        ignoredFolders,
                        session,
                        (vfsRoot, season, fileName, source) =>
                        {
                            if (string.IsNullOrEmpty(season))
                                rootFiles.TryAdd(fileName, source ?? "Local Metadata");
                            else
                                seasons.GetOrAdd(season, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(fileName, source ?? "Local Metadata");
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
                    seriesDetailsBag.Add(new SeriesProcessDetails(series.PreferredTitle?.Value ?? series.ID.ToString(), seriesSw.ElapsedMilliseconds, sCreated));

                    if (sCreated > 0 || sErrors.Count > 0)
                        s_logger.Info("VFS: Processed series -> '{0}' ({1} links created) in {2}ms", series.PreferredTitle?.Value ?? series.ID.ToString(), sCreated, seriesSw.ElapsedMilliseconds);

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
                    errorsBag.Add($"Failed series {series.PreferredTitle?.Value}: {ex.Message}");
                    s_logger.Error(ex, "VFS: Build failed for series {SeriesId}", series.ID);
                }
            }
        );

        sw.Stop(); // Capture total elapsed time here
        var errors = errorsBag.ToList();

        s_logger.Info(
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
        var (created, skipped, planned, errors, skippedDetails) = (0, 0, 0, new List<string>(), new List<string>());
        var (displayTitle, _, _) = TextHelper.ResolveFullSeriesTitles(series);
        int folderId = EnforceTmdbNumbering ? OverrideHelper.GetPrimary(series.ID, metadataService) : series.ID;

        var fileData = GetSeriesFileDataCached(series, session);
        if (!fileData.Mappings.Any())
        {
            PruneSeries(rootFolderName, folderId, errors.Add);
            return (0, 0, skippedDetails, errors, 0);
        }

        // Collect the base names of all source video files to prevent them from being linked as series-level metadata
        var videoBaseNames = fileData.Mappings.Select(m => Path.GetFileNameWithoutExtension(m.FileName)).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coordCounts = fileData.Mappings.GroupBy(m => (m.Coords.Season, m.Coords.Episode, m.IsVariation)).ToDictionary(g => g.Key, g => g.Count());
        var versionCounters = coordCounts.Where(g => g.Value > 1).ToDictionary(g => g.Key, _ => 1);
        int epPad = Math.Max(2, fileData.Mappings.Where(m => m.Coords.Season >= 0).DefaultIfEmpty().Max(m => m?.Coords.EndEpisode ?? m?.Coords.Episode ?? 1).ToString().Length);
        var extraPad = fileData.Mappings.Where(m => m.Coords.Season < 0).GroupBy(m => m.Coords.Season).ToDictionary(g => g.Key, g => g.Count() > 9 ? 2 : 1);

        var resolvedVfsSeriesPaths = new HashSet<string>(VfsShared.PathComparer);
        var expectedFiles = new HashSet<string>(VfsShared.PathComparer);
        bool skipCheck = cleanRoot && !isFiltered;

        // Callback wrapper to automatically track the expected physical destination of any correctly generated link to protect it from the cleanup phase
        void LocalOnLink(string importRoot, string season, string fileName, string? source)
        {
            string path = string.IsNullOrEmpty(season)
                ? Path.Combine(importRoot, rootFolderName, folderId.ToString(), fileName)
                : Path.Combine(importRoot, rootFolderName, folderId.ToString(), season, fileName);
            expectedFiles.Add(path);
            onLink?.Invoke(importRoot, season, fileName, source);
        }

        foreach (var mapping in fileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
        {
            IVideoFile? loc = null;
            string? importRoot = null;
            string? src = null;

            foreach (var file in mapping.Video?.Files ?? [])
            {
                if (VfsShared.IsSourceOnly(file.ManagedFolder))
                    continue;

                importRoot = VfsShared.ResolveImportRootPath(file);
                if (importRoot != null)
                {
                    src = VfsShared.ResolveSourcePath(file, importRoot);
                    if (src != null)
                    {
                        loc = file;
                        break;
                    }
                }
            }

            if (loc == null || src == null || importRoot == null)
            {
                skipped++;
                skippedDetails.Add($"[Missing/Source-Only] {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
                continue;
            }

            string rootPath = Path.Combine(importRoot, rootFolderName),
                seriesPath = Path.Combine(rootPath, folderId.ToString());
            resolvedVfsSeriesPaths.Add(seriesPath);

            if (session.CreatedDirs.TryAdd(rootPath, 0))
            {
                Directory.CreateDirectory(rootPath);
                try
                {
                    File.WriteAllText(Path.Combine(rootPath, ".ignore"), "");
                }
                catch { }
            }
            if (session.CreatedDirs.TryAdd(seriesPath, 0))
                Directory.CreateDirectory(seriesPath);

            // Check if the source path is ignored under exclusion or extra rules
            if (VfsShared.IsPathIgnored(src, ignoredFolders))
            {
                skipped++;
                skippedDetails.Add($"[Excluded Path] {series.PreferredTitle?.Value} S{mapping.Coords.Season}E{mapping.Coords.Episode} - {mapping.FileName}");
                continue;
            }

            string seasonName = PlexMapping.GetSeasonFolder(mapping.Coords.Season);
            string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(seasonName));
            if (session.CreatedDirs.TryAdd(seasonPath, 0))
                Directory.CreateDirectory(seasonPath);

            var key = (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation);
            bool hasPeer = coordCounts.TryGetValue(key, out var count) && count > 1;
            int? vIdx = (hasPeer && !mapping.PartIndex.HasValue && versionCounters.TryGetValue(key, out var v)) ? (versionCounters[key] = v + 1) - 1 : null;
            string fileName = VfsHelper.SanitizeName(
                PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var ex)
                    ? VfsHelper.BuildExtrasFileName(
                        mapping,
                        ex,
                        extraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                        Path.GetExtension(src),
                        displayTitle,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx,
                        mapping.IsVariation
                    )
                    : VfsHelper.BuildStandardFileName(
                        mapping,
                        epPad,
                        Path.GetExtension(src),
                        mapping.Video!.ID,
                        mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        vIdx,
                        mapping.IsVariation
                    )
            );

            var destFilePath = Path.Combine(seasonPath, fileName);

            if (VfsShared.TryCreateLink(src, destFilePath, s_logger, skipExistenceCheck: skipCheck))
            {
                created++;
                planned++;

                // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                LocalOnLink(importRoot, seasonName, fileName, src);
                var distinctPrimarySeriesCount =
                    mapping.Video?.CrossReferences?.Where(cr => cr.ShokoEpisode != null).Select(cr => OverrideHelper.GetPrimary(cr.ShokoEpisode!.SeriesID, metadataService)).Distinct().Count() ?? 0;
                if (distinctPrimarySeriesCount <= 1)
                {
                    assetLinker.LinkSeriesMetadata(Path.GetDirectoryName(src)!, seriesPath, videoBaseNames, session.MetadataFileCache, (name, s) => LocalOnLink(importRoot, "", name, s), skipCheck);
                    assetLinker.LinkEpisodeMetadata(
                        src,
                        Path.GetDirectoryName(src)!,
                        Path.GetFileNameWithoutExtension(destFilePath),
                        seasonPath,
                        session.SubtitleFileCache,
                        ref planned,
                        ref skipped,
                        errors,
                        ref created,
                        (name, s) => LocalOnLink(importRoot, seasonName, name, s),
                        skipCheck
                    );
                }
            }
            else
            {
                skipped++;
                errors.Add($"Link failed: {src} -> {destFilePath}");
            }
        }

        // Plex Local Extras: Evaluates source directories exactly once, broadcasting results across all unique VFS roots.
        if (Settings.Advanced.PlexLocalExtras)
            assetLinker.LinkLocalExtras(fileData, resolvedVfsSeriesPaths, videoBaseNames, epPad, LocalOnLink, skipCheck);

        // Dynamically register physically present AnimeThemes mapping files inside the blueprint Shorts directory to protect them from cleanup
        var anidbIds = EnforceTmdbNumbering
            ? OverrideHelper.GetGroup(folderId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().Select(s => s.AnidbAnimeID).ToList()
            : [series.AnidbAnimeID];

        foreach (var seriesPath in resolvedVfsSeriesPaths)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(seriesPath))!;
            string themeRootName = VfsShared.ResolveAnimeThemesFolderName();

            foreach (var anidbId in anidbIds)
            {
                if (session.ThemeMappings.TryGetValue(anidbId, out var themes))
                {
                    foreach (var theme in themes)
                    {
                        if (VfsHelper.GetThemeSourcePath(theme.RelativePath, root, themeRootName, session) is string srcPath)
                            LocalOnLink(root, "Shorts", theme.FinalName, srcPath);
                    }
                }
            }
        }

        VfsHelper.CleanupOrphanedFilesAndFolders(resolvedVfsSeriesPaths, expectedFiles);

        return (created, skipped, skippedDetails, errors, planned);
    }

    #endregion

    #region Internal Helpers

    /// <summary>Deletes the entire VFS root folder across all managed locations.</summary>
    /// <param name="rootName">The name of the VFS root folder.</param>
    /// <param name="cleanupDetails">List to record the cleanup details.</param>
    private void PerformGlobalRootCleanup(string rootName, List<RootCleanupDetails> cleanupDetails)
    {
        s_logger.Info("VFS: Performing global root cleanup...");
        List<string> allRoots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

        foreach (var root in allRoots)
        {
            string path = Path.Combine(root, rootName);
            if (Directory.Exists(path))
            {
                if (!VfsShared.IsSafeToDelete(path))
                {
                    s_logger.Warn("VFS: Refusing to delete unsafe path -> {0}", path);
                    continue;
                }
                try
                {
                    var cleanSw = Stopwatch.StartNew();
                    var subDirs = Directory.GetDirectories(path);

                    // Delete series folders in parallel to overcome single-threaded network/FUSE bottlenecks
                    Parallel.ForEach(
                        subDirs,
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
                    s_logger.Info("VFS: Cleaned root folder -> '{0}' in {1}ms", path, cleanSw.ElapsedMilliseconds);

                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, ".ignore"), "");
                }
                catch (Exception ex)
                {
                    s_logger.Warn(ex, "VFS: Failed to clean root -> {0}", path);
                }
            }
        }
    }

    /// <summary>Retrieves the files and episode mappings for a series, leveraging the build-session cache.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="session">Active build session context containing caches.</param>
    /// <returns>A data container holding files and coordinates for the series.</returns>
    private MapHelper.SeriesFileData GetSeriesFileDataCached(IShokoSeries series, VfsBuildSession session)
    {
        if (!EnforceTmdbNumbering)
            return session.SeriesFileDataCache.GetOrAdd(series.ID, _ => MapHelper.GetSeriesFileData(series, metadataService));
        int pId = OverrideHelper.GetPrimary(series.ID, metadataService);
        return session.SeriesFileDataCache.GetOrAdd(
            pId,
            _ =>
            {
                var group = OverrideHelper.GetGroup(pId, metadataService).Select(metadataService.GetShokoSeriesByID).OfType<IShokoSeries>().ToList();
                return group.Count <= 1
                    ? MapHelper.GetSeriesFileData(group.FirstOrDefault() ?? series, metadataService)
                    : MapHelper.GetSeriesFileDataMerged(group[0], group.Skip(1).Cast<ISeries>(), metadataService);
            }
        );
    }

    /// <summary>Recursively deletes the virtual directory for a series across all active VFS roots.</summary>
    /// <param name="rootFolderName">The name of the VFS root folder.</param>
    /// <param name="folderId">The folder ID representation of the series to prune.</param>
    /// <param name="onError">Callback to invoke if an exception occurs during pruning.</param>
    private void PruneSeries(string rootFolderName, int folderId, Action<string> onError)
    {
        List<string> roots = [.. (videoService.GetAllManagedFolders() ?? []).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct()];

        foreach (var root in roots)
        {
            string path = Path.Combine(root, rootFolderName, folderId.ToString());
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    s_logger.Info("VFS: Pruned empty/orphaned series folder -> '{0}'", path);
                }
            }
            catch (Exception ex)
            {
                onError($"Prune failed {path}: {ex.Message}");
                s_logger.Warn(ex, "VFS: Failed to prune series path {Path}", path);
            }
        }
    }

    #endregion
}
