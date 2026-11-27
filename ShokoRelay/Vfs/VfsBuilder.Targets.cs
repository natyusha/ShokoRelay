namespace ShokoRelay.Vfs;

public partial class VfsBuilder
{
    #region Standard VFS

    /// <summary>Generates standard TV Show VFS symlinks, sidecars, and local extras for a series.</summary>
    /// <param name="state">The active series build state.</param>
    private void BuildVfs(SeriesBuildState state)
    {
        var versionCounters = state.CoordCounts.Where(group => group.Value > 1).ToDictionary(group => group.Key, _ => 1);

        foreach (var mapping in state.FileData.Mappings.OrderBy(map => map.Coords.Season).ThenBy(map => map.Coords.Episode).ThenBy(map => map.PartIndex ?? 0))
            try
            {
                if (!TryResolveAndValidate(state, mapping, out var sourceInfo))
                    continue;

                string rootPath = Path.Combine(sourceInfo.ImportRoot, state.RootFolderName);
                string seriesPath = Path.Combine(rootPath, state.FolderId.ToString());
                state.ResolvedVfsSeriesPaths.Add(seriesPath);

                EnsureDirectory(rootPath, state.Session, true);
                EnsureDirectory(seriesPath, state.Session);

                string seasonName = PlexMapping.GetSeasonFolder(mapping.Coords.Season);
                string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(seasonName));
                EnsureDirectory(seasonPath, state.Session);

                int? versionIndex = GetNextVersionIndex(state, versionCounters, mapping, out bool hasPeer);

                (string Folder, string Subtype) extraSeason = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var extraMatch) ? extraMatch : ("Featurettes", "featurette");

