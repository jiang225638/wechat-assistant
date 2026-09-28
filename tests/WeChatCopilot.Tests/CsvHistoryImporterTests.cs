using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.Tests;

/// <summary>M5 CSV 历史导入器测试：表头/引号/角色别名/坏行。</summary>
public class CsvHistoryImporterTests
{
    [Fact]
    public void Parse_WithHeader_AndRoles()
    {
        string csv =
            "role,text,ts\n" +
            "incoming,你好,2026-01-02T10:00:00\n" +
            "outgoing,在的,\n" +
            "other,嗯嗯,2026-01-02T11:00:00";

        var list = CsvHistoryImporter.Parse(csv);

        Assert.Equal(3, list.Count);
        Assert.Equal(MessageRole.Incoming, list[0].Role);
        Assert.Equal("你好", list[0].Text);
        Assert.NotNull(list[0].Timestamp);
        Assert.Equal(MessageRole.Outgoing, list[1].Role);
        Assert.Null(list[1].Timestamp);
        Assert.Equal(MessageRole.Incoming, list[2].Role);
    }

    [Fact]
    public void Parse_QuotedFieldWithComma()
    {
        var list = CsvHistoryImporter.Parse("incoming,\"你好, 在忙吗\",2026-01-02T10:00:00");

        Assert.Single(list);
        Assert.Equal("你好, 在忙吗", list[0].Text);
    }

    [Fact]
    public void Parse_ChineseRoleAliases()
    {
        var list = CsvHistoryImporter.Parse("对方,甲\n我,乙");

        Assert.Equal(MessageRole.Incoming, list[0].Role);
        Assert.Equal(MessageRole.Outgoing, list[1].Role);
    }

    [Fact]
    public void Parse_SkipsBadLines()
    {
        var list = CsvHistoryImporter.Parse("incoming\nincoming,,\nincoming,有效");

        Assert.Single(list);
        Assert.Equal("有效", list[0].Text);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(CsvHistoryImporter.Parse(null));
        Assert.Empty(CsvHistoryImporter.Parse("  \n "));
    }
}
