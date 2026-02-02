using Shoko.Plugin.Abstractions.DataModels;

namespace ShokoRelay.Helpers
{
    public static class RatingHelper
    {
        public static string? GetContentRating(ISeries series)
        {
            if (!ShokoRelay.Settings.ContentRatings) return null;

            // Get all tags as lowercase for comparison
            var tags = series.Tags.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
            string? rating = null;

            // If the rating wasn't already determined using the content indicators above take the lowest target audience rating
            if (tags.Contains("kodomo"))             rating = "TV-Y";
            else if (tags.Contains("mina"))          rating = "TV-G";
            else if (tags.Contains("shoujo") || 
                     tags.Contains("shounen"))       rating = "TV-PG";
            else if (tags.Contains("josei") || 
                     tags.Contains("seinen"))        rating = "TV-14";

            // Override any previous rating for borderline porn content
            if (tags.Contains("borderline porn"))    rating = "TV-MA";

            // Override any previous rating and remove content indicators for 18 restricted content
            if (tags.Contains("18 restricted"))     rating = "X";

            return rating;
        }
    }
}