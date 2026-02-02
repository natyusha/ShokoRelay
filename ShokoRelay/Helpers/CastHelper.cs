using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.Enums;

namespace ShokoRelay.Helpers
{
    public static class CastHelper
    {
        public static object[] GetCastAndCrew(IWithCastAndCrew item, string apiBaseUrl)
        {
            int order = 1;

            var cast = item.Cast?.Select(c => new
            {
                order = order++,
                tag = c.Creator?.Name ?? c.Name,
                role = c.Character?.Name ?? c.Name,
                thumb = GetStaffPortraitUrl(c.Creator, apiBaseUrl)
            }) ?? Enumerable.Empty<object>();

            if (!ShokoRelay.Settings.CrewListings)
                return cast.ToArray();

            var crew = item.Crew?.Select(c => new
            {
                order = order++,
                tag = c.Creator?.Name ?? c.Name,
                role = c.RoleType == CrewRoleType.Music ? "Composer" : c.Name,
                thumb = GetStaffPortraitUrl(c.Creator, apiBaseUrl)
            }) ?? Enumerable.Empty<object>();

            return cast.Concat(crew).ToArray();
        }

        public static object[] GetDirectors(IWithCastAndCrew item)
            => FilterCrew(item, CrewRoleType.Director);

        public static object[] GetWriters(IWithCastAndCrew item)
            => item.Crew?
                .Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork)
                .Select(c => new { tag = c.Creator?.Name ?? c.Name })
                .ToArray<object>() ?? [];

        public static object[] GetProducers(IWithCastAndCrew item)
            => FilterCrew(item, CrewRoleType.Producer);

        public static object[] GetStudioTags(ISeries series)
            => series.Studios?
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new { tag = name })
                .ToArray<object>() ?? [];

        public static string? GetStudio(ISeries series)
            => series.Studios?.FirstOrDefault()?.Name;

        private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType)
            => item.Crew?
                .Where(c => c.RoleType == roleType)
                .Select(c => new { tag = c.Creator?.Name ?? c.Name })
                .ToArray<object>() ?? [];

        private static string? GetStaffPortraitUrl(ICreator? creator, string apiBaseUrl)
        {
            var image = creator?.PortraitImage;
            return image != null ? $"{apiBaseUrl}/api/v3/Image/{image.Source}/Staff/{image.ID}" : null;
        }
    }
}