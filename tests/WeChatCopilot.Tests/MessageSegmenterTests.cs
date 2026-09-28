using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;

namespace WeChatCopilot.Tests;

public class MessageSegmenterTests
{
    // captureWidth=1000，默认锚点：左边缘<=350 为对方(左)，右边缘>=650 为自己(右)
    private const int Width = 1000;

    private static OcrLine Line(string text, double x, double y, double w = 100, double h = 20)
        => new(text, new[] { new OcrWord(text, x, y, w, h) });

    private static OcrResult Result(params OcrLine[] lines)
        => new(string.Join("\n", lines.Select(l => l.Text)), lines, "test", TimeSpan.Zero);

    [Fact]
    public void Segment_ClassifiesLeftAsIncoming_RightAsOutgoing()
    {
        var seg = new MessageSegmenter();
        var ocr = Result(
            Line("你好", x: 50, y: 100),
            Line("在的", x: 700, y: 160, w: 250)); // 右边缘 950

        var msgs = seg.Segment(ocr, Width);

        Assert.Equal(2, msgs.Count);
        Assert.Equal(MessageRole.Incoming, msgs[0].Role);
        Assert.Equal("你好", msgs[0].Text);
        Assert.Equal(MessageRole.Outgoing, msgs[1].Role);
        Assert.Equal("在的", msgs[1].Text);
    }

    [Fact]
    public void Segment_MergesConsecutiveSameRoleLinesIntoOneMessage()
    {
        var seg = new MessageSegmenter();
        var ocr = Result(
            Line("第一行", x: 50, y: 100, h: 20),
            Line("第二行", x: 50, y: 128, h: 20)); // 间距 8 <= 20*1.8

        var msgs = seg.Segment(ocr, Width);

        Assert.Single(msgs);
        Assert.Equal("第一行\n第二行", msgs[0].Text);
        Assert.Equal(2, msgs[0].LineCount);
    }

    [Fact]
    public void Segment_SplitsWhenVerticalGapLarge()
    {
        var seg = new MessageSegmenter();
        var ocr = Result(
            Line("消息A", x: 50, y: 100, h: 20), // bottom 120
            Line("消息B", x: 50, y: 300, h: 20)); // 间距 180 > 36

        var msgs = seg.Segment(ocr, Width);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("消息A", msgs[0].Text);
        Assert.Equal("消息B", msgs[1].Text);
        Assert.All(msgs, m => Assert.Equal(MessageRole.Incoming, m.Role));
    }

    [Fact]
    public void Segment_DropsCenteredTimestamp_AndUsesItAsSeparator()
    {
        var seg = new MessageSegmenter();
        var ocr = Result(
            Line("在吗", x: 50, y: 100),
            Line("12:30", x: 460, y: 200, w: 80), // 居中 -> Unknown，被丢弃
            Line("在的", x: 700, y: 300, w: 250));

        var msgs = seg.Segment(ocr, Width);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("在吗", msgs[0].Text);
        Assert.Equal("在的", msgs[1].Text);
        Assert.DoesNotContain(msgs, m => m.Role == MessageRole.Unknown);
    }

    [Fact]
    public void Segment_RoleChangeSplitsMessages_EvenWhenAdjacent()
    {
        var seg = new MessageSegmenter();
        var ocr = Result(
            Line("问", x: 50, y: 100),
            Line("答", x: 700, y: 128, w: 250)); // 间距小但方向变化

        var msgs = seg.Segment(ocr, Width);

        Assert.Equal(2, msgs.Count);
        Assert.Equal(MessageRole.Incoming, msgs[0].Role);
        Assert.Equal(MessageRole.Outgoing, msgs[1].Role);
    }

    [Fact]
    public void Segment_UsesMinMaxAcrossMultipleWords()
    {
        var seg = new MessageSegmenter();
        // 一行两个词：整体左边缘 40，右边缘 40+200+30+300=... 判定按 min left / max right
        var line = new OcrLine("你好 世界", new[]
        {
            new OcrWord("你好", 40, 100, 200, 20),
            new OcrWord("世界", 270, 100, 300, 20), // 右边缘 570
        });
        var msgs = seg.Segment(Result(line), Width);

        Assert.Single(msgs);
        Assert.Equal(MessageRole.Incoming, msgs[0].Role); // 左边缘 40 <= 350
    }

    [Fact]
    public void Segment_NullOrInvalid_ReturnsEmpty()
    {
        var seg = new MessageSegmenter();

        Assert.Empty(seg.Segment(null, Width));
        Assert.Empty(seg.Segment(Result(), Width));
        Assert.Empty(seg.Segment(Result(Line("x", 50, 100)), 0));
    }

    [Fact]
    public void Segment_RespectsCustomAnchors()
    {
        // 自定义：左锚点放宽到 0.5，则 x=450 也算对方
        var seg = new MessageSegmenter(new SegmentationOptions { AnchorLeftMaxRatio = 0.5 });
        var msgs = seg.Segment(Result(Line("中间偏左", x: 450, y: 100, w: 100)), Width);

        Assert.Single(msgs);
        Assert.Equal(MessageRole.Incoming, msgs[0].Role);
    }
}
