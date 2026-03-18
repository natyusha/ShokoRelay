using System.Text;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Helpers;

/// <summary>Utility methods for writing plugin-specific diagnostic logs and structured reports.</summary>
public static class LogHelper
{
    #region Logging Interface

    /// <summary>Write content to a log file inside the plugin's logs directory.</summary>
    /// <param name="pluginDir">Root plugin directory.</param>
    /// <param name="fileName">Target filename.</param>
    /// <param name="content">Text content.</param>
    /// <returns>Absolute path to the created file.</returns>
    public static string WriteLog(string pluginDir, string fileName, string content)
    {
        string dir = Path.Combine(pluginDir, "logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    #endregion

    #region Report Generation

    #region Internal Helpers

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine($"{title} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();
    }

    #endregion

    #region Report Builders

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogCollections"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="r">Build result data.</param>
    public static void BuildCollectionsReport(StringBuilder sb, BuildCollectionsResult r)
    {
        AppendHeader(sb, "Collection Build Report");
        sb.AppendLine($"  Series Processed:          {r.Processed}");
        sb.AppendLine($"  Collections Assigned:      {r.Created}");
        sb.AppendLine($"  Posters Uploaded:          {r.Uploaded}");
        sb.AppendLine($"  Empty Collections Deleted: {r.DeletedEmptyCollections}");
        sb.AppendLine($"  Errors:                    {r.Errors}");
        if (r.CreatedCollections.Count > 0)
        {
            sb.AppendLine("\nAssignments:");
            foreach (dynamic c in r.CreatedCollections)
                sb.AppendLine($"  [{c.sectionId}] {c.collectionName} (Series: {c.seriesId})");
        }
        if (r.ErrorsList.Count > 0)
        {
            sb.AppendLine("\nErrors:");
            foreach (var e in r.ErrorsList)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogRatings"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="result">Rating result data.</param>
    public static void BuildRatingsReport(StringBuilder sb, ApplyRatingsResult result)
    {
        AppendHeader(sb, "Audience Rating Report");
        sb.AppendLine($"  Series Processed:   {result.ProcessedShows}");
        sb.AppendLine($"  Series Updated:     {result.UpdatedShows}");
        sb.AppendLine($"  Episodes Processed: {result.ProcessedEpisodes}");
        sb.AppendLine($"  Episodes Updated:   {result.UpdatedEpisodes}");
        sb.AppendLine($"  Errors:             {result.Errors}");
        if (result.AppliedChanges.Count > 0)
        {
            sb.AppendLine("\nApplied Updates:");
            foreach (var c in result.AppliedChanges.OrderBy(x => x.Title))
            {
                var oldR = c.OldRating?.ToString("F1") ?? "0.0";
                var newR = c.NewRating?.ToString("F1") ?? "0.0";
                sb.AppendLine($"  [{c.Type}] {c.Title} ({c.RatingKey}): {oldR} -> {newR}");
            }
        }
        if (result.ErrorsList?.Count > 0)
        {
            sb.AppendLine("\nErrors:");
            foreach (var e in result.ErrorsList)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogVfs"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="r">Build result data.</param>
    public static void BuildVfsReport(StringBuilder sb, VfsBuildResult r)
    {
        AppendHeader(sb, "VFS Generation Report");
        sb.AppendLine($"  Elapsed Time:       {r.TotalElapsed.TotalSeconds:F2}s");
        sb.AppendLine($"  Series Processed:   {r.SeriesProcessed}");
        sb.AppendLine($"  Links Created:      {r.CreatedLinks}");
        sb.AppendLine($"  Links Planned:      {r.PlannedLinks}");
        sb.AppendLine($"  Links Skipped:      {r.Skipped}");
        sb.AppendLine();

        if (r.CleanupDetails.Any())
        {
            sb.AppendLine("Cleanup Details:");
            foreach (var c in r.CleanupDetails)
                sb.AppendLine($"  [{c.ElapsedMs.ToString().PadLeft(5)}ms] {c.Path}");
            sb.AppendLine();
        }

        if (r.SeriesDetails != null && r.SeriesDetails.Count > 0)
        {
            sb.AppendLine("Processed Series Details:");
            foreach (var detail in r.SeriesDetails.OrderByDescending(x => x.ElapsedMs))
                sb.AppendLine($"  [{detail.ElapsedMs.ToString().PadLeft(5)}ms] {detail.Name} ({detail.CreatedLinks} links)");
        }

        if (r.Errors.Count > 0)
        {
            sb.AppendLine("\nErrors:");
            foreach (var e in r.Errors)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogRemoveMissing"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="dryRun">Dry run flag.</param>
    /// <param name="removed">List of removed paths.</param>
    public static void BuildRemoveMissingReport(StringBuilder sb, bool dryRun, IReadOnlyList<string>? removed)
    {
        AppendHeader(sb, "Remove Missing Files Report");
        sb.AppendLine($"  Mode:          {(dryRun ? "Dry Run" : "Live")}");
        sb.AppendLine($"  Files Found:   {removed?.Count ?? 0}");
        if (removed != null)
            foreach (var f in removed)
                sb.AppendLine($"  - {f}");
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogSyncWatched"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="result">Sync result data.</param>
    /// <param name="dir">Sync direction string.</param>
    /// <param name="dry">Dry run flag.</param>
    /// <param name="ratings">Whether ratings were synced.</param>
    public static void BuildSyncWatchedReport(StringBuilder sb, PlexWatchedSyncResult result, string dir, bool dry, bool ratings)
    {
        AppendHeader(sb, "Sync Watched Report");
        sb.AppendLine($"  Direction:        {dir}");
        sb.AppendLine($"  Mode:             {(dry ? "Dry Run" : "Live")}");
        sb.AppendLine($"  Include Ratings:  {ratings}");
        sb.AppendLine($"\n  Processed: {result.Processed} | Marked: {result.MarkedWatched} | Skipped: {result.Skipped}");
        foreach (var kv in result.PerUserChanges)
        {
            sb.AppendLine($"\nUser: {kv.Key}");
            foreach (var c in kv.Value)
                sb.AppendLine($"  {c.SeriesTitle} (S{c.SeasonNumber:D2}E{c.EpisodeNumber:D2}) -> {(c.WouldMark ? "Marked" : "Already Watched")}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtVfs"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="result">Build result data.</param>
    /// <param name="filter">Series filter list.</param>
    public static void BuildAtVfsBuildReport(StringBuilder sb, AnimeThemesMappingApplyResult result, List<int> filter)
    {
        AppendHeader(sb, "AnimeThemes: VFS Build Report");
        sb.AppendLine($"  Elapsed Time:   {result.Elapsed.TotalSeconds:F2}s");
        sb.AppendLine($"  Series Matched: {result.SeriesMatched}");
        sb.AppendLine($"  Links Created:  {result.LinksCreated}");
        if (filter.Count > 0)
            sb.AppendLine($"  Filter: {string.Join(", ", filter)}");
        sb.AppendLine("\nPlanned Links:");
        foreach (var ce in result.CacheEntries)
            sb.AppendLine($"  {ce.VfsPath} (VideoID: {ce.VideoId})");
        if (result.Errors?.Count > 0)
        {
            sb.AppendLine("\nErrors:");
            foreach (var e in result.Errors)
                sb.AppendLine($"  {e}");
        }
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtMap"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="result">Map result data.</param>
    public static void BuildAtVfsMapReport(StringBuilder sb, AnimeThemesMappingBuildResult result)
    {
        AppendHeader(sb, "AnimeThemes: Mapping Build Report");
        sb.AppendLine($"  Entries Written: {result.EntriesWritten}");
        sb.AppendLine($"  Reused:          {result.Reused}");
        foreach (var m in result.Messages)
            sb.AppendLine($"  {m}");
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtMp3"/>.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="result">Batch result data.</param>
    public static void BuildAtMp3Report(StringBuilder sb, ThemeMp3BatchResult result)
    {
        AppendHeader(sb, "AnimeThemes: MP3 Batch Report");
        sb.AppendLine($"  Processed: {result.Processed} | Skipped: {result.Skipped} | Errors: {result.Errors}");
        foreach (var i in result.Items)
            sb.AppendLine($"  [{i.Status}] {i.AnimeTitle ?? i.Folder} - {i.Slug}");
    }

    #endregion

    #endregion
}
