using System.Net;
using System.Text;
using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class GitHubReleaseUpdaterTests
{
    [Theory]
    [InlineData("3.2.0", "3.2.1", true)]
    [InlineData("v3.2.1", "v3.2.10", true)]
    [InlineData("3.2", "3.2.1", true)]
    [InlineData("3.2.5", "3.2.5", false)]
    [InlineData("3.2.10", "3.2.2", false)]
    [InlineData("1.0.16+abc123", "3.2.1", true)]
    public void IsNewerVersion_ComparesSemanticVersionsCorrectly(string currentVersion, string latestVersion, bool expectedNewer)
    {
        bool actual = GitHubReleaseUpdater.IsNewerVersion(currentVersion, latestVersion);
        Assert.Equal(expectedNewer, actual);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenNewerReleaseExists_ParsesSetupAndPortableAssets()
    {
        const string releaseJson = """
        {
            "tag_name": "v3.2.25",
            "name": "微信副驾 (WeChat Copilot) v3.2.25",
            "body": "## 更新内容\n- 修复撤回消息角色归属问题",
            "html_url": "https://github.com/jiang225638/wechat-assistant/releases/tag/v3.2.25",
            "assets": [
                {
                    "name": "WeChatCopilot_Setup_v3.2.25.exe",
                    "size": 75497472,
                    "browser_download_url": "https://github.com/jiang225638/wechat-assistant/releases/download/v3.2.25/WeChatCopilot_Setup_v3.2.25.exe"
                },
                {
                    "name": "WeChatCopilot_Portable_v3.2.25.exe",
                    "size": 73400320,
                    "browser_download_url": "https://github.com/jiang225638/wechat-assistant/releases/download/v3.2.25/WeChatCopilot_Portable_v3.2.25.exe"
                }
            ]
        }
        """;

        using var updater = new GitHubReleaseUpdater(new StubHttpHandler(releaseJson, HttpStatusCode.OK));
        var result = await updater.CheckForUpdateAsync("3.2.10");

        Assert.True(result.Success);
        Assert.True(result.HasUpdate);
        Assert.Equal("3.2.10", result.CurrentVersion);
        Assert.Equal("3.2.25", result.LatestVersion);
        Assert.Contains("修复撤回消息角色归属问题", result.ReleaseNotes);
        Assert.Equal("https://github.com/jiang225638/wechat-assistant/releases/download/v3.2.25/WeChatCopilot_Setup_v3.2.25.exe", result.SetupDownloadUrl);
        Assert.Equal("https://github.com/jiang225638/wechat-assistant/releases/download/v3.2.25/WeChatCopilot_Portable_v3.2.25.exe", result.PortableDownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenAlreadyUpToDate_ReturnsHasUpdateFalse()
    {
        const string releaseJson = """
        {
            "tag_name": "v3.2.25",
            "name": "微信副驾 (WeChat Copilot) v3.2.25",
            "body": "已是最新",
            "html_url": "https://github.com/jiang225638/wechat-assistant/releases/tag/v3.2.25",
            "assets": []
        }
        """;

        using var updater = new GitHubReleaseUpdater(new StubHttpHandler(releaseJson, HttpStatusCode.OK));
        var result = await updater.CheckForUpdateAsync("3.2.25");

        Assert.True(result.Success);
        Assert.False(result.HasUpdate);
        Assert.Equal("3.2.25", result.LatestVersion);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public StubHttpHandler(string responseBody, HttpStatusCode statusCode)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
