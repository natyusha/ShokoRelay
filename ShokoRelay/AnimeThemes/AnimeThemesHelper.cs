using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Shared constants and helper utilities used throughout the AnimeThemes subcomponent.
/// </summary>
internal static class AnimeThemesHelper
{
    internal const string AtApiBase = "https://api.animethemes.moe";

    internal const string AtMapFileName = "anidb_animethemes_xrefs.csv";
    internal const string AtFavsFileName = "favs_animethemes.cache";

    internal const string AtRawMapUrl = "https://gist.githubusercontent.com/natyusha/bb33a3b3bc95bc7a3869633e23d522bb/raw";

    internal static readonly FrozenSet<string> VideoFileExtensions = FrozenSet.ToFrozenSet([".mkv", ".avi", ".mp4", ".mov", ".ogm", ".wmv", ".mpg", ".mpeg", ".mk3d", ".m4v"], StringComparer.OrdinalIgnoreCase);

    internal static readonly Regex SlugRegex = new("^(?:op|ed)(?!0)[0-9]{0,2}(?:-(?:bd|web|tv|original))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Dictionary<string, string> SlugFormatting = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Animax", "Animax" },
        { "ATX", "AT-X" },
        { "BD", "Blu-ray" },
        { "BestSelection", "Best Selection" },
        { "EN", "English" },
        { "HD", "High Definition" },
        { "KO", "Korean" },
        { "NoitaminA", "noitaminA" },
        { "ORIGINAL", "Original" },
        { "Oversea", "Overseas" },
        { "Plus", "Plus" },
        { "RepeatShow", "Repeat Show" },
        { "ShounenHen", "Shounen-hen" },
        { "Sound", "Sound" },
        { "Theatrical2", "Theatrical 2" },
        { "Theatrical3", "Theatrical 3" },
        { "Theatrical4", "Theatrical 4" },
        { "Theatrical5", "Theatrical 5" },
        { "Theatrical6", "Theatrical 6" },
        { "Theatrical7", "Theatrical 7" },
        { "Theatrical8", "Theatrical 8" },
        { "TV", "Broadcast" },
        { "WEB", "Web" },
        { "YorinukiGintamaSan", "Yorinuki Gintama-san" },
    };

    /// <summary>
    /// Add a default User-Agent header to <paramref name="client"/> if none present. Identifies requests as originating from ShokoRelay.
    /// </summary>
    internal static void EnsureUserAgent(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Any())
            return;

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShokoRelay", ShokoRelayInfo.Version));
    }

    /// <summary>
    /// Parse slug to extract base slug and suffix variant. Examples: "ED1-EN" -> ("ED1", "EN"), "OP2" -> ("OP2", null). Also, Normalizes "OP1" to "OP" and "ED1" to "ED".
    /// </summary>
    internal static (string baseSlug, string? suffix) ParseSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return ("", null);

        int hyphenIndex = slug.IndexOf('-');

        // Split into base and suffix
        string base_ = hyphenIndex < 0 ? slug : slug[..hyphenIndex];
        string? suff = hyphenIndex < 0 ? null : slug[(hyphenIndex + 1)..];

        // Remove "1" from OP1 or ED1 (Case-insensitive check)
        if (base_.Equals("OP1", StringComparison.OrdinalIgnoreCase) || base_.Equals("ED1", StringComparison.OrdinalIgnoreCase))
        {
            base_ = base_[..2];
        }

        return (base_, string.IsNullOrWhiteSpace(suff) ? null : suff);
    }

    /// <summary>
    /// Format a slug suffix using the configured mappings. Examples: "BD" -> " (Blu-ray)", "EN" -> " (English)". Returns empty string if suffix is null.
    /// </summary>
    internal static string FormatSlugTag(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return "";

        // Directly lookup the suffix (case-insensitive thanks to Dictionary setup)
        if (SlugFormatting.TryGetValue(suffix.Trim(), out var formatted))
            return $" ({formatted})";

        // Fallback if no mapping exists
        return $" ({suffix})";
    }
}
