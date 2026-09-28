using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class TraceMemoTokenProviderTests
{
    [Fact]
    public void TryGetToken_WithEnvironmentVariable_ReturnsTrueAndToken()
    {
        string original = Environment.GetEnvironmentVariable("TRACEMEMO_TOKEN") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("TRACEMEMO_TOKEN", "test-bearer-token-12345");
            bool ok = TraceMemoTokenProvider.TryGetToken(out string token, out string? error);

            Assert.True(ok);
            Assert.Equal("test-bearer-token-12345", token);
            Assert.Null(error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACEMEMO_TOKEN", string.IsNullOrEmpty(original) ? null : original);
        }
    }
}
