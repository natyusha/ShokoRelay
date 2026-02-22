using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace ShokoRelay.AnimeThemes;

internal static class AnimeThemesConstants
{
    internal const string ApiBase = "https://api.animethemes.moe";

    internal const string BasePath = "/animethemes/";

    internal const string DefaultRootFolder = "!AnimeThemes";

    internal const string MapFileName = "AniDB-AnimeThemes-xrefs.json";

    internal const string RawMapUrl = "https://gist.githubusercontent.com/natyusha/4e29252d939d0f522d38732facf328c7/raw/AniDB-AnimeThemes-xrefs.json";

    internal static readonly string[] VideoFileExtensions = { ".mkv", ".avi", ".mp4", ".mov", ".ogm", ".wmv", ".mpg", ".mpeg", ".mk3d", ".m4v" };

    internal static readonly Regex SlugRegex = new("^(?:op|ed)(?!0)[0-9]{0,2}(?:-(?:bd|web|tv|original))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Dictionary<string, string> SlugFormatting = new(StringComparer.OrdinalIgnoreCase)
    {
        { "OP", "Opening " },
        { "ED", "Ending " },
        { "-BD", " (Blu-ray Version)" },
        { "-ORIGINAL", " (Original Version)" },
        { "-TV", " (Broadcast Version)" },
        { "-WEB", " (Web Version)" },
        { "  ", " " },
    };

    internal static void EnsureUserAgent(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Any())
            return;

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShokoRelay", ShokoRelayInfo.Version));
    }
}
