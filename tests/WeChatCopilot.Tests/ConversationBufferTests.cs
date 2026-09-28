using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;

namespace WeChatCopilot.Tests;

public class ConversationBufferTests
{
    private static ChatMessage In(string text) => new(MessageRole.Incoming, text);
    private static ChatMessage Out(string text) => new(MessageRole.Outgoing, text);

    [Fact]
    public void Ingest_FirstFrame_AppendsAll()
    {
        var buf = new ConversationBuffer();

        int added = buf.Ingest(new[] { In("A"), Out("B"), In("C") });

        Assert.Equal(3, added);
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void Ingest_OverlappingFrame_AppendsOnlyNewSuffix()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("A"), Out("B"), In("C") });

        // 向上/向下滚动后，帧与缓冲尾部重叠 [B,C]，只应追加 D
        int added = buf.Ingest(new[] { Out("B"), In("C"), Out("D") });

        Assert.Equal(1, added);
        Assert.Equal(4, buf.Count);
        Assert.Equal("D", buf.Messages[^1].Text);
    }

    [Fact]
    public void Ingest_IdenticalFrame_AppendsNothing()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("A"), Out("B") });

        int added = buf.Ingest(new[] { In("A"), Out("B") });

        Assert.Equal(0, added);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Ingest_DisjointFrame_AppendsAll()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("A"), In("B") });

        int added = buf.Ingest(new[] { In("X"), In("Y") });

        Assert.Equal(2, added);
        Assert.Equal(4, buf.Count);
    }

    [Fact]
    public void Ingest_NormalizesTextForDedup()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("你好 世界") });

        // 换行/多余空白归一化后 Key 相同 -> 视为重复
        int added = buf.Ingest(new[] { In("你好\n世界") });

        Assert.Equal(0, added);
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void Ingest_RoleIsPartOfIdentity()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("收到") });

        // 同文本但方向不同 -> 不算重叠
        int added = buf.Ingest(new[] { Out("收到") });

        Assert.Equal(1, added);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Ingest_RespectsCapacity_TrimsOldest()
    {
        var buf = new ConversationBuffer(capacity: 3);

        buf.Ingest(new[] { In("A"), In("B"), In("C"), In("D") });

        Assert.Equal(3, buf.Count);
        Assert.Equal(new[] { "B", "C", "D" }, buf.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void Ingest_NullOrEmpty_ReturnsZero()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("A") });

        Assert.Equal(0, buf.Ingest(null));
        Assert.Equal(0, buf.Ingest(Array.Empty<ChatMessage>()));
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buf = new ConversationBuffer();
        buf.Ingest(new[] { In("A"), In("B") });

        buf.Clear();

        Assert.Equal(0, buf.Count);
        Assert.Empty(buf.Messages);
    }
}
