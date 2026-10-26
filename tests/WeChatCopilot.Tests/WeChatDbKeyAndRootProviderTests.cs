using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;
using Xunit;

namespace WeChatCopilot.Tests;

public class WeChatDbKeyAndRootProviderTests : IDisposable
{
    private readonly string _tempDir;

    public WeChatDbKeyAndRootProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WeChatCopilot_WcdbKeyTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData("wxid_4ev1rkdk0jgm22_1889", "wxid_4ev1rkdk0jgm22")]
    [InlineData("wxid_abc123", "wxid_abc123")]
    [InlineData("custom_user_a1b2", "custom_user")]
    public void CleanAccountDirName_ExtractsExpectedWxid(string dirName, string expectedWxid)
    {
        string actual = WeChatDbKeyAndRootProvider.CleanAccountDirName(dirName);
        Assert.Equal(expectedWxid, actual);
    }

    [Fact]
    public void ComputeAccountRootHash_MatchesTraceMemoSha256()
    {
        string hash = WeChatDbKeyAndRootProvider.ComputeAccountRootHash(@"E:\xwechat_files\wxid_4ev1rkdk0jgm22_1889\");
        Assert.Equal("8fc3c790993a92e0726dd03daa5a9e807c8753fc866ff115e9105e3db94bc408", hash);
    }

    [Fact]
    public void ResolveAccountRoot_FromParentXWeChatFiles_SelectsLatestSessionDbAccount()
    {
        string xwechatRoot = Path.Combine(_tempDir, "xwechat_files");
        string acc1 = Path.Combine(xwechatRoot, "wxid_old_1111");
        string acc2 = Path.Combine(xwechatRoot, "wxid_new_2222");

        Directory.CreateDirectory(Path.Combine(acc1, "db_storage", "session"));
        Directory.CreateDirectory(Path.Combine(acc2, "db_storage", "session"));

        string session1 = Path.Combine(acc1, "db_storage", "session", "session.db");
        string session2 = Path.Combine(acc2, "db_storage", "session", "session.db");
        File.WriteAllText(session1, "dummy1");
        File.WriteAllText(session2, "dummy2");
        File.SetLastWriteTimeUtc(session1, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(session2, DateTime.UtcNow);

        bool ok = WeChatDbKeyAndRootProvider.TryResolveAccountRoot(xwechatRoot, out string resolvedRoot, out string sessionDbPath, out string wxid, out string? error);

        Assert.True(ok, error);
        Assert.Equal(Path.GetFullPath(acc2), Path.GetFullPath(resolvedRoot));
        Assert.Equal(Path.GetFullPath(session2), Path.GetFullPath(sessionDbPath));
        Assert.Equal("wxid_new", wxid);
    }

    [Fact]
    public void TryDecryptTraceMemoDatabaseKey_WithValidSafeStorageFiles_Returns64CharHexKey()
    {
        string fakeTraceMemoAppData = Path.Combine(_tempDir, "TraceMemo");
        string dbKeysDir = Path.Combine(fakeTraceMemoAppData, "database-keys");
        Directory.CreateDirectory(dbKeysDir);

        string accountRoot = @"E:\xwechat_files\wxid_4ev1rkdk0jgm22_1889";
        string expectedKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // 1. 生成 32 字节 masterKey 并用 DPAPI 加密写入 Local State
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        byte[] dpapiEncrypted = ProtectedData.Protect(masterKey, null, DataProtectionScope.CurrentUser);
        byte[] prefixedMaster = new byte[5 + dpapiEncrypted.Length];
        "DPAPI"u8.CopyTo(prefixedMaster);
        Buffer.BlockCopy(dpapiEncrypted, 0, prefixedMaster, 5, dpapiEncrypted.Length);

        string localStateJson = JsonSerializer.Serialize(new
        {
            os_crypt = new
            {
                encrypted_key = Convert.ToBase64String(prefixedMaster)
            }
        });
        File.WriteAllText(Path.Combine(fakeTraceMemoAppData, "Local State"), localStateJson);

        // 2. 用 AES-256-GCM (v10 前缀) 加密 64 位密钥并写入 database-keys/<sha256>.bin
        byte[] plainBytes = Encoding.UTF8.GetBytes("0x" + expectedKey);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(masterKey, 16))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        byte[] binPayload = new byte[3 + 12 + cipherBytes.Length + 16];
        "v10"u8.CopyTo(binPayload);
        Buffer.BlockCopy(nonce, 0, binPayload, 3, 12);
        Buffer.BlockCopy(cipherBytes, 0, binPayload, 15, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, binPayload, 15 + cipherBytes.Length, 16);

        string hash = WeChatDbKeyAndRootProvider.ComputeAccountRootHash(accountRoot);
        File.WriteAllBytes(Path.Combine(dbKeysDir, hash + ".bin"), binPayload);

        // 3. 验证解密
        bool ok = WeChatDbKeyAndRootProvider.TryGetKeyFromTraceMemoDir(fakeTraceMemoAppData, accountRoot, out string actualKey, out string? error);

        Assert.True(ok, error);
        Assert.Equal(expectedKey, actualKey);
    }

    [Fact]
    public void TryResolveConfiguration_PrefersAiSettingsOverTraceMemoFallback()
    {
        string accDir = Path.Combine(_tempDir, "wxid_custom_9999");
        Directory.CreateDirectory(Path.Combine(accDir, "db_storage", "session"));
        string sessionDb = Path.Combine(accDir, "db_storage", "session", "session.db");
        File.WriteAllText(sessionDb, "dummy");

        string rawKey = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var settings = new AiSettings
        {
            WeChatDbRoot = accDir,
            EncryptedWeChatDbKey = DpapiProtector.Protect(rawKey)
        };

        bool ok = WeChatDbKeyAndRootProvider.TryResolveConfiguration(
            settings,
            out string resolvedRoot,
            out string resolvedSessionDb,
            out string resolvedWxid,
            out string resolvedKey,
            out string? error);

        Assert.True(ok, error);
        Assert.Equal(Path.GetFullPath(accDir), Path.GetFullPath(resolvedRoot));
        Assert.Equal(Path.GetFullPath(sessionDb), Path.GetFullPath(resolvedSessionDb));
        Assert.Equal("wxid_custom", resolvedWxid);
        Assert.Equal(rawKey, resolvedKey);
    }
}
