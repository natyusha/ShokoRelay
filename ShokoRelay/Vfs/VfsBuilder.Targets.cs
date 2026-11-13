using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Vfs;

public partial class VfsBuilder
{
    #region Standard VFS

    /// <summary>Generates standard TV Show VFS symlinks, sidecars, and local extras for a series.</summary>
    /// <param name="state">The active series build state.</param>
    private void BuildVfs(SeriesBuildState state)
    {
        var versionCounters = state.CoordCounts.Where(g => g.Value > 1).ToDictionary(g => g.Key, _ => 1);

        foreach (var mapping in state.FileData.Mappings.OrderBy(m => m.Coords.Season).ThenBy(m => m.Coords.Episode).ThenBy(m => m.PartIndex ?? 0))
        {
            try
            {
                if (!TryResolveAndValidate(state, mapping, out var locInfo))
                    continue;

                string rootPath = Path.Combine(locInfo.ImportRoot, state.RootFolderName);
                string seriesPath = Path.Combine(rootPath, state.FolderId.ToString());
                state.ResolvedVfsSeriesPaths.Add(seriesPath);

                EnsureDirectory(rootPath, state.Session, true);
                EnsureDirectory(seriesPath, state.Session);

                string seasonName = PlexMapping.GetSeasonFolder(mapping.Coords.Season);
                string seasonPath = Path.Combine(seriesPath, VfsHelper.SanitizeName(seasonName));
                EnsureDirectory(seasonPath, state.Session);

                var key = (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation);
                bool hasPeer = state.CoordCounts.TryGetValue(key, out var count) && count > 1;
                int? vIdx = (hasPeer && !mapping.PartIndex.HasValue && versionCounters.TryGetValue(key, out var v)) ? (versionCounters[key] = v + 1) - 1 : null;

                (string Folder, string Subtype) ex = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var e) ? e : ("Featurettes", "featurette");

                string fileName = VfsHelper.SanitizeName(
                    PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out _)
                        ? VfsHelper.BuildExtrasFileName(
                            mapping,
                            ex,
                            state.ExtraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                            Path.GetExtension(locInfo.Src),
                            state.DisplayTitle,
                            hasPeer ? mapping.PartIndex : null,
                            hasPeer ? mapping.PartCount : 1,
                            vIdx,
                            mapping.IsVariation
                        )
                        : VfsHelper.BuildStandardFileName(
                            mapping,
                            state.EpPad,
                            Path.GetExtension(locInfo.Src),
                            mapping.Video!.LocalID,
                            mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                            hasPeer ? mapping.PartIndex : null,
                            hasPeer ? mapping.PartCount : 1,
                            vIdx,
                            mapping.IsVariation
                        )
                );

                var destFilePath = Path.Combine(seasonPath, fileName);

                if (VfsShared.TryCreateLink(locInfo.Src, destFilePath, logger, skipExistenceCheck: state.SkipCheck))
                {
                    state.Created++;
                    state.Planned++;
                    state.ExpectedFiles.Add(destFilePath);
                    state.OnLink?.Invoke(locInfo.ImportRoot, seasonName, fileName, locInfo.Src);

                    // Resolve Primary IDs to allow local asset linking for crossover files that have been consolidated via VFS Overrides.
                    bool isCrossover =
                        mapping.Video?.CrossReferences is { Count: > 1 } xrefs
                        && xrefs.Where(cr => cr.ShokoEpisode != null).Select(cr => OverrideHelper.GetPrimary(cr.ShokoEpisode!.ShokoSeriesID, metadataService)).Distinct().Count() > 1;

                    if (!isCrossover)
                        LinkLocalMetadataForFile(state, locInfo.Src, seriesPath, seasonPath, destFilePath, locInfo.ImportRoot, "", seasonName);
                }
                else
                {
                    state.Skipped++;
                    state.Errors.Add($"Link failed: {locInfo.Src} -> {destFilePath}");
                }
            }
            catch (Exception ex)
            {
                state.Skipped++;
                state.Errors.Add($"Error processing mapping for {mapping.FileName}: {ex.Message}");
            }
        }

        // Plex Local Extras: Evaluates source directories exactly once, broadcasting results across all unique VFS roots.
        if (Settings.Advanced.PlexLocalExtras)
            assetLinker.LinkLocalExtras(
                state.FileData,
                state.ResolvedVfsSeriesPaths,
                state.VideoBaseNames,
                state.EpPad,
                (importRoot, season, fileName, source) =>
                {
                    string path = string.IsNullOrEmpty(season)
                        ? Path.Combine(importRoot, state.RootFolderName, state.FolderId.ToString(), fileName)
                        : Path.Combine(importRoot, state.RootFolderName, state.FolderId.ToString(), season, fileName);
                    state.ExpectedFiles.Add(path);
                    state.OnLink?.Invoke(importRoot, season, fileName, source);
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
        var movieDirs = new List<(string ImportRoot, string FolderName, string FullPath)>();
        var mainMappings = state.FileData.Mappings.Where(m => m.PrimaryEpisode.Type == EpisodeType.Episode).ToList();
        var extraMappings = state.FileData.Mappings.Where(m => m.PrimaryEpisode.Type != EpisodeType.Episode).ToList();
        var movieVersionCounters = state.CoordCounts.Where(g => g.Value > 1).ToDictionary(g => g.Key, _ => 1);

        foreach (var mapping in mainMappings)
        {
            if (!TryResolveAndValidate(state, mapping, out var locInfo))
                continue;

            string folderName = mapping.PrimaryEpisode.ID.ID;
            string rootPath = Path.Combine(locInfo.ImportRoot, state.MovieRootName);
            string moviePath = Path.Combine(rootPath, folderName);

            state.ResolvedVfsSeriesPaths.Add(moviePath);
            movieDirs.Add((locInfo.ImportRoot, folderName, moviePath));

            EnsureDirectory(rootPath, state.Session, true);
            EnsureDirectory(moviePath, state.Session);

            var key = (mapping.Coords.Season, mapping.Coords.Episode, mapping.IsVariation);
            bool hasPeer = state.CoordCounts.TryGetValue(key, out var count) && count > 1;
            int? vIdx = (hasPeer && !mapping.PartIndex.HasValue && movieVersionCounters.TryGetValue(key, out var v)) ? (movieVersionCounters[key] = v + 1) - 1 : null;

            string fileName = VfsHelper.SanitizeName(
                VfsHelper.BuildMovieFileName(
                    mapping,
                    Path.GetExtension(locInfo.Src),
                    mapping.Video.LocalID,
                    mapping.PartCount > 1 && mapping.PartIndex.HasValue,
                    hasPeer ? mapping.PartIndex : null,
                    hasPeer ? mapping.PartCount : 1,
                    vIdx,
                    mapping.IsVariation
                )
            );
            string destFilePath = Path.Combine(moviePath, fileName);

            if (VfsShared.TryCreateLink(locInfo.Src, destFilePath, logger, skipExistenceCheck: state.SkipCheck))
            {
                state.Created++;
                state.Planned++;
                state.ExpectedFiles.Add(destFilePath);
                state.OnLink?.Invoke(locInfo.ImportRoot, $"Movie ❯ {folderName}", fileName, locInfo.Src);

                LinkLocalMetadataForFile(state, locInfo.Src, moviePath, moviePath, destFilePath, locInfo.ImportRoot, $"Movie ❯ {folderName}", $"Movie ❯ {folderName}");
            }
            else
            {
                state.Skipped++;
                state.Errors.Add($"Link failed: {locInfo.Src} -> {destFilePath}");
            }
        }

        if (Settings.Advanced.PlexLocalExtras)
        {
            foreach (var mapping in extraMappings)
            {
                if (!TryResolveAndValidate(state, mapping, out var locInfo))
                    continue;

                (string Folder, string Subtype) ex = PlexMapping.TryGetExtraSeason(mapping.Coords.Season, out var e) ? e : ("Featurettes", "featurette");
                string fileName = VfsHelper.BuildExtrasFileName(
                    mapping,
                    ex,
                    state.ExtraPad.GetValueOrDefault(mapping.Coords.Season, 1),
                    Path.GetExtension(locInfo.Src),
                    state.DisplayTitle,
                    mapping.PartIndex,
                    mapping.PartCount,
                    null,
                    mapping.IsVariation
                );

                foreach (var (importRoot, folderName, moviePath) in movieDirs)
                {
                    string extraPath = Path.Combine(moviePath, ex.Folder);
                    if (!Settings.Advanced.DisableVfsGeneration)
                        Directory.CreateDirectory(extraPath);
                    string destFilePath = Path.Combine(extraPath, fileName);

                    if (VfsShared.TryCreateLink(locInfo.Src, destFilePath, logger, skipExistenceCheck: state.SkipCheck))
                    {
                        state.Created++;
                        state.Planned++;
                        state.ExpectedFiles.Add(destFilePath);
                        state.OnLink?.Invoke(importRoot, $"Movie ❯ {folderName}", $"{ex.Folder}/{fileName}", locInfo.Src);
                    }
                }
            }
        }
    }

    #endregion
}
