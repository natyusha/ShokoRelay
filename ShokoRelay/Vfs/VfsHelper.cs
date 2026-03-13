using System.Text.RegularExpressions;
using ShokoRelay.Helpers;

namespace ShokoRelay.Vfs;

/// <summary>
/// Utilities used during virtual filesystem (VFS) generation, including filename sanitization and naming helpers.
/// </summary>
public static class VfsHelper
{
    private static readonly Regex _quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);
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

    /// <summary>
    /// Remove invalid filename characters from name, condense whitespace and trim trailing dots.
    /// </summary>
    /// <param name="name">Input string to sanitize.</param>
    /// <returns>The sanitized filename, or "Unknown" when empty.</returns>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown";
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = TextHelper.CondenseSpaces(new string([.. name.Select(c => invalid.Contains(c) ? ' ' : c)])).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    /// <summary>
    /// Construct a standard episode filename using season/episode coordinates.
    /// </summary>
    /// <param name="mapping">Mapping information for the episode.</param>
    /// <param name="pad">Zero‑padding width for episode numbers.</param>
    /// <param name="ext">File extension including the dot.</param>
    /// <param name="fileId">Numeric identifier to append.</param>
    /// <param name="omitFileId">If true, do not include the file ID bracket.</param>
    /// <param name="partIdx">Optional manual part index.</param>
    /// <param name="partCount">Optional total part count.</param>
    /// <param name="vIdx">Optional version index.</param>
    /// <returns>A formatted episode filename string.</returns>
    public static string BuildStandardFileName(MapHelper.FileMapping mapping, int pad, string ext, int fileId, bool omitFileId = false, int? partIdx = null, int? partCount = null, int? vIdx = null)
    {
        string name = $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{pad}")}";
        if (mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode != mapping.Coords.Episode)
            name += $"-E{mapping.Coords.EndEpisode.Value.ToString($"D{pad}")}";

        int pCount = partCount ?? mapping.PartCount;
        if (pCount > 1)
            name += $"-pt{partIdx ?? mapping.PartIndex}";
        else if (vIdx.HasValue)
            name += $"-v{vIdx}";

        return omitFileId ? $"{name}{ext}" : $"{name} [{fileId}]{ext}";
    }

    /// <summary>
    /// Build a filename for extras (trailers, featurettes, etc.) using special prefixes.
    /// </summary>
    public static string BuildExtrasFileName(MapHelper.FileMapping m, (string Folder, string Subtype) ex, int pad, string ext, string seriesTitle, int? pIdx = null, int? pCount = null, int? vIdx = null)
    {
        string ep = _extraTypePrefixes.TryGetValue(ex.Subtype, out var pref) ? pref + m.Coords.Episode.ToString($"D{pad}") : m.Coords.Episode.ToString($"D{pad}");
        string part = (pCount ?? m.PartCount) > 1 ? $"-pt{pIdx ?? m.PartIndex}" : (vIdx.HasValue ? $"-v{vIdx}" : "");
        return SanitizeName($"{ep}{part} ❯ {CleanEpisodeTitleForFilename(TextHelper.ResolveEpisodeTitle(m.PrimaryEpisode, seriesTitle))}{ext}");
    }

    /// <summary>
    /// Clean an episode title for use in a filename by replacing styled substrings and invalid characters.
    /// </summary>
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
}
