using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Vfs;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Represents a single theme mapping between a local file and AnimeThemes identifiers.
/// </summary>
public sealed record AnimeThemesMappingEntry(
    string FilePath,
    int VideoId,
    int AniDbId,
    bool NC,
    string Slug,
    int Version,
    string ArtistName,
    string SongTitle,
    bool Lyrics,
    bool Subbed,
    bool Uncen,
    bool NSFW,
    bool Spoiler,
    string Source,
    int Resolution,
    string Episodes,
    string Overlap
);

/// <summary>
/// Result returned by a mapping file build operation.
/// </summary>
public sealed record AnimeThemesMappingBuildResult(string MapPath, int EntriesWritten, int Reused, int Errors, IReadOnlyList<string> Messages);

/// <summary>
/// Outcome of applying a mapping file to create VFS links.
/// </summary>
public sealed record AnimeThemesMappingApplyResult(int LinksCreated, int Skipped, int SeriesMatched, IReadOnlyList<string> Errors, IReadOnlyList<string> Planned, TimeSpan Elapsed);

internal sealed record AnimeThemesVideoLookup(
    int VideoId,
    int ThemeId,
    int AniDbId,
    bool NC,
    string Slug,
    int Version,
    string ArtistName,
    string SongTitle,
    bool Lyrics,
    bool Subbed,
    bool Uncen,
    bool NSFW,
    bool Spoiler,
    string Source,
    int Resolution,
    string Episodes,
    string Overlap
);

