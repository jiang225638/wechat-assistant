using System.Net;
using System.Text;
using WeChatCopilot.AI;
using WeChatCopilot.Core.Diagnostics;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

/// <summary>OpenAiCompatibleProvider 与 AppLog 单元测试。</summary>
public class OpenAiCompatibleProviderTests
{
    private static readonly AiRequest s_sampleRequest = new(
        SystemPrompt: "You are a helpful assistant.",
        UserPrompt: "Say hello",
        Model: "deepseek-chat",
        Temperature: 0.7,
        MaxTokens: 256);

    [Fact]
    public async Task CompleteAsync_Success_ParsesChoiceContentAndSendsBearerHeader()
    {
        string? capturedAuth = null;
        string? capturedPath = null;

        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedPath = req.RequestUri?.AbsolutePath;
            const string json = """
                {
                  "choices": [
                    {
                      "message": {
                        "role": "assistant",
                        "content": "你好，很高兴认识你！"
                      }
                    }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        using var provider = new OpenAiCompatibleProvider(
            "https://api.example.com/v1",
            () => "sk-test-123",
            handler);

        AiReply reply = await provider.CompleteAsync(s_sampleRequest);

        Assert.True(reply.Success);
        Assert.Equal("你好，很高兴认识你！", reply.Text);
        Assert.Null(reply.Error);
        Assert.Equal("Bearer sk-test-123", capturedAuth);
        Assert.Equal("/v1/chat/completions", capturedPath);
    }

    [Fact]
    public async Task CompleteAsync_EmptyApiKey_ReturnsFriendlyErrorWithoutNetworkCall()
    {
        bool networkCalled = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var provider = new OpenAiCompatibleProvider(
            "https://api.example.com/v1",
            () => "   ",
            handler);

        AiReply reply = await provider.CompleteAsync(s_sampleRequest);

        Assert.False(reply.Success);
        Assert.Contains("API Key", reply.Error);
        Assert.False(networkCalled);
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ReturnsStatusCodeInError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_api_key\"}", Encoding.UTF8, "application/json")
            }));

        using var provider = new OpenAiCompatibleProvider(
            "https://api.example.com/v1",
            () => "sk-bad",
            handler);

        AiReply reply = await provider.CompleteAsync(s_sampleRequest);

        Assert.False(reply.Success);
        Assert.Contains("HTTP 401", reply.Error);
    }

    [Fact]
    public async Task CompleteAsync_UserCancelled_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new StubHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var provider = new OpenAiCompatibleProvider(
            "https://api.example.com/v1",
            () => "sk-test",
            handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CompleteAsync(s_sampleRequest, cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_MalformedJson_ReturnsFailureReplyWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-valid-json", Encoding.UTF8, "application/json")
            }));

        using var provider = new OpenAiCompatibleProvider(
            "https://api.example.com/v1",
            () => "sk-test",
            handler);

        AiReply reply = await provider.CompleteAsync(s_sampleRequest);

        Assert.False(reply.Success);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
    }

    [Fact]
    public void AppLog_WritesDailyLogFileSafely()
    {
        string originalDir = AppLog.LogDirectory;
        string tempDir = Path.Combine(AppContext.BaseDirectory, "applog-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLog.LogDirectory = tempDir;
            AppLog.Warn("UnitTest", "测试警告日志", new InvalidOperationException("模拟异常"));

            string[] files = Directory.GetFiles(tempDir, "copilot-*.log");
            Assert.Single(files);
            string content = File.ReadAllText(files[0], Encoding.UTF8);
            Assert.Contains("[WARN] [UnitTest] 测试警告日志", content);
            Assert.Contains("InvalidOperationException: 模拟异常", content);
        }
        finally
        {
            AppLog.LogDirectory = originalDir;
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // 忽略测试临时目录清理异常
            }
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
