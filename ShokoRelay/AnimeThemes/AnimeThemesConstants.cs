using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace ShokoRelay.AnimeThemes;

/// <summary>
/// Shared constants and helper utilities used throughout the AnimeThemes subcomponent.
/// </summary>
internal static class AnimeThemesConstants
{
    internal const string AtApiBase = "https://api.animethemes.moe";

    internal const string AtDefaultRootFolder = "!AnimeThemes";

    internal const string AtMapFileName = "anidb_animethemes_xrefs.csv";

    internal const string AtRawMapUrl = "https://gist.githubusercontent.com/natyusha/bb33a3b3bc95bc7a3869633e23d522bb/raw/";

    internal static readonly FrozenSet<string> VideoFileExtensions = FrozenSet.ToFrozenSet([".mkv", ".avi", ".mp4", ".mov", ".ogm", ".wmv", ".mpg", ".mpeg", ".mk3d", ".m4v"], StringComparer.OrdinalIgnoreCase);

    internal static readonly Regex SlugRegex = new("^(?:op|ed)(?!0)[0-9]{0,2}(?:-(?:bd|web|tv|original))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Dictionary<string, string> SlugFormatting = new(StringComparer.OrdinalIgnoreCase)
    {
        { "OP", "Opening " },
        { "ED", "Ending " },
        { "-BD", " (Blu-ray Version)" },
        { "-ORIGINAL", " (Original Version)" },
        { "-TV", " (Broadcast Version)" },
        { "-WEB", " (Web Version)" },
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
}
