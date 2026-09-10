namespace ShokoRelay.Vfs;

/// <summary>An episode sidecar destination and the original file that supplies it.</summary>
/// <param name="Source">Original sidecar path.</param>
/// <param name="Name">Destination filename in the VFS.</param>
/// <param name="Reason">Explanation of the source selection, also used in previews.</param>
public sealed record EpisodeSidecarLink(string Source, string Name, string Reason);

/// <summary>Plans literal subtitle suffix conversions without reading or changing the filesystem.</summary>
public static class SubtitleRenamer
{
    private sealed record Candidate(string Source, string Suffix, string Extension);

    /// <summary>Plans episode sidecars, selecting one source per configured final suffix.</summary>
    /// <param name="sourceFile">Original video path, used to identify its sidecars.</param>
    /// <param name="destBase">Video basename in the VFS.</param>
    /// <param name="files">The existing episode-sidecar candidates from the source directory.</param>
    /// <param name="rules">Validated rules in priority order.</param>
    /// <returns>Deterministically ordered destinations, including unchanged metadata and subtitles.</returns>
    public static List<EpisodeSidecarLink> Plan(string sourceFile, string destBase, IEnumerable<string> files, IReadOnlyList<SubtitleRenameRule> rules)
    {
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var links = new List<EpisodeSidecarLink>();
        var subtitles = new List<Candidate>();
        foreach (var source in files.Distinct(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(source);
            if (!name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase))
                continue;
            string extension = Path.GetExtension(name);
            if (!PlexConstants.LocalMediaAssets.SubtitleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                links.Add(new(source, destBase + name[originalBase.Length..], "Metadata sidecar"));
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length < originalBase.Length || (stem.Length > originalBase.Length && stem[originalBase.Length] != '.'))
                continue;
            string suffix = stem.Length == originalBase.Length ? "" : stem[(originalBase.Length + 1)..];
            if (suffix.Length == 0 || rules.Count == 0)
                links.Add(new(source, destBase + name[originalBase.Length..], suffix.Length == 0 ? "No suffix; unchanged" : "No rules; unchanged"));
            else
                subtitles.Add(new(source, suffix, extension));
        }

        var groups = subtitles.GroupBy(s => s.Suffix, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var ambiguous = groups.Where(g => g.Value.GroupBy(s => s.Extension, StringComparer.OrdinalIgnoreCase).Any(e => e.Count() > 1)).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = rules.Select(r => r.OriginalSuffix).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = rules.Select(r => r.FinalSuffix).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Case-only duplicates are passed through together, including their other formats. They cannot supply conversions.
        foreach (var (suffix, candidates) in groups)
        {
            bool conflict = ambiguous.Contains(suffix);
            if (conflict || (!sources.Contains(suffix) && !targets.Contains(suffix, StringComparer.OrdinalIgnoreCase)))
                foreach (var candidate in candidates)
                    links.Add(new(candidate.Source, destBase + "." + candidate.Suffix + candidate.Extension, conflict ? "Case ambiguity; unchanged" : "Unmatched suffix; unchanged"));
        }

        foreach (var target in targets)
        {
            // Preserve ambiguous existing targets rather than overwrite their passthrough links with an alias conversion.
            if (ambiguous.Contains(target))
                continue;
            if (groups.TryGetValue(target, out var existing))
            {
                AddPreferred(existing, target, "Existing final suffix");
                continue;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (!rule.FinalSuffix.Equals(target, StringComparison.OrdinalIgnoreCase) || ambiguous.Contains(rule.OriginalSuffix) || !groups.TryGetValue(rule.OriginalSuffix, out var candidates))
                    continue;
                AddPreferred(candidates, target, $"Rule {i + 1}");
                break;
            }
        }

        return [.. links.OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Source, StringComparer.Ordinal)];

        void AddPreferred(List<Candidate> candidates, string suffix, string reason)
        {
            var selected = candidates.OrderBy(c => FormatPreference(c.Extension)).ThenBy(c => c.Source, StringComparer.Ordinal).First();
            links.Add(new(selected.Source, destBase + "." + suffix + selected.Extension, reason));
        }
    }

    private static int FormatPreference(string extension)
    {
        var formats = PlexConstants.LocalMediaAssets.SubtitleExtensions;
        for (int i = 0; i < formats.Count; i++)
            if (formats[i].Equals(extension, StringComparison.OrdinalIgnoreCase))
                return i;
        return formats.Count;
    }
}
