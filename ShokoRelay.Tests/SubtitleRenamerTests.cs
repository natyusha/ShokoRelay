using ShokoRelay.Config;
using ShokoRelay.Vfs;

namespace ShokoRelay.Tests;

public class SubtitleRenamerTests
{
    private const string VideoBase = "[DBD-Raws][Steins;Gate][04][1080P][BDRip][HEVC-10bit][FLAC]";
    private const string VfsBase = "S01E04 [42]";

    private static List<SubtitleRenameRule> Rules =>
        [
            new() { OriginalSuffix = "scjp", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "scjp", FinalSuffix = "ja" },
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "cht", FinalSuffix = "zh-Hant" },
        ];

    [Theory]
    [InlineData(new[] { "scjp.ass", "chs.ass" }, new[] { "ja.ass=scjp.ass", "zh-Hans.ass=scjp.ass" })]
    [InlineData(new[] { "scjp.ass", "cht.ass" }, new[] { "ja.ass=scjp.ass", "zh-Hans.ass=scjp.ass", "zh-Hant.ass=cht.ass" })]
    [InlineData(new[] { "chs.ass", "cht.ass" }, new[] { "zh-Hans.ass=chs.ass", "zh-Hant.ass=cht.ass" })]
    [InlineData(new[] { "chs.ass" }, new[] { "zh-Hans.ass=chs.ass" })]
    [InlineData(new[] { "scjp.ass", "zh-Hans.srt" }, new[] { "ja.ass=scjp.ass", "zh-Hans.srt=zh-Hans.srt" })]
    [InlineData(new[] { "scjp.ass", "ja.srt" }, new[] { "ja.srt=ja.srt", "zh-Hans.ass=scjp.ass" })]
    [InlineData(new[] { "scjp.srt", "chs.ass" }, new[] { "ja.srt=scjp.srt", "zh-Hans.srt=scjp.srt" })]
    [InlineData(new[] { "scjp.srt", "scjp.ass" }, new[] { "ja.ass=scjp.ass", "zh-Hans.ass=scjp.ass" })]
    [InlineData(new[] { "ScJp.ass" }, new[] { "ja.ass=ScJp.ass", "zh-Hans.ass=ScJp.ass" })]
    [InlineData(new[] { "SCJP.ass", "scjp.srt" }, new[] { "ja.ass=SCJP.ass", "zh-Hans.ass=SCJP.ass" })]
    [InlineData(new[] { "SCJP.ass", "scjp.ass", "ScJp.ass" }, new[] { "SCJP.ass=SCJP.ass", "ScJp.ass=ScJp.ass", "scjp.ass=scjp.ass" })]
    [InlineData(new[] { "SCJP.ass", "scjp.ass", "chs.ass" }, new[] { "SCJP.ass=SCJP.ass", "scjp.ass=scjp.ass", "zh-Hans.ass=chs.ass" })]
    [InlineData(new[] { "SCJP.ass", "scjp.ass", "scjp.srt" }, new[] { "SCJP.ass=SCJP.ass", "scjp.ass=scjp.ass", "scjp.srt=scjp.srt" })]
    [InlineData(new[] { "ZH-HANS.ass", "zh-Hans.ass", "scjp.ass" }, new[] { "ZH-HANS.ass=ZH-HANS.ass", "ja.ass=scjp.ass", "zh-Hans.ass=zh-Hans.ass" })]
    [InlineData(new[] { "en.ass", "en.srt", "chs2.ass", "chs.forced.ass" }, new[] { "chs.forced.ass=chs.forced.ass", "chs2.ass=chs2.ass", "en.ass=en.ass", "en.srt=en.srt" })]
    public void SelectsTheAgreedSourcesRegardlessOfDirectoryOrder(string[] suffixes, string[] expected)
    {
        var files = suffixes.Select(Source).ToArray();
        Assert.Equal(expected, Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, files, Rules)));
        Assert.Equal(expected, Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, files.Reverse(), Rules)));
    }

    [Theory]
    [InlineData("ass", "ssa")]
    [InlineData("ssa", "srt")]
    [InlineData("srt", "vtt")]
    [InlineData("vtt", "smi")]
    public void AppliesTheInternalFormatPreference(string preferred, string other)
    {
        var links = SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs." + other), Source("chs." + preferred)], Rules);
        Assert.Equal(["zh-Hans." + preferred + "=chs." + preferred], Describe(links));
    }

    [Fact]
    public void ReorderingRowsChangesOnlyTheCompetingOutput()
    {
        var rules = Rules;
        (rules[0], rules[2]) = (rules[2], rules[0]);
        var links = SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs.ass"), Source("scjp.ass")], rules);
        Assert.Equal(["ja.ass=scjp.ass", "zh-Hans.ass=chs.ass"], Describe(links));
    }

    [Fact]
    public void DoesNotChainGeneratedSuffixes()
    {
        var rules = new List<SubtitleRenameRule>
        {
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "zh-Hans", FinalSuffix = "ja" },
        };
        Assert.Equal(["zh-Hans.ass=chs.ass"], Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs.ass")], rules)));
        Assert.Equal(["ja.srt=zh-Hans.srt", "zh-Hans.srt=zh-Hans.srt"], Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs.ass"), Source("zh-Hans.srt")], rules)));
    }

    [Fact]
    public void DuplicateAndCircularRulesDoNotDuplicateLinks()
    {
        var rules = new List<SubtitleRenameRule>
        {
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
            new() { OriginalSuffix = "chs", FinalSuffix = "ZH-HANS" },
            new() { OriginalSuffix = "zh-Hans", FinalSuffix = "chs" },
        };
        Assert.Equal(["chs.ass=chs.ass", "zh-Hans.ass=chs.ass"], Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs.ass")], rules)));
    }

    [Fact]
    public void TreatsCompoundSuffixesLiterally()
    {
        var rules = new List<SubtitleRenameRule>
        {
            new() { OriginalSuffix = "chs.forced", FinalSuffix = "zh-Hans.forced" },
        };
        var links = SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [Source("chs.ass"), Source("chs.forced.ass")], rules);
        Assert.Equal(["chs.ass=chs.ass", "zh-Hans.forced.ass=chs.forced.ass"], Describe(links));
    }

    [Fact]
    public void PreservesSuffixlessSubtitlesAndNonSubtitleSidecars()
    {
        var links = SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, [VideoBase + ".ass", VideoBase + ".srt", Source("chs.nfo"), Source("chs.jpg")], Rules);
        Assert.Equal([VfsBase + ".ass", VfsBase + ".chs.jpg", VfsBase + ".chs.nfo", VfsBase + ".srt"], links.Select(l => l.Name));
    }

    [Fact]
    public void EmptyRulesPreserveEveryFormatAndOriginalSuffix()
    {
        var files = new[] { Source("scjp.ass"), Source("scjp.srt"), Source("chs.ass") };
        Assert.Equal(["chs.ass=chs.ass", "scjp.ass=scjp.ass", "scjp.srt=scjp.srt"], Describe(SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, files, [])));
    }

    [Fact]
    public void RequiresTheCompleteVideoBasenameBoundary()
    {
        var links = SubtitleRenamer.Plan("Episode1.mkv", VfsBase, ["Episode1.chs.ass", "Episode10.chs.ass", "Episode1extra.chs.ass", "Other.chs.ass"], Rules);
        var link = Assert.Single(links);
        Assert.Equal("Episode1.chs.ass", link.Source);
        Assert.Equal(VfsBase + ".zh-Hans.ass", link.Name);
    }

    [Fact]
    public void RepeatedPlanningHasIdenticalResultsAndDoesNotMutateInputs()
    {
        var files = new[] { Source("scjp.srt"), Source("scjp.ass"), Source("chs.ass") };
        var rules = Rules;
        var first = SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, files, rules);
        Assert.Equal(first, SubtitleRenamer.Plan(VideoBase + ".mkv", VfsBase, files, rules));
        Assert.Equal(["scjp", "scjp", "chs", "cht"], rules.Select(r => r.OriginalSuffix));
        Assert.Equal([Source("scjp.srt"), Source("scjp.ass"), Source("chs.ass")], files);
    }

    private static string Source(string suffix) => VideoBase + "." + suffix;

    private static string[] Describe(IEnumerable<EpisodeSidecarLink> links) => [.. links.Select(l => l.Name[(VfsBase.Length + 1)..] + "=" + l.Source[(VideoBase.Length + 1)..])];
}
