using System.Net;
using System.Text;
using System.Text.Json;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.Tests;

/// <summary>M5 TraceMemo 客户端测试（stub handler，不打真机）。</summary>
public class TraceMemoClientTests
{
    private const string ContactsJson = "[{\"id\":\"wxid_1\",\"name\":\"张三\"},{\"id\":\"wxid_2\",\"name\":\"李四\"}]";
    private const string MessagesJson =
        "[{\"role\":\"incoming\",\"text\":\"在吗\",\"ts\":\"1700000000\"}," +
        "{\"role\":\"outgoing\",\"content\":\"在的\",\"time\":\"2026-01-02T10:00:00\"}," +
        "{\"role\":\"unknown\",\"text\":\"\"}]";

    [Fact]
    public async Task FetchHistory_ParsesContactsAndMessages()
    {
        using var client = new TraceMemoClient("http://stub.local", new StubHandler(path =>
            path.Contains("/messages") ? MessagesJson : ContactsJson));

        var res = await client.FetchHistoryAsync("张三");

        Assert.True(res.Success);
        Assert.Equal(2, res.Messages.Count);  // 空 text 行被跳过
        Assert.Equal(MessageRole.Incoming, res.Messages[0].Role);
        Assert.NotNull(res.Messages[0].Timestamp);
        Assert.Equal(MessageRole.Outgoing, res.Messages[1].Role);
        Assert.Equal("在的", res.Messages[1].Text);
    }

    [Fact]
    public async Task FetchHistory_ContactNotFound_Fails()
    {
        using var client = new TraceMemoClient("http://stub.local", new StubHandler(_ => ContactsJson));

        var res = await client.FetchHistoryAsync("王五");

        Assert.False(res.Success);
        Assert.Contains("王五", res.Error);
    }

    [Fact]
    public async Task FetchHistory_HttpError_FailsWithoutThrow()
    {
        using var client = new TraceMemoClient("http://stub.local", new StubHandler(_ => null!));

        var res = await client.FetchHistoryAsync("张三");

        Assert.False(res.Success);
        Assert.Contains("500", res.Error);
    }

    [Fact]
    public async Task FetchHistory_ParsesIsSenderAndDes()
    {
        const string rawJson =
            "[{\"isSender\":false,\"text\":\"对方发的消息\"}," +
            "{\"isSender\":true,\"text\":\"我回复的消息\"}," +
            "{\"Des\":0,\"text\":\"底层Des0也是自己发出的\"}," +
            "{\"Des\":1,\"text\":\"底层Des1是对方接收的\"}]";

        using var client = new TraceMemoClient("http://stub.local", new StubHandler(path =>
            path.Contains("/messages") ? rawJson : ContactsJson));

        var res = await client.FetchHistoryAsync("张三");

        Assert.True(res.Success);
        Assert.Equal(4, res.Messages.Count);
        Assert.Equal(MessageRole.Incoming, res.Messages[0].Role);
        Assert.Equal(MessageRole.Outgoing, res.Messages[1].Role);
        Assert.Equal(MessageRole.Outgoing, res.Messages[2].Role);
        Assert.Equal(MessageRole.Incoming, res.Messages[3].Role);
    }

    [Fact]
    public async Task GetActiveContactName_FiltersGroupsAndSystem_ReturnsFirst1v1()
    {
        const string recentChatsJson = "[" +
            "{\"type\":\"single\",\"m_nsNickName\":\"服务通知\",\"wxid\":\"notifymessage\"}," +
            "{\"type\":\"group\",\"m_nsNickName\":\"某某大群\",\"wxid\":\"12345@chatroom\"}," +
            "{\"type\":\"single\",\"isOfficialAccount\":true,\"m_nsNickName\":\"微信支付\",\"wxid\":\"gh_001\"}," +
            "{\"type\":\"single\",\"m_nsNickName\":\"微信团队\",\"wxid\":\"weixin\"}," +
            "{\"type\":\"single\",\"m_nsNickName\":\"杨柳依依\",\"wxid\":\"wxid_friend_888\"}" +
            "]";

        using var client = new TraceMemoClient("http://stub.local", new StubHandler(path =>
            path.Contains("recent_chat") ? recentChatsJson : ContactsJson));

        string? contact = await client.GetActiveContactNameAsync();

        Assert.Equal("杨柳依依", contact);
    }

    [Theory]
    [InlineData("notifymessage", "服务通知", true)]
    [InlineData("notifymessage", "", true)]
    [InlineData("mphelper", "公众平台", true)]
    [InlineData("gh_abcdef", "某公众号", true)]
    [InlineData("123@chatroom", "工作群", true)]
    [InlineData("brandsessionholder", "", true)]
    [InlineData("", "微信支付", true)]
    [InlineData(null, null, true)]
    [InlineData("wxid_friend888", "杨柳依依", false)]
    [InlineData("wxid_user1", "张三", false)]
    public void IsSystemContact_ClassifiesCorrectly(string? wxid, string? nick, bool expected)
    {
        Assert.Equal(expected, TraceMemoClient.IsSystemContact(wxid, nick));
    }

    [Fact]
    public void NormalizeWeChatMessageText_ConvertsEmojiAndLinkCards()
    {
        using var doc1 = JsonDocument.Parse("{\"type\":47}");
        Assert.Equal("[动画表情]", TraceMemoClient.NormalizeWeChatMessageText("", doc1.RootElement));

        using var doc2 = JsonDocument.Parse("{\"type\":3}");
        Assert.Equal("[图片]", TraceMemoClient.NormalizeWeChatMessageText("", doc2.RootElement));

        using var doc3 = JsonDocument.Parse("{\"type\":49}");
        string xml = "<msg><appmsg><title>AI深度洞察文章</title></appmsg></msg>";
        Assert.Equal("[分享链接] AI深度洞察文章", TraceMemoClient.NormalizeWeChatMessageText(xml, doc3.RootElement));

        using var doc4 = JsonDocument.Parse("{\"type\":1}");
        Assert.Equal("你好呀", TraceMemoClient.NormalizeWeChatMessageText("你好呀", doc4.RootElement));
    }

    /// <summary>按路径返回固定 JSON 的测试 handler；body 为 null 时返回 500。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responder;

        public StubHandler(Func<string, string> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = _responder(request.RequestUri!.PathAndQuery);
            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
