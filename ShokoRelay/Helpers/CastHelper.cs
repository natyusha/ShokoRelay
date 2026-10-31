using System.Collections.Frozen;
using Newtonsoft.Json;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;

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
    #region Static Configuration

    // csharpier-ignore
    /// <summary>TMDB crew departments used to strip category prefixes from role names.</summary>
    private static readonly FrozenSet<string> s_tmdbDepartments = new[] {
        "Production", "Costume & Make-Up", "Lighting", "Actors", "Crew", "Directing", "Writing", "Editing", "Sound", "Visual Effects", "Camera", "Art"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Cast and Crew Logic

    /// <summary>Return an array of objects representing cast (and optionally crew) for the specified media item. The crew portion is included based on the <c>CrewListings</c> setting.</summary>
    /// <param name="item">Item implementing <see cref="IWithCastAndCrew"/>.</param>
    /// <returns>An array of anonymous cast/crew objects suitable for Plex metadata responses.</returns>
    public static object[] GetCastAndCrew(IWithCastAndCrew item)
    {
        int order = 1;
        var finalRoles = new List<object>();

        void ProcessRoleGroup<T>(IEnumerable<T> roles, Func<T, ICreator?> getCreator, Func<T, string?> getName, Func<T, string?> getRole)
            where T : class
        {
            var allRoles = roles.ToList();
            if (allRoles.Count == 0)
                return;

            var tmdbRoles = allRoles.Where(c => getCreator(c)?.ID.Source == MetadataSource.TMDB).ToList();
            var anidbRoles = allRoles.Where(c => getCreator(c) == null || getCreator(c)!.ID.Source != MetadataSource.TMDB).ToList();

            if (tmdbRoles.Count == 0)
            {
                foreach (var c in anidbRoles)
                {
                    string? tag = GetName(getCreator(c), getName(c));
                    string? role = getRole(c);
                    if (string.Equals(tag, role, StringComparison.Ordinal))
                        continue;

                    var thumbImg = getCreator(c)?.PrimaryImage;
                    finalRoles.Add(
                        new
                        {
                            order = order++,
                            tag,
                            role,
                            thumb = thumbImg != null ? ImageHelper.GetImageUrl(thumbImg, forceRemote: true) : null,
                        }
                    );
                }
                return;
            }

            var index = new TmdbRoleIndex<T>(tmdbRoles, getCreator, getName);
            var matchedTmdb = new HashSet<T>();

            foreach (var c in anidbRoles)
            {
                string? tag = GetName(getCreator(c), getName(c));
                string? role = getRole(c);

                // Filter out non-person entries (characters, vehicles, mecha, etc.) where the actor name matches the role name.
                if (string.Equals(tag, role, StringComparison.Ordinal))
                    continue;

                var match = index.FindMatch(getCreator(c), getName(c) ?? "");
                var thumbImg = getCreator(c)?.PrimaryImage;

                if (match != null)
                {
                    matchedTmdb.Add(match);
                    if (Settings.TmdbCastImages && getCreator(match)?.PrimaryImage != null)
                        thumbImg = getCreator(match)!.PrimaryImage;
                }

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role,
                        thumb = thumbImg != null ? ImageHelper.GetImageUrl(thumbImg, forceRemote: true) : null,
                    }
                );
            }

            foreach (var c in tmdbRoles)
            {
                if (matchedTmdb.Contains(c))
                    continue;

                string? tag = GetName(getCreator(c), getName(c));
                string? role = getRole(c);

                if (string.Equals(tag, role, StringComparison.Ordinal))
                    continue;

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role,
                        thumb = getCreator(c)?.PrimaryImage is { } img ? ImageHelper.GetImageUrl(img, forceRemote: true) : null,
                    }
                );
            }
        }

        if (item.Cast != null)
            ProcessRoleGroup(item.Cast, c => c.Creator, c => c.Name, c => c.Character?.Title ?? c.Name);

        if (Settings.CrewListings && item.Crew != null)
            foreach (var group in item.Crew.GroupBy(c => c.RoleType))
                ProcessRoleGroup(group, c => c.Creator, c => c.Name, c => c.RoleType == CrewRoleType.Music ? "Composer" : CleanCrewRole(c.Name));

        return [.. finalRoles];
    }

    /// <summary>Return director credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing director names.</returns>
    public static object[] GetDirectors(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Director);

    /// <summary>Retrieve writing credits (series composers or source work) for the item.</summary>
    /// <param name="item">Source item.</param>
    /// <returns>An array of anonymous objects containing writer names.</returns>
    public static object[] GetWriters(IWithCastAndCrew item) =>
        [
            .. DeduplicateCrew(item.Crew?.Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork))
                .Select(c => GetName(c.Creator, c.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (object)new { tag = name }),
        ];

    /// <summary>Retrieve producer credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing producer names.</returns>
    public static object[] GetProducers(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Producer);

    #endregion

    #region Studio Logic

    /// <summary>Assemble an array of <see cref="TagItem"/> objects representing the series' studios.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of <see cref="TagItem"/> instances, one per distinct studio.</returns>
    public static TagItem[] GetStudioTags(ISeries series) =>
        series
            .Studios?.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .OrderBy(s => s.ID.Source == MetadataSource.TMDB ? 1 : 0)
            .DistinctBy(s => s.Name.Contains(' ') ? s.Name.Replace(" ", "", StringComparison.Ordinal) : s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new TagItem { Tag = s.Name })
            .ToArray()
        ?? [];

    #endregion

    #region Internal Helpers

    /// <summary>Resolves the creator's name with a fallback value.</summary>
    /// <param name="creator">The creator metadata object.</param>
    /// <param name="fallback">The fallback name.</param>
    /// <returns>The resolved name string.</returns>
    private static string? GetName(ICreator? creator, string? fallback) => creator?.Title ?? fallback;

    /// <summary>Flips a two-part name (Given Family to Family Given) with zero allocations using memory spans.</summary>
    /// <param name="name">The raw name string.</param>
    /// <returns>The flipped name or null if unparseable.</returns>
    private static string? GetFlippedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        int spaceIdx = name.IndexOf(' ');
        return (spaceIdx <= 0 || spaceIdx != name.LastIndexOf(' ')) ? null : string.Concat(name.AsSpan(spaceIdx + 1), " ", name.AsSpan(0, spaceIdx));
    }

    /// <summary>Strips department category prefixes from TMDB crew role names.</summary>
    /// <param name="role">The raw crew role string.</param>
    /// <returns>The cleaned role string.</returns>
    private static string CleanCrewRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "";
        int commaIdx = role.IndexOf(',');
        return commaIdx > 0 && s_tmdbDepartments.Contains(role[..commaIdx].Trim()) ? role[(commaIdx + 1)..].Trim() : role.Trim();
    }

    /// <summary>Filters the crew by a specific role type and returns a formatted array of tags.</summary>
    /// <param name="item">The media item containing cast and crew.</param>
    /// <param name="roleType">The role type to filter for.</param>
    /// <returns>An array of anonymous objects containing crew tags.</returns>
    private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType) =>
        [
            .. DeduplicateCrew(item.Crew?.Where(c => c.RoleType == roleType))
                .Select(c => GetName(c.Creator, c.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (object)new { tag = name }),
        ];

    /// <summary>Deduplicates a collection of crew members per role type, preferring AniDB entries over matching TMDB entries.</summary>
    /// <param name="crewItems">The raw collection of crew items.</param>
    /// <returns>A deduplicated collection of crew items.</returns>
    private static IEnumerable<ICrew> DeduplicateCrew(IEnumerable<ICrew>? crewItems)
    {
        if (crewItems == null)
            yield break;

        foreach (var group in crewItems.GroupBy(c => c.RoleType))
        {
            var tmdb = group.Where(c => c.Creator?.ID.Source == MetadataSource.TMDB).ToList();
            var anidb = group.Where(c => c.Creator == null || c.Creator.ID.Source != MetadataSource.TMDB);

            if (tmdb.Count == 0)
            {
                foreach (var c in anidb)
                    yield return c;
                continue;
            }

            var index = new TmdbRoleIndex<ICrew>(tmdb, t => t.Creator, t => t.Name);
            var matchedTmdbEntries = new HashSet<ICrew>();

            foreach (var c in anidb)
            {
                yield return c;
                var match = index.FindMatch(c.Creator, c.Name ?? "");
                if (match != null)
                    matchedTmdbEntries.Add(match);
            }

            foreach (var c in tmdb)
                if (!matchedTmdbEntries.Contains(c))
                    yield return c;
        }
    }

    /// <summary>High-performance lookup index for matching AniDB cast and crew against TMDB records in O(1) time.</summary>
    private sealed class TmdbRoleIndex<T>
        where T : class
    {
        /// <summary>Lookup index matching exact TMDB creator original titles without whitespace.</summary>
        private readonly Dictionary<string, T> _exactOriginal = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator alternative titles without whitespace.</summary>
        private readonly Dictionary<string, T> _altOriginal = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator primary and flipped names.</summary>
        private readonly Dictionary<string, T> _names = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup index matching TMDB creator alternative names and their flipped variants.</summary>
        private readonly Dictionary<string, T> _altNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new lookup index by indexing all provided TMDB cast or crew entries.</summary>
        /// <param name="tmdbItems">Collection of TMDB role entries to index.</param>
        /// <param name="getCreator">Accessor function to retrieve the creator from a role entry.</param>
        /// <param name="getName">Accessor function to retrieve the fallback name from a role entry.</param>
        public TmdbRoleIndex(IEnumerable<T> tmdbItems, Func<T, ICreator?> getCreator, Func<T, string?> getName)
        {
            foreach (var item in tmdbItems)
            {
                var creator = getCreator(item);
                string? name = creator?.Title ?? getName(item);

                // Index Original Title without spaces
                string? orig = creator?.OriginalTitle?.Value;
                if (!string.IsNullOrEmpty(orig))
                    _exactOriginal.TryAdd(orig.Contains(' ') ? orig.Replace(" ", "", StringComparison.Ordinal) : orig, item);

                // Index Primary and Flipped Names
                if (!string.IsNullOrEmpty(name))
                {
                    _names.TryAdd(name, item);
                    if (GetFlippedName(name) is { } flipped)
                        _names.TryAdd(flipped, item);
                }

                // Index Alternative Titles / Aliases
                if (creator?.Titles is { Count: > 0 } titles)
                {
                    foreach (var title in titles)
                    {
                        if (string.IsNullOrWhiteSpace(title.Value))
                            continue;

                        string alt = title.Value.Trim();
                        string altNoSpace = alt.Replace(" ", "", StringComparison.Ordinal);
                        _altOriginal.TryAdd(altNoSpace, item);

                        _altNames.TryAdd(alt, item);
                        if (GetFlippedName(alt) is { } altFlipped)
                            _altNames.TryAdd(altFlipped, item);
                    }
                }
            }
        }

        /// <summary>Finds the best matching TMDB role entry for a given AniDB creator and name.</summary>
        /// <param name="anidbCreator">The AniDB creator entity, if available.</param>
        /// <param name="anidbName">The fallback AniDB creator or character name.</param>
        /// <returns>The matching TMDB entry, or null if no match is found.</returns>
        public T? FindMatch(ICreator? anidbCreator, string anidbName)
        {
            string? orig = anidbCreator?.OriginalTitle?.Value;
            string? origNoSpace = (!string.IsNullOrEmpty(orig) && orig.Contains(' ')) ? orig.Replace(" ", "", StringComparison.Ordinal) : orig;
            string name = anidbCreator?.Title ?? anidbName;
            string? flippedName = GetFlippedName(name);

            // OriginalName matches OriginalName (whitespace stripped)
            if (!string.IsNullOrEmpty(origNoSpace) && _exactOriginal.TryGetValue(origNoSpace, out var m1))
                return m1;

            // OriginalName matches TMDB AlternativeNames (whitespace stripped)
            if (!string.IsNullOrEmpty(origNoSpace) && _altOriginal.TryGetValue(origNoSpace, out var m2))
                return m2;

            // Name matches Name (romanised)
            if (!string.IsNullOrEmpty(name) && _names.TryGetValue(name, out var m3))
                return m3;

            // Flipped Name matches Name (romanised)
            if (flippedName != null && _names.TryGetValue(flippedName, out var m4))
                return m4;

            // AniDB Name matches TMDB AlternativeNames
            if (!string.IsNullOrEmpty(name) && _altNames.TryGetValue(name, out var m5))
                return m5;

            // Flipped AniDB Name matches TMDB AlternativeNames (and their flipped variants)
            return flippedName != null && _altNames.TryGetValue(flippedName, out var m6) ? m6 : null;
        }
    }

    #endregion
}
