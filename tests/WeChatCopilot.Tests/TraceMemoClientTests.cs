using System.Net;
using System.Text;
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
