using System.Collections.Concurrent;
using System.Diagnostics;
using Shoko.Abstractions.FileSystem;

namespace ShokoRelay.Vfs;

public partial class VfsBuilder
{
    #region Audit

    /// <summary>Audits the VFS to find and remove orphaned series folders and broken symlinks.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A result object containing audit statistics.</returns>
    public VfsAuditResult Audit(CancellationToken cancellationToken = default)
    {
        if (Settings.Advanced.DisableVfsGeneration)
        {
            logger.LogInformation("VFS Audit: Skipped -> VFS generation is disabled.");
            return new VfsAuditResult(0, 0, 0, [], []);
        }

        logger.LogInformation("VFS Audit: Starting task...");
        var stopwatch = Stopwatch.StartNew();

        var removed = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();
        var (brokenLinks, orphanedFolders, seriesChecked, blueprintUpdated) = (0, 0, 0, 0);

        string rootName = VfsShared.ResolveRootFolderName();
        string movieRootName = VfsShared.ResolveMovieRootFolderName();
        var allRoots = VfsShared.GetManagedFolderPaths(videoService, onlyVfsEnabled: true);

        var validFolderIds = new HashSet<int>();
        var validMovieFolders = new HashSet<int>();
        var allSeries = metadataService.GetAllShokoSeries();
        var mode = Settings.Advanced.MovieGenerationMode;

        if (allSeries != null)
            foreach (var series in allSeries)
            {
                int folderId = series.GetPrimaryId(metadataService);
                var (doStandard, doMovie) = MapHelper.GetGenerationModes(MapHelper.IsMovie(series), mode);

                if (doStandard)
                    validFolderIds.Add(folderId);

                if (doMovie)
                    foreach (var episode in series.Episodes.Where(episode => episode.Type == EpisodeType.Episode))
                        validMovieFolders.Add(episode.LocalID);
            }

        var blueprint = VfsShared.LoadBlueprint();
        var recursiveOptions = new FileSystemListingOptions { Recursive = true };

        void AuditRoot(string rootFolderName, HashSet<int> validIds)
        {
            foreach (var root in allRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string vfsRoot = Path.Combine(root, rootFolderName);
                if (!fileSystem.DirectoryExists(vfsRoot))
                    continue;

                Parallel.ForEach(fileSystem.GetDirectoryPaths(vfsRoot, cancellationToken: cancellationToken), DefaultParallelOptions(cancellationToken), seriesFolder =>
                {
                    string folderName = Path.GetFileName(seriesFolder);

                    // Ignore special files/folders like .ignore
                    if (folderName.StartsWith('.'))
                        return;

                    if (!int.TryParse(folderName, out int parsedFolderId) || !validIds.Contains(parsedFolderId))
                    {
                        try
                        {
                            fileSystem.DeleteDirectory(seriesFolder, true);
                            Interlocked.Increment(ref orphanedFolders);
                            removed.Add($"[Orphaned Series] {seriesFolder}");

                            if (parsedFolderId > 0)
                                foreach (var rootDict in blueprint.Values)
                                    if (rootDict.TryRemove(parsedFolderId, out _))
                                        Interlocked.Exchange(ref blueprintUpdated, 1);
                        }
                        catch (Exception exception)
                        {
                            errors.Add($"Failed to delete orphaned folder {seriesFolder}: {exception.Message}");
                        }
                        return;
                    }

                    Interlocked.Increment(ref seriesChecked);

                    IReadOnlyList<FileSystemEntry> filesToAudit;
                    try
                    {
                        filesToAudit = fileSystem.GetFileEntries(seriesFolder, recursiveOptions, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"Failed to enumerate series folder {seriesFolder}: {exception.Message}");
                        return;
                    }

                    foreach (var entry in filesToAudit)
                        try
                        {
                            if (entry.IsSymbolicLink && entry.Stat == null)
                            {
                                VfsShared.DeleteSymbolicLink(fileSystem, entry.Path);
                                Interlocked.Increment(ref brokenLinks);
                                removed.Add($"[Broken Link] {entry.Path}");
                            }
                        }
                        catch (Exception exception)
                        {
                            errors.Add($"Failed to process file {entry.Path}: {exception.Message}");
                        }

                    foreach (var subDir in fileSystem.GetDirectoryPaths(seriesFolder, recursiveOptions, cancellationToken).OrderByDescending(dir => dir.Length))
                        try
                        {
                            if (fileSystem.IsDirectoryEmpty(subDir))
                            {
                                fileSystem.DeleteDirectory(subDir, false);
                                Interlocked.Increment(ref orphanedFolders);
                                removed.Add($"[Empty Folder] {subDir}");
                            }
                        }
                        catch { }
                });
            }
        }

        AuditRoot(rootName, validFolderIds);
        AuditRoot(movieRootName, validMovieFolders);

        if (blueprintUpdated > 0)
            VfsShared.SaveBlueprint(blueprint);

        stopwatch.Stop();
        logger.LogInformation(
            "VFS Audit: Task finished -> seriesChecked={Checked}, brokenLinksRemoved={Broken}, orphanedFoldersRemoved={Orphaned} in {Elapsed}ms.",
            seriesChecked,
            brokenLinks,
            orphanedFolders,
            stopwatch.ElapsedMilliseconds
        );
        return new VfsAuditResult(seriesChecked, brokenLinks, orphanedFolders, [.. removed.OrderBy(item => item)], [.. errors.OrderBy(item => item)]);
    }

    #endregion
}
