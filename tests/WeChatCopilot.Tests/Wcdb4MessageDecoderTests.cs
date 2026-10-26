using System.Text;
using System.Text.Json;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using Xunit;
using ZstdSharp;

namespace WeChatCopilot.Tests;

public class Wcdb4MessageDecoderTests
{
    [Fact]
    public void DecodeMaybeCompressed_WithZstdHexString_ReturnsDecompressedUtf8Text()
    {
        string original = "今晚八点老地方见，不见不散！";
        byte[] rawUtf8 = Encoding.UTF8.GetBytes(original);
        using var compressor = new Compressor();
        byte[] compressed = compressor.Wrap(rawUtf8).ToArray();
        string hex = Convert.ToHexString(compressed).ToLowerInvariant();

        string decoded = Wcdb4MessageDecoder.DecodeMaybeCompressed(hex);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeMaybeCompressed_WithZstdBase64String_ReturnsDecompressedUtf8Text()
    {
        string original = "<msg><appmsg><title>测试压缩卡片</title></appmsg></msg>";
        byte[] rawUtf8 = Encoding.UTF8.GetBytes(original);
        using var compressor = new Compressor();
        byte[] compressed = compressor.Wrap(rawUtf8).ToArray();
        string b64 = Convert.ToBase64String(compressed);

        string decoded = Wcdb4MessageDecoder.DecodeMaybeCompressed(b64);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeMaybeCompressed_WithCorruptedZstdHeader_DoesNotThrowAndReturnsEmpty()
    {
        // Zstd magic 0xFD2FB528 in little-endian: 28 B5 2F FD followed by garbage
        byte[] corrupted = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0xFF, 0x00, 0x01, 0x02, 0x03 };
        string hex = Convert.ToHexString(corrupted);

        string decoded = Wcdb4MessageDecoder.DecodeMaybeCompressed(hex);

        Assert.Equal(string.Empty, decoded);
    }

    [Fact]
    public void ParseMessageRow_WithSelfWxidOrIsSendFlag_ResolvesOutgoingAndIncomingCorrectly()
    {
        using var doc1 = JsonDocument.Parse("""
        {
            "local_id": 101,
            "create_time": 1760000100,
            "local_type": 1,
            "is_send": 0,
            "sender_username": "wxid_4ev1rkdk0jgm22",
            "message_content": "这是我自己发出的消息"
        }
        """);

        using var doc2 = JsonDocument.Parse("""
        {
            "local_id": 102,
            "create_time": 1760000200,
            "local_type": 1,
            "is_send": 0,
            "sender_username": "wxid_friend_001",
            "message_content": "这是对方发来的消息"
        }
        """);

        string[] myCandidates = new[] { "wxid_4ev1rkdk0jgm22", "wxid_4ev1rkdk0jgm22_1889" };

        HistoryMessage? msg1 = Wcdb4MessageDecoder.ParseMessageRow(doc1.RootElement, myCandidates);
        HistoryMessage? msg2 = Wcdb4MessageDecoder.ParseMessageRow(doc2.RootElement, myCandidates);

        Assert.NotNull(msg1);
        Assert.Equal(MessageRole.Outgoing, msg1!.Role);
        Assert.Equal("这是我自己发出的消息", msg1.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000100).UtcDateTime, msg1.Timestamp);

        Assert.NotNull(msg2);
        Assert.Equal(MessageRole.Incoming, msg2!.Role);
        Assert.Equal("这是对方发来的消息", msg2.Text);
    }

