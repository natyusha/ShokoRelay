using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using NLog;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;
using ShokoRelay.Vfs;
using static ShokoRelay.Helpers.MapHelper;
using static ShokoRelay.Plex.PlexMapping;

namespace ShokoRelay.Controllers;

public partial class ShokoRelayController
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Write a structured report to a log file inside the plugin's <c>logs</c> directory.
    /// </summary>
    /// <param name="fileName">Name of the log file to create or overwrite.</param>
    /// <param name="buildReport">Callback that populates the report content via a <see cref="System.Text.StringBuilder"/>.</param>
    private void WriteReportLog(string fileName, Action<System.Text.StringBuilder> buildReport)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            buildReport(sb);
            LogHelper.WriteLog(_configProvider.PluginDirectory, fileName, sb.ToString());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to write {FileName}", fileName);
        }
    }

    /// <summary>
    /// Parse a comma-separated filter string into a list of valid positive integers; invalid entries are reported via the output <paramref name="errors"/> list.
    /// </summary>
    /// <param name="filter">Raw filter string from query parameter.</param>
    /// <param name="errors">Collected parse error messages.</param>
    private static List<int> ParseFilterIds(string? filter, out List<string> errors)
    {
        errors = new List<string>();
        var ids = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(filter))
            return ids.ToList();

        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(raw, out int id) || id <= 0)
            {
                errors.Add($"Invalid series id: {raw}");
                continue;
            }

            ids.Add(id);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Validate and parse a <paramref name="filter"/> query string; returns a <see cref="BadRequestResult"/> if any entry is invalid, otherwise <c>null</c>.
    /// Also converts secondary IDs to their primary equivalents when TMDB numbering is enabled.
    /// </summary>
    /// <param name="filter">Raw comma-separated filter string.</param>
    /// <param name="ids">Parsed and resolved list of series IDs (output).</param>
    /// <returns><c>null</c> on success, or an <see cref="IActionResult"/> describing the validation error.</returns>
    private IActionResult? ValidateFilterOrBadRequest(string? filter, out List<int> ids)
    {
        ids = ParseFilterIds(filter, out var errors);
        if (errors.Count > 0)
            return BadRequest(
                new
                {
                    status = "error",
                    message = "Invalid filter values.",
                    errors = errors,
                }
            );

        // convert any secondary ids to their primary equivalents based on overrides
        OverrideHelper.EnsureLoaded();
        if (ShokoRelay.Settings.TmdbEpNumbering)
        {
            ids = ids.Select(i => OverrideHelper.GetPrimary(i, _metadataService)).Distinct().ToList();
        }

        return null;
    }

    #region Dashboard / Config

    /// <summary>
    /// Map a file extension to its MIME content type for dashboard static assets. Returns <c>null</c> for disallowed types.
    /// </summary>
    /// <param name="ext">File extension including the leading dot (e.g. <c>.css</c>).</param>
    /// <returns>The MIME type string, or <c>null</c> if the extension is not allowed.</returns>
    private static string? GetDashboardContentTypeForExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return null;
        return ext.ToLowerInvariant() switch
        {
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".ico" => "image/x-icon",
            _ => null,
        };
    }

    private sealed record ConfigPropertySchema(string Path, string Type, string? Display, string? Description, object? DefaultValue, object? EnumValues);

    /// <summary>
    /// Recursively build a list of <see cref="ConfigPropertySchema"/> entries describing each browsable property on <paramref name="type"/>, used to render dynamic dashboard forms.
    /// </summary>
    /// <param name="type">The type to reflect over.</param>
    /// <param name="prefix">Dot-delimited property path prefix for nested types.</param>
    /// <returns>A flat list of schema entries.</returns>
    private static List<ConfigPropertySchema> BuildConfigSchema(Type type, string prefix)
    {
        var props = new List<ConfigPropertySchema>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
            if (browsable != null && !browsable.Browsable)
                continue;

            string path = string.IsNullOrWhiteSpace(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            var display = prop.GetCustomAttribute<DisplayAttribute>();
            var defaultValue = prop.GetCustomAttribute<DefaultValueAttribute>()?.Value;
            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (propType.IsEnum)
            {
                var values = Enum.GetValues(propType)
                    .Cast<object>()
                    .Select(v =>
                    {
                        int iv = Convert.ToInt32(v);
                        string memberName = Enum.GetName(propType, v) ?? iv.ToString();
                        // attempt to read a custom DisplayAttribute from the enum field
                        var field = propType.GetField(memberName);
                        string? custom = field?.GetCustomAttribute<DisplayAttribute>()?.Name;
                        string nm = custom ?? memberName;

                        return new { name = nm, value = iv };
                    })
                    .ToArray();

                props.Add(new ConfigPropertySchema(path, "enum", display?.Name, display?.Description, defaultValue, values));
                continue;
            }

            if (propType == typeof(bool))
            {
                props.Add(new ConfigPropertySchema(path, "bool", display?.Name, display?.Description, defaultValue, null));
                continue;
            }

            if (propType == typeof(string))
            {
                props.Add(new ConfigPropertySchema(path, "string", display?.Name, display?.Description, defaultValue, null));
                continue;
            }

            if (propType.IsPrimitive || propType == typeof(decimal))
            {
                props.Add(new ConfigPropertySchema(path, "number", display?.Name, display?.Description, defaultValue, null));
                continue;
            }

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(propType))
            {
                props.Add(new ConfigPropertySchema(path, "json", display?.Name, display?.Description, defaultValue, null));
                continue;
            }

            if (propType.IsClass)
            {
                props.AddRange(BuildConfigSchema(propType, path));
            }
        }

        return props;
    }
    #endregion

    #region Metadata Provider
    /// <summary>
    /// Represents the body of a Plex matching request. Fields are optional; <see cref="Manual"/> can be used to force a particular series ID.
    /// </summary>
    public record PlexMatchBody(string? Filename, string? Title = null, int? Manual = null);

    /// <summary>
    /// Aggregates relevant information about a Shoko series for controller responses.
    /// Includes the series metadata, API base URL, resolved titles, content rating string, and file data used when building VFS links.
    /// </summary>
    public record SeriesContext(ISeries Series, string ApiUrl, (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles, string ContentRating, SeriesFileData FileData);

    /// <summary>
    /// Extract the <c>Image</c> array from a Plex metadata dictionary object, returning an empty array if not present.
    /// </summary>
    private static object[] ExtractImages(object metadata) => metadata is IDictionary<string, object?> dict && dict.TryGetValue("Image", out var img) && img is object[] arr ? arr : Array.Empty<object>();

    /// <summary>
    /// Convert a Plex-style <paramref name="ratingKey"/> into a <see cref="SeriesContext"/>.
    /// This resolves the underlying Shoko series, applies any merge overrides, and gathers file data and titles. Returns <c>null</c> if the key cannot be parsed or the series is missing.
    /// </summary>
    /// <param name="ratingKey">Plex rating key representing show, season or episode.</param>
    private SeriesContext? GetSeriesContext(string ratingKey)
    {
        int seriesId;

        // alias: if key starts with 'a' followed by digits treat as AniDB series ID
        if (ratingKey.Length > 1 && ratingKey[0] == 'a' && int.TryParse(ratingKey.Substring(1), out var anidb))
        {
            var candidate = _metadataService.GetShokoSeriesByAnidbID(anidb);
            if (candidate == null)
                return null;
            seriesId = candidate.ID;
        }
        else if (ratingKey.StartsWith(PlexConstants.EpisodePrefix))
        {
            var epPart = ratingKey.Substring(PlexConstants.EpisodePrefix.Length);
            if (epPart.Contains(PlexConstants.PartPrefix))
                epPart = epPart.Split(PlexConstants.PartPrefix)[0];

            var ep = _metadataService.GetShokoEpisodeByID(int.Parse(epPart));
            if (ep?.Series == null)
                return null;
            seriesId = ep.Series.ID;
        }
        else if (ratingKey.Contains(PlexConstants.SeasonPrefix))
        {
            if (!int.TryParse(ratingKey.Split(PlexConstants.SeasonPrefix)[0], out seriesId))
                return null;
        }
        else
        {
            if (!int.TryParse(ratingKey, out seriesId))
                return null;
        }

        var series = _metadataService.GetShokoSeriesByID(seriesId);
        if (series == null)
            return null;

        // apply overrides: use primary metadata but merge file data for the entire group
        OverrideHelper.EnsureLoaded();
        int primaryId = OverrideHelper.GetPrimary(series.ID, _metadataService);
        var primarySeries = _metadataService.GetShokoSeriesByID(primaryId) ?? series;
        var group = OverrideHelper.GetGroup(primaryId, _metadataService);
        List<ISeries> extras = group.Skip(1).Select(id => _metadataService.GetShokoSeriesByID(id)).Where(s => s != null).Cast<ISeries>().ToList();

        var fileData = extras.Count > 0 ? MapHelper.GetSeriesFileDataMerged(primarySeries, extras) : MapHelper.GetSeriesFileData(primarySeries);

        return new SeriesContext(primarySeries, ApiBase, TextHelper.ResolveFullSeriesTitles(primarySeries), RatingHelper.GetContentRatingAndAdult(primarySeries).Rating ?? "", fileData);
    }

    /// <summary>
    /// Wrap a single metadata object in the standard Plex <c>MediaContainer</c> envelope used throughout controller responses.
    /// </summary>
    /// <param name="metadata">The object to embed.</param>
    private IActionResult WrapInContainer(object metadata) =>
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

    /// <summary>
    /// Wrap a list of metadata objects in a paged <c>MediaContainer</c>, honouring Plex pagination headers or query parameters.
    /// </summary>
    /// <param name="metadataList">Collection of metadata items to page.</param>
    private IActionResult WrapInPagedContainer(IEnumerable<object> metadataList)
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

    /// <summary>
    /// Return an empty Plex match response (zero results) using the standard <c>MediaContainer</c> format.
    /// </summary>
    private IActionResult EmptyMatch() => Ok(new { MediaContainer = new { size = 0, Metadata = Array.Empty<object>() } });

    /// <summary>
    /// Build a sorted list of mapped episode metadata objects for a given <paramref name="seasonNum"/>, resolving multi-episode files, TMDB reassignments and cross-season overrides.
    /// </summary>
    /// <param name="ctx">Series context containing file data and title information.</param>
    /// <param name="seasonNum">Season number whose episodes should be returned.</param>
    /// <returns>An ordered list of episode metadata objects for the season.</returns>
    private List<object> BuildEpisodeList(SeriesContext ctx, int seasonNum)
    {
        var items = new List<(PlexCoords Coords, object Meta)>();

        foreach (var m in ctx.FileData.GetForSeason(seasonNum))
        {
            if (m.Episodes.Count == 1)
            {
                items.Add((m.Coords, _mapper.MapEpisode(m.PrimaryEpisode, m.Coords, ctx.Series, ctx.Titles, m.PartIndex, m.TmdbEpisode)));
                continue;
            }

            foreach (var ep in m.Episodes)
            {
                var coordsEp = GetPlexCoordinates(ep);
                if (coordsEp.Season != seasonNum)
                {
                    // If the episode is originally 'Other' but this file mapping was reassigned into this season, apply that season to the episode so metadata matches the VFS placement.
                    if (coordsEp.Season == PlexConstants.SeasonOther && m.Coords.Season == seasonNum)
                    {
                        coordsEp = new PlexCoords
                        {
                            Season = m.Coords.Season,
                            Episode = coordsEp.Episode,
                            EndEpisode = coordsEp.EndEpisode,
                        };
                    }
                    else
                    {
                        continue;
                    }
                }

                items.Add((coordsEp, _mapper.MapEpisode(ep, coordsEp, ctx.Series, ctx.Titles)));
            }
        }

        return items.OrderBy(x => x.Coords.Episode).Select(x => x.Meta).ToList();
    }

    /// <summary>
    /// Map a file extension to its MIME content type for collection poster images. Returns <c>null</c> for unsupported types.
    /// </summary>
    /// <param name="ext">File extension including the leading dot (e.g. <c>.jpg</c>).</param>
    /// <returns>The MIME type string, or <c>null</c> if the extension is not recognized.</returns>
    private static string? GetCollectionContentTypeForExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return null;
        return ext.ToLowerInvariant() switch
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

    #endregion

    #region Plex: Authentication

    /// <summary>
    /// De-duplicate discovered Plex library entries from multiple servers and return a clean list of <see cref="PlexAvailableLibrary"/> objects.
    /// </summary>
    /// <param name="pairs">Tuples of library info and server info returned by Plex discovery.</param>
    /// <returns>De-duplicated list of available libraries.</returns>
    private static List<PlexAvailableLibrary> CollectDiscoveredLibraries(IEnumerable<(PlexLibraryInfo, PlexServerInfo)>? pairs)
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

            var uuidVal = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : key;
            collected.Add(
                new PlexAvailableLibrary
                {
                    Id = lib.Id,
                    Title = lib.Title,
                    Type = lib.Type,
                    Agent = lib.Agent,
                    Uuid = uuidVal,
                    ServerId = srv.Id,
                    ServerName = srv.Name,
                    ServerUrl = srv.PreferredUri ?? string.Empty,
                }
            );
        }

        return collected;
    }

    /// <summary>
    /// Persist discovered Plex servers and libraries from a discovery result into the token file.
    /// </summary>
    private void PersistDiscoveryResults((bool TokenValid, List<PlexServerInfo> Servers, List<(PlexLibraryInfo Library, PlexServerInfo Server)> ShokoLibraries) discovery)
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

    #region Plex: Automation

    /// <summary>
    /// Combined guard for Plex automation endpoints: checks Plex is enabled, validates filter/seriesId, and resolves the series list.
    /// Returns a non-null <see cref="IActionResult"/> if validation fails (caller should return it immediately).
    /// </summary>
    private IActionResult? ValidatePlexFilterRequest(int? seriesId, string? filter, out List<IShokoSeries?> seriesList, out List<int> filterIds)
    {
        seriesList = new List<IShokoSeries?>();
        filterIds = new List<int>();

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

    /// <summary>
    /// Return a no-op success response when no Plex library targets are configured, indicating that all series were skipped.
    /// </summary>
    /// <param name="seriesList">Series that would have been processed.</param>
    /// <returns>An OK result with all counts zeroed except <c>processed</c> and <c>skipped</c>.</returns>
    private IActionResult NoPlexTargetsResponse(IEnumerable<IShokoSeries?> seriesList)
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

    #region Plex: Webhook

    /// <summary>
    /// Safely extracts and deserializes the JSON payload from the current Request,
    /// supporting both multipart form-data and raw JSON bodies.
    /// </summary>
    /// <returns>A deserialized <see cref="PlexWebhookPayload"/>, or <c>null</c> if the payload is missing or invalid.</returns>
    private async Task<PlexWebhookPayload?> ExtractPlexWebhookPayloadAsync()
    {
        string? payloadJson = null;

        if (Request.HasFormContentType && Request.Form.ContainsKey("payload"))
        {
            payloadJson = Request.Form["payload"].ToString();
        }
        else
        {
            using var sr = new StreamReader(Request.Body);
            payloadJson = await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PlexWebhookPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates whether the user identified in the Plex webhook is permitted to sync data based on the server owner status and the <c>ExtraPlexUsers</c> configuration.
    /// This also ensures that the server the event is from is one that is actually using Shoko Relay.
    /// </summary>
    /// <param name="evt">The deserialized Plex webhook payload.</param>
    /// <param name="cfg">The current relay configuration.</param>
    /// <returns><c>true</c> if the user is the server owner or is listed in the allowed extra users; otherwise, <c>false</c>.</returns>
    private async Task<(bool Allowed, string Reason)> ValidateWebhookSource(PlexWebhookPayload evt, RelayConfig cfg, CancellationToken ct)
    {
        if (!_configProvider.IsManagedServer(evt.Server?.Uuid))
            return (false, "unrecognized_server_uuid");

        var plexUser = evt.Account?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(plexUser))
            return (false, "empty_account_title");

        // Check Extra Users list
        var extraEntries = _configProvider.GetExtraPlexUserEntries();
        if (extraEntries.Any(e => string.Equals(e.Name, plexUser, StringComparison.OrdinalIgnoreCase)))
            return (true, "allowed_extra_user");

        // Handle Admin/Owner logic
        if (evt.Owner == true)
        {
            string? adminName = _configProvider.GetAdminUsername();

            // If we don't have the admin name yet, fetch it once
            if (string.IsNullOrEmpty(adminName))
            {
                await _configProvider.RefreshAdminUsername(_plexAuth, ct);
                adminName = _configProvider.GetAdminUsername();
            }

            // If we STILL don't have the admin name (API failure), and Exclude Admin is OFF, we have to assume the 'Owner' is the admin.
            if (string.IsNullOrEmpty(adminName))
            {
                if (cfg.ShokoSyncWatchedExcludeAdmin)
                    return (false, "admin_excluded_identity_unknown");

                return (true, "allowed_owner_identity_assumed");
            }

            // If we know the admin name, do a strict check
            if (string.Equals(plexUser, adminName, StringComparison.OrdinalIgnoreCase))
            {
                if (cfg.ShokoSyncWatchedExcludeAdmin)
                    return (false, "admin_excluded_by_config");

                return (true, "allowed_admin");
            }

            // If Owner: true but name doesn't match and isn't in Extra Users, it's a random managed user.
            return (false, $"unauthorized_managed_user ({plexUser})");
        }

        return (false, $"user_not_authorized ({plexUser})");
    }

    #endregion

    #region Virtual File System

    /// <summary>
    /// Given an optional <paramref name="seriesId"/> or a set of <paramref name="filterIds"/>, return the corresponding list of series objects.
    /// If neither is provided the full series catalog is returned.
    /// </summary>
    /// <param name="seriesId">Single series override.</param>
    /// <param name="filterIds">Collection of series ids from a filter.</param>
    private List<IShokoSeries?> ResolveSeriesList(int? seriesId, IReadOnlyCollection<int> filterIds)
    {
        if (seriesId.HasValue)
            return new List<IShokoSeries?> { _metadataService.GetShokoSeriesByID(seriesId.Value) };

        if (filterIds.Count > 0)
            return filterIds.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)).ToList();

        return _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>().ToList();
    }

    #endregion

    #region Shoko: Automation

    /// <summary>
    /// Executes the removal of missing files from Shoko and generates a report log.
    /// </summary>
    /// <param name="dryRun">If true, prevents actual deletion and only returns a summary.</param>
    /// <returns>
    /// A tuple containing the formatted data object on success, or an error message if the service is unavailable.
    /// </returns>
    private async Task<(object? Data, string? Error)> PerformRemoveMissingFilesAsync(bool? dryRun)
    {
        if (_shokoImportService == null)
            return (null, "Import service not available.");

        bool doDry = dryRun ?? true;
        var removed = await _shokoImportService.RemoveMissingFilesAsync(true, doDry).ConfigureAwait(false);

        // Centralized log writing logic
        WriteReportLog("remove-missing-report.log", sb => LogHelper.BuildRemoveMissingReport(sb, doDry, removed));

        return (
            new
            {
                status = "ok",
                dryRun = doDry,
                removed,
                count = removed?.Count ?? 0,
                logUrl = $"{ApiBase}/logs/remove-missing-report.log",
            },
            null
        );
    }

    /// <summary>
    /// Triggers a Shoko import scan and optionally resets the automation schedule.
    /// </summary>
    /// <param name="markSchedule">If true, updates the ShokoRelay last-run timestamp.</param>
    /// <returns>
    /// A tuple containing the scan results on success, or an error message if the service is unavailable.
    /// </returns>
    private async Task<(object? Data, string? Error)> PerformShokoImportAsync(bool markSchedule)
    {
        if (_shokoImportService == null)
            return (null, "Import service not available.");

        var scanned = await _shokoImportService.TriggerImportAsync().ConfigureAwait(false);

        if (markSchedule)
            ShokoRelay.MarkImportRunNow();

        return (
            new
            {
                status = "ok",
                scanned,
                scannedCount = scanned?.Count ?? 0,
            },
            null
        );
    }

    #endregion

    #region Sync Watched

    /// <summary>
    /// Parse a nullable string query parameter into a boolean dry-run flag. Returns a <see cref="BadRequestResult"/> if the value is invalid.
    /// Defaults to <c>true</c> (safe dry-run) when the parameter is omitted.
    /// </summary>
    private static (bool Parsed, IActionResult? Error) ParseDryRunParam(string? dryRun)
    {
        if (string.IsNullOrWhiteSpace(dryRun))
            return (true, null);

        var v = dryRun.Trim();
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            return (true, null);
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        return (true, null); // treat unrecognized as dry-run for safety; alternatively return error below
    }

    /// <summary>
    /// Fire-and-forget helper that triggers a Plex section refresh for every VFS root path belonging to the supplied series.
    /// </summary>
    /// <param name="series">Series whose VFS root paths should be refreshed in Plex.</param>
    /// <returns>A task that completes when all refresh requests have been issued.</returns>
    private Task SchedulePlexRefreshForSeriesAsync(IEnumerable<IShokoSeries> series)
    {
        return Task.Run(async () =>
        {
            foreach (var s in series)
            {
                try
                {
                    var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    string rootName = VfsShared.ResolveRootFolderName();

                    var fileData = GetSeriesFileData(s);
                    foreach (var mapping in fileData.Mappings)
                    {
                        var location = mapping.Video.Files.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path)) ?? mapping.Video.Files.FirstOrDefault();
                        if (location == null)
                            continue;

                        string? importRoot = VfsShared.ResolveImportRootPath(location);
                        if (string.IsNullOrWhiteSpace(importRoot))
                            continue;

                        string seriesPath = Path.Combine(importRoot, rootName, s.ID.ToString());
                        roots.Add(seriesPath);
                    }

                    foreach (var path in roots)
                    {
                        bool ok = await _plexLibrary.RefreshSectionPathAsync(path).ConfigureAwait(false);
                        if (ok)
                            Logger.Info("Triggered Plex refresh for path {Path} (series id {SeriesId})", path, s.ID);
                        else
                            Logger.Warn("Plex refresh failed for path {Path} (series id {SeriesId})", path, s.ID);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Manual VFS scan trigger failed for series {SeriesId}", s?.ID);
                }
            }
        });
    }

    #endregion

    #region AnimeThemes

    /// <summary>
    /// Read ID3v2 text frames from an MP3 file and return them as a dictionary keyed by frame ID (e.g. TIT2, TPE1, TALB). Only reads the first 64 KB to avoid scanning large files.
    /// </summary>
    private static Dictionary<string, string> ReadId3v2Tags(string filePath)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> header = stackalloc byte[10];
        if (fs.Read(header) < 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return tags;

        int tagSize = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        int maxRead = Math.Min(tagSize, 65536);
        byte[] data = new byte[maxRead];
        int read = fs.Read(data, 0, maxRead);

        int pos = 0;
        while (pos + 10 <= read)
        {
            string frameId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            if (frameId[0] == '\0')
                break;
            int frameSize = (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
            if (frameSize <= 0 || pos + 10 + frameSize > read)
                break;

            // Text frames start with T (except TXXX)
            if (frameId.StartsWith('T') && frameId != "TXXX" && frameSize > 1)
            {
                byte encoding = data[pos + 10];
                string value = encoding switch
                {
                    1 => System.Text.Encoding.Unicode.GetString(data, pos + 11, frameSize - 1).TrimEnd('\0'),
                    2 => System.Text.Encoding.BigEndianUnicode.GetString(data, pos + 11, frameSize - 1).TrimEnd('\0'),
                    3 => System.Text.Encoding.UTF8.GetString(data, pos + 11, frameSize - 1).TrimEnd('\0'),
                    _ => System.Text.Encoding.Latin1.GetString(data, pos + 11, frameSize - 1).TrimEnd('\0'),
                };
                // Strip BOM if present
                if (value.Length > 0 && value[0] == '\uFEFF')
                    value = value[1..];
                tags[frameId] = value;
            }
            pos += 10 + frameSize;
        }
        return tags;
    }

    #endregion
}
