using System.Text;
using System.Text.RegularExpressions;
using ShokoRelay.Helpers;

namespace ShokoRelay.Vfs
{
    public static class VfsHelper
    {
        private static readonly Regex _quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);
        private static readonly (string Find, string Replace)[] _styledTitleReplacements = { ("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←") };
        private static readonly IReadOnlyDictionary<char, char> _filenameCharMap = new Dictionary<char, char>
        {
            ['\\'] = '⧵',
            ['/'] = '⁄',
            [':'] = '꞉',
            ['*'] = '＊',
            ['?'] = '？',
            ['<'] = '＜',
            ['>'] = '＞',
            ['|'] = '｜',
        };

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

        public static string BuildStandardFileName(
            MapHelper.FileMapping mapping,
            int pad,
            string extension,
            int fileId,
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
            return $"{epPart} {fileIdPart}{extension}";
        }

        public static string BuildExtrasFileName(
            MapHelper.FileMapping mapping,
            (string Folder, string Prefix, string Subtype) extraInfo,
            int pad,
            string extension,
            string displaySeriesTitle,
            int? partIndexOverride = null,
            int? partCountOverride = null,
            int? versionIndexOverride = null
        )
        {
            string epPart = mapping.Coords.Episode.ToString($"D{pad}");
            string? prefix = extraInfo.Subtype switch
            {
                "trailer" => "T",
                "sceneOrSample" => "P",
                "featurette" => "O",
                _ => null,
            };
            if (!string.IsNullOrEmpty(prefix))
                epPart = $"{prefix}{epPart}";
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
            return TextHelper.ReplaceFirstHyphenWithArrow(fileName);
        }

        // Episode titles for Extras that are generated for the VFS need to be cleaned of invalid filename characters and condensed to prevent issues with Plex's file parsing
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