    [Fact]
    public void ParseMessageRow_WithQuoteReplyXml_ExtractsReplyAndReferencedText()
    {
        string xml = """
        <msg><appmsg appid="" sdkver="0">
            <title>没问题，那就周六见</title>
            <type>57</type>
            <refermsg>
                <type>1</type>
                < displayname >小美</ displayname >
                <content>这周六下午有空一起喝咖啡吗？</content>
            </refermsg>
        </appmsg></msg>
        """;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            local_id = 201,
            create_time = 1760000500,
            local_type = 49,
            is_send = 1,
            message_content = xml
        }));

        HistoryMessage? msg = Wcdb4MessageDecoder.ParseMessageRow(doc.RootElement, Array.Empty<string>());

        Assert.NotNull(msg);
        Assert.Equal(MessageRole.Outgoing, msg!.Role);
        Assert.Contains("没问题，那就周六见", msg.Text);
        Assert.Contains("这周六下午有空一起喝咖啡吗？", msg.Text);
    }

    [Fact]
    public void ResolveSessionUsername_MatchesRemarkNicknameAndWxid()
    {
        var sessions = new List<Wcdb4SessionItem>
        {
            new(Username: "wxid_111", Nickname: "A-张总", Remark: "张总(客户)", Alias: "zhang_boss", LastTimestamp: 100),
            new(Username: "wxid_222", Nickname: "林小鹿", Remark: "", Alias: "xiaolu99", LastTimestamp: 200)
        };

        Assert.Equal("wxid_111", Wcdb4MessageDecoder.ResolveSessionUsername(sessions, "张总(客户)"));
        Assert.Equal("wxid_111", Wcdb4MessageDecoder.ResolveSessionUsername(sessions, "张总"));
        Assert.Equal("wxid_222", Wcdb4MessageDecoder.ResolveSessionUsername(sessions, "林小鹿"));
        Assert.Equal("wxid_222", Wcdb4MessageDecoder.ResolveSessionUsername(sessions, "xiaolu99"));
        Assert.Equal("wxid_222", Wcdb4MessageDecoder.ResolveSessionUsername(sessions, "wxid_222"));
    }

    [Fact]
    public void SelectActiveSingleChatContact_SkipsGroupsOfficialAndSystemAccounts()
    {
        var sessions = new List<Wcdb4SessionItem>
        {
            new(Username: "123456@chatroom", Nickname: "周末约球群", Remark: "", Alias: "", LastTimestamp: 500),
            new(Username: "gh_abcdef", Nickname: "招商银行", Remark: "", Alias: "", LastTimestamp: 400),
            new(Username: "filehelper", Nickname: "文件传输助手", Remark: "", Alias: "", LastTimestamp: 350),
            new(Username: "wxid_target_1", Nickname: "苏苏", Remark: "苏苏(相亲)", Alias: "", LastTimestamp: 300)
        };

        string? active = Wcdb4MessageDecoder.SelectActiveSingleChatContact(sessions, selfNickname: "我");

        Assert.Equal("苏苏(相亲)", active);
    }

    [Fact]
    public void BuildRecentSessionSummaries_FiltersGroupsAndFormatsUnreadAndRemarks()
    {
        var sessions = new List<Wcdb4SessionItem>
        {
            new(Username: "123456@chatroom", Nickname: "项目群", Remark: "", Alias: "", LastTimestamp: 1760000900, UnreadCount: 5),
            new(Username: "filehelper", Nickname: "文件传输助手", Remark: "", Alias: "", LastTimestamp: 1760000800, UnreadCount: 0),
            new(Username: "wxid_friend_b", Nickname: "陈小雨", Remark: "小雨", Alias: "", LastTimestamp: 1760000700, UnreadCount: 3, Summary: "晚上一起吃饭吗"),
            new(Username: "wxid_friend_a", Nickname: "老王", Remark: "", Alias: "", LastTimestamp: 1760000600, UnreadCount: 0, Summary: "好的")
        };

        var summaries = Wcdb4MessageDecoder.BuildRecentSessionSummaries(sessions, selfNickname: "本人", maxCount: 10);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("小雨", summaries[0].DisplayName);
        Assert.Equal("陈小雨", summaries[0].Nickname);
        Assert.Equal(3, summaries[0].UnreadCount);
        Assert.Contains("🔴[3未读]", summaries[0].DropdownLabel);
        Assert.Contains("小雨 (陈小雨)", summaries[0].DropdownLabel);

        Assert.Equal("老王", summaries[1].DisplayName);
        Assert.Equal(0, summaries[1].UnreadCount);
        Assert.DoesNotContain("未读", summaries[1].DropdownLabel);
    }
}