/// <summary>
/// Provides operations for building and applying mappings between anime theme files and AniDB/video identifiers.
/// </summary>
/// <param name="metadataService">Service for accessing Shoko metadata.</param>
/// <param name="videoService">Service for resolving video file objects.</param>
/// <param name="configProvider">Provider for configuration settings.</param>
public class AnimeThemesMapping(IMetadataService metadataService, IVideoService videoService, ConfigProvider configProvider)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMetadataService _metadataService = metadataService;
    private readonly IVideoService _videoService = videoService;
    private readonly AnimeThemesApi _apiClient = new();
    private readonly string _configDirectory = configProvider.ConfigDirectory;

    /// <summary>
    /// Serialize a single AnimeThemesMappingEntry to a CSV line.
    /// </summary>
    /// <param name="entry">The entry to serialize.</param>
    /// <returns>A comma-separated string.</returns>
    public static string SerializeMappingEntry(AnimeThemesMappingEntry entry) => AnimeThemesHelper.SerializeEntry(entry);

    /// <summary>
    /// Download the mapping file from a direct raw URL and save it.
    /// </summary>
    /// <param name="rawUrl">Raw URL to download from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries parsed and a log message.</returns>
    public async Task<(int Count, string Log)> ImportMappingFromUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient();
            AnimeThemesHelper.EnsureUserAgent(client);
            var content = await client.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return (0, "Downloaded content empty");

            await File.WriteAllTextAsync(Path.Combine(_configDirectory, AnimeThemesHelper.AtMapFileName), content, ct).ConfigureAwait(false);
            return (AnimeThemesHelper.ParseMappingContent(content).Count, $"AnimeThemes mapping import - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nUrl: {rawUrl}\nEntries: {content.Length}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to import mapping from URL");
            return (0, "Import failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Scan configured import roots for AnimeThemes files and write a mapping CSV.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A build result containing statistics and any error messages.</returns>
    public async Task<AnimeThemesMappingBuildResult> BuildMappingFileAsync(CancellationToken ct = default)
    {
        string themeFolder = VfsShared.ResolveAnimeThemesFolderName();
        var roots = _videoService
            .GetAllVideoFiles()
            .Select(v => v.ManagedFolder?.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .Select(p => Path.Combine(p!, themeFolder))
            .Where(Directory.Exists)
            .ToList();
        if (roots.Count == 0)
            throw new DirectoryNotFoundException("AnimeThemes root folder not found");

        string mapPath = Path.Combine(_configDirectory, AnimeThemesHelper.AtMapFileName);
        var entries = new List<AnimeThemesMappingEntry>();
        var existing = new Dictionary<string, AnimeThemesMappingEntry>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(mapPath))
        {
            try
            {
                foreach (var e in AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct)))
                    existing.TryAdd(e.FilePath, e);
            }
            catch { }
        }

        var files = roots
            .SelectMany(root =>
                Directory.EnumerateFiles(root, "*.webm", SearchOption.AllDirectories).Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("misc", StringComparison.OrdinalIgnoreCase)))
            )
            .ToList();
        var toProcess = new List<(string File, string Rel)>();

        foreach (string file in files)
        {
            string? root = roots.FirstOrDefault(r => file.StartsWith(r + Path.DirectorySeparatorChar));
            string rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/') : file;
            if (existing.TryGetValue(rel, out var old))
                entries.Add(old);
            else
                toProcess.Add((file, rel));
        }

        var messages = new List<string>();
        int errors = 0;
        await Parallel.ForEachAsync(
            toProcess,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ShokoRelay.Settings.Advanced.Parallelism), CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var (lookup, idMissing) = await FetchMetadataAsync(Path.GetFileName(item.File), token);
                    if (lookup == null)
                    {
                        lock (messages)
                        {
                            errors++;
                            messages.Add(idMissing ? $"AniDB ID missing for {item.Rel}" : $"Missing metadata for {item.Rel}");
                        }
                        return;
                    }
                    lock (entries)
                        entries.Add(
                            new AnimeThemesMappingEntry(
                                item.Rel,
                                lookup.VideoId,
                                lookup.AniDbId,
                                lookup.NC,
                                lookup.Slug,
                                lookup.Version,
                                lookup.ArtistName,
                                lookup.SongTitle,
                                lookup.Lyrics,
                                lookup.Subbed,
                                lookup.Uncen,
                                lookup.NSFW,
                                lookup.Spoiler,
                                lookup.Source,
                                lookup.Resolution,
                                lookup.Episodes,
                                lookup.Overlap
                            )
                        );
                }
                catch (Exception ex)
                {
                    lock (messages)
                    {
                        errors++;
                        messages.Add($"{item.Rel}: {ex.Message}");
                    }
                }
            }
        );

        var finalEntries = entries.GroupBy(e => e.FilePath).Select(g => g.First()).ToList();
        await File.WriteAllTextAsync(mapPath, AnimeThemesHelper.SerializeMapping(finalEntries), ct);
        return new AnimeThemesMappingBuildResult(mapPath, finalEntries.Count, entries.Count - toProcess.Count, errors, messages);
    }

    /// <summary>
    /// Test the mapping process for a single webm filename.
    /// </summary>
    /// <param name="webmFileName">The filename to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated entry and prospective filename.</returns>
    public async Task<(AnimeThemesMappingEntry? entry, string? error, string filename)> TestMappingEntryAsync(string webmFileName, CancellationToken ct = default)
    {
        var (lookup, idMissing) = await FetchMetadataAsync(webmFileName, ct);
        if (lookup == null)
            return (null, idMissing ? "AniDB ID missing" : "Missing metadata", webmFileName);
        var entry = new AnimeThemesMappingEntry(
            $"/test/{webmFileName}",
            lookup.VideoId,
            lookup.AniDbId,
            lookup.NC,
            lookup.Slug,
            lookup.Version,
            lookup.ArtistName,
            lookup.SongTitle,
            lookup.Lyrics,
            lookup.Subbed,
            lookup.Uncen,
            lookup.NSFW,
            lookup.Spoiler,
            lookup.Source,
            lookup.Resolution,
            lookup.Episodes,
            lookup.Overlap
        );
        return (entry, null, AnimeThemesHelper.BuildNewFileName(lookup, Path.GetExtension(webmFileName)));
    }

    /// <summary>
    /// Applies the mapping file to create VFS symlinks for matching theme files.
    /// </summary>
    /// <param name="seriesFilter">Optional set of Shoko series IDs to restrict processing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An apply result containing link statistics.</returns>
    public async Task<AnimeThemesMappingApplyResult> ApplyMappingAsync(IReadOnlyCollection<int>? seriesFilter = null, CancellationToken ct = default)
    {
        string mapPath = Path.Combine(_configDirectory, AnimeThemesHelper.AtMapFileName);
        if (!File.Exists(mapPath))
            throw new FileNotFoundException("Mapping file not found");
        var entries = AnimeThemesHelper.ParseMappingContent(await File.ReadAllTextAsync(mapPath, ct));
        var sw = Stopwatch.StartNew();
        var state = new MappingState();
        string themeRoot = VfsShared.ResolveAnimeThemesFolderName();
        string vfsRoot = VfsShared.ResolveRootFolderName();

        var seriesList = (seriesFilter?.Any() == true ? seriesFilter.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)) : _metadataService.GetAllShokoSeries())
            .Where(s => s?.AnidbAnimeID > 0)
            .ToList();

        foreach (var group in seriesList.GroupBy(s => s!.AnidbAnimeID))
        {
            ct.ThrowIfCancellationRequested();
            var matched = entries.Where(e => e.AniDbId == group.Key && IsAllowed(e, ShokoRelay.Settings.Advanced.AnimeThemesOverlapLevel)).ToList();
            if (!matched.Any())
                continue;

            foreach (var series in group)
            {
                var roots = series!.Episodes.SelectMany(ep => ep.VideoList).SelectMany(v => v.Files).Select(VfsShared.ResolveImportRootPath).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
                if (!roots.Any())
                {
                    state.Skipped++;
                    continue;
                }
                state.Matched++;
                int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);

                foreach (var entry in matched)
                {
                    string relPath = entry.FilePath.TrimStart('/', '\\');
                    string? src = AnimeThemesHelper.ResolveThemeSourcePath(relPath, roots[0]!, themeRoot);
                    if (src == null)
                    {
                        state.Skipped++;
                        continue;
                    }

                    var lookup = new AnimeThemesVideoLookup(
                        entry.VideoId,
                        0,
                        entry.AniDbId,
                        entry.NC,
                        entry.Slug,
                        entry.Version,
                        entry.ArtistName,
                        entry.SongTitle,
                        entry.Lyrics,
                        entry.Subbed,
                        entry.Uncen,
                        entry.NSFW,
                        entry.Spoiler,
                        entry.Source,
                        entry.Resolution,
                        entry.Episodes,
                        entry.Overlap
                    );
                    string ext = Path.GetExtension(src);
                    string destName = AnimeThemesHelper.EnsureExtension(AnimeThemesHelper.BuildNewFileName(lookup, ext), ext);
                    string shortsDir = Path.Combine(roots[0]!, vfsRoot, primaryId.ToString(), "Shorts");
                    string destPath = Path.Combine(shortsDir, destName);

                    Directory.CreateDirectory(shortsDir);
                    if (VfsShared.TryCreateLink(src, destPath, Logger, targetOverride: AnimeThemesHelper.BuildThemeRelativeTarget(relPath, themeRoot)))
                    {
                        state.Created++;
                        state.Planned.Add($"{destPath} <- {entry.FilePath}");
                    }
                    else
                        state.Errors.Add($"Link failed: {destPath}");
                }
            }
        }
        return new AnimeThemesMappingApplyResult(state.Created, state.Skipped, state.Matched, state.Errors, state.Planned, sw.Elapsed);
    }

    /// <summary>
    /// Checks if a mapping entry is allowed based on user overlap preferences.
    /// </summary>
    private static bool IsAllowed(AnimeThemesMappingEntry e, OverlapLevel level) => level == OverlapLevel.All || e.Overlap == "None" || (level == OverlapLevel.TransitionOnly && e.Overlap == "Transition");

    /// <summary>
    /// Fetches theme metadata from the AnimeThemes API.
    /// </summary>
    private async Task<(AnimeThemesVideoLookup? lookup, bool idMissing)> FetchMetadataAsync(string fileName, CancellationToken ct)
    {
        var v = await _apiClient.FetchVideoWithArtistsAsync(fileName, ct);
        if (v?.Video == null)
            return (null, false);
        var first = v.Video.Animethemeentries?.FirstOrDefault();
        if (first?.Animetheme == null)
            return (null, false);

        var anime = await _apiClient.FetchAnimeResourcesAsync(v.Video.Id, ct);
        string[] seasonOrder = ["Winter", "Spring", "Summer", "Fall"];
        var best = anime
            ?.Anime?.Select(e => new
            {
                Entry = e,
                Year = e.Year ?? 9999,
                SVal = e.Season != null ? Array.IndexOf(seasonOrder, e.Season) : 9999,
                ids = e.Resources?.Where(r => r.Site == "AniDB" && r.ExternalId.HasValue).Select(r => r.ExternalId!.Value).OrderBy(i => i).ToList(),
            })
            .OrderBy(x => x.Year)
            .ThenBy(x => x.SVal)
            .ThenBy(x => x.ids?.FirstOrDefault() ?? 9999)
            .FirstOrDefault();

        if (best?.ids?.Count > 0)
        {
            var theme = first.Animetheme;
            return (
                new AnimeThemesVideoLookup(
                    v.Video.Id,
                    theme.Id,
                    best.ids[0],
                    v.Video.NC,
                    theme.Slug ?? "",
                    first.Version,
                    string.Join(" / ", theme.Song?.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)) ?? []),
                    theme.Song?.Title ?? "",
                    v.Video.Lyrics,
                    v.Video.Subbed,
                    v.Video.Uncen,
                    first.NSFW,
                    first.Spoiler,
                    v.Video.Source ?? "",
                    v.Video.Resolution ?? 0,
                    first.Episodes ?? "",
                    v.Video.Overlap ?? ""
                ),
                false
            );
        }
        return (null, true);
    }

    private class MappingState
    {
        public int Created,
            Skipped,
            Matched;
        public List<string> Errors = [],
            Planned = [];
    }
}
