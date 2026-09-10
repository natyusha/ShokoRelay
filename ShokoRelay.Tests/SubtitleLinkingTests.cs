using System.Collections.Concurrent;
using ShokoRelay.Config;
using ShokoRelay.Vfs;

namespace ShokoRelay.Tests;

public class SubtitleLinkingTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "shokorelay-subtitles-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MultipleOutputsAreLinksToTheSameOriginalAndRefreshRemovesObsoleteNames()
    {
        string sourceDir = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string destDir = Directory.CreateDirectory(Path.Combine(_root, "vfs")).FullName;
        string video = Path.Combine(sourceDir, "Episode.mkv");
        string bilingual = Path.Combine(sourceDir, "Episode.scjp.ass");
        string chinese = Path.Combine(sourceDir, "Episode.chs.ass");
        File.WriteAllText(video, "video fixture");
        File.WriteAllText(bilingual, "bilingual fixture");
        File.WriteAllText(chinese, "Chinese fixture");
        var rules = new List<SubtitleRenameRule>
        {
            new() { OriginalSuffix = "scjp", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "scjp", FinalSuffix = "ja" },
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
        };

        Refresh(video, destDir, []);
        Assert.Equal(["S01E01.chs.ass", "S01E01.scjp.ass"], Names(destDir));

        Refresh(video, destDir, rules);
        Assert.Equal(["S01E01.ja.ass", "S01E01.zh-Hans.ass"], Names(destDir));
        Assert.Equal(bilingual, File.ResolveLinkTarget(Path.Combine(destDir, "S01E01.ja.ass"), true)!.FullName);
        Assert.Equal(bilingual, File.ResolveLinkTarget(Path.Combine(destDir, "S01E01.zh-Hans.ass"), true)!.FullName);

        Refresh(video, destDir, rules);
        Assert.Equal(["S01E01.ja.ass", "S01E01.zh-Hans.ass"], Names(destDir));

        (rules[0], rules[2]) = (rules[2], rules[0]);
        Refresh(video, destDir, rules);
        Assert.Equal(chinese, File.ResolveLinkTarget(Path.Combine(destDir, "S01E01.zh-Hans.ass"), true)!.FullName);
        Assert.Equal(bilingual, File.ResolveLinkTarget(Path.Combine(destDir, "S01E01.ja.ass"), true)!.FullName);

        Refresh(video, destDir, []);
        Assert.Equal(["S01E01.chs.ass", "S01E01.scjp.ass"], Names(destDir));
        Assert.Equal("bilingual fixture", File.ReadAllText(bilingual));
        Assert.Equal("Chinese fixture", File.ReadAllText(chinese));
        Assert.Equal("video fixture", File.ReadAllText(video));
    }

    [CaseSensitiveFileSystemFact]
    public void CaseOnlyDuplicatesAreLinkedAsIsAndSurviveCleanup()
    {
        string sourceDir = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string destDir = Directory.CreateDirectory(Path.Combine(_root, "vfs")).FullName;
        string video = Path.Combine(sourceDir, "Episode.mkv");
        File.WriteAllText(video, "video fixture");
        foreach (string suffix in new[] { "SCJP", "scjp", "ScJp", "chs" })
            File.WriteAllText(Path.Combine(sourceDir, $"Episode.{suffix}.ass"), suffix);
        var rules = new List<SubtitleRenameRule>
        {
            new() { OriginalSuffix = "scjp", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "scjp", FinalSuffix = "ja" },
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
        };
        Refresh(video, destDir, rules);
        Assert.Equal(["S01E01.SCJP.ass", "S01E01.ScJp.ass", "S01E01.scjp.ass", "S01E01.zh-Hans.ass"], Names(destDir));
        foreach (string suffix in new[] { "SCJP", "scjp", "ScJp" })
            Assert.Equal(suffix, File.ReadAllText(Path.Combine(destDir, $"S01E01.{suffix}.ass")));
    }

    private static void Refresh(string video, string destDir, IReadOnlyList<SubtitleRenameRule> rules)
    {
        var linker = new VfsAssetLinker(null!); // Video service is only used by local-extra discovery.
        var cache = new ConcurrentDictionary<string, Lazy<string[]>>(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        int planned = 0,
            skipped = 0,
            created = 0;
        linker.LinkEpisodeMetadata(
            video,
            Path.GetDirectoryName(video)!,
            "S01E01",
            destDir,
            cache,
            ref planned,
            ref skipped,
            errors,
            ref created,
            (name, _) => expected.Add(Path.Combine(destDir, name)),
            subtitleRules: rules
        );
        VfsHelper.CleanupOrphanedFilesAndFolders([destDir], expected);
        Assert.Empty(errors);
        Assert.Equal(0, skipped);
        Assert.Equal(expected.Count, planned);
        Assert.Equal(expected.Count, created);
    }

    private static string[] Names(string directory) => [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).Cast<string>()];

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Checks the actual test-output volume before creating case-only filenames.</summary>
public sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    public CaseSensitiveFileSystemFactAttribute()
    {
        string probe = Path.Combine(AppContext.BaseDirectory, "case-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            File.WriteAllText(Path.Combine(probe, "lower"), "probe");
            if (File.Exists(Path.Combine(probe, "LOWER")))
                Skip = "Requires a case-sensitive test-output volume.";
        }
        finally
        {
            Directory.Delete(probe, true);
        }
    }
}
