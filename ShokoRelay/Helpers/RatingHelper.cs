using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;

namespace ShokoRelay.Helpers
{
    public static class RatingHelper
    {
        private static readonly HashSet<string> RatingTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "kodomo",
            "mina",
            "shoujo",
            "shounen",
            "josei",
            "seinen",
            "borderline porn",
            "18 restricted",
            "nudity",
            "sex",
            "violence",
            "sexual humour",
        };

        public static (string? Rating, bool IsAdult) GetContentRatingAndAdult(ISeries? series)
        {
            var tagSet = BuildTagSet(series);

            // Build AniDB weight dictionary for precision-based decisions (defaults to 0 when not present).
            var anidbWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (series is IShokoSeries ss && ss.AnidbAnime?.Tags is IReadOnlyList<Shoko.Abstractions.Metadata.Anidb.IAnidbTagForAnime> atags)
            {
                foreach (var t in atags)
                {
                    if (t == null)
                        continue;
                    var name = t.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!RatingTags.Contains(name))
                        continue; // only track rating-related tags here
                    anidbWeights[name.ToLowerInvariant()] = t.Weight;
                }
            }

            // 18 restricted => X (adult) immediately
            if (tagSet.Contains("18 restricted"))
                return ("X", true);

            if (!ShokoRelay.Settings.AssumedContentRatings)
                return (null, false);

            // A rough approximation of: http://www.tvguidelines.org/resources/TheRatings.pdf
            // Uses the content indicators described here: https://wiki.anidb.net/Categories:Content_Indicators
            // Descriptor characters (Suggestive Dialogue, Sexual Situations, Violence)
            var descriptorD = tagSet.Contains("sexual humour") ? "D" : string.Empty;
            var descriptorS = (tagSet.Contains("nudity") || tagSet.Contains("sex")) ? "S" : string.Empty;
            var descriptorV = tagSet.Contains("violence") ? "V" : string.Empty;
            var descriptor =
                (!string.IsNullOrEmpty(descriptorD) || !string.IsNullOrEmpty(descriptorS) || !string.IsNullOrEmpty(descriptorV)) ? "-" + descriptorD + descriptorS + descriptorV : string.Empty;

            string? c_rating = null;

            // Consolidated helper: iterate tags in order, check AniDB weight thresholds
            (string Rating, bool IsAdult)? ApplyWeightedChecks(params (string Key, int Tv14, int TvMa)[] checks)
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
            if (ApplyWeightedChecks(("nudity", 400, 500), ("violence", 400, 500), ("sex", 300, 400)) is (var finalRating, var finalAdult))
                return (finalRating, finalAdult);

            // Uses the AniDB "target audience" tags to select a baseline rating when no weighted content indicators apply: https://anidb.net/tag/2606/animetb
            // Check higher (more restrictive) ratings first and return immediately when matched.
            if (string.IsNullOrEmpty(c_rating))
            {
                if (tagSet.Contains("josei") || tagSet.Contains("seinen"))
                    return ("TV-14" + descriptor, false);
                if (tagSet.Contains("shoujo") || tagSet.Contains("shounen"))
                    return ("TV-PG" + descriptor, false);
                if (tagSet.Contains("mina"))
                    return ("TV-G" + descriptor, false);
                if (tagSet.Contains("kodomo"))
                    return ("TV-Y" + descriptor, false);
            }

            if (!string.IsNullOrEmpty(c_rating) && c_rating != "X")
                c_rating += descriptor;

            return (c_rating, false);
        }

        private static HashSet<string> BuildTagSet(ISeries? series)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tags = (series as IShokoSeries)?.Tags;
            if (tags == null)
                return set;

            foreach (var t in tags)
            {
                if (t == null)
                    continue;
                var name = t.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var srcProp = t.GetType().GetProperty("Source");
                if (srcProp != null)
                {
                    var srcVal = srcProp.GetValue(t) as string;
                    if (!string.IsNullOrEmpty(srcVal) && srcVal.Equals("User", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!RatingTags.Contains(name))
                    continue;

                set.Add(name);
            }

            // Also include AniDB tags so content-rating can consult them (weights are read separately).
            if (series is IShokoSeries ss && ss.AnidbAnime?.Tags is IReadOnlyList<Shoko.Abstractions.Metadata.Anidb.IAnidbTagForAnime> anidbTags)
            {
                foreach (var at in anidbTags)
                {
                    var name = at?.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!RatingTags.Contains(name))
                        continue;
                    set.Add(name.ToLowerInvariant());
                }
            }

            return set;
        }
    }
}
