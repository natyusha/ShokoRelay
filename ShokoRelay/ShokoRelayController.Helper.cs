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
    public record PlexMatchBody(string? Filename, string? Title = null, int? Manual = null);

    public record SeriesContext(ISeries Series, string ApiUrl, (string DisplayTitle, string SortTitle, string? OriginalTitle) Titles, string ContentRating, MapHelper.SeriesFileData FileData);

    public partial class ShokoRelayController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private SeriesContext? GetSeriesContext(string ratingKey)
        {
            int seriesId;

            if (ratingKey.StartsWith(PlexConstants.EpisodePrefix))
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

            return new SeriesContext(series, BaseUrl, TextHelper.ResolveFullSeriesTitles(series), RatingHelper.GetContentRatingAndAdult(series).Rating ?? "", GetSeriesFileData(series));
        }

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

        private IActionResult EmptyMatch() => Ok(new { MediaContainer = new { size = 0, Metadata = Array.Empty<object>() } });

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

        private List<IShokoSeries?> ResolveSeriesList(int? seriesId, IReadOnlyCollection<int> filterIds)
        {
            if (seriesId.HasValue)
                return new List<IShokoSeries?> { _metadataService.GetShokoSeriesByID(seriesId.Value) };

            if (filterIds.Count > 0)
                return filterIds.Distinct().Select(id => _metadataService.GetShokoSeriesByID(id)).ToList();

            return _metadataService.GetAllShokoSeries().Cast<IShokoSeries?>().ToList();
        }

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
                    var values = Enum.GetValues(propType).Cast<object>().Select(v => new { name = Enum.GetName(propType, v) ?? v.ToString(), value = Convert.ToInt32(v) }).ToArray();

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

        private static string? LoadControllerTemplate()
        {
            string baseDir = AppContext.BaseDirectory;

            // Load the dashboard template from the 'dashboard' folder in the application output (lowercase).
            string candidate = Path.Combine(baseDir, "dashboard", ControllerPageFileName);
            if (!System.IO.File.Exists(candidate))
                return null;

            return System.IO.File.ReadAllText(candidate);
        }

        private RelayConfig EnsurePlexAuthConfig()
        {
            var settings = _configProvider.GetSettings();
            var plexAuth = settings.PlexAuth;
            bool changed = false;

            if (string.IsNullOrWhiteSpace(plexAuth.ClientIdentifier))
            {
                plexAuth.ClientIdentifier = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (changed)
                _configProvider.SaveSettings(settings);

            return settings;
        }

        private string EnsurePlexClientIdentifier(RelayConfig settings)
        {
            string clientIdentifier = settings.PlexLibrary.ClientIdentifier;
            if (string.IsNullOrWhiteSpace(clientIdentifier))
                clientIdentifier = settings.PlexAuth.ClientIdentifier;

            if (string.IsNullOrWhiteSpace(clientIdentifier))
            {
                clientIdentifier = Guid.NewGuid().ToString("N");
                settings.PlexAuth.ClientIdentifier = clientIdentifier;
                settings.PlexLibrary.ClientIdentifier = clientIdentifier;
                _configProvider.SaveSettings(settings);
            }

            return clientIdentifier;
        }

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
                        // If the episode is originally 'Other' but this file mapping was reassigned into this season,
                        // apply that season to the episode's coordinates so metadata matches the VFS placement.
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

        // Plex discovery helpers & network access wrappers

        private static List<PlexAvailableLibrary> CollectDiscoveredLibraries(IEnumerable<(PlexLibraryInfo, PlexServerInfo)>? pairs)
        {
            var collected = new List<PlexAvailableLibrary>();
            if (pairs == null)
                return collected;
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (lib, srv) in pairs)
            {
                var key = !string.IsNullOrWhiteSpace(lib.Uuid) ? lib.Uuid : $"{srv.PreferredUri}::{lib.Id}";
                if (seenKeys.Contains(key))
                    continue;
                seenKeys.Add(key);

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

            return null;
        }

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

        // Content types used by the dashboard (only serve the small set of web assets we ship)
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
                _ => null,
            };
        }

        // Content types for collection/poster images (broader image set)
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

        // Plex discovery types & network access moved to PlexAuth
        // Wrapper methods below forward to PlexAuth for discovery and library fetching.

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
