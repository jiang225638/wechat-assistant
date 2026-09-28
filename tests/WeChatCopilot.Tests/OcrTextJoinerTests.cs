using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

public class OcrTextJoinerTests
{
    private static OcrWord W(string t) => new(t, 0, 0, 10, 10);

    [Fact]
    public void Join_CjkWords_NoSpaces()
    {
        var joined = OcrTextJoiner.Join(new[] { W("连"), W("吃"), W("大"), W("面") });
        Assert.Equal("连吃大面", joined);
    }

    [Fact]
    public void Join_SplitRadical_MergedWithoutSpace()
    {
        // Windows.Media.Ocr 把"你"拆成"亻""尔"，拼接后至少不再带空格
        var joined = OcrTextJoiner.Join(new[] { W("亻"), W("尔") });
        Assert.Equal("亻尔", joined);
    }

    [Fact]
    public void Join_LatinWords_WithSpaces()
    {
        var joined = OcrTextJoiner.Join(new[] { W("hello"), W("world") });
        Assert.Equal("hello world", joined);
    }

    [Fact]
    public void Join_CjkPunctuation_NoSpaces()
    {
        var joined = OcrTextJoiner.Join(new[] { W("你好"), W("。"), W("世界") });
        Assert.Equal("你好。世界", joined);
    }

    [Fact]
    public void Join_MixedCjkLatin_NoSpaceAtBoundary()
    {
        var joined = OcrTextJoiner.Join(new[] { W("版本"), W("v2") });
        Assert.Equal("版本v2", joined);
    }

    [Fact]
    public void Join_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OcrTextJoiner.Join(null));
        Assert.Equal(string.Empty, OcrTextJoiner.Join(Array.Empty<OcrWord>()));
    }

    [Fact]
    public void IsCjk_DetectsRanges()
    {
        Assert.True(OcrTextJoiner.IsCjk('你'));
        Assert.True(OcrTextJoiner.IsCjk('。'));
        Assert.True(OcrTextJoiner.IsCjk('，'));
        Assert.False(OcrTextJoiner.IsCjk('a'));
        Assert.False(OcrTextJoiner.IsCjk('1'));
    }
}
