using System.Text;
using NLog;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;

namespace ShokoRelay.Helpers;

/// <summary>
/// Utility methods for writing plugin-specific diagnostic logs.
/// </summary>
public static class LogHelper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Ensure that the <c>logs</c> subdirectory exists beneath <paramref name="pluginDir"/> and return its path. Throws if <paramref name="pluginDir"/> is null or whitespace.
    /// </summary>
    /// <param name="pluginDir">The plugin directory under which the logs folder will be created.</param>
    /// <returns>The full path to the <c>logs</c> subdirectory.</returns>
    public static string GetLogsDir(string pluginDir)
    {
        if (string.IsNullOrWhiteSpace(pluginDir))
            throw new ArgumentException("pluginDir is required", nameof(pluginDir));

        string dir = Path.Combine(pluginDir, "logs");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to create logs directory '{Dir}'", dir);
        }
        return dir;
    }

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="fileName"/> inside the logs directory obtained from <paramref name="pluginDir"/>. Returns the full path to the created file.
    /// </summary>
    /// <param name="pluginDir">The plugin directory (parent of the logs folder).</param>
    /// <param name="fileName">Name of the log file to write.</param>
    /// <param name="content">Text content to write.</param>
    /// <returns>The absolute path of the written log file.</returns>
    public static string WriteLog(string pluginDir, string fileName, string content)
    {
        var dir = GetLogsDir(pluginDir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    #region Report Builders

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine($"{title} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();
    }

    /// <summary>Build the report content for <c>collections-report.log</c>.</summary>
    public static void BuildCollectionsReport(StringBuilder sb, BuildCollectionsResult r)
    {
        AppendHeader(sb, "Collection Build Report");
        sb.AppendLine("Builds Plex collections for each series by mapping Shoko group data to Plex library sections, creating named collections and uploading poster artwork.");
        sb.AppendLine("Empty collections are cleaned up automatically.");
        sb.AppendLine();
        sb.AppendLine($"  Series Processed:          {r.Processed}");
        sb.AppendLine($"  Collections Created:       {r.Created}");
        sb.AppendLine($"  Posters Uploaded:          {r.Uploaded}");
        sb.AppendLine($"  Season Posters Uploaded:   {r.SeasonPostersUploaded}");
        sb.AppendLine($"  Skipped (unchanged):       {r.Skipped}");
        sb.AppendLine($"  Empty Collections Deleted: {r.DeletedEmptyCollections}");
        sb.AppendLine($"  Errors:                    {r.Errors}");
        if (r.CreatedCollections.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Created Collections ({r.CreatedCollections.Count}):");
            foreach (dynamic c in r.CreatedCollections)
            {
                try
                {
                    sb.AppendLine($"  [{c.sectionId}] {c.collectionName} (series {c.seriesId}, ratingKey {c.ratingKey})");
                }
                catch
                {
                    sb.AppendLine($"  {c}");
                }
            }
        }
        if (r.ErrorsList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Errors ({r.ErrorsList.Count}):");
            foreach (var e in r.ErrorsList)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <c>ratings-report.log</c>.</summary>
    public static void BuildRatingsReport(StringBuilder sb, ApplyRatingsResult result)
    {
        AppendHeader(sb, "Audience Rating Report");
        sb.AppendLine("Applies critic/audience ratings from the configured source to Plex metadata for series and episodes, updating only items whose rating has changed since the last sync.");
        sb.AppendLine();
        sb.AppendLine($"  Series Processed:   {result.ProcessedShows}");
        sb.AppendLine($"  Series Updated:     {result.UpdatedShows}");
        sb.AppendLine($"  Episodes Processed: {result.ProcessedEpisodes}");
        sb.AppendLine($"  Episodes Updated:   {result.UpdatedEpisodes}");
        sb.AppendLine($"  Errors:             {result.Errors}");
        if (result.ErrorsList?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Errors ({result.ErrorsList.Count}):");
            foreach (var e in result.ErrorsList)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <c>remove-missing-report.log</c>.</summary>
    public static void BuildRemoveMissingReport(StringBuilder sb, bool dryRun, IReadOnlyList<string>? removed)
    {
        AppendHeader(sb, "Remove Missing Files Report");
        sb.AppendLine("Scans the Shoko database for video file entries whose physical files no longer exist on disk and removes the stale records.");
        sb.AppendLine();
        sb.AppendLine($"  Mode:          {(dryRun ? "Dry Run (no changes made)" : "Live (records removed)")}");
        sb.AppendLine($"  Files Found:   {removed?.Count ?? 0}");
        if (removed != null && removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{(dryRun ? "Would Remove" : "Removed")} ({removed.Count}):");
            for (int i = 0; i < removed.Count; i++)
                sb.AppendLine($"  {i + 1}. {removed[i]}");
        }
    }

    /// <summary>Build the report content for <c>sync-watched-report.log</c>.</summary>
    public static void BuildSyncWatchedReport(StringBuilder sb, PlexWatchedSyncResult result, string direction, bool dryRun, bool includeRatings)
    {
        AppendHeader(sb, "Sync Watched Report");
        sb.AppendLine(direction == "Plex->Shoko" ? "Syncs watched states from Plex to Shoko, marking episodes as" : "Syncs watched states from Shoko to Plex, marking episodes as");
        sb.AppendLine("watched and optionally syncing user ratings/votes.");
        sb.AppendLine();
        sb.AppendLine($"  Mode:             {(dryRun ? "Dry Run (no changes made)" : "Live (changes applied)")}");
        sb.AppendLine($"  Direction:        {direction}");
        sb.AppendLine($"  Include Ratings:  {includeRatings}");
        sb.AppendLine();
        sb.AppendLine($"  Episodes Processed: {result.Processed}");
        sb.AppendLine($"  Marked Watched:     {result.MarkedWatched}");
        sb.AppendLine($"  Skipped:            {result.Skipped}");
        sb.AppendLine($"  Matched:            {result.Matched}");
        sb.AppendLine($"  Scheduled Jobs:     {result.ScheduledJobs}");
        sb.AppendLine();
        sb.AppendLine($"  Votes Found:   {result.VotesFound}");
        sb.AppendLine($"  Votes Updated: {result.VotesUpdated}");
        sb.AppendLine($"  Votes Skipped: {result.VotesSkipped}");
        var missingList = result.MissingMappings ?? [];
        int missingCount = missingList.Count;
        sb.AppendLine();
        sb.AppendLine($"  Missing Mappings: {missingCount}");
        if (missingCount > 0)
        {
            sb.AppendLine($"  Missing IDs: {string.Join(", ", missingList)}");
            var diag = result.MissingMappingsDiagnostics;
            if (diag?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Missing Mapping Diagnostics:");
                foreach (var kv in diag)
                {
                    sb.AppendLine($"  Episode {kv.Key}:");
                    foreach (var msg in kv.Value)
                        sb.AppendLine($"    - {msg}");
                }
            }
        }
        if (result.PerUser?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Per-User Summary:");
            foreach (var kv in result.PerUser)
                sb.AppendLine($"  {kv.Key}: Processed={kv.Value.Processed}, Marked={kv.Value.MarkedWatched}, Skipped={kv.Value.Skipped}, Errors={kv.Value.Errors}");
        }
        if (result.PerUserChanges?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Per-User Changes:");
            foreach (var kv in result.PerUserChanges)
            {
                sb.AppendLine($"  {kv.Key} ({kv.Value.Count} change{(kv.Value.Count == 1 ? "" : "s")}):");
                foreach (var ch in kv.Value)
                {
                    var label = ch.SeriesTitle != null ? $"{ch.SeriesTitle}" : $"Episode {ch.ShokoEpisodeId}";
                    if (ch.EpisodeTitle != null)
                        label += $" - {ch.EpisodeTitle}";
                    if (ch.SeasonNumber.HasValue)
                        label += $" (S{ch.SeasonNumber:D2}E{ch.EpisodeNumber:D2})";
                    var action = ch.WouldMark ? "mark watched" : (ch.AlreadyWatchedInShoko ? "already watched" : "skip");
                    sb.Append($"    {label} -> {action}");
                    if (ch.PlexUserRating.HasValue || ch.ShokoUserRating.HasValue)
                        sb.Append($" [plex={ch.PlexUserRating?.ToString("F1") ?? "-"}, shoko={ch.ShokoUserRating?.ToString("F1") ?? "-"}]");
                    if (ch.Reason != null)
                        sb.Append($" ({ch.Reason})");
                    sb.AppendLine();
                }
            }
        }
        if (result.ErrorsList?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Errors ({result.ErrorsList.Count}):");
            foreach (var e in result.ErrorsList)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <c>at-vfs-build-report.log</c>.</summary>
    public static void BuildAtVfsBuildReport(StringBuilder sb, AnimeThemesMappingApplyResult result, List<int> filterIds)
    {
        AppendHeader(sb, "AnimeThemes: VFS Build Report");
        sb.AppendLine("Applies the anime-themes mapping CSV to the VFS directory structure, creating symbolic links that map each series folder to its matching theme audio entries.");
        sb.AppendLine();
        sb.AppendLine($"  Elapsed:        {result.Elapsed}");
        sb.AppendLine($"  Series Matched: {result.SeriesMatched}");
        sb.AppendLine($"  Links Created:  {result.LinksCreated}");
        sb.AppendLine($"  Skipped:        {result.Skipped}");
        sb.AppendLine($"  Errors:         {result.Errors?.Count ?? 0}");
        if (filterIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Filter (series IDs): {string.Join(", ", filterIds)}");
        }
        if (result.Planned?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Planned Links ({result.Planned.Count}):");
            foreach (var p in result.Planned)
                sb.AppendLine($"  {p}");
        }
        if (result.Errors?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Errors ({result.Errors.Count}):");
            foreach (var e in result.Errors)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <c>at-vfs-map-report.log</c>.</summary>
    public static void BuildAtVfsMapReport(StringBuilder sb, AnimeThemesMappingBuildResult result)
    {
        AppendHeader(sb, "AnimeThemes: Mapping Build Report");
        sb.AppendLine("Scans the configured anime-themes directory and rebuilds the mapping CSV file that links each local series folder to its AnimeThemes entry.");
        sb.AppendLine();
        sb.AppendLine($"  Map File:        {result.MapPath}");
        sb.AppendLine($"  Entries Written: {result.EntriesWritten}");
        sb.AppendLine($"  Reused:          {result.Reused}");
        sb.AppendLine($"  Errors:          {result.Errors}");
        if (result.Messages?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Messages ({result.Messages.Count}):");
            foreach (var m in result.Messages)
                sb.AppendLine($"  {m}");
        }
    }

    /// <summary>Build the report content for <c>at-mp3-report.log</c>.</summary>
    public static void BuildAtMp3Report(StringBuilder sb, ThemeMp3BatchResult batch)
    {
        AppendHeader(sb, "AnimeThemes: MP3 Batch Report");
        sb.AppendLine("Batch-generates Theme.mp3 files for anime series by fetching theme audio from AnimeThemes and converting it to MP3 via FFmpeg.");
        sb.AppendLine();
        sb.AppendLine($"  Root:      {batch.Root}");
        sb.AppendLine($"  Processed: {batch.Processed}");
        sb.AppendLine($"  Skipped:   {batch.Skipped}");
        sb.AppendLine($"  Errors:    {batch.Errors}");
        if (batch.Items.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Items ({batch.Items.Count}):");
            foreach (var item in batch.Items)
            {
                var title = item.AnimeTitle ?? item.Folder;
                sb.Append($"  [{item.Status}] {title}");
                if (item.AnimeSlug != null)
                    sb.Append($" (slug: {item.AnimeSlug})");
                if (item.Slug != null)
                    sb.Append($" [{item.Slug}]");
                if (item.DurationSeconds.HasValue)
                    sb.Append($" {item.DurationSeconds:F1}s");
                if (item.ThemePath != null)
                    sb.Append($" -> {item.ThemePath}");
                if (item.Message != null)
                    sb.Append($" | {item.Message}");
                sb.AppendLine();
            }
        }
    }

    #endregion
}
