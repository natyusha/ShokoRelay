using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Plugin;
using ShokoRelay.Config;
using ShokoRelay.Controllers;

namespace ShokoRelay.Tests;

public class SubtitleRenameRuleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".chs")]
    [InlineData("chs.")]
    [InlineData("../chs")]
    [InlineData("..\\chs")]
    [InlineData("chs:ja")]
    [InlineData("chs\nja")]
    [InlineData("chs\0ja")]
    [InlineData("<chs>")]
    [InlineData("chs*")]
    [InlineData("chs?")]
    public void RejectsInvalidSuffixesInEitherColumn(string suffix)
    {
        Assert.Throws<ValidationException>(() => SubtitleRenameRule.Normalize([new() { OriginalSuffix = suffix, FinalSuffix = "zh-Hans" }]));
        Assert.Throws<ValidationException>(() => SubtitleRenameRule.Normalize([new() { OriginalSuffix = "chs", FinalSuffix = suffix }]));
    }

    [Fact]
    public void TrimsAndPreservesOrderedDuplicateSourcesThroughJsonRoundTrip()
    {
        var config = new RelayConfig();
        config.Advanced.SubtitleRenameRules = SubtitleRenameRule.Normalize([
            new() { OriginalSuffix = " scjp ", FinalSuffix = " zh-Hans " },
            new() { OriginalSuffix = "scjp", FinalSuffix = "ja" },
            new() { OriginalSuffix = "chs", FinalSuffix = "zh-Hans" },
        ]);
        var restored = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(["scjp", "scjp", "chs"], restored.Advanced.SubtitleRenameRules.Select(r => r.OriginalSuffix));
        Assert.Equal(["zh-Hans", "ja", "zh-Hans"], restored.Advanced.SubtitleRenameRules.Select(r => r.FinalSuffix));
    }

    [Fact]
    public void AllowsArbitraryFilenameSafeSuffixes()
    {
        var rule = Assert.Single(SubtitleRenameRule.Normalize([new() { OriginalSuffix = "简日", FinalSuffix = "Custom bilingual.forced" }]));
        Assert.Equal("Custom bilingual.forced", rule.FinalSuffix);
    }

    [Fact]
    public void OldConfigurationsHaveNoPresetRules()
    {
        var config = JsonSerializer.Deserialize<RelayConfig>("{}")!;
        Assert.Empty(config.Advanced.SubtitleRenameRules);
        Assert.Equal(["ass", "ssa", "srt", "vtt", "smi"], config.Advanced.SubtitleFormatPreference);
        Assert.Empty(SubtitleRenameRule.Normalize(null));
        Assert.Throws<ValidationException>(() => SubtitleRenameRule.Normalize([null!]));
    }

    [Fact]
    public void ConfigProviderSavesOrderedRulesAndRejectsAnInvalidReplacement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "config-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TestApplicationPaths(root);
            var provider = new ConfigProvider(paths);
            var config = new RelayConfig();
            config.Advanced.SubtitleRenameRules = [new() { OriginalSuffix = " scjp ", FinalSuffix = " zh-Hans " }, new() { OriginalSuffix = "scjp", FinalSuffix = "ja" }];
            provider.SaveSettings(config);
            var loaded = new ConfigProvider(paths).GetSettings();
            Assert.Equal(["scjp", "scjp"], loaded.Advanced.SubtitleRenameRules.Select(r => r.OriginalSuffix));
            Assert.Equal(["zh-Hans", "ja"], loaded.Advanced.SubtitleRenameRules.Select(r => r.FinalSuffix));

            loaded.Advanced.SubtitleRenameRules[0].FinalSuffix = "../escape";
            Assert.Throws<ValidationException>(() => provider.SaveSettings(loaded));
            Assert.Equal("zh-Hans", new ConfigProvider(paths).GetSettings().Advanced.SubtitleRenameRules[0].FinalSuffix);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InvalidRulesInAnExternallyEditedConfigDisableConversion()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "config-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ConfigProvider(new TestApplicationPaths(root));
            var config = new RelayConfig { SeriesTitleLanguage = "EN" };
            config.Advanced.SubtitleRenameRules = [new() { OriginalSuffix = "chs", FinalSuffix = "../escape" }];
            File.WriteAllText(Path.Combine(provider.ConfigDirectory, ShokoRelayConstants.FilePreferences), JsonSerializer.Serialize(config));
            var loaded = provider.GetSettings();
            Assert.Empty(loaded.Advanced.SubtitleRenameRules);
            Assert.Equal("EN", loaded.SeriesTitleLanguage);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("null", "")]
    [InlineData("[]", "")]
    [InlineData("""[".SRT","ass","SRT"," .VTT ",".ssa","SMI"]""", "srt,ass,vtt,ssa,smi")]
    [InlineData("""[null,"",".","..ass","ass.","s/rt","s\\rt","s:rt","s rt","s*rt","s?rt","s\"rt","s<rt","s>rt","s|rt","srt"]""", "srt")]
    [InlineData("""["sr\nt","s\u0000rt","ſrt","ＡＳＳ","字幕","12345678901","srt"]""", "srt")]
    [InlineData("""["sub","sup","123"]""", "")]
    public void NormalizesFormatPreferencesOnLoadAndSave(string preferences, string expected)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "config-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ConfigProvider(new TestApplicationPaths(root));
            string json = """{"SeriesTitleLanguage":"EN","Advanced":{"SubtitleFormatPreference":""" + preferences + "}}";
            string path = Path.Combine(provider.ConfigDirectory, ShokoRelayConstants.FilePreferences);
            File.WriteAllText(path, json);
            var loaded = provider.GetSettings();
            Assert.Equal(expected, string.Join(',', loaded.Advanced.SubtitleFormatPreference));
            Assert.Equal("EN", loaded.SeriesTitleLanguage);

            provider.SaveSettings(JsonSerializer.Deserialize<RelayConfig>(json)!);
            var saved = JsonSerializer.Deserialize<RelayConfig>(File.ReadAllText(path))!;
            Assert.Equal(expected, string.Join(',', saved.Advanced.SubtitleFormatPreference));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DashboardHidesSubtitleOptionsButPreservesThemWhenSavingOtherSettings()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "config-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TestApplicationPaths(root);
            var provider = new ConfigProvider(paths);
            var config = new RelayConfig();
            config.Advanced.SubtitleRenameRules = [new() { OriginalSuffix = "scjp", FinalSuffix = "zh-Hans" }, new() { OriginalSuffix = "scjp", FinalSuffix = "ja" }];
            config.Advanced.SubtitleFormatPreference = ["srt"];
            provider.SaveSettings(config);

            var controller = new DashboardController(provider, null!, null!, null!, paths);
            var schema = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.GetConfigSchema()).Value);
            var fields = schema.GetProperty("properties").EnumerateArray().Select(p => p.GetProperty("Path").GetString()).ToArray();
            Assert.Contains("Advanced.PathMappings", fields);
            Assert.DoesNotContain(fields, f => f!.StartsWith("Advanced.Subtitle", StringComparison.Ordinal));

            var payload = JsonSerializer.Deserialize<RelayConfig>(JsonSerializer.Serialize(provider.GetDashboardConfig()))!;
            payload.SeriesTitleLanguage = "EN";
            Assert.IsType<OkObjectResult>(controller.SaveConfig(payload));
            var restored = new ConfigProvider(paths).GetSettings();
            Assert.Equal("EN", restored.SeriesTitleLanguage);
            Assert.Equal(["scjp", "scjp"], restored.Advanced.SubtitleRenameRules.Select(r => r.OriginalSuffix));
            Assert.Equal(["zh-Hans", "ja"], restored.Advanced.SubtitleRenameRules.Select(r => r.FinalSuffix));
            Assert.Equal(["srt"], restored.Advanced.SubtitleFormatPreference);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public string ApplicationPath => root;
        public string WebPath => root;
        public string DataPath => root;
        public string ImagesPath => root;
        public string PluginsPath => root;
        public string ThemesPath => root;
        public string ConfigurationsPath => root;
        public string LogsPath => root;
    }
}
