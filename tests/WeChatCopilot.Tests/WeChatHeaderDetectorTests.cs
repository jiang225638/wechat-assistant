using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Windows;

namespace WeChatCopilot.Tests;

public class WeChatHeaderDetectorTests
{
    [Theory]
    [InlineData("Raelin (2)", "Raelin")]
    [InlineData("苏晴（15）", "苏晴")]
    [InlineData("张小美 ...", "张小美")]
    [InlineData("  Alex · ", "Alex")]
    public void CleanContactTitle_ShouldStripCountsAndTrailingNoise(string raw, string expected)
    {
        string result = WeChatHeaderDetector.CleanContactTitle(raw);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatchContactFromOcrLines_ShouldMatchKnownContactAndIgnoreChrome()
    {
        var known = new[] { "Raelin", "张小美", "林晚晚" };
        var ocrLines = new[] { "搜索", "微信", "Raelin (1)", "..." };

        string? matched = WeChatHeaderDetector.MatchContactFromOcrLines(
            ocrLines,
            known,
            selfNickname: "我的昵称",
            allowUnmatchedFallback: false);

        Assert.Equal("Raelin", matched);
    }

    [Fact]
    public void MatchContactFromOcrLines_ShouldTolerateSingleCharOcrNoiseOnLongCjkName()
    {
        var known = new[] { "阿狸不吃鱼", "Raelin" };
        var ocrLines = new[] { "阿狸不吃渔" };

        string? matched = WeChatHeaderDetector.MatchContactFromOcrLines(
            ocrLines,
            known,
            selfNickname: null,
            allowUnmatchedFallback: false);

        Assert.Equal("阿狸不吃鱼", matched);
    }

    [Fact]
    public void MatchContactFromOcrLines_ShouldRejectUnmatchedNoiseWhenFallbackDisabled()
    {
        var known = new[] { "Raelin", "张小美" };
        var ocrLines = new[] { "Visual Studio 2026", "正在编译项目" };

        string? matched = WeChatHeaderDetector.MatchContactFromOcrLines(
            ocrLines,
            known,
            selfNickname: null,
            allowUnmatchedFallback: false);

        Assert.Null(matched);
    }

    [Fact]
    public void BuildKnownContactCandidates_ShouldMapNicknameAndRemarkToCanonicalDisplayName()
    {
        var sessions = new[]
        {
            new WeChatSessionSummary("wxid_1", "女神备注", "Raelin", "女神备注")
        };

        var (candidates, canonicalMap) = WeChatHeaderDetector.BuildKnownContactCandidates(sessions, new[] { "历史档案A" });

        Assert.Contains("女神备注", candidates);
        Assert.Contains("Raelin", candidates);
        Assert.Equal("女神备注", canonicalMap["Raelin"]);
        Assert.Equal("女神备注", canonicalMap["女神备注"]);
        Assert.Equal("历史档案A", canonicalMap["历史档案A"]);
    }

    [Theory]
    [InlineData("wcdb api.dll")]
    [InlineData("wcdb_api.dll")]
    [InlineData("OverlayWindow.cs")]
    [InlineData("https://api.openai.com")]
    public void MatchContactFromOcrLines_ShouldAlwaysRejectCodeAndFileExtensionNoise(string noiseLine)
    {
        string? matched = WeChatHeaderDetector.MatchContactFromOcrLines(
            new[] { noiseLine },
            Array.Empty<string>(),
            selfNickname: null,
            allowUnmatchedFallback: true);

        Assert.Null(matched);
    }
}
