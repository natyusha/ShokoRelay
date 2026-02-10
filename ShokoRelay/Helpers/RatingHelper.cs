using Shoko.Plugin.Abstractions.DataModels;

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

            // If 18 restricted is present override the contentRatings setting
            if (tagSet.Contains("18 restricted"))
                return ("X", true);

            if (!ShokoRelay.Settings.AssumedContentRatings)
                return (null, false);

            // A rough approximation of: http://www.tvguidelines.org/resources/TheRatings.pdf
            // Uses the content indicators described here: https://wiki.anidb.net/Categories:Content_Indicators
            var descriptorD = tagSet.Contains("sexual humour") ? "D" : "";
            var descriptorS = (tagSet.Contains("nudity") || tagSet.Contains("sex")) ? "S" : "";
            var descriptorV = tagSet.Contains("violence") ? "V" : "";
            var descriptor = (descriptorD + descriptorS + descriptorV) != "" ? "-" + (descriptorD + descriptorS + descriptorV) : "";

            // Uses the target audience tags on AniDB: https://anidb.net/tag/2606/animetb
            string? c_rating = null;
            if (tagSet.Contains("kodomo"))
                c_rating = "TV-Y";
            else if (tagSet.Contains("mina"))
                c_rating = "TV-G";
            else if (tagSet.Contains("shoujo") || tagSet.Contains("shounen"))
                c_rating = "TV-PG";
            else if (tagSet.Contains("josei") || tagSet.Contains("seinen"))
                c_rating = "TV-14";

            if (tagSet.Contains("borderline porn"))
                c_rating = "TV-MA";

            if (!string.IsNullOrEmpty(c_rating))
                c_rating = c_rating + descriptor;

            return (c_rating, false);
        }

        private static HashSet<string> BuildTagSet(ISeries? series)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (series?.Tags == null)
                return set;

            foreach (var t in series.Tags)
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

            return set;
        }
    }
}
