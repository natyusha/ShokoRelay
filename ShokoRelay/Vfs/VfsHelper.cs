using System.Text.RegularExpressions;
using ShokoRelay.Helpers;

namespace ShokoRelay.Vfs;

/// <summary>Utilities used during VFS generation including filename sanitization.</summary>
public static class VfsHelper
{
    #region Regex and Mappings

    /// <summary>Regex to match standard quotes with the intention of turning them curly.</summary>
    private static readonly Regex _quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);

    /// <summary>Regex to match whitespace except for Hair Space and Zero Width Spaceas they are part of some Plex Extra filename formatting.</summary>
    private static readonly Regex _whitespaceRegex = new(@"((?![\u200A\u200B])\s)+", RegexOptions.Compiled);
    private static readonly (string Find, string Replace)[] _styledReplacements = [("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←")];
    private static readonly IReadOnlyDictionary<string, string> _extraTypePrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trailer"] = "T",
        ["sceneOrSample"] = "P",
        ["featurette"] = "O",
        ["short"] = "C",
        ["other"] = "U",
    };

    #endregion

    #region Sanitization

    /// <summary>Sanitizes a string for use as a filename.</summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown";
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = TextHelper.CondenseSpaces(new string([.. name.Select(c => invalid.Contains(c) ? ' ' : c)])).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    /// <summary>Cleans episode titles for filename use.</summary>
    public static string CleanEpisodeTitleForFilename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        string c = title;
        foreach (var (f, r) in _styledReplacements)
            c = c.Replace(f, r, StringComparison.Ordinal);
        c = _quotedTextRegex.Replace(c, "“$1”");
        return _whitespaceRegex.Replace(new string([.. c.Select(ch => TextHelper.ReplacementCharMap.TryGetValue(ch, out var m) ? m : ch)]), " ").Trim(' ');
    }

    #endregion

    #region Naming Logic

    /// <summary>Builds a standard episode filename based on coordinates, versioning, and variation status.</summary>
    /// <param name="mapping">The mapping containing coordinates and video data.</param>
    /// <param name="pad">The number of digits to pad the episode number with.</param>
    /// <param name="ext">The file extension (including the dot).</param>
    /// <param name="fileId">The unique Shoko video identifier.</param>
    /// <param name="omitFileId">Whether to exclude the Shoko video identifier from the filename.</param>
    /// <param name="partIdx">The 1-based index for multi-part files.</param>
    /// <param name="partCount">The total number of parts for the file.</param>
    /// <param name="vIdx">The 1-based version index for duplicate items in the same variation bucket.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A formatted filename string (e.g., "S01E01-v1 [123][variation].mkv").</returns>
    public static string BuildStandardFileName(
        MapHelper.FileMapping mapping,
        int pad,
        string ext,
        int fileId,
        bool omitFileId = false,
        int? partIdx = null,
        int? partCount = null,
        int? vIdx = null,
        bool isVariation = false
    )
    {
        string name = $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{pad}")}";
        if (mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode != mapping.Coords.Episode)
            name += $"-E{mapping.Coords.EndEpisode.Value.ToString($"D{pad}")}";
        int pCount = partCount ?? mapping.PartCount;
        if (pCount > 1)
            name += $"-pt{partIdx ?? mapping.PartIndex}";
        else if (vIdx.HasValue)
            name += $"-v{vIdx}";

        string result = omitFileId ? name : $"{name} [{fileId}]";
        if (isVariation)
            result += "[variation]";
        return result + ext;
    }

    /// <summary>Builds a filename for extra content (trailers, credits, etc.) using special type prefixes and title metadata.</summary>
    /// <param name="m">The mapping containing coordinates and metadata.</param>
    /// <param name="ex">A tuple containing the target folder and the Plex extra subtype.</param>
    /// <param name="pad">The number of digits to pad the extra number with.</param>
    /// <param name="ext">The file extension (including the dot).</param>
    /// <param name="seriesTitle">The display title of the parent series used for name resolution.</param>
    /// <param name="pIdx">Optional 1-based index for multi-part extras.</param>
    /// <param name="pCount">Optional total number of parts for multi-part extras.</param>
    /// <param name="vIdx">Optional version index for duplicate extras.</param>
    /// <param name="isVariation">Whether the file is marked as a variation in Shoko.</param>
    /// <returns>A sanitized and formatted filename string for Plex extras (e.g., "C01 ❯ Episode Title [variation].mkv").</returns>
    public static string BuildExtrasFileName(
        MapHelper.FileMapping m,
        (string Folder, string Subtype) ex,
        int pad,
        string ext,
        string seriesTitle,
        int? pIdx = null,
        int? pCount = null,
        int? vIdx = null,
        bool isVariation = false
    )
    {
        string ep = _extraTypePrefixes.TryGetValue(ex.Subtype, out var pref) ? pref + m.Coords.Episode.ToString($"D{pad}") : m.Coords.Episode.ToString($"D{pad}");
        string part = (pCount ?? m.PartCount) > 1 ? $"-pt{pIdx ?? m.PartIndex}" : (vIdx.HasValue ? $"-v{vIdx}" : "");

        string name = $"{ep}{part} ❯ {CleanEpisodeTitleForFilename(TextHelper.ResolveEpisodeTitle(m.PrimaryEpisode, seriesTitle))}";
        if (isVariation)
            name += "[variation]";
        return SanitizeName(name + ext);
    }

    #endregion
}
