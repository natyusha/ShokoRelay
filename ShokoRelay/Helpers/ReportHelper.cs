using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Controllers;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Helpers;

/// <summary>Utility methods for writing plugin-specific diagnostic reports and CSV summaries.</summary>
public static class ReportHelper
{
    #region Report Storage

    /// <summary>Generates and writes a report to the plugin's reports directory using the provided builder logic.</summary>
    /// <typeparam name="T">The type of data being reported.</typeparam>
    /// <param name="fileName">Target filename.</param>
    /// <param name="data">Data object to process.</param>
    /// <param name="builder">Logic to format the data into the StringBuilder.</param>
    /// <param name="logger">Optional logger for reporting write failures.</param>
    public static void WriteReport<T>(string fileName, T data, Action<StringBuilder, T> builder, ILogger? logger = null)
    {
        try
        {
            var stringBuilder = new StringBuilder();
            builder(stringBuilder, data);
            string logsDir = LogsDirectory;
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, fileName), stringBuilder.ToString(), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "ReportHelper: Failed to write {FileName}", fileName);
        }
    }

    #endregion

    #region Generic Builder

    /// <summary>Builds a standardized report with a header, an aligned stats block, and an optional list of details.</summary>
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

        var tvLibs = libs.Where(library => !string.Equals(library.Type, "movie", StringComparison.OrdinalIgnoreCase)).ToList();
        var movieLibs = libs.Where(library => string.Equals(library.Type, "movie", StringComparison.OrdinalIgnoreCase)).ToList();

        void PrintLib(PlexAvailableLibrary library)
        {
            string urlLabel = (customUrl != null && string.Equals(library.ServerUrl.TrimEnd('/'), customUrl, StringComparison.OrdinalIgnoreCase)) ? " [Override]" : "";
            builder.AppendLine($"  - [{library.ServerName}] {library.Title}");
            builder.AppendLine($"    URL   : {library.ServerUrl}{urlLabel}");
            builder.AppendLine($"    Agent : {library.Agent}");
            if (library.Locations.Count > 0)
                builder.AppendLine($"    Paths : {string.Join(", ", library.Locations)}");
        }

        if (tvLibs.Count > 0)
        {
            builder.AppendLine().AppendLine("TV Shows Libraries:");
            foreach (var library in tvLibs)
                PrintLib(library);
        }

        if (movieLibs.Count > 0)
        {
            builder.AppendLine().AppendLine("Movie Libraries:");
            foreach (var library in movieLibs)
                PrintLib(library);
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
            var assigned = result.CreatedCollections.Where(col => col.IsMovie == isMovie).OrderBy(col => col.TargetTitle).ThenBy(col => col.CollectionName).ToList();
            var uploaded = result.UploadedDetails.Where(upload => upload.IsMovie == isMovie).OrderBy(upload => upload.TargetTitle).ThenBy(upload => upload.CollectionName).ToList();
            var deleted = result.DeletedCollections.Where(del => del.IsMovie == isMovie).OrderBy(del => del.TargetTitle).ThenBy(del => del.CollectionName).ToList();

            if (assigned.Count == 0 && uploaded.Count == 0 && deleted.Count == 0)
                return;

            items.Add(header);
            items.AddRange(assigned.Select(col => $"  ASSIGNED: [{col.TargetTitle}] {col.CollectionName} (Series: {col.SeriesId})"));
            items.AddRange(uploaded.Select(upload => $"  UPLOADED: [{upload.TargetTitle}] Applied {upload.Label} for collection -> {upload.CollectionName} (RatingKey: {upload.RatingKey})"));
            items.AddRange(deleted.Select(del => $"  REMOVED:  [{del.TargetTitle}] {del.CollectionName} (RatingKey: {del.RatingKey})"));
            items.Add("");
        }

        AddItems("TV Shows Libraries:", false);
        AddItems("Movie Libraries:", true);

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.Select(error => $"  ERROR: {error}"));
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
            var changes = result.AppliedChanges.Where(change => change.Type == type).OrderBy(change => change.Title).ToList();
            if (changes.Count == 0)
                return;

            items.Add(header);
            items.AddRange(changes.Select(change => $"  {change.Title} ({change.RatingKey}): {change.OldRating?.ToString("F2") ?? "0.00"} -> {change.NewRating?.ToString("F2") ?? "0.00"}"));
            items.Add("");
        }

        AddChanges("Series Updates:", "Series");
        AddChanges("Episode Updates:", "Episode");
        AddChanges("Movie Updates:", "Movie");

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.Select(error => $"  ERROR: {error}"));
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
        var items = result.UploadedDetails.OrderBy(detail => detail).Select(detail => $"UPLOADED: {detail}").ToList();
        items.AddRange(result.ErrorsList.OrderBy(error => error).Select(error => $"ERROR: {error}"));
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
        builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexCollectionsBuild}.report");
        builder.AppendLine($"  - Critic Rating Application: {result.RatingsElapsed.TotalSeconds:F2}s");
        builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexRatingsApply}.report");

        if (result.ImageSyncElapsed.HasValue)
        {
            builder.AppendLine($"  - Plex Image Sync          : {result.ImageSyncElapsed.Value.TotalSeconds:F2}s");
            builder.AppendLine($"    Log File URL             : {apiBase}/logs/{ShokoRelayConstants.TaskPlexImagesSync}.report");
        }

        if (result.TrashElapsed.HasValue)
        {
            builder.AppendLine($"  - Empty Plex Trash         : {result.TrashElapsed.Value.TotalSeconds:F2}s");
            foreach (var message in result.TrashMessages ?? [])
                builder.AppendLine($"    -> {message}");
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
            items.AddRange(result.CleanupDetails.OrderBy(cleanup => cleanup.Path).Select(cleanup => $"[{cleanup.ElapsedMs, 5}ms] {cleanup.Path}"));
            items.Add("");
        }

        var tvSeries = result.SeriesDetails.Where(detail => !detail.IsMovie).ToList();
        var movies = result.SeriesDetails.Where(detail => detail.IsMovie).ToList();

        if (tvSeries.Count > 0)
        {
            items.Add("Processed Series Details:");
            items.AddRange(tvSeries.OrderByDescending(detail => detail.ElapsedMs).ThenBy(detail => detail.Name).Select(detail => $"[{detail.ElapsedMs, 5}ms] {detail.Name} ({detail.CreatedLinks} links)"));
        }

        if (movies.Count > 0)
        {
            if (tvSeries.Count > 0)
                items.Add("");
            items.Add("Processed Movie Details:");
            items.AddRange(movies.OrderByDescending(detail => detail.ElapsedMs).ThenBy(detail => detail.Name).Select(detail => $"[{detail.ElapsedMs, 5}ms] {detail.Name} ({detail.CreatedLinks} links)"));
        }

        if (result.SkippedDetails.Any())
        {
            items.Add("");
            items.Add("Skipped Items:");
            items.AddRange(result.SkippedDetails.OrderBy(skippedItem => skippedItem).Select(skippedItem => $"SKIPPED: {skippedItem}"));
        }
        items.AddRange(result.Errors.OrderBy(error => error).Select(error => $"ERROR: {error}"));

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
        var items = result.RemovedItems.OrderBy(removedItem => removedItem).ToList();
        items.AddRange(result.Errors.OrderBy(error => error).Select(error => $"ERROR: {error}"));
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
            items.AddRange(plexMessages.Select(message => $"  {message}"));
            items.Add("");
        }
        if (removed?.Count > 0)
        {
            items.Add("Shoko Removed Paths:");
            items.AddRange(removed.OrderBy(path => path).Select(path => $"  {path}"));
            items.Add("");
        }
        if (plexRemoved?.Count > 0)
        {
            items.Add("Plex Trashed Items:");
            items.AddRange(plexRemoved.OrderBy(path => path).Select(path => $"  {path}"));
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
            string? shokoUser = userGroup.Value.FirstOrDefault(change => !string.IsNullOrWhiteSpace(change.ShokoUser))?.ShokoUser;
            items.Add(string.IsNullOrWhiteSpace(shokoUser) ? $"User: {userGroup.Key}" : $"User: {userGroup.Key} -> Shoko: {shokoUser}");
            var byLibrary = userGroup.Value.GroupBy(change => change.LibraryName ?? "Default").OrderBy(group => group.Key);

            foreach (var libGroup in byLibrary)
            {
                items.Add($"  Library: {libGroup.Key}");
                items.AddRange(
                    libGroup
                        .OrderBy(change => change.SeriesTitle)
                        .ThenBy(change => change.SeasonNumber)
                        .ThenBy(change => change.EpisodeNumber)
                        .Select(change =>
                        {
                            string action = change.Reason switch
                            {
                                "progress_updated" => "Updated Progress",
                                "rating_updated" => "Updated Rating",
                                _ => change.WouldMark && string.IsNullOrEmpty(change.Reason) ? "Marked" : "Already Watched",
                            };
                            return $"    - {change.SeriesTitle} (S{change.SeasonNumber:D2}E{change.EpisodeNumber:D2}) -> {action}";
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

        var items = result.CacheEntries.OrderBy(entry => entry.VfsPath).Select(entry => $"{entry.VfsPath} (VideoID: {entry.VideoId})").ToList();
        items.AddRange(result.Errors.OrderBy(error => error).Select(error => $"ERROR: {error}"));

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
            .Items.OrderBy(item =>
                item.Status == "ok" ? 0
                : item.Status == "skipped" ? 1
                : 2
            )
            .ThenBy(item => item.Folder)
            .Select(item =>
            {
                string line = $"[{item.Status}] {item.Folder}";
                return item.Status == "ok" ? (!string.IsNullOrWhiteSpace(item.Slug) ? $"{line} | {item.Slug}" : line) : (!string.IsNullOrWhiteSpace(item.Message) ? $"{line} | {item.Message}" : line);
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
            items.AddRange(result.Upgrades.OrderBy(upgrade => upgrade).Select(upgrade => $"  {upgrade}"));
            items.Add("");
        }

        if (result.OverriddenOps.Count > 0)
        {
            items.Add("Overridden Openings:");
            items.AddRange(result.OverriddenOps.OrderBy(ovr => ovr).Select(ovr => $"  {ovr}"));
            items.Add("");
        }

        if (result.OverriddenEds.Count > 0)
        {
            items.Add("Overridden Endings:");
            items.AddRange(result.OverriddenEds.OrderBy(ovr => ovr).Select(ovr => $"  {ovr}"));
            items.Add("");
        }

        if (result.ErrorsList.Count > 0)
        {
            items.Add("Errors:");
            items.AddRange(result.ErrorsList.OrderBy(error => error).Select(error => $"ERROR: {error}"));
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
        var items = result.Downloads.OrderBy(download => download).Select(download => $"DOWNLOADED: {download}").ToList();
        items.AddRange(result.Messages.OrderBy(message => message).Select(message => $"MESSAGE: {message}"));
        BuildReport(builder, "AnimeThemes: WebM Download Report", stats, "Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.TaskMapSymlinks"/>.</summary>
    /// <param name="builder"><inheritdoc cref="BuildReport" path="/param[@name='builder']" /></param>
    /// <param name="result">Map result data.</param>
    public static void BuildSourceLinkReport(StringBuilder builder, SourceLinkResult result)
    {
        var stats = new Dictionary<string, object> { ["Mode"] = result.IsPurge ? "Purge" : "Map", ["Operations Completed"] = result.Count };
        BuildReport(builder, "Source Link Report", stats, result.IsPurge ? null : "Links Created:", result.Details.OrderBy(detail => detail));
    }

    #endregion

    #region CSV Generation

    /// <summary>Parallelized, per-thread memoized aggregator for building diagnostic CSV reports grouped by normalized key with merged variants appended after semicolons.</summary>
    /// <param name="title">Report title banner (e.g., "Tag" or "Credit").</param>
    /// <param name="columnName">Name of the item column (e.g., "tag" or "credit").</param>
    /// <param name="normalizeKey">Function generating a normalized lookup key from a raw name string.</param>
    /// <param name="merges">Dictionary mapping alias keys to canonical target names.</param>
    /// <param name="blacklist">Set of normalized blacklisted keys to exclude.</param>
    /// <param name="formatDisplayName">Optional formatting function for display names (e.g., TitleCase).</param>
    /// <param name="cleanRaw">Optional pre-cleaning function applied before normalization (e.g., stripping moderator notes).</param>
    /// <param name="allowSamePrimaryMerge">True to allow distinct primary-source (AniDB) entries sharing a normalized key to merge; false to keep unconfigured AniDB entries separate.</param>
    internal sealed class CsvReportBuilder(
        string title,
        string columnName,
        Func<string, string> normalizeKey,
        IReadOnlyDictionary<string, string> merges,
        IReadOnlySet<string> blacklist,
        Func<string, string>? formatDisplayName = null,
        Func<string, string>? cleanRaw = null,
        bool allowSamePrimaryMerge = true
    )
    {
        /// <summary>Delegate for recording a single candidate item from a series into the report builder.</summary>
        /// <param name="source">Display name of the metadata source (e.g., "AniDB", "TMDB", "User").</param>
        /// <param name="sourceOrder">Numerical sort order for the source.</param>
        /// <param name="rawName">The raw candidate name string.</param>
        /// <param name="category">Optional isolation category (e.g., "Person" vs "Company") preventing cross-category merges.</param>
        public delegate void RecordDelegate(string source, int sourceOrder, string? rawName, string category = "");

        private readonly record struct ResolvedItem(string NormKey, string DisplayName, string? MergeTarget, bool IsBlacklisted);

        private sealed class VariantStats
        {
            public int Count;
        }

        private sealed class GroupStats
        {
            public string? MergeTarget;
            public readonly HashSet<int> SeriesIds = [];
            public readonly Dictionary<(int SourceOrder, string Source, string DisplayName), VariantStats> Variants = [];
        }

        private sealed class ThreadState
        {
            public readonly Dictionary<string, ResolvedItem> ResolveCache = new(StringComparer.Ordinal);
            public readonly Dictionary<(string Category, string NormKey), GroupStats> Groups = [];
            public readonly HashSet<(string Category, string Source, string NormKey, string DisplayName)> SeenInSeries = [];
        }

        private readonly Lock _mergeLock = new();
        private readonly Dictionary<(string Category, string NormKey), GroupStats> _globalGroups = [];

        /// <summary>Scans all supplied Shoko series in parallel and formats the aggregated results into a sorted CSV report string.</summary>
        /// <param name="seriesList">All Shoko series to evaluate.</param>
        /// <param name="collectSeries">Callback invoked per series to record its items via the supplied recorder delegate.</param>
        /// <returns>A CSV-formatted report string.</returns>
        public string Build(IEnumerable<IShokoSeries> seriesList, Action<IShokoSeries, RecordDelegate> collectSeries)
        {
            Parallel.ForEach(
                seriesList,
                DefaultParallelOptions(),
                () => new ThreadState(),
                (series, loopState, local) =>
                {
                    local.SeenInSeries.Clear();
                    collectSeries(series, (source, sourceOrder, rawName, category) => RecordLocal(local, series.LocalID, category, source, sourceOrder, rawName));
                    return local;
                },
                local =>
                {
                    lock (_mergeLock)
                    {
                        foreach (var (groupKey, localGroup) in local.Groups)
                        {
                            if (!_globalGroups.TryGetValue(groupKey, out var globalGroup))
                                _globalGroups[groupKey] = globalGroup = new GroupStats();

                            globalGroup.MergeTarget ??= localGroup.MergeTarget;
                            foreach (var seriesId in localGroup.SeriesIds)
                                globalGroup.SeriesIds.Add(seriesId);

                            foreach (var (variantKey, vStats) in localGroup.Variants)
                            {
                                if (!globalGroup.Variants.TryGetValue(variantKey, out var genreValues))
                                    globalGroup.Variants[variantKey] = genreValues = new VariantStats();
                                genreValues.Count += vStats.Count;
                            }
                        }
                    }
                }
            );

            return FormatReport();
        }

        /// <summary>Resolves and records a single item into thread-local storage using per-thread memoization.</summary>
        /// <param name="local">Thread-local report accumulation state.</param>
        /// <param name="seriesId">The Shoko series identifier being evaluated.</param>
        /// <param name="category">Isolation category preventing cross-category merges.</param>
        /// <param name="source">Display name of the metadata source (e.g., "AniDB", "TMDB", "User").</param>
        /// <param name="sourceOrder">Numerical sort order for the source.</param>
        /// <param name="rawName">The raw candidate name string.</param>
        private void RecordLocal(ThreadState local, int seriesId, string category, string source, int sourceOrder, string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return;

            if (!local.ResolveCache.TryGetValue(rawName, out var resolved))
            {
                string trimmed = (cleanRaw != null ? cleanRaw(rawName) : rawName).Trim();
                if (trimmed.Length == 0)
                {
                    local.ResolveCache[rawName] = new ResolvedItem(string.Empty, string.Empty, null, true);
                    return;
                }

                string origNorm = normalizeKey(trimmed);
                bool isMerged = merges.TryGetValue(origNorm, out var target);
                string targetStr = isMerged ? target! : trimmed;
                string normKey = isMerged ? normalizeKey(targetStr) : origNorm;
                bool isBlacklisted = normKey.Length == 0 || blacklist.Contains(normKey) || blacklist.Contains(origNorm);

                string displayName = !isBlacklisted && formatDisplayName != null ? formatDisplayName(trimmed) : trimmed;
                string? formattedTarget = !isBlacklisted && isMerged ? (formatDisplayName != null ? formatDisplayName(targetStr) : targetStr) : null;

                resolved = local.ResolveCache[rawName] = new ResolvedItem(normKey, displayName, formattedTarget, isBlacklisted);
            }

            if (resolved.IsBlacklisted || !local.SeenInSeries.Add((category, source, resolved.NormKey, resolved.DisplayName)))
                return;

            var gKey = (category, resolved.NormKey);
            if (!local.Groups.TryGetValue(gKey, out var group))
                local.Groups[gKey] = group = new GroupStats();

            group.MergeTarget ??= resolved.MergeTarget;
            group.SeriesIds.Add(seriesId);

            var vKey = (sourceOrder, source, resolved.DisplayName);
            if (!group.Variants.TryGetValue(vKey, out var vStats))
                group.Variants[vKey] = vStats = new VariantStats();
            vStats.Count++;
        }

        /// <summary>Orders normalized groups and formats each line with merged variants separated by semicolons.</summary>
        /// <returns>The completed CSV report string.</returns>
        private string FormatReport()
        {
            var rows = new List<(int SourceOrder, string Name, int Count, string Line)>(_globalGroups.Count);

            foreach (var group in _globalGroups.Values)
            {
                if (group.Variants.Count == 0)
                    continue;

                // Sort variants so the primary winner comes first matching GlobalNameEntry tiebreaking:
                // 1. Explicit CSV merge target match
                // 2. Source priority (AniDB = 0, TMDB = 1, User = 2)
                // 3. Highest series count
                // 4. Shorter name, then alphabetical
                var ordered = group
                    .Variants.Select(entry => (entry.Key.SourceOrder, entry.Key.Source, entry.Key.DisplayName, entry.Value.Count))
                    .OrderByDescending(variant => group.MergeTarget != null && string.Equals(variant.DisplayName, group.MergeTarget, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(variant => variant.SourceOrder)
                    .ThenByDescending(variant => variant.Count)
                    .ThenBy(variant => variant.DisplayName.Length)
                    .ThenBy(variant => variant.DisplayName, StringComparer.Ordinal)
                    .ToList();

                // When distinct primary-source (AniDB) entries are not explicitly merged by CSV rules, emit each as its own line and attach secondary-source matches
                if (!allowSamePrimaryMerge && group.MergeTarget == null && ordered.Count(variant => variant.SourceOrder == 0) > 1)
                {
                    var primaries = ordered.Where(variant => variant.SourceOrder == 0).ToList();
                    var secondaries = ordered.Where(variant => variant.SourceOrder > 0).ToList();
                    var attached = primaries.Select(_ => new List<(int SourceOrder, string Source, string DisplayName, int Count)>()).ToList();

                    foreach (var sec in secondaries)
                    {
                        int matchIdx = primaries.FindIndex(primary => string.Equals(primary.DisplayName, sec.DisplayName, StringComparison.OrdinalIgnoreCase));
                        attached[matchIdx >= 0 ? matchIdx : 0].Add(sec);
                    }

                    for (int primIdx = 0; primIdx < primaries.Count; primIdx++)
                    {
                        var (sourceOrder, source, displayName, count) = primaries[primIdx];
                        var stringBuilderSplit = new StringBuilder($"{source},{TextHelper.EscapeCsvCommas(displayName)},{count}");
                        foreach (var (_, sourceAtch, displayNameAtch, countAtch) in attached[primIdx])
                            stringBuilderSplit.Append($";{sourceAtch},{TextHelper.EscapeCsvCommas(displayNameAtch)},{countAtch}");
                        rows.Add((sourceOrder, displayName, count, stringBuilderSplit.ToString()));
                    }
                    continue;
                }

                var stringBuilderLine = new StringBuilder();
                int primaryOrder = ordered[0].SourceOrder;
                string primaryName = ordered[0].DisplayName;
                int primaryCount = ordered[0].Count;

                // Case A: The entry has an explicit CSV merge target that no series used directly -> output Override entry first, then all variants
                if (group.MergeTarget != null && !ordered.Any(variant => string.Equals(variant.DisplayName, group.MergeTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    primaryName = group.MergeTarget;
                    primaryCount = group.SeriesIds.Count;
                    stringBuilderLine.Append($"Override,{TextHelper.EscapeCsvCommas(primaryName)},{primaryCount}");
                    foreach (var (_, source, displayName, count) in ordered)
                        stringBuilderLine.Append($";{source},{TextHelper.EscapeCsvCommas(displayName)},{count}");
                }
                // Case B: Has a natural winner (or target match) as ordered[0], append any subsequent variants/plurals/sources after ';'
                else
                {
                    stringBuilderLine.Append($"{ordered[0].Source},{TextHelper.EscapeCsvCommas(ordered[0].DisplayName)},{ordered[0].Count}");
                    for (int ordIdx = 1; ordIdx < ordered.Count; ordIdx++)
                        stringBuilderLine.Append($";{ordered[ordIdx].Source},{TextHelper.EscapeCsvCommas(ordered[ordIdx].DisplayName)},{ordered[ordIdx].Count}");
                }

                rows.Add((primaryOrder, primaryName, primaryCount, stringBuilderLine.ToString()));
            }

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"## {title} Report ##").AppendLine();
            stringBuilder.AppendLine($"#source,{columnName},count");

            foreach (var (_, _, _, line) in rows.OrderBy(row => row.SourceOrder).ThenByDescending(row => row.Count).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
                stringBuilder.AppendLine(line);

            return stringBuilder.ToString();
        }
    }

    #endregion
}
