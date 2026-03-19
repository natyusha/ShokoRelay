using System.Text;
using NLog;
using ShokoRelay.AnimeThemes;
using ShokoRelay.Services;
using ShokoRelay.Sync;
using ShokoRelay.Vfs;

namespace ShokoRelay.Helpers;

/// <summary>Utility methods for writing plugin-specific diagnostic logs and structured reports.</summary>
public static class LogHelper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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

    /// <summary>Generates and writes a report to the plugin's logs directory using the provided builder logic.</summary>
    /// <typeparam name="T">The type of data being reported.</typeparam>
    /// <param name="pluginDir">Root plugin directory.</param>
    /// <param name="fileName">Target filename.</param>
    /// <param name="data">Data object to process.</param>
    /// <param name="builder">Logic to format the data into the StringBuilder.</param>
    public static void WriteReport<T>(string pluginDir, string fileName, T data, Action<StringBuilder, T> builder)
    {
        try
        {
            var sb = new StringBuilder();
            builder(sb, data);
            WriteLog(pluginDir, fileName, sb.ToString());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to write {FileName}", fileName);
        }
    }

    #endregion

    #region Generic Builder

    /// <summary>Builds a standardized report with a header, a aligned stats block, and an optional list of details.</summary>
    /// <param name="sb">Target builder.</param>
    /// <param name="title">The title for the report header.</param>
    /// <param name="stats">A dictionary of key-value pairs to display in the stats block.</param>
    /// <param name="listHeader">Optional header for the details list.</param>
    /// <param name="items">Optional collection of detail strings.</param>
    public static void BuildReport(StringBuilder sb, string title, Dictionary<string, object> stats, string? listHeader = null, IEnumerable<string>? items = null)
    {
        AppendHeader(sb, title);
        foreach (var stat in stats)
            sb.AppendLine($"  {stat.Key.PadRight(25)}: {stat.Value}");

        if (items?.Any() == true)
        {
            sb.AppendLine();
            if (!string.IsNullOrEmpty(listHeader))
                sb.AppendLine(listHeader);
            foreach (var item in items)
                sb.AppendLine($"  {item}");
        }
    }

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine($"{title} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();
    }

    #endregion

    #region Report Builders

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogCollections"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="r">Build result data.</param>
    public static void BuildCollectionsReport(StringBuilder sb, BuildCollectionsResult r)
    {
        var stats = new Dictionary<string, object>
        {
            ["Series Processed"] = r.Processed,
            ["Collections Assigned"] = r.Created,
            ["Posters Uploaded"] = r.Uploaded,
            ["Empty Collections Deleted"] = r.DeletedEmptyCollections,
            ["Errors"] = r.Errors,
        };

        var items = new List<string>();
        foreach (dynamic c in r.CreatedCollections)
            items.Add($"[{c.sectionId}] {c.collectionName} (Series: {c.seriesId})");
        foreach (var e in r.ErrorsList)
            items.Add($"ERROR: {e}");

        BuildReport(sb, "Collection Build Report", stats, "Assignments & Errors:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogRatings"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="result">Rating result data.</param>
    public static void BuildRatingsReport(StringBuilder sb, ApplyRatingsResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Series Processed"] = result.ProcessedShows,
            ["Series Updated"] = result.UpdatedShows,
            ["Episodes Processed"] = result.ProcessedEpisodes,
            ["Episodes Updated"] = result.UpdatedEpisodes,
            ["Errors"] = result.Errors,
        };

        var items = result.AppliedChanges.OrderBy(x => x.Title).Select(c => $"[{c.Type}] {c.Title} ({c.RatingKey}): {c.OldRating?.ToString("F1") ?? "0.0"} -> {c.NewRating?.ToString("F1") ?? "0.0"}").ToList();
        items.AddRange(result.ErrorsList.Select(e => $"ERROR: {e}"));

        BuildReport(sb, "Audience Rating Report", stats, "Updates & Errors:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogVfs"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="r">Build result data.</param>
    public static void BuildVfsReport(StringBuilder sb, VfsBuildResult r)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{r.TotalElapsed.TotalSeconds:F2}s",
            ["Series Processed"] = r.SeriesProcessed,
            ["Links Created"] = r.CreatedLinks,
            ["Links Planned"] = r.PlannedLinks,
            ["Links Skipped"] = r.Skipped,
            ["Errors"] = r.Errors.Count,
        };

        var items = new List<string>();
        if (r.CleanupDetails.Any())
        {
            items.Add("Cleanup Details:");
            items.AddRange(r.CleanupDetails.Select(c => $"[{c.ElapsedMs, 5}ms] {c.Path}"));
            items.Add("");
        }
        items.Add("Processed Series Details:");
        items.AddRange(r.SeriesDetails.OrderByDescending(x => x.ElapsedMs).Select(d => $"[{d.ElapsedMs, 5}ms] {d.Name} ({d.CreatedLinks} links)"));
        items.AddRange(r.Errors.Select(e => $"ERROR: {e}"));

        BuildReport(sb, "VFS Generation Report", stats, "Report Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogRemoveMissing"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="dryRun">Dry run flag.</param>
    /// <param name="removed">List of removed paths.</param>
    public static void BuildRemoveMissingReport(StringBuilder sb, bool dryRun, IReadOnlyList<string>? removed)
    {
        var stats = new Dictionary<string, object> { ["Mode"] = dryRun ? "Dry Run" : "Live", ["Files Found"] = removed?.Count ?? 0 };
        BuildReport(sb, "Remove Missing Files Report", stats, "Removed Paths:", removed);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogSyncWatched"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="result">Sync result data.</param>
    /// <param name="dir">Sync direction string.</param>
    /// <param name="dry">Dry run flag.</param>
    /// <param name="ratings">Whether ratings were synced.</param>
    public static void BuildSyncWatchedReport(StringBuilder sb, PlexWatchedSyncResult result, string dir, bool dry, bool ratings)
    {
        var stats = new Dictionary<string, object>
        {
            ["Direction"] = dir,
            ["Mode"] = dry ? "Dry Run" : "Live",
            ["Include Ratings"] = ratings,
            ["Processed"] = result.Processed,
            ["Marked"] = result.MarkedWatched,
            ["Skipped"] = result.Skipped,
        };

        var items = new List<string>();
        foreach (var kv in result.PerUserChanges)
        {
            items.Add($"User: {kv.Key}");
            items.AddRange(kv.Value.Select(c => $"- {c.SeriesTitle} (S{c.SeasonNumber:D2}E{c.EpisodeNumber:D2}) -> {(c.WouldMark ? "Marked" : "Already Watched")}"));
        }

        BuildReport(sb, "Sync Watched Report", stats, "Change Details:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtVfs"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="result">Build result data.</param>
    /// <param name="filter">Series filter list.</param>
    public static void BuildAtVfsBuildReport(StringBuilder sb, AnimeThemesMappingApplyResult result, List<int> filter)
    {
        var stats = new Dictionary<string, object>
        {
            ["Elapsed Time"] = $"{result.Elapsed.TotalSeconds:F2}s",
            ["Series Matched"] = result.SeriesMatched,
            ["Links Created"] = result.LinksCreated,
        };
        if (filter.Count > 0)
            stats["Filter"] = string.Join(", ", filter);

        var items = result.CacheEntries.Select(ce => $"{ce.VfsPath} (VideoID: {ce.VideoId})").ToList();
        items.AddRange(result.Errors.Select(e => $"ERROR: {e}"));

        BuildReport(sb, "AnimeThemes: VFS Build Report", stats, "Planned Links & Errors:", items);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtMap"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="result">Map result data.</param>
    public static void BuildAtVfsMapReport(StringBuilder sb, AnimeThemesMappingBuildResult result)
    {
        var stats = new Dictionary<string, object> { ["Entries Written"] = result.EntriesWritten, ["Reused"] = result.Reused };
        BuildReport(sb, "AnimeThemes: Mapping Build Report", stats, "Messages:", result.Messages);
    }

    /// <summary>Build the report content for <see cref="ShokoRelayConstants.LogAtMp3"/>.</summary>
    /// <param name="sb"><inheritdoc cref="BuildReport" path="/param[@name='sb']" /></param>
    /// <param name="result">Batch result data.</param>
    public static void BuildAtMp3Report(StringBuilder sb, ThemeMp3BatchResult result)
    {
        var stats = new Dictionary<string, object>
        {
            ["Processed"] = result.Processed,
            ["Skipped"] = result.Skipped,
            ["Errors"] = result.Errors,
        };
        var items = result.Items.Select(i => $"[{i.Status}] {i.AnimeTitle ?? i.Folder} - {i.Slug}").ToList();
        BuildReport(sb, "AnimeThemes: MP3 Batch Report", stats, "Item Details:", items);
    }

    #endregion
}
