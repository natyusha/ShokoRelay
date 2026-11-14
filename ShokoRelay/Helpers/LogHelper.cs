using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Controllers;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Helpers;

/// <summary>Utility methods for writing plugin-specific diagnostic logs and structured reports.</summary>
public static class LogHelper
{
    #region Logging Interface

    /// <summary>Generates and writes a report to the plugin's logs directory using the provided builder logic.</summary>
    /// <typeparam name="T">The type of data being reported.</typeparam>
    /// <param name="pluginDir">Root plugin directory.</param>
    /// <param name="fileName">Target filename.</param>
    /// <param name="data">Data object to process.</param>
    /// <param name="builder">Logic to format the data into the StringBuilder.</param>
    /// <param name="logger">Optional logger for reporting write failures.</param>
    public static void WriteReport<T>(string pluginDir, string fileName, T data, Action<StringBuilder, T> builder, ILogger? logger = null)
    {
        try
        {
            var sb = new StringBuilder();
            builder(sb, data);
            string dir = Path.Combine(pluginDir, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "LogHelper: Failed to write {FileName}", fileName);
        }
    }

    #endregion

    #region Generic Builder

    /// <summary>Builds a standardized report with a header, a aligned stats block, and an optional list of details.</summary>
    /// <param name="builder">Target builder.</param>
    /// <param name="title">The title for the report header.</param>
    /// <param name="stats">A dictionary of key-value pairs to display in the stats block.</param>
    /// <param name="listHeader">Optional header for the details list.</param>
    /// <param name="items">Optional collection of detail strings.</param>
    public static void BuildReport(StringBuilder builder, string title, Dictionary<string, object> stats, string? listHeader = null, IEnumerable<string>? items = null)
    {
        AppendHeader(builder, title);
        foreach (var stat in stats)
            builder.AppendLine($"  {stat.Key.PadRight(25)}: {stat.Value}");

        if (items?.Any() == true)
        {
            builder.AppendLine();
            if (!string.IsNullOrEmpty(listHeader))
                builder.AppendLine(listHeader);
            foreach (var item in items)
                builder.AppendLine($"  {item}");
        }
    }

    /// <summary>Appends a standardized header to a report string builder.</summary>
    /// <param name="builder">The string builder to append to.</param>
    /// <param name="title">The title for the report header.</param>
    private static void AppendHeader(StringBuilder builder, string title) =>
        builder.AppendLine($"{title} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {ShokoRelayConstants.Name} v{ShokoRelayConstants.Version}").AppendLine(new string('-', 90)).AppendLine();

    #endregion

