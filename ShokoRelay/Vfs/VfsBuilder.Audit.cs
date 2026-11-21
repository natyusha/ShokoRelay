using System.Collections.Concurrent;
using System.Diagnostics;

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
        var allRoots = videoService
            .GetAllManagedFolders()
            ?.Where(VfsShared.IsVfsEnabledFolder)
            .Select(folder => folder.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(VfsShared.PathComparer)
            .ToList()
            ?? [];

        var validFolderIds = new HashSet<int>();
        var validMovieFolders = new HashSet<int>();
        var allSeries = metadataService.GetAllShokoSeries();
        var mode = Settings.Advanced.MovieGenerationMode;

        if (allSeries != null)
        {
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
        }

        var blueprint = VfsShared.LoadBlueprint();

        void AuditRoot(string rootFolderName, HashSet<int> validIds)
        {
            foreach (var root in allRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string vfsRoot = Path.Combine(root, rootFolderName);
                if (!Directory.Exists(vfsRoot))
                    continue;

                Parallel.ForEach(Directory.GetDirectories(vfsRoot), DefaultParallelOptions(cancellationToken), seriesFolder =>
                {
                    string folderName = Path.GetFileName(seriesFolder);

                    // Ignore special files/folders like .ignore
                    if (folderName.StartsWith('.'))
                        return;

                    if (!int.TryParse(folderName, out int parsedFolderId) || !validIds.Contains(parsedFolderId))
                    {
                        try
                        {
                            Directory.Delete(seriesFolder, true);
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

                    var filesToAudit = new List<string>();

                    try
                    {
                        filesToAudit = [.. Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories)];
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"Failed to enumerate series folder {seriesFolder}: {exception.Message}");
                        return;
                    }

                    foreach (var file in filesToAudit)
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
                        catch (Exception exception)
                        {
                            errors.Add($"Failed to process file {file}: {exception.Message}");
                        }
                    }

                    foreach (var subDir in Directory.EnumerateDirectories(seriesFolder, "*", SearchOption.AllDirectories).OrderByDescending(dir => dir.Length))
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
