using Newtonsoft.Json;
using Shoko.QueueProcessor.Acquisition.Attributes;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;
using Shoko.QueueProcessor.Workers;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Jobs;

#region Mapping Build

/// <summary>Queue job scanning AnimeThemes WebM folders and building the cross-reference mapping CSV.</summary>
[NetworkRequired]
[DisallowConcurrentExecution]
[JobKeyGroup("ShokoRelay")]
public sealed class AtMapBuildJob(AnimeThemesMapping atMapping, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<AtMapBuildJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => "Shoko Relay: Build AnimeThemes Mapping";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => "Build AnimeThemes Mapping";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskAtMapBuild,
            $"{ShokoRelayConstants.TaskAtMapBuild}.report",
            ReportHelper.BuildAtVfsMapReport,
            () => atMapping.BuildMappingFileAsync(CancellationToken, Progress),
            semaphore: atMapping.MapBuildLock
        );
}

#endregion

#region WebM Download

/// <summary>Queue job downloading AnimeThemes WebM videos matching filters or recent additions.</summary>
[NetworkRequired]
[DisallowConcurrentExecution]
[JobKeyGroup("ShokoRelay")]
public sealed class AtWebmDownloadJob(AnimeThemesWebmDownloader webmDownloader, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<AtWebmDownloadJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Optional anime name filter.</summary>
    [JobKeyMember("name", 0)]
    public string? Name { get; set; }

    /// <summary>Optional broadcast year filter.</summary>
    [JobKeyMember("year", 1)]
    public int? Year { get; set; }

    /// <summary>Optional broadcast season filter.</summary>
    [JobKeyMember("season", 2)]
    public string? Season { get; set; }

    /// <summary>Optional count of most recently added videos.</summary>
    [JobKeyMember("recent", 3)]
    public int? Recent { get; set; }

    /// <summary>Whether to overwrite existing WebM files.</summary>
    [JobKeyMember("force", 4)]
    public bool Force { get; set; }

    /// <summary>Optional managed folder destination path.</summary>
    [JobKeyMember("dest", 5)]
    public string? Destination { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => "Shoko Relay: Download AnimeThemes WebMs";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title =>
        Recent is > 0 ? $"Download Recent AnimeThemes ({Recent})"
        : !string.IsNullOrWhiteSpace(Name) ? $"Download AnimeThemes ({Name})"
        : $"Download AnimeThemes ({string.Join(" ", new[] { Season, Year?.ToString() }.Where(part => !string.IsNullOrWhiteSpace(part)))})";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskAtWebmDownload,
            $"{ShokoRelayConstants.TaskAtWebmDownload}.report",
            ReportHelper.BuildWebmDownloadReport,
            () => webmDownloader.DownloadAsync(new AnimeThemesWebmQuery(Name, Year, Season, Recent, Force, Destination), CancellationToken, Progress),
            semaphore: webmDownloader.DownloadLock
        );
}

#endregion

#region MP3 Batch & Audit

/// <summary>Queue job generating <c>Theme.mp3</c> files in batch across a directory tree.</summary>
[DatabaseRequired]
[NetworkRequired]
[DisallowConcurrencyGroup("ShokoRelay_VFS")]
[JobKeyGroup("ShokoRelay")]
public sealed class AtMp3BatchJob(AnimeThemesMp3Generator mp3Generator, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<AtMp3BatchJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <summary>Root directory path to process.</summary>
    [JobKeyMember("path", 0)]
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Optional theme slug filter.</summary>
    [JobKeyMember("slug", 1)]
    public string? Slug { get; set; }

    /// <summary>Theme index offset.</summary>
    [JobKeyMember("offset", 2)]
    public int Offset { get; set; }

    /// <summary>Whether to overwrite existing <c>Theme.mp3</c> files.</summary>
    [JobKeyMember("force", 3)]
    public bool Force { get; set; }

    /// <summary>Whether to restrict batching to current-season anime.</summary>
    [JobKeyMember("seasonal", 4)]
    public bool Seasonal { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => "Shoko Relay: Generate Theme.mp3 Batch";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => $"Generate Theme.mp3 Batch ({Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskAtMp3Build,
            $"{ShokoRelayConstants.TaskAtMp3Build}.report",
            ReportHelper.BuildAtMp3Report,
            () => mp3Generator.ProcessBatchAsync(new AnimeThemesMp3Query(FolderPath, Slug, Offset, Batch: true, Force, Seasonal), CancellationToken, Progress),
            result => result.Items.Any(item => item.Status == "cancelled"),
            semaphore: VfsShared.VfsLock
        );
}

/// <summary>Queue job auditing cached non-OP1 <c>Theme.mp3</c> files for available upgrades.</summary>
[DatabaseRequired]
[NetworkRequired]
[DisallowConcurrentExecution]
[JobKeyGroup("ShokoRelay")]
public sealed class AtMp3AuditJob(AnimeThemesMp3Generator mp3Generator, IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger<AtMp3AuditJob> logger)
    : RelayJobBase(cancellation, progress, logger)
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string TypeName => "Shoko Relay: Audit Theme.mp3s";

    /// <inheritdoc/>
    [JsonIgnore]
    public override string Title => "Audit Theme.mp3 Files";

    /// <inheritdoc/>
    public override Task Process() =>
        ExecuteWithTaskTrackingAsync(
            ShokoRelayConstants.TaskAtMp3Audit,
            $"{ShokoRelayConstants.TaskAtMp3Audit}.report",
            ReportHelper.BuildAtMp3AuditReport,
            () => mp3Generator.AuditAsync(CancellationToken, Progress),
            semaphore: mp3Generator.AuditLock
        );
}

#endregion
