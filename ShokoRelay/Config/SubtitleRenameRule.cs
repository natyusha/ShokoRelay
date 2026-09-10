using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config;

/// <summary>A literal subtitle suffix mapping. List order determines priority for each final suffix.</summary>
public sealed class SubtitleRenameRule
{
    /// <summary>Suffix in the original filename, without the surrounding dots or file extension.</summary>
    public string OriginalSuffix { get; set; } = "";

    /// <summary>Suffix to use in the VFS filename.</summary>
    public string FinalSuffix { get; set; } = "";

    /// <summary>Trims and validates rules while preserving their order and repeated source or target suffixes.</summary>
    /// <param name="rules">Rules supplied by configuration or the preview form.</param>
    /// <returns>A normalized copy of the rules.</returns>
    public static List<SubtitleRenameRule> Normalize(IEnumerable<SubtitleRenameRule>? rules)
    {
        var normalized = new List<SubtitleRenameRule>();
        foreach (var rule in rules ?? [])
        {
            int row = normalized.Count + 1;
            string original = rule?.OriginalSuffix?.Trim() ?? "";
            string final = rule?.FinalSuffix?.Trim() ?? "";
            if (!IsValidSuffix(original) || !IsValidSuffix(final))
                throw new ValidationException($"Subtitle rule {row}: enter both suffixes without surrounding dots, path separators, control characters, or any of these characters: <>:\"|?*");
            normalized.Add(new SubtitleRenameRule { OriginalSuffix = original, FinalSuffix = final });
        }
        return normalized;
    }

    private static bool IsValidSuffix(string suffix) => suffix.Length > 0 && !suffix.StartsWith('.') && !suffix.EndsWith('.') && !suffix.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));
}
