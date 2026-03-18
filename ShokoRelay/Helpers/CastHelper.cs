using Newtonsoft.Json;
using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers;

#region Data Models

/// <summary>Represents a metadata tag item for Plex responses.</summary>
public sealed class TagItem
{
    /// <summary>The display string for the tag.</summary>
    [JsonProperty("tag")]
    public string Tag { get; init; } = "";
}

#endregion

/// <summary>Helper utilities for generating cast, crew and studio tag information suitable for Plex metadata responses.</summary>
public static class CastHelper
{
    #region Cast and Crew Logic

    /// <summary>Return an array of objects representing cast (and optionally crew) for the specified media item. The crew portion is included based on the <c>CrewListings</c> setting.</summary>
    /// <param name="item">Item implementing <see cref="IWithCastAndCrew"/>.</param>
    /// <returns>An array of anonymous cast/crew objects suitable for Plex metadata responses.</returns>
    public static object[] GetCastAndCrew(IWithCastAndCrew item)
    {
        int order = 1;

        var cast =
            item.Cast?.Select(c => new
                {
                    order = order++,
                    tag = GetName(c.Creator, c.Name),
                    role = c.Character?.Name ?? c.Name,
                    thumb = c.Creator?.PortraitImage is { } img ? ImageHelper.GetImageUrl(img, "Staff") : null,
                })
                // Filter out non-person entries (characters, vehicles, mecha, etc.) where the actor name matches the role name.
                .Where(c => !string.Equals(c.tag, c.role, StringComparison.Ordinal))
                .Cast<object>()
                .ToArray()
            ?? [];

        if (!ShokoRelay.Settings.CrewListings)
            return cast;

        var crew =
            item.Crew?.Select(c =>
                    (object)
                        new
                        {
                            order = order++,
                            tag = GetName(c.Creator, c.Name),
                            role = c.RoleType == CrewRoleType.Music ? "Composer" : c.Name,
                            thumb = c.Creator?.PortraitImage is { } img ? ImageHelper.GetImageUrl(img, "Staff") : null,
                        }
                )
                .ToArray()
            ?? [];

        return [.. cast, .. crew];
    }

    /// <summary>Return director credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing director names.</returns>
    public static object[] GetDirectors(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Director);

    /// <summary>Retrieve writing credits (series composers or source work) for the item.</summary>
    /// <param name="item">Source item.</param>
    /// <returns>An array of anonymous objects containing writer names.</returns>
    public static object[] GetWriters(IWithCastAndCrew item) =>
        item.Crew?.Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray() ?? [];

    /// <summary>Retrieve producer credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing producer names.</returns>
    public static object[] GetProducers(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Producer);

    #endregion

    #region Studio Logic

    /// <summary>Assemble an array of <see cref="TagItem"/> objects representing the series' studios.</summary>
    /// <param name="series">Series metadata.</param>
    /// <returns>An array of <see cref="TagItem"/> instances, one per distinct studio.</returns>
    public static TagItem[] GetStudioTags(ISeries series) =>
        series.Studios?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new TagItem { Tag = name }).ToArray() ?? [];

    /// <summary>Return the first studio name associated with a series, or <c>null</c> if none exist.</summary>
    /// <param name="series">Series metadata.</param>
    /// <returns>The primary studio name, or null if not found.</returns>
    public static string? GetStudio(ISeries series) => series.Studios?.FirstOrDefault()?.Name;

    #endregion

    #region Internal Helpers

    private static string? GetName(ICreator? creator, string fallback) => creator?.Name ?? fallback;

    private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType) =>
        item.Crew?.Where(c => c.RoleType == roleType).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray() ?? [];

    #endregion
}
