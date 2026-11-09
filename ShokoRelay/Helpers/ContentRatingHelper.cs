using System.Collections.Frozen;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Containers;

namespace ShokoRelay.Helpers;

/// <summary>Utility functions for deriving content rating and adult flag from series metadata.</summary>
public static class ContentRatingHelper
{
    #region Constants

    // csharpier-ignore
    /// <summary>Tags used to determine Assumed Content Ratings.</summary>
    private static readonly FrozenSet<string> s_ratingTags = new[] {
        "kodomo", "mina", "shoujo", "shounen", "josei", "seinen", "borderline porn", "18 restricted", "nudity", "sex", "violence", "sexual humour"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Static weight thresholds for content rating checks.</summary>
    private static readonly (string Key, int Tv14, int TvMa)[] s_weightedRatingChecks = [("nudity", 400, 500), ("violence", 400, 500), ("sex", 300, 400)];

    #endregion

    #region Content Rating Logic

    /// <summary>Determine a combined rating string and an adult flag for a series or movie based on metadata, tags, and TMDB fallback.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <param name="fallback">Optional entity containing TMDB content ratings (e.g. TMDB series, movie, or season).</param>
    /// <returns>A tuple of (Rating, IsAdult).</returns>
    public static (string? Rating, bool IsAdult) GetContentRatingAndAdult(ISeries? series, IWithContentRatings? fallback = null)
    {
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = (series as IShokoSeries)?.Tags;
        if (tags != null)
        {
            foreach (var t in tags)
            {
                if (t == null)
                    continue;
                var name = t.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (t.GetType().GetProperty("Source")?.GetValue(t) is string srcVal && srcVal.Equals("User", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!s_ratingTags.Contains(name))
                    continue;
                tagSet.Add(name);
            }
        }

        // Build AniDB weight dictionary and populate tagSet for precision-based decisions (defaults to 0 when not present).
        var anidbWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (series is IShokoSeries ss && ss.AnidbAnime?.Tags is IReadOnlyList<IAnidbTagForAnime> atags)
        {
            foreach (var t in atags)
            {
                var name = t?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !s_ratingTags.Contains(name))
                    continue;
                var lower = name.ToLowerInvariant();
                tagSet.Add(lower);
                anidbWeights[lower] = t!.Weight;
            }
        }

        // 18 restricted -> X (adult) immediately
        if (tagSet.Contains("18 restricted"))
            return ("X", true);

        if (Settings.AssumedContentRatings)
        {
            // A rough approximation of: https://www.tvguidelines.org/resources/TheRatings.pdf
            // Uses the content indicators described here: https://wiki.anidb.net/Categories:Content_Indicators
            // Descriptor tags (Suggestive Dialogue, Sexual Situations, Violence)
            var descriptorD = tagSet.Contains("sexual humour") ? "D" : string.Empty;
            var descriptorS = (tagSet.Contains("nudity") || tagSet.Contains("sex")) ? "S" : string.Empty;
            var descriptorV = tagSet.Contains("violence") ? "V" : string.Empty;
            var descriptor = (!string.IsNullOrEmpty(descriptorD) || !string.IsNullOrEmpty(descriptorS) || !string.IsNullOrEmpty(descriptorV)) ? "-" + descriptorD + descriptorS + descriptorV : string.Empty;

            string? c_rating = null;

            // Iterate tags in order, check AniDB weight thresholds
            (string Rating, bool IsAdult)? ApplyWeightedChecks((string Key, int Tv14, int TvMa)[] checks)
            {
                foreach (var (key, tv14, tvma) in checks)
                {
                    if (!tagSet.Contains(key))
                        continue;
                    var w = anidbWeights.TryGetValue(key, out var ww) ? ww : 0;
                    if (w >= tvma)
                        return ("TV-MA" + descriptor, false);
                    if (w >= tv14 && c_rating != "TV-MA")
                        c_rating = "TV-14";
                }
                return null;
            }

            // Borderline porn forces TV-MA
            if (tagSet.Contains("borderline porn"))
                return ("TV-MA" + descriptor, false);

            // Run weighted checks in priority order (nudity, violence, sex). Returns immediately on TV-MA.
            if (ApplyWeightedChecks(s_weightedRatingChecks) is (var finalRating, var finalAdult))
                return (finalRating, finalAdult);

            // Uses the AniDB "target audience" tags to select a baseline rating when no weighted content indicators apply: https://anidb.net/tag/2606/animetb
            // Check higher (more restrictive) ratings first and return immediately when matched.
            if (string.IsNullOrEmpty(c_rating))
            {
                c_rating = tagSet switch
                {
                    _ when tagSet.Contains("josei") || tagSet.Contains("seinen") => "TV-14" + descriptor,
                    _ when tagSet.Contains("shoujo") || tagSet.Contains("shounen") => "TV-PG" + descriptor,
                    _ when tagSet.Contains("mina") => "TV-G" + descriptor,
                    _ when tagSet.Contains("kodomo") => "TV-Y" + descriptor,
                    _ => null,
                };
            }
            else if (c_rating != "X")
                c_rating += descriptor;

            if (!string.IsNullOrEmpty(c_rating))
                return (c_rating, false);
        }

        // Fallback to TMDB content ratings when assumed ratings are disabled or undetermined
        string? tmdbRating = GetTmdbContentRating(series, fallback);
        return !string.IsNullOrWhiteSpace(tmdbRating) ? (tmdbRating, tmdbRating is "X" or "18+" or "R18+") : (null, false);
    }

    /// <summary>Resolves the best available TMDB content rating, prioritizing US ratings before falling back to the first available entry.</summary>
    /// <param name="series">The primary series entity.</param>
    /// <param name="fallback">The optional fallback content rating container.</param>
    /// <returns>The resolved rating string or null.</returns>
    private static string? GetTmdbContentRating(ISeries? series, IWithContentRatings? fallback) =>
        (fallback?.ContentRatings ?? (series as IWithContentRatings)?.ContentRatings ?? (series as IShokoSeries)?.GetLinkedSeries(MetadataSource.TMDB)?.FirstOrDefault()?.ContentRatings)
            is { Count: > 0 } ratings
            ? ratings.FirstOrDefault(r => string.Equals(r.CountryCode, "US", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Value))?.Value
                ?? ratings.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Value))?.Value
            : null;

    #endregion
}
