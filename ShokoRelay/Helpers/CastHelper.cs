using Shoko.Abstractions.Enums;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers
{
    public sealed class TagItem
    {
        public string tag { get; init; } = "";
    }

    public static class CastHelper
    {
        private static string? GetName(ICreator? creator, string fallback) => creator?.Name ?? fallback;

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

        public static object[] GetDirectors(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Director);

        public static object[] GetWriters(IWithCastAndCrew item) =>
            item.Crew?.Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray()
            ?? Array.Empty<object>();

        public static object[] GetProducers(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Producer);

        public static TagItem[] GetStudioTags(ISeries series) =>
            series.Studios?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new TagItem { tag = name }).ToArray()
            ?? Array.Empty<TagItem>();

        public static string? GetStudio(ISeries series) => series.Studios?.FirstOrDefault()?.Name;

        private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType) =>
            item.Crew?.Where(c => c.RoleType == roleType).Select(c => (object)new { tag = GetName(c.Creator, c.Name) }).ToArray() ?? Array.Empty<object>();
    }
}
