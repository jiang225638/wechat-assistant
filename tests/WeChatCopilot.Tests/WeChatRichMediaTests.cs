using System.Text.Json;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class WeChatRichMediaTests
{
    [Fact]
    public void ReplaceWeChatEmojis_ReplacesPopularTagsWithColoredEmojis()
    {
        string raw = "真不错～我也刚打上卡[嘿哈] 上班先打水[捂脸] 导游[得意] 预约[呲牙]";
        string result = WeChatEmojiTable.ReplaceWeChatEmojis(raw);

        Assert.Contains("🥳", result);
        Assert.Contains("🤦‍♂️", result);
        Assert.Contains("😎", result);
        Assert.Contains("😁", result);
        Assert.DoesNotContain("[嘿哈]", result);
        Assert.DoesNotContain("[捂脸]", result);
    }

    [Fact]
    public void ReplaceWeChatEmojis_PreservesNonEmojiTextAndUnknownBrackets()
    {
        string raw = "这是正常的[测试内容]和[12345]括号";
        string result = WeChatEmojiTable.ReplaceWeChatEmojis(raw);

        Assert.Equal(raw, result);
    }

    [Fact]
    public void DecryptDat_WithJpegMagicXor_DerivesKeyAndDecryptsCorrectly()
    {
        byte xorKey = 0x7A;
        byte[] originalJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

        byte[] encrypted = new byte[originalJpeg.Length];
        for (int i = 0; i < originalJpeg.Length; i++)
        {
            encrypted[i] = (byte)(originalJpeg[i] ^ xorKey);
        }

        var (decrypted, ext) = WeChatDatImageDecoder.DecryptDat(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal(".jpg", ext);
        Assert.Equal(originalJpeg, decrypted);
    }

    [Fact]
    public void DecryptDat_WithPngMagicXor_DerivesKeyAndDecryptsCorrectly()
    {
        byte xorKey = 0x3C;
        byte[] originalPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        byte[] encrypted = new byte[originalPng.Length];
        for (int i = 0; i < originalPng.Length; i++)
        {
            encrypted[i] = (byte)(originalPng[i] ^ xorKey);
        }

        var (decrypted, ext) = WeChatDatImageDecoder.DecryptDat(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal(".png", ext);
        Assert.Equal(originalPng, decrypted);
    }

    [Fact]
    public void ParseMessageRow_WithCustomEmojiXml_ExtractsMd5AndCdnUrl()
    {
        string xml = """
        <msg><emoji md5="abc123456789" cdnurl="http://emoji.qpic.cn/wx_emoji/abc123456789/0" type="2" width="120" height="120" /></msg>
        """;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            local_id = 301,
            create_time = 1760001000,
            local_type = 47,
            is_send = 0,
            message_content = xml
        }));

        HistoryMessage? msg = Wcdb4MessageDecoder.ParseMessageRow(doc.RootElement, Array.Empty<string>());

        Assert.NotNull(msg);
        Assert.Contains("abc123456789", msg!.Text);
        Assert.Contains("http://emoji.qpic.cn", msg.Text);
        Assert.Contains("[动画表情: md5=", msg.Text);
    }

    [Fact]
    public void ParseMessageRow_WithShareCardXml_ExtractsUrlTitleAndDes()
    {
        string xml = """
        <msg><appmsg appid="" sdkver="0">
            <title>腾讯发布最新财报</title>
            <des>净利润稳健增长超市场预期</des>
            <url>https://news.qq.com/article/123456</url>
            <type>5</type>
        </appmsg></msg>
        """;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            local_id = 302,
            create_time = 1760001100,
            local_type = 49,
            is_send = 1,
            message_content = xml
        }));

        HistoryMessage? msg = Wcdb4MessageDecoder.ParseMessageRow(doc.RootElement, Array.Empty<string>());

        Assert.NotNull(msg);
        Assert.Contains("[分享卡片: url=https://news.qq.com/article/123456", msg!.Text);
        Assert.Contains("title=腾讯发布最新财报", msg.Text);
        Assert.Contains("des=净利润稳健增长超市场预期", msg.Text);
    }

    [Fact]
    public void StripMediaMarkupAndCleanForAi_RemovesMarkupParameters()
    {
        string rawCard = "[分享卡片: url=https://example.com|title=科技前沿深度报道|des=关于人工智能的讨论]";
        string rawEmoji = "[动画表情: md5=abc&cdn=http://example.com/1.gif]";
        string rawImg = "[图片: md5=xyz789]";

        string cleanCard = Wcdb4MessageDecoder.StripMediaMarkup(rawCard);
        string cleanEmoji = Wcdb4MessageDecoder.StripMediaMarkup(rawEmoji);
        string cleanImg = Wcdb4MessageDecoder.StripMediaMarkup(rawImg);

        Assert.Equal("[分享] 科技前沿深度报道", cleanCard);
        Assert.Equal("[动画表情]", cleanEmoji);
        Assert.Equal("[图片]", cleanImg);

        string transcript = PromptBuilder.CleanMessageTextForAi(rawCard);
        Assert.Equal("[分享: 科技前沿深度报道]", transcript);
    }
}