                string fileName = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out _)
                    ? VfsHelper.BuildExtrasFileName(
                        mapping,
                        extraSeason,
                        state.ExtraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                        Path.GetExtension(sourceInfo.SourcePath),
                        state.DisplayTitle,
                        hasPeer ? mapping.PartIndex : null,
                        hasPeer ? mapping.PartCount : 1,
                        versionIndex,
                        mapping.IsVariation
                    )
                    : VfsHelper.SanitizeName(
                        VfsHelper.BuildStandardFileName(
                            mapping,
                            state.EpisodePad,
                            Path.GetExtension(sourceInfo.SourcePath),
                            mapping.Video!.LocalID,
                            mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                            hasPeer ? mapping.PartIndex : null,
                            hasPeer ? mapping.PartCount : 1,
                            versionIndex,
                            mapping.IsVariation
                        )
                    );

                var destinationFilePath = Path.Combine(seasonPath, fileName);

                if (TryCreateMappedVideoLink(state, sourceInfo.SourcePath, destinationFilePath, rootPath, seasonName, fileName))
                {
                    // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                    bool isCrossover =
                        mapping.Video?.CrossReferences is { Count: > 1 } xrefs
                        && xrefs.Where(xref => xref.ShokoEpisode != null).Select(xref => OverrideHelper.GetPrimary(xref.ShokoEpisode!.ShokoSeriesID, metadataService)).Distinct().Count() > 1;

                    if (!isCrossover)
                    {
                        bool isPrimary = (mapping.PrimaryEpisode as IShokoEpisode)?.ShokoSeriesID == state.FolderId;
                        LinkLocalMetadataForFile(state, sourceInfo.SourcePath, seriesPath, seasonPath, destinationFilePath, rootPath, sourceInfo.ImportRoot, "", seasonName, isPrimary);
                    }
                }
            }
            catch (Exception exception)
            {
                state.Skipped++;
                state.Errors.Add($"Error processing mapping for {mapping.FileName}: {exception.Message}");
            }

        FlushSeriesMetadataLinks(state);

        // Plex Local Extras: Evaluates source directories exactly once, broadcasting results across all unique VFS roots.
        if (Settings.Advanced.PlexLocalExtras)
            assetLinker.LinkLocalExtras(
                state.FileData,
                state.ResolvedVfsSeriesPaths,
                state.VideoBaseNames,
                state.EpisodePad,
                state.Session,
                (importRoot, season, fileName, source) =>
                {
                    string path = string.IsNullOrEmpty(season)
                        ? Path.Combine(importRoot, state.RootFolderName, state.FolderId.ToString(), fileName)
                        : Path.Combine(importRoot, state.RootFolderName, state.FolderId.ToString(), season, fileName);
                    state.ExpectedFiles.Add(path);
                    state.OnLink?.Invoke(Path.Combine(importRoot, state.RootFolderName), season, fileName, source);
                },
                state.SkipCheck
            );
    }

    #endregion

    #region Movie VFS

    /// <summary>Generates standalone Movie VFS symlinks, sidecars, and extras for a movie series.</summary>
    /// <param name="state">The active series build state.</param>
    private void BuildMovieVfs(SeriesBuildState state)
    {
        var movieDirs = new Dictionary<string, (string ImportRoot, string FolderName)>(VfsShared.PathComparer);
        var mainMappings = state.FileData.Mappings.Where(mapping => mapping.PrimaryEpisode.Type == EpisodeType.Episode).ToList();
        var extraMappings = state.FileData.Mappings.Where(mapping => mapping.PrimaryEpisode.Type != EpisodeType.Episode).ToList();
        var movieVersionCounters = state.CoordCounts.Where(group => group.Value > 1).ToDictionary(group => group.Key, _ => 1);

        foreach (var mapping in mainMappings)
        {
            if (!TryResolveAndValidate(state, mapping, out var sourceInfo))
                continue;

            string folderName = mapping.PrimaryEpisode.ID.ID;
            string rootPath = Path.Combine(sourceInfo.ImportRoot, state.MovieRootName);
            string moviePath = Path.Combine(rootPath, folderName);

            state.ResolvedVfsSeriesPaths.Add(moviePath);
            movieDirs.TryAdd(moviePath, (rootPath, folderName));

            EnsureDirectory(rootPath, state.Session, true);
            EnsureDirectory(moviePath, state.Session);

            int? versionIndex = GetNextVersionIndex(state, movieVersionCounters, mapping, out bool hasPeer);

            string fileName = VfsHelper.SanitizeName(
                VfsHelper.BuildMovieFileName(
                    mapping,
                    Path.GetExtension(sourceInfo.SourcePath),
                    mapping.Video.LocalID,
                    mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                    hasPeer ? mapping.PartIndex : null,
                    hasPeer ? mapping.PartCount : 1,
                    versionIndex,
                    mapping.IsVariation
                )
            );
            string destinationFilePath = Path.Combine(moviePath, fileName);

            if (TryCreateMappedVideoLink(state, sourceInfo.SourcePath, destinationFilePath, rootPath, $"Movie ❯ {folderName}", fileName))
            {
                bool isPrimary = (mapping.PrimaryEpisode as IShokoEpisode)?.ShokoSeriesID == state.FolderId;
                LinkLocalMetadataForFile(state, sourceInfo.SourcePath, moviePath, moviePath, destinationFilePath, rootPath, sourceInfo.ImportRoot, $"Movie ❯ {folderName}", $"Movie ❯ {folderName}", isPrimary);
            }
        }

        FlushSeriesMetadataLinks(state);

        if (Settings.Advanced.PlexLocalExtras)
            foreach (var mapping in extraMappings)
            {
                if (!TryResolveAndValidate(state, mapping, out var sourceInfo))
                    continue;

                int? extraVersionIndex = GetNextVersionIndex(state, movieVersionCounters, mapping, out bool hasExtraPeer);
                (string Folder, string Subtype) extraSeason = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var extraMatch) ? extraMatch : ("Featurettes", "featurette");
                string fileName = VfsHelper.BuildExtrasFileName(
                    mapping,
                    extraSeason,
                    state.ExtraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                    Path.GetExtension(sourceInfo.SourcePath),
                    state.DisplayTitle,
                    hasExtraPeer ? mapping.PartIndex : null,
                    hasExtraPeer ? mapping.PartCount : 1,
                    extraVersionIndex,
                    mapping.IsVariation,
                    metadataService
                );

                foreach (var (moviePath, (rootPath, folderName)) in movieDirs)
                {
                    string extraPath = Path.Combine(moviePath, extraSeason.Folder);
                    if (!Settings.Advanced.DisableVfsGeneration)
                        fileSystem.CreateDirectory(extraPath);
                    string destinationFilePath = Path.Combine(extraPath, fileName);

                    if (state.ExpectedFiles.Contains(destinationFilePath))
                        continue;

                    if (VfsShared.TryCreateLink(sourceInfo.SourcePath, destinationFilePath, logger, fileSystem, skipExistenceCheck: state.SkipCheck))
                    {
                        state.Created++;
                        state.Planned++;
                        state.ExpectedFiles.Add(destinationFilePath);
                        state.OnLink?.Invoke(rootPath, $"Movie ❯ {folderName}", $"{extraSeason.Folder}/{fileName}", sourceInfo.SourcePath);
                    }
                }
            }
    }

    #endregion
}
