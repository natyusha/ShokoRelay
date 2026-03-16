using System.Text;
using Microsoft.AspNetCore.Mvc;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Controllers;

/// <summary>
/// Provides the foundational infrastructure for all Shoko Relay controllers.
/// Contains shared logic for logging, validation, response formatting, and Plex discovery.
/// </summary>
[ApiVersionNeutral]
[ApiController]
[Route(ShokoRelayInfo.BasePath)]
public abstract class ShokoRelayBaseController(ConfigProvider configProvider, IMetadataService metadataService, PlexClient plexLibrary) : ControllerBase
{
    #region Fields & Properties

    /// <summary>Shared logger instance for ShokoRelay controllers.</summary>
    protected static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Service used for reading and persisting plugin settings and secrets.</summary>
    protected readonly ConfigProvider _configProvider = configProvider;

    /// <summary>Service for querying the Shoko metadata database.</summary>
    protected readonly IMetadataService _metadataService = metadataService;

    /// <summary>Client used for interacting with configured Plex server instances.</summary>
    protected readonly PlexClient _plexLibrary = plexLibrary;

    /// <summary>Returns the absolute base URL of the plugin's API on the current host.</summary>
    protected string ApiBase => $"{Request.Scheme}://{Request.Host}{ShokoRelayInfo.BasePath}";

    #endregion

    #region Logging Helpers

    /// <summary>Write a structured report to a log file inside the plugin's logs directory.</summary>
    /// <param name="fileName">Name of the log file to create or overwrite.</param>
    /// <param name="buildReport">Callback that populates the report content via a StringBuilder.</param>
    protected void WriteReportLog(string fileName, Action<StringBuilder> buildReport)
    {
        try
        {
            var sb = new StringBuilder();
            buildReport(sb);
            LogHelper.WriteLog(_configProvider.PluginDirectory, fileName, sb.ToString());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to write {FileName}", fileName);
        }
    }

    /// <summary>Standardized helper for long-running tasks: executes logic, writes a report, and returns a JSON response with a log link.</summary>
    /// <typeparam name="T">The type of the result data object.</typeparam>
    /// <param name="logName">The name of the log file to generate (e.g., vfs-report.log).</param>
    /// <param name="resultData">The data object to return in the JSON response.</param>
    /// <param name="reportBuilder">The logic used to format the resultData into a text report.</param>
    /// <returns>An IActionResult containing the status, data, and logUrl.</returns>
    protected IActionResult LogAndReturn<T>(string logName, T resultData, Action<StringBuilder, T> reportBuilder)
    {
        WriteReportLog(logName, sb => reportBuilder(sb, resultData));

        return Ok(
            new
            {
                status = "ok",
                data = resultData,
                logUrl = $"{ApiBase}/logs/{logName}",
            }
        );
    }

    #endregion

    #region Validation Helpers

