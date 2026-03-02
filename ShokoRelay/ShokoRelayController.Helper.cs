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

namespace ShokoRelay.Controllers
{
    /// <summary>
    /// Represents the body of a Plex matching request. Fields are optional; <see cref="Manual"/> can be used to force a particular series ID.
    /// </summary>
    public record PlexMatchBody(string? Filename, string? Title = null, int? Manual = null);

    /// <summary>
    /// Aggregates relevant information about a Shoko series for controller responses.
    /// Includes the series metadata, API base URL, resolved titles, content rating string, and file data used when building VFS links.
    /// </summary>
    public record SeriesContext(ISeries Series, string ApiUrl, (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles, string ContentRating, SeriesFileData FileData);

    public partial class ShokoRelayController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly System.Text.Json.JsonSerializerOptions _jsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };

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
        /// Return an empty Plex match response (zero results) using the standard <c>MediaContainer</c> format.
        /// </summary>
        private IActionResult EmptyMatch() => Ok(new { MediaContainer = new { size = 0, Metadata = Array.Empty<object>() } });

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

        private sealed record ConfigPropertySchema(string Path, string Type, string? Display, string? Description, object? DefaultValue, object? EnumValues);

        /// <summary>
        /// Map a Plex library type string (e.g. "movie", "show") to its <see cref="PlexLibraryType"/> enum value, defaulting to <see cref="PlexLibraryType.Show"/>.
        /// </summary>
        /// <param name="type">Raw Plex library type string.</param>
        /// <returns>The corresponding <see cref="PlexLibraryType"/>.</returns>
        private static PlexLibraryType MapLibraryType(string? type)
        {
            return type?.Trim().ToLowerInvariant() switch
            {
                "movie" => PlexLibraryType.Movie,
                "show" => PlexLibraryType.Show,
                "artist" => PlexLibraryType.Music,
                "photo" => PlexLibraryType.Photo,
                _ => PlexLibraryType.Show,
            };
        }

        /// <summary>
        /// Attempt to load the dashboard Razor template from the application's <c>dashboard</c> output directory.
        /// </summary>
        /// <returns>The template HTML string, or <c>null</c> if the file does not exist.</returns>
        private static string? LoadControllerTemplate()
        {
            string baseDir = AppContext.BaseDirectory;

            // Load the dashboard template from the 'dashboard' folder in the application output (lowercase).
            string candidate = Path.Combine(baseDir, "dashboard", ControllerPageFileName);
            if (!System.IO.File.Exists(candidate))
                return null;

            return System.IO.File.ReadAllText(candidate);
        }

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

        /// <summary>
        /// Wrapper around <see cref="PlexAuth.GetPlexServerListAsync"/> that catches exceptions and returns empty results on failure.
        /// </summary>
        /// <param name="token">Plex authentication token.</param>
        /// <param name="clientIdentifier">Client identifier for the Plex API.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A tuple indicating token validity, discovered servers and raw devices.</returns>
        private async Task<(bool TokenValid, List<PlexServerInfo> Servers, List<PlexDevice> Devices)> GetPlexServerListAsync(string token, string clientIdentifier, CancellationToken cancellationToken)
        {
            try
            {
                return await _plexAuth.GetPlexServerListAsync(token, clientIdentifier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PlexAuth.GetPlexServerListAsync failed: {ex.Message}");
                return (false, new List<PlexServerInfo>(), new List<PlexDevice>());
            }
        }

        /// <summary>
        /// Wrapper around <see cref="PlexAuth.GetPlexLibrariesAsync"/> that catches exceptions and returns an empty list on failure.
        /// </summary>
        /// <param name="token">Plex authentication token.</param>
        /// <param name="clientIdentifier">Client identifier for the Plex API.</param>
        /// <param name="serverUrl">Base URL of the Plex server to query.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of <see cref="PlexLibraryInfo"/> entries discovered on the server.</returns>
        private async Task<List<PlexLibraryInfo>> GetPlexLibrariesAsync(string token, string clientIdentifier, string serverUrl, CancellationToken cancellationToken)
        {
            try
            {
                return await _plexAuth.GetPlexLibrariesAsync(token, clientIdentifier, serverUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PlexAuth.GetPlexLibrariesAsync failed for {serverUrl}: {ex.Message}");
                return new List<PlexLibraryInfo>();
            }
        }
    }
}
