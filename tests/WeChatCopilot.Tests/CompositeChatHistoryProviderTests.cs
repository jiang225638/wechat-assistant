using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class CompositeChatHistoryProviderTests
{
    private sealed class FakeHistoryProvider : IChatHistoryProvider
    {
        private readonly ChatHistoryResult _historyResult;
        private readonly string? _activeContact;
        private readonly ChatConnectionStatus _status;

        public int FetchCallCount { get; private set; }
        public int ActiveCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public string SourceName { get; }

        public FakeHistoryProvider(
            string sourceName,
            ChatHistoryResult historyResult,
            string? activeContact,
            ChatConnectionStatus status)
        {
            SourceName = sourceName;
            _historyResult = historyResult;
            _activeContact = activeContact;
            _status = status;
        }

        public Task<ChatHistoryResult> FetchHistoryAsync(string contactName, CancellationToken cancellationToken = default)
        {
            FetchCallCount++;
            return Task.FromResult(_historyResult);
        }

        public Task<string?> GetActiveContactNameAsync(CancellationToken cancellationToken = default)
        {
            ActiveCallCount++;
            return Task.FromResult(_activeContact);
        }

        public Task<IReadOnlyList<WeChatSessionSummary>> GetRecentSessionsAsync(int maxCount = 40, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(_activeContact))
            {
                IReadOnlyList<WeChatSessionSummary> list = new[]
                {
                    new WeChatSessionSummary("wxid_test", _activeContact, _activeContact, _activeContact, 0)
                };
                return Task.FromResult(list);
            }
            return Task.FromResult<IReadOnlyList<WeChatSessionSummary>>(Array.Empty<WeChatSessionSummary>());
        }

        public Task<ChatConnectionStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(_status);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task FetchHistoryAsync_WhenPrimarySucceeds_ReturnsPrimaryAndSkipsFallback()
    {
        var messages = new List<HistoryMessage>
        {
            new(MessageRole.Incoming, "你好", DateTime.UtcNow)
        };
        var primary = new FakeHistoryProvider(
            "WCDB4 本地直读",
            new ChatHistoryResult(true, messages, null, "WCDB4 本地直读"),
            "好友A",
            new ChatConnectionStatus(true, @"E:\xwechat_files\wxid_1", "wxid_1", 10, "OK", "WCDB4 本地直读"));

        var fallback = new FakeHistoryProvider(
            "TraceMemo API",
            new ChatHistoryResult(true, Array.Empty<HistoryMessage>(), null, "TraceMemo API"),
            "好友B",
            new ChatConnectionStatus(true, "http://127.0.0.1:6131", "", 5, "OK", "TraceMemo API"));

        using var composite = new CompositeChatHistoryProvider(primary, fallback);
        var result = await composite.FetchHistoryAsync("好友A");

        Assert.True(result.Success);
        Assert.Equal("WCDB4 本地直读", result.SourceName);
        Assert.Single(result.Messages);
        Assert.Equal(1, primary.FetchCallCount);
        Assert.Equal(0, fallback.FetchCallCount);
    }

    [Fact]
    public async Task FetchHistoryAsync_WhenPrimaryFails_FallsBackToSecondary()
    {
        var fallbackMessages = new List<HistoryMessage>
        {
            new(MessageRole.Outgoing, "兜底消息", DateTime.UtcNow)
        };
        var primary = new FakeHistoryProvider(
            "WCDB4 本地直读",
            new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), "未找到密钥", "WCDB4 本地直读"),
            null,
            new ChatConnectionStatus(false, "", "", 0, "未找到密钥", "WCDB4 本地直读"));

        var fallback = new FakeHistoryProvider(
            "TraceMemo API",
            new ChatHistoryResult(true, fallbackMessages, null, "TraceMemo API"),
            "好友B",
            new ChatConnectionStatus(true, "http://127.0.0.1:6131", "", 5, "OK", "TraceMemo API"));

        using var composite = new CompositeChatHistoryProvider(primary, fallback);
        var result = await composite.FetchHistoryAsync("好友B");

        Assert.True(result.Success);
        Assert.Equal("TraceMemo API", result.SourceName);
        Assert.Single(result.Messages);
        Assert.Equal(1, primary.FetchCallCount);
        Assert.Equal(1, fallback.FetchCallCount);
    }

    [Fact]
    public async Task FetchHistoryAsync_WhenAllProvidersFail_ReturnsCombinedErrorWithoutThrowing()
    {
        var primary = new FakeHistoryProvider(
            "WCDB4 本地直读",
            new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), "密钥未配置", "WCDB4 本地直读"),
            null,
            new ChatConnectionStatus(false, "", "", 0, "密钥未配置", "WCDB4 本地直读"));

        var fallback = new FakeHistoryProvider(
            "TraceMemo API",
            new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), "6131 端口拒绝连接", "TraceMemo API"),
            null,
            new ChatConnectionStatus(false, "", "", 0, "6131 端口拒绝连接", "TraceMemo API"));

        using var composite = new CompositeChatHistoryProvider(primary, fallback);
        var result = await composite.FetchHistoryAsync("好友C");

        Assert.False(result.Success);
        Assert.Empty(result.Messages);
        Assert.NotNull(result.Error);
        Assert.Contains("密钥未配置", result.Error);
    }

    [Fact]
    public void CreateDefault_OnlyInitializesWcdb4ProviderWithoutTraceMemo()
    {
        var settings = new AiSettings { WeChatDbRoot = @"E:\test" };
        using var provider = CompositeChatHistoryProvider.CreateDefault(settings);
        Assert.Equal("微信 4.x 本地库直读", provider.SourceName);
    }
}