    /// <summary>Parse a comma-separated filter string into a list of valid positive integers.</summary>
    /// <param name="filter">Raw filter string from query parameter.</param>
    /// <param name="errors">Output list of collected parse error messages.</param>
    /// <returns>A list of unique, valid Shoko Series IDs.</returns>
    protected static List<int> ParseFilterIds(string? filter, out List<string> errors)
    {
        errors = [];
        var ids = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(filter))
            return [.. ids];

        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(raw, out int id) || id <= 0)
            {
                errors.Add($"Invalid series id: {raw}");
                continue;
            }
            ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>Validates a filter query string and resolves any secondary IDs to their primary equivalents.</summary>
    /// <param name="filter">Raw comma-separated filter string.</param>
    /// <param name="ids">Parsed and resolved list of series IDs (output).</param>
    /// <returns>BadRequest if any ID is invalid; otherwise null.</returns>
    protected IActionResult? ValidateFilterOrBadRequest(string? filter, out List<int> ids)
    {
        ids = ParseFilterIds(filter, out var errors);
        if (errors.Count > 0)
            return BadRequest(
                new
                {
                    status = "error",
                    message = "Invalid filter values.",
                    errors,
                }
            );

        OverrideHelper.EnsureLoaded();
        if (ShokoRelay.Settings.TmdbEpNumbering)
        {
            ids = [.. ids.Select(i => OverrideHelper.GetPrimary(i, _metadataService)).Distinct()];
        }
        return null;
    }

    /// <summary>Combined guard for Plex automation requests: checks Plex configuration, validates filter/seriesId, and resolves the target series list.</summary>
    /// <param name="seriesId">Optional single series ID.</param>
    /// <param name="filter">Optional filter string.</param>
    /// <param name="seriesList">Resolved Shoko series objects (output).</param>
    /// <param name="filterIds">Resolved numeric IDs (output).</param>
    /// <returns>BadRequest if validation fails; otherwise null.</returns>
    protected IActionResult? ValidatePlexFilterRequest(int? seriesId, string? filter, out List<IShokoSeries?> seriesList, out List<int> filterIds)
    {
        seriesList = [];
        filterIds = [];

        if (!_plexLibrary.IsEnabled)
            return BadRequest(new { status = "error", message = "Plex server configuration is missing or no library selected." });

        var validation = ValidateFilterOrBadRequest(filter, out filterIds);
        if (validation != null)
            return validation;

        if (seriesId.HasValue && filterIds.Count > 0)
            return BadRequest(new { status = "error", message = "Use either seriesId or filter, not both." });

        seriesList = ResolveSeriesList(seriesId, filterIds);
        return null;
    }

    #endregion

    #region Response Formatting

    /// <summary>Wrap a single metadata object in the standard Plex MediaContainer envelope.</summary>
    /// <param name="metadata">The metadata item to embed.</param>
    /// <returns>Plex-compatible JSON response.</returns>
    protected IActionResult WrapInContainer(object metadata) =>
        Ok(
            new
            {
                MediaContainer = new
                {
                    size = 1,
                    totalSize = 1,
                    offset = 0,
                    identifier = ShokoRelayInfo.AgentScheme,
                    Metadata = new[] { metadata },
                },
            }
        );

    /// <summary>Wrap a list of metadata objects in a paged MediaContainer, honouring Plex pagination headers or query parameters.</summary>
    /// <param name="metadataList">Collection of metadata items to page.</param>
    /// <returns>Paged Plex-compatible JSON response.</returns>
    protected IActionResult WrapInPagedContainer(IEnumerable<object> metadataList)
    {
        int start =
            int.TryParse(Request.Headers["X-Plex-Container-Start"], out var s) ? s
            : int.TryParse(Request.Query["X-Plex-Container-Start"], out var sq) ? sq
            : 0;
        int size =
            int.TryParse(Request.Headers["X-Plex-Container-Size"], out var z) ? z
            : int.TryParse(Request.Query["X-Plex-Container-Size"], out var zq) ? zq
            : 50;

        var allItems = metadataList.ToList();
        var pagedData = allItems.Skip(start).Take(size).ToArray();

        return Ok(
            new
            {
                MediaContainer = new
                {
                    offset = start,
                    totalSize = allItems.Count,
                    identifier = ShokoRelayInfo.AgentScheme,
                    size = pagedData.Length,
                    Metadata = pagedData,
                },
            }
        );
    }

    /// <summary>Return an empty Plex match response (zero results).</summary>
    /// <returns>Plex-compatible empty MediaContainer result.</returns>
    protected IActionResult EmptyMatch() => Ok(new { MediaContainer = new { size = 0, Metadata = Array.Empty<object>() } });

    /// <summary>Return a no-op success response used when an operation is requested but no Plex library targets are configured.</summary>
    /// <param name="seriesList">List of series that would have been processed.</param>
    /// <returns>Success status with counts indicating items were skipped.</returns>
    protected IActionResult NoPlexTargetsResponse(IEnumerable<IShokoSeries?> seriesList)
    {
        int processedNone = seriesList.Count(s => s != null);
        return Ok(
            new
            {
                status = "ok",
                processed = processedNone,
                created = 0,
                skipped = processedNone,
                errors = 0,
                deletedEmptyCollections = 0,
            }
        );
    }

    #endregion

    #region Shared Logic Helpers

    /// <summary>Given an optional seriesId or a set of filterIds, returns the corresponding list of Shoko series objects from the database.</summary>
    /// <param name="seriesId">Optional single series ID.</param>
    /// <param name="filterIds">Optional collection of series IDs.</param>
    /// <returns>A list of Shoko series objects.</returns>
    protected List<IShokoSeries?> ResolveSeriesList(int? seriesId, IReadOnlyCollection<int> filterIds)
    {
        return seriesId.HasValue ? [_metadataService.GetShokoSeriesByID(seriesId.Value)]
            : filterIds.Count > 0 ? [.. filterIds.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id))]
            : [.. _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>()];
    }

    /// <summary>Maps a file extension to its corresponding MIME content type for collection poster images.</summary>
    /// <param name="ext">The file extension string.</param>
    /// <returns>A MIME type string or null if unsupported.</returns>
    protected static string? GetCollectionContentTypeForExtension(string ext)
    {
        return string.IsNullOrWhiteSpace(ext)
            ? null
            : ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".tbn" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => null,
            };
    }

    /// <summary>Parses a query parameter into a boolean dry-run flag.</summary>
    /// <param name="dryRun">String value ("true" or "false").</param>
    /// <returns>A tuple containing the parsed bool and an error IActionResult if parsing failed.</returns>
    protected static (bool Parsed, IActionResult? Error) ParseDryRunParam(string? dryRun)
    {
        if (string.IsNullOrWhiteSpace(dryRun))
            return (true, null);
        var v = dryRun.Trim();
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            return (true, null);
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
            return (false, null);
        return (true, null);
    }

    #endregion

    #region Plex Helper Logic

    /// <summary>De-duplicates and cleans discovered Plex library entries obtained from multiple servers.</summary>
    /// <param name="pairs">Tuples of Library and Server information.</param>
    /// <returns>A unique list of available libraries.</returns>
    protected static List<PlexAvailableLibrary> CollectDiscoveredLibraries(IEnumerable<(PlexLibraryInfo Library, PlexServerInfo Server)>? pairs)
    {
        var collected = new List<PlexAvailableLibrary>();
        if (pairs == null)
            return collected;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (lib, srv) in pairs)
        {
            var key = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : $"{srv.PreferredUri}::{lib.Id}";
            if (!seenKeys.Add(key))
                continue;

            collected.Add(
                new PlexAvailableLibrary
                {
                    Id = lib.Id,
                    Title = lib.Title,
                    Type = lib.Type,
                    Agent = lib.Agent,
                    Uuid = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : key,
                    ServerId = srv.Id,
                    ServerName = srv.Name,
                    ServerUrl = srv.PreferredUri ?? string.Empty,
                }
            );
        }
        return collected;
    }

    /// <summary>Persists the results of a Plex discovery run into the plugin secrets file.</summary>
    /// <param name="discovery">The discovery result payload containing servers and libraries.</param>
    protected void PersistDiscoveryResults((bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries) discovery)
    {
        if (discovery.TokenValid && discovery.Servers?.Count > 0)
        {
            var servers = discovery
                .Servers.Select(s => new PlexAvailableServer
                {
                    Id = s.Id,
                    Name = s.Name,
                    PreferredUri = s.PreferredUri ?? string.Empty,
                })
                .ToList();
            _configProvider.UpdatePlexTokenInfo(servers: servers);
        }

        var libs = CollectDiscoveredLibraries(discovery.ShokoLibraries);
        _configProvider.UpdatePlexTokenInfo(libraries: libs);
    }

    #endregion
}
