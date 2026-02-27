using System.Text;
using System.Text.RegularExpressions;
using ShokoRelay.Helpers;

namespace ShokoRelay.Vfs
{
    /// <summary>
    /// Utilities used during virtual filesystem (VFS) generation, including filename sanitization and naming helpers.
    /// </summary>
    public static class VfsHelper
    {
        private static readonly Regex _quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);
        private static readonly (string Find, string Replace)[] _styledTitleReplacements = { ("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←") };
        private static readonly IReadOnlyDictionary<char, char> _filenameCharMap = TextHelper.ReplacementCharMap;

        // prefixes to apply to the episode number when generating filenames for extras
        private static readonly IReadOnlyDictionary<string, string> _extraTypePrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trailer"] = "T",
            ["sceneOrSample"] = "P",
            ["featurette"] = "O",
            ["short"] = "C",
            ["other"] = "U",
        };

        /// <summary>
        /// Remove invalid filename characters from <paramref name="name"/>, condense whitespace and trim trailing dots. Returns "Unknown" when the result is empty.
        /// </summary>
        /// <param name="name">Input string to sanitize.</param>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? ' ' : c);
            }

            string cleaned = sb.ToString();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            cleaned = cleaned.Trim().TrimEnd('.');

            return cleaned.Length == 0 ? "Unknown" : cleaned;
        }

        /// <summary>
        /// Construct a standard episode filename using season/episode coordinates, zero-padding, file ID and optional part/version overrides.
        /// </summary>
        /// <param name="mapping">Mapping information for the episode.</param>
        /// <param name="pad">Zero‑padding width for episode numbers.</param>
        /// <param name="extension">File extension including the dot.</param>
        /// <param name="fileId">Numeric identifier to append to the filename.</param>
        /// <param name="omitFileId">If true, do not include the file ID bracket.</param>
        /// <param name="partIndexOverride">Optional manual part index.</param>
        /// <param name="partCountOverride">Optional total part count.</param>
        /// <param name="versionIndexOverride">Optional version index.</param>
        public static string BuildStandardFileName(
            MapHelper.FileMapping mapping,
            int pad,
            string extension,
            int fileId,
            bool omitFileId = false,
            int? partIndexOverride = null,
            int? partCountOverride = null,
            int? versionIndexOverride = null
        )
        {
            string epPart = $"S{mapping.Coords.Season:D2}E{mapping.Coords.Episode.ToString($"D{pad}")}";
            if (mapping.Coords.EndEpisode.HasValue && mapping.Coords.EndEpisode.Value != mapping.Coords.Episode)
            {
                epPart += $"-E{mapping.Coords.EndEpisode.Value.ToString($"D{pad}")}";
            }

            int partCount = partCountOverride ?? mapping.PartCount;
            int? partIndex = partIndexOverride ?? mapping.PartIndex;
            int? versionIndex = versionIndexOverride;

            if (partCount > 1 && partIndex.HasValue)
            {
                epPart += $"-pt{partIndex.Value}";
            }
            else if (versionIndex.HasValue)
            {
                epPart += $"-v{versionIndex.Value}";
            }

            string fileIdPart = $"[{fileId}]";
            if (omitFileId)
                return $"{epPart}{extension}";
            return $"{epPart} {fileIdPart}{extension}";
        }

        /// <summary>
        /// Build a filename for extras (trailers, featurettes, etc.) using the provided mapping and subtype information. Applies special prefixes and sanitizes the episode title.
        /// </summary>
        /// <param name="mapping">File mapping data.</param>
        /// <param name="extraInfo">Folder/subtype information for the extra.</param>
        /// <param name="pad">Zero‑pad width for the episode portion.</param>
        /// <param name="extension">File extension including dot.</param>
        /// <param name="displaySeriesTitle">Series title used for fallback when cleaning episode title.</param>
        /// <param name="partIndexOverride">Optional override for part index.</param>
        /// <param name="partCountOverride">Optional override for part count.</param>
        /// <param name="versionIndexOverride">Optional override for version index.</param>
        public static string BuildExtrasFileName(
            MapHelper.FileMapping mapping,
            (string Folder, string Subtype) extraInfo,
            int pad,
            string extension,
            string displaySeriesTitle,
            int? partIndexOverride = null,
            int? partCountOverride = null,
            int? versionIndexOverride = null
        )
        {
            string epPart = mapping.Coords.Episode.ToString($"D{pad}");
            if (_extraTypePrefixes.TryGetValue(extraInfo.Subtype, out var pref) && !string.IsNullOrEmpty(pref))
            {
                epPart = pref + epPart;
            }
            int partCount = partCountOverride ?? mapping.PartCount;
            int? partIndex = partIndexOverride ?? mapping.PartIndex;
            int? versionIndex = versionIndexOverride;
            string part = partCount > 1 && partIndex.HasValue ? $"-pt{partIndex.Value}" : string.Empty;
            if (string.IsNullOrEmpty(part) && versionIndex.HasValue)
            {
                part = $"-v{versionIndex.Value}";
            }

            string epTitle = TextHelper.ResolveEpisodeTitle(mapping.PrimaryEpisode, displaySeriesTitle);
            epTitle = CleanEpisodeTitleForFilename(epTitle);
            epTitle = SanitizeName(epTitle);

            string fileName = $"{epPart}{part} - {epTitle}{extension}";
            return TextHelper.ReplaceFirstHyphenWithChevron(fileName);
        }

        /// <summary>
        /// Clean an episode title for use in a filename by replacing styled
        /// substrings, mapping invalid characters and condensing whitespace.
        /// </summary>
        /// <param name="title">Original episode title.</param>
        public static string CleanEpisodeTitleForFilename(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            string cleaned = title;

            foreach (var (find, replace) in _styledTitleReplacements)
            {
                cleaned = cleaned.Replace(find, replace, StringComparison.Ordinal);
            }

            cleaned = _quotedTextRegex.Replace(cleaned, "“$1”");

            var sb = new StringBuilder(cleaned.Length);
            foreach (char c in cleaned)
            {
                if (_filenameCharMap.TryGetValue(c, out var mapped))
                {
                    sb.Append(mapped);
                }
                else
                {
                    sb.Append(c);
                }
            }

            cleaned = Regex.Replace(sb.ToString(), "\\s+", " ", RegexOptions.Compiled).Trim();
            return cleaned;
        }
    }
}
