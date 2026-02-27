using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers
{
    public sealed class TagItem
    {
        public string tag { get; init; } = "";
    }

    /// <summary>
    /// Helper utilities for generating cast, crew and studio tag information suitable for Plex metadata responses.
    /// </summary>
    public static class CastHelper
    {
        private static string? GetName(ICreator? creator, string fallback) => creator?.Name ?? fallback;

        /// <summary>
        /// Return an array of objects representing cast (and optionally crew) for the specified media item. The crew portion is included based on the <c>CrewListings</c> setting.
        /// </summary>
        /// <param name="item">Item implementing <see cref="IWithCastAndCrew"/>.</param>
        public static object[] GetCastAndCrew(IWithCastAndCrew item)
        {
            int order = 1;

            var cast =
                item.Cast?.Select(c =>
                        (object)
                            new
                            {
                                order = order++,
                                tag = GetName(c.Creator, c.Name),
                                role = c.Character?.Name ?? c.Name,
                                thumb = c.Creator?.PortraitImage is { } img ? ImageHelper.GetImageUrl(img, "Staff") : null,
                            }
                    )
                    .ToArray()
                ?? Array.Empty<object>();

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
                ?? Array.Empty<object>();

            return cast.Concat(crew).ToArray();
        }

        /// <summary>
        /// Return director credits for the given item.
        /// </summary>
        /// <param name="item">Item with cast/crew data.</param>
        public static object[] GetDirectors(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Director);

        /// <summary>
        /// Retrieve writing credits (series composers or source work) for the item.
        /// </summary>
        /// <param name="item">Source item.</param>
        public static object[] GetWriters(IWithCastAndCrew item) =>
            item.Crew?.Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray() ?? Array.Empty<object>();

        /// <summary>
        /// Retrieve producer credits for the given item.
        /// </summary>
        /// <param name="item">Item with cast/crew data.</param>
        public static object[] GetProducers(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Producer);

        /// <summary>
        /// Assemble an array of <see cref="TagItem"/> objects representing the series' studios.
        /// </summary>
        /// <param name="series">Series metadata.</param>
        public static TagItem[] GetStudioTags(ISeries series) =>
            series.Studios?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new TagItem { tag = name }).ToArray()
            ?? Array.Empty<TagItem>();

        /// <summary>
        /// Return the first studio name associated with a series, or <c>null</c> if none exist.
        /// </summary>
        /// <param name="series">Series metadata.</param>
        public static string? GetStudio(ISeries series) => series.Studios?.FirstOrDefault()?.Name;

        private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType) =>
            item.Crew?.Where(c => c.RoleType == roleType).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray() ?? Array.Empty<object>();
    }
}
