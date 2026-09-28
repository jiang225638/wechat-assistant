using WeChatCopilot.Data.Security;
using Xunit;

namespace WeChatCopilot.Tests;

public class DpapiProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTrip_RestoresOriginalString()
    {
        string secret = "sk-test-api-key-1234567890-abcdef";
        string cipher = DpapiProtector.Protect(secret);

        Assert.False(string.IsNullOrEmpty(cipher));
        Assert.NotEqual(secret, cipher);

        string restored = DpapiProtector.Unprotect(cipher);
        Assert.Equal(secret, restored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, DpapiProtector.Protect(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-base64!!!")]
    [InlineData("YWJjZA==")] // Valid base64 but invalid DPAPI payload
    public void Unprotect_InvalidOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, DpapiProtector.Unprotect(input));
    }
}