    #region Report Builders

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskPlexAuthRefresh"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Plex discovery result data.</param>
    public static void BuildDiscoveryReport(StringBuilder builder, PlexDiscoveryResult result)
    {
        var libs = result.Libraries;
        string? customUrl = !string.IsNullOrWhiteSpace(result.CustomUrl) ? result.CustomUrl.Trim().TrimEnd('/') : null;

        AppendHeader(builder, "Plex Discovery Report");
        builder.AppendLine($"  Libraries Found          : {libs.Count}");

        if (customUrl != null)
            builder.AppendLine($"  Configured Override URL  : {customUrl}");

        var tvLibs = libs.Where(l => !string.Equals(l.Type, "movie", StringComparison.OrdinalIgnoreCase)).ToList();
        var movieLibs = libs.Where(l => string.Equals(l.Type, "movie", StringComparison.OrdinalIgnoreCase)).ToList();

        void PrintLib(PlexAvailableLibrary l)
        {
            string urlLabel = (customUrl != null && string.Equals(l.ServerUrl.TrimEnd('/'), customUrl, StringComparison.OrdinalIgnoreCase)) ? " [Override]" : "";
            builder.AppendLine($"  - [{l.ServerName}] {l.Title}");
            builder.AppendLine($"    URL   : {l.ServerUrl}{urlLabel}");
            builder.AppendLine($"    Agent : {l.Agent}");
            if (l.Locations.Count > 0)
                builder.AppendLine($"    Paths : {string.Join(", ", l.Locations)}");
        }

        if (tvLibs.Count > 0)
        {
            builder.AppendLine().AppendLine("TV Shows Libraries:");
            foreach (var l in tvLibs)
                PrintLib(l);
        }

        if (movieLibs.Count > 0)
        {
            builder.AppendLine().AppendLine("Movie Libraries:");
            foreach (var l in movieLibs)
                PrintLib(l);
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskPlexCollectionsBuild"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Build result data.</param>
    public static void BuildCollectionsReport(StringBuilder builder, BuildCollectionsResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.TotalElapsed.TotalSeconds:F2}s",
            ["Series Processed"] = result.Processed,
            ["Collections Assigned"] = result.Created,
            ["Images Uploaded"] = result.Uploaded,
            ["Images Already Uploaded"] = result.AlreadyUploaded,
            ["Empty Collections Deleted"] = result.DeletedEmptyCollections,
            ["Errors"] = result.Errors,
        };

        var items = new List<string>();

        void AddItems(string header, bool isMovie)
        {
            var assigned = result.CreatedCollections.Where(c => c.IsMovie == isMovie).OrderBy(c => c.TargetTitle).ThenBy(c => c.CollectionName).ToList();
            var uploaded = result.UploadedDetails.Where(u => u.IsMovie == isMovie).OrderBy(u => u.TargetTitle).ThenBy(u => u.CollectionName).ToList();
            var deleted = result.DeletedCollections.Where(d => d.IsMovie == isMovie).OrderBy(d => d.TargetTitle).ThenBy(d => d.CollectionName).ToList();

            if (assigned.Count == 0 && uploaded.Count == 0 && deleted.Count == 0)
                return;

            items.Add(header);
            items.AddRange(assigned.Select(c => $"  ASSIGNED: [{c.TargetTitle}] {c.CollectionName} (Series: {c.SeriesId})"));
            items.AddRange(uploaded.Select(u => $"  UPLOADED: [{u.TargetTitle}] Applied {u.Label} for collection -> {u.CollectionName} (RatingKey: {u.RatingKey})"));
            items.AddRange(deleted.Select(d => $"  REMOVED:  [{d.TargetTitle}] {d.CollectionName} (RatingKey: {d.RatingKey})"));
            items.Add("");
        }

        AddItems("TV Shows Libraries:", false);
        AddItems("Movie Libraries:", true);

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.Select(e => $"  ERROR: {e}"));
        }

        BuildReport(builder, "Collection Build Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskPlexRatingsApply"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Rating result data.</param>
    public static void BuildRatingsReport(StringBuilder builder, ApplyRatingsResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.TotalElapsed.TotalSeconds:F2}s",
            ["Series Processed"] = result.ProcessedShows,
            ["Series Updated"] = result.UpdatedShows,
            ["Episodes Processed"] = result.ProcessedEpisodes,
            ["Episodes Updated"] = result.UpdatedEpisodes,
            ["Errors"] = result.Errors,
        };

        var items = new List<string>();

        void AddChanges(string header, string type)
        {
            var changes = result.AppliedChanges.Where(x => x.Type == type).OrderBy(x => x.Title).ToList();
            if (changes.Count == 0)
                return;

            items.Add(header);
            items.AddRange(changes.Select(c => $"  {c.Title} ({c.RatingKey}): {c.OldRating?.ToString("F2") ?? "0.00"} -> {c.NewRating?.ToString("F2") ?? "0.00"}"));
            items.Add("");
        }

        AddChanges("Series Updates:", "Series");
        AddChanges("Episode Updates:", "Episode");
        AddChanges("Movie Updates:", "Movie");

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.Select(e => $"  ERROR: {e}"));
        }

        BuildReport(builder, "Audience Rating Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskPlexImagesSync"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Image sync result data.</param>
    public static void BuildImageSyncReport(StringBuilder builder, ImageSyncResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.TotalElapsed.TotalSeconds:F2}s",
            ["Images Processed"] = result.Processed,
            ["Images Uploaded"] = result.Uploaded,
            ["Images Already Uploaded"] = result.Skipped,
            ["Errors"] = result.Errors,
        };
        var items = result.UploadedDetails.OrderBy(u => u).Select(u => $"UPLOADED: {u}").ToList();
        items.AddRange(result.ErrorsList.OrderBy(e => e).Select(e => $"ERROR: {e}"));
        BuildReport(builder, "Plex Image Sync Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskPlexAutomationRun"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Automation run result data.</param>
    /// <param name="apiBase">The base API URL for report links.</param>
    public static void BuildPlexAutomationReport(StringBuilder builder, PlexAutomationRunResult result, string apiBase)
    {
        AppendHeader(builder, "Plex Automation Run Report");
        builder.AppendLine($"  Total Elapsed Time         : {result.TotalElapsed.TotalSeconds:F2}s");
        builder.AppendLine();
        builder.AppendLine("Tasks Executed:");
        builder.AppendLine($"  - Collection Generation    : {result.CollectionsElapsed.TotalSeconds:F2}s");
        builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexCollectionsBuild}-report.log");
        builder.AppendLine($"  - Critic Rating Application: {result.RatingsElapsed.TotalSeconds:F2}s");
        builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexRatingsApply}-report.log");

        if (result.ImageSyncElapsed.HasValue)
        {
            builder.AppendLine($"  - Plex Image Sync          : {result.ImageSyncElapsed.Value.TotalSeconds:F2}s");
            builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexImagesSync}-report.log");
        }

        if (result.TrashElapsed.HasValue)
        {
            builder.AppendLine($"  - Empty Plex Trash         : {result.TrashElapsed.Value.TotalSeconds:F2}s");
            foreach (var msg in result.TrashMessages ?? [])
                builder.AppendLine($"    -> {msg}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskVfsBuild"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Build result data.</param>
    public static void BuildVfsReport(StringBuilder builder, VfsBuildResult result)
    {
        var mode = Settings.Advanced.MovieGenerationMode;
        string modeName = typeof(MovieGenerationMode).GetField(mode.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? mode.ToString();

        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.TotalElapsed.TotalSeconds:F2}s",
            ["Movie Generation Mode"] = modeName,
            ["Series Processed"] = result.SeriesProcessed,
            ["Consolidated (Overrides)"] = result.ConsolidatedSeries,
            ["Links Created"] = result.CreatedLinks,
            ["Links Skipped"] = result.Skipped,
            ["Errors"] = result.Errors.Count,
        };

        var items = new List<string>();
        if (result.CleanupDetails.Any())
        {
            items.Add("Cleanup Details:");
            items.AddRange(result.CleanupDetails.OrderBy(c => c.Path).Select(c => $"[{c.ElapsedMs, 5}ms] {c.Path}"));
            items.Add("");
        }

        var tvSeries = result.SeriesDetails.Where(d => !d.IsMovie).ToList();
        var movies = result.SeriesDetails.Where(d => d.IsMovie).ToList();

        if (tvSeries.Count > 0)
        {
            items.Add("Processed Series Details:");
            items.AddRange(tvSeries.OrderByDescending(x => x.ElapsedMs).ThenBy(x => x.Name).Select(d => $"[{d.ElapsedMs, 5}ms] {d.Name} ({d.CreatedLinks} links)"));
        }

        if (movies.Count > 0)
        {
            if (tvSeries.Count > 0)
                items.Add("");
            items.Add("Processed Movie Details:");
            items.AddRange(movies.OrderByDescending(x => x.ElapsedMs).ThenBy(x => x.Name).Select(d => $"[{d.ElapsedMs, 5}ms] {d.Name} ({d.CreatedLinks} links)"));
        }

        if (result.SkippedDetails.Any())
        {
            items.Add("");
            items.Add("Skipped Items:");
            items.AddRange(result.SkippedDetails.OrderBy(s => s).Select(s => $"SKIPPED: {s}"));
        }
        items.AddRange(result.Errors.OrderBy(e => e).Select(e => $"ERROR: {e}"));

        BuildReport(builder, "VFS Generation Report", stats, "Report Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskVfsAudit"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Audit result data.</param>
    public static void BuildVfsAuditReport(StringBuilder builder, VfsAuditResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Series Checked"] = result.SeriesChecked,
            ["Broken Links Removed"] = result.BrokenLinksRemoved,
            ["Orphaned Folders Removed"] = result.OrphanedFoldersRemoved,
            ["Errors"] = result.Errors.Count,
        };
        var items = result.RemovedItems.OrderBy(i => i).ToList();
        items.AddRange(result.Errors.OrderBy(e => e).Select(e => $"ERROR: {e}"));
        BuildReport(builder, "VFS Audit Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskShokoPurgeMissing"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="dryRun">Dry run flag.</param>
    /// <param name="removed">List of removed paths.</param>
    /// <param name="plexRemoved">List of Plex trashed paths.</param>
    /// <param name="plexMessages">List of Plex trash status messages.</param>
    public static void BuildPurgeMissingReport(StringBuilder builder, bool dryRun, IReadOnlyList<string>? removed, IReadOnlyList<string>? plexRemoved, IReadOnlyList<string>? plexMessages)
    {
        var stats = new Dictionary<string, object>
        {
            ["Mode"] = dryRun ? "Dry Run" : "Live",
            ["Shoko Missing Files"] = removed?.Count ?? 0,
            ["Plex Trash Items"] = plexRemoved?.Count ?? 0,
        };

        var items = new List<string>();
        if (plexMessages?.Count > 0)
        {
            items.Add("Plex Trash Status:");
            items.AddRange(plexMessages.Select(m => $"  {m}"));
            items.Add("");
        }
        if (removed?.Count > 0)
        {
            items.Add("Shoko Removed Paths:");
            items.AddRange(removed.OrderBy(p => p).Select(p => $"  {p}"));
            items.Add("");
        }
        if (plexRemoved?.Count > 0)
        {
            items.Add("Plex Trashed Items:");
            items.AddRange(plexRemoved.OrderBy(p => p).Select(p => $"  {p}"));
        }

        BuildReport(builder, "Purge Missing Files Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskShokoSyncWatched"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Sync result data.</param>
    /// <param name="dir">Sync direction string.</param>
    /// <param name="dry">Dry run flag.</param>
    /// <param name="ratings">Whether ratings were synced.</param>
    public static void BuildSyncWatchedReport(StringBuilder builder, PlexWatchedSyncResult result, string dir, bool dry, bool ratings)
    {
        var stats = new Dictionary<string, object>
        {
            ["Direction"] = dir,
            ["Mode"] = dry ? "Dry Run" : "Live",
            ["Include Ratings"] = ratings,
            ["Processed"] = result.Processed,
            ["Marked"] = result.MarkedWatched,
            ["Progress Updated"] = result.ProgressUpdated,
            ["Skipped"] = result.Skipped,
        };

        var items = new List<string>();
        foreach (var userGroup in result.PerUserChanges.OrderBy(kv => kv.Key))
        {
            items.Add($"User: {userGroup.Key}");
            var byLibrary = userGroup.Value.GroupBy(c => c.LibraryName ?? "Default").OrderBy(g => g.Key);

            foreach (var libGroup in byLibrary)
            {
                items.Add($"  Library: {libGroup.Key}");
                items.AddRange(
                    libGroup
                        .OrderBy(c => c.SeriesTitle)
                        .ThenBy(c => c.SeasonNumber)
                        .ThenBy(c => c.EpisodeNumber)
                        .Select(c =>
                        {
                            string action = c.Reason == "progress_updated" ? "Updated Progress" : (c.WouldMark && string.IsNullOrEmpty(c.Reason) ? "Marked" : "Already Watched");
                            return $"    - {c.SeriesTitle} (S{c.SeasonNumber:D2}E{c.EpisodeNumber:D2}) -> {action}";
                        })
                );
            }
        }

        BuildReport(builder, "Sync Watched Report", stats, "Change Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskAtVfsBuild"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Build result data.</param>
    /// <param name="filter">Optional comma-separated list of Shoko or AniDB series IDs to filter the operation.</param>
    public static void BuildAtVfsBuildReport(StringBuilder builder, AnimeThemesMappingApplyResult result, List<int> filter)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.Elapsed.TotalSeconds:F2}s",
            ["Series Matched"] = result.SeriesMatched,
            ["Links Created"] = result.LinksCreated,
        };
        if (filter.Count > 0)
            stats["Filter"] = string.Join(", ", filter);

        var items = result.CacheEntries.OrderBy(ce => ce.VfsPath).Select(ce => $"{ce.VfsPath} (VideoID: {ce.VideoId})").ToList();
        items.AddRange(result.Errors.OrderBy(e => e).Select(e => $"ERROR: {e}"));

        BuildReport(builder, "AnimeThemes: VFS Build Report", stats, "Planned Links & Errors:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskAtMapBuild"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Map result data.</param>
    public static void BuildAtVfsMapReport(StringBuilder builder, AnimeThemesMappingBuildResult result)
    {
        var stats = new Dictionary<string, object> { ["Entries Written"] = result.EntriesWritten, ["Reused"] = result.Reused };
        BuildReport(builder, "AnimeThemes: Mapping Build Report", stats, "Messages:", result.Messages);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskAtMp3Build"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Batch result data.</param>
    public static void BuildAtMp3Report(StringBuilder builder, ThemeMp3BatchResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Processed"] = result.Processed,
            ["Skipped"] = result.Skipped,
            ["Errors"] = result.Errors,
        };

        // Sort priority: [ok] (0), [skipped] (1), [error] (2). Secondary sort by Folder path since that is the primary text displayed in the report.
        var items = result
            .Items.OrderBy(i =>
                i.Status == "ok" ? 0
                : i.Status == "skipped" ? 1
                : 2
            )
            .ThenBy(i => i.Folder)
            .Select(i =>
            {
                string line = $"[{i.Status}] {i.Folder}";
                return i.Status == "ok" ? (!string.IsNullOrWhiteSpace(i.Slug) ? $"{line} | {i.Slug}" : line) : (!string.IsNullOrWhiteSpace(i.Message) ? $"{line} | {i.Message}" : line);
            })
            .ToList();

        BuildReport(builder, "AnimeThemes: MP3 Batch Report", stats, "Item Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskAtMp3Audit"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Audit result data.</param>
    public static void BuildAtMp3AuditReport(StringBuilder builder, ThemeMp3AuditResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Non-OP1 Themes Checked"] = result.Processed,
            ["Missing Slugs Fixed"] = result.MissingSlugsFixed,
            ["Non-Standard Themes"] = result.UpgradesFound,
            ["Overridden Openings"] = result.OverriddenOps.Count,
            ["Overridden Endings"] = result.OverriddenEds.Count,
        };

        var items = new List<string>();

        if (result.Upgrades.Count > 0)
        {
            items.Add("Non-Standard Themes:");
            items.AddRange(result.Upgrades.OrderBy(u => u).Select(u => $"  {u}"));
            items.Add("");
        }

        if (result.OverriddenOps.Count > 0)
        {
            items.Add("Overridden Openings:");
            items.AddRange(result.OverriddenOps.OrderBy(o => o).Select(o => $"  {o}"));
            items.Add("");
        }

        if (result.OverriddenEds.Count > 0)
        {
            items.Add("Overridden Endings:");
            items.AddRange(result.OverriddenEds.OrderBy(o => o).Select(o => $"  {o}"));
            items.Add("");
        }

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.OrderBy(e => e).Select(e => $"ERROR: {e}"));
        }

        BuildReport(builder, "AnimeThemes: MP3 Audit Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskAtWebmDownload"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Download result data.</param>
    public static void BuildWebmDownloadReport(StringBuilder builder, WebmDownloadResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Downloaded"] = result.Downloaded,
            ["Skipped"] = result.Skipped,
            ["Errors"] = result.Errors,
        };
        var items = result.Downloads.OrderBy(d => d).Select(d => $"DOWNLOADED: {d}").ToList();
        items.AddRange(result.Messages.OrderBy(m => m).Select(m => $"MESSAGE: {m}"));
        BuildReport(builder, "AnimeThemes: WebM Download Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskMapSymlinks"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Map result data.</param>
    public static void BuildSourceLinkReport(StringBuilder builder, SourceLinkResult result)
    {
        var stats = new Dictionary<string, object> { ["Mode"] = result.IsPurge ? "Purge" : "Map", ["Operations Completed"] = result.Count };
        BuildReport(builder, "Source Link Report", stats, result.IsPurge ? null : "Links Created:", result.Details.OrderBy(d => d));
    }

    #endregion
}
