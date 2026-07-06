using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ShokoRelay.AnimeThemes;

namespace ShokoRelay.Vfs;

/// <summary>Information about a specific series processed during a VFS build.</summary>
/// <param name="Name">The display name of the series.</param>
/// <param name="ElapsedMs">Time taken to process in milliseconds.</param>
/// <param name="CreatedLinks">Number of links created for this series.</param>
public record SeriesProcessDetails(string Name, long ElapsedMs, int CreatedLinks);

/// <summary>Information about a root cleanup operation.</summary>
/// <param name="Path">The filesystem path that was cleaned.</param>
/// <param name="ElapsedMs">Time taken in milliseconds.</param>
public record RootCleanupDetails(string Path, long ElapsedMs);

/// <summary>Result returned by <see cref="VfsBuilder"/> after a build or clean operation.</summary>
/// <param name="RootPath">VFS root folder name.</param>
/// <param name="SeriesProcessed">Processed series count.</param>
/// <param name="ConsolidatedSeries">Count of secondary series merged into primary series via overrides.</param>
/// <param name="CreatedLinks">Successful links created.</param>
/// <param name="Skipped">Skipped items count.</param>
/// <param name="SkippedDetails">List of specific skipped link descriptions.</param>
/// <param name="Errors">Encountered errors.</param>
/// <param name="PlannedLinks">Target link count.</param>
/// <param name="SeriesDetails">List of detailed processing stats for each series.</param>
/// <param name="CleanupDetails">Details regarding root folder deletions.</param>
/// <param name="TotalElapsed">Total time taken for the entire operation.</param>
public record VfsBuildResult(
    string RootPath,
    int SeriesProcessed,
    int ConsolidatedSeries,
    int CreatedLinks,
    int Skipped,
    List<string> SkippedDetails,
    List<string> Errors,
    int PlannedLinks,
    List<SeriesProcessDetails> SeriesDetails,
    List<RootCleanupDetails> CleanupDetails,
    TimeSpan TotalElapsed
);

/// <summary>Result returned by <see cref="VfsBuilder.Audit"/>.</summary>
/// <param name="SeriesChecked">Number of valid series folders checked.</param>
/// <param name="BrokenLinksRemoved">Number of broken symlinks deleted.</param>
/// <param name="OrphanedFoldersRemoved">Number of orphaned series or empty subfolders deleted.</param>
/// <param name="RemovedItems">List of deleted paths.</param>
/// <param name="Errors">Encountered errors.</param>
public record VfsAuditResult(int SeriesChecked, int BrokenLinksRemoved, int OrphanedFoldersRemoved, List<string> RemovedItems, List<string> Errors);

/// <summary>Represents a file entity inside the VFS Blueprint.</summary>
/// <param name="Name">The formatted filename.</param>
/// <param name="Source">The absolute path to the source file.</param>
public record VfsBlueprintFile([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("source")] string Source);

/// <summary>Represents a season folder entity inside the VFS Blueprint.</summary>
/// <param name="Name">The formatted folder name.</param>
/// <param name="SeasonId">The numeric season ID identifier.</param>
/// <param name="Files">The collection of files within the season.</param>
public record VfsBlueprintSeason(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seasonId")] int? SeasonId,
    [property: JsonPropertyName("files")] IEnumerable<VfsBlueprintFile> Files
);

/// <summary>Represents a full series entity inside the VFS Blueprint.</summary>
/// <param name="Id">The Shoko Series ID.</param>
/// <param name="AnidbId">The AniDB Series ID.</param>
/// <param name="Title">The formatted display title of the series.</param>
/// <param name="RootFiles">Files located in the root of the series folder.</param>
/// <param name="Seasons">The collection of season folders.</param>
public record VfsBlueprintSeries(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("anidbId")] int AnidbId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("rootFiles")] IEnumerable<VfsBlueprintFile> RootFiles,
    [property: JsonPropertyName("seasons")] IEnumerable<VfsBlueprintSeason> Seasons
);

/// <summary>Holds caching dictionaries for a single VFS build session to minimize disk and database I/O.</summary>
/// <summary>Holds caching dictionaries for a single VFS build session to minimize disk and database I/O.</summary>
public sealed class VfsBuildSession
{
    /// <summary>Caches the resolved file data and Plex mappings for series to prevent redundant processing.</summary>
    public ConcurrentDictionary<int, MapHelper.SeriesFileData> SeriesFileDataCache { get; } = new();

    /// <summary>Caches directory enumeration results for episode-level subtitle and metadata sidecar files.</summary>
    public ConcurrentDictionary<string, Lazy<string[]>> SubtitleFileCache { get; } = new(VfsShared.PathComparer);

    /// <summary>Caches directory enumeration results for series-level metadata files like posters and backdrops.</summary>
    public ConcurrentDictionary<string, Lazy<string[]>> MetadataFileCache { get; } = new(VfsShared.PathComparer);

    /// <summary>Tracks directories that have already been created during this session to avoid redundant disk operations.</summary>
    public ConcurrentDictionary<string, byte> CreatedDirs { get; } = new(VfsShared.PathComparer);

    /// <summary>Holds the loaded AnimeThemes mapping entries parsed from the local CSV configuration.</summary>
    public Dictionary<int, List<ThemeMapItem>> ThemeMappings { get; } = AnimeThemesMapping.LoadThemeMappings(ConfigDirectory);

    /// <summary>Caches the enumeration of physically present WebM files in the AnimeThemes directory to speed up path resolution.</summary>
    public ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> PhysicalThemeCaches { get; } = new(VfsShared.PathComparer);
}
