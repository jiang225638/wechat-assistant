using System.IO;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WeChatCopilot_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new SettingsStore(_tempDir);
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

    [Fact]
    public void Load_WhenFileNotExists_ReturnsDefaultSettings()
    {
        var settings = _store.Load();

        Assert.NotNull(settings);
        Assert.Equal(10, settings.ContextMessageCount);
        Assert.Equal("Light", settings.ThemeMode);
        Assert.Equal(430.0, settings.WindowWidth);
        Assert.Equal(690.0, settings.WindowHeight);
        Assert.Equal(1.0, settings.WindowOpacity);
        Assert.Null(settings.SelfNickname);
        Assert.Equal(string.Empty, settings.WeChatDbRoot);
        Assert.Equal(string.Empty, settings.EncryptedWeChatDbKey);
        Assert.Equal(string.Empty, settings.NativeWcdbDllDir);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesNewProperties()
    {
        var original = new AiSettings
        {
            Endpoint = "https://custom.api.com/v1",
            Model = "gpt-5-turbo",
            Temperature = 0.85,
            ContextMessageCount = 15,
            ThemeMode = "Light",
            WindowWidth = 520.0,
            WindowHeight = 780.0,
            WindowOpacity = 0.85,
            SelfNickname = "测试本人昵称",
            WeChatDbRoot = @"E:\xwechat_files\wxid_test_1234",
            EncryptedWeChatDbKey = "encrypted_key_payload",
            NativeWcdbDllDir = @"C:\wcdb\win32\x64"
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal("https://custom.api.com/v1", loaded.Endpoint);
        Assert.Equal("gpt-5-turbo", loaded.Model);
        Assert.Equal(0.85, loaded.Temperature);
        Assert.Equal(15, loaded.ContextMessageCount);
        Assert.Equal("Light", loaded.ThemeMode);
        Assert.Equal(520.0, loaded.WindowWidth);
        Assert.Equal(780.0, loaded.WindowHeight);
        Assert.Equal(0.85, loaded.WindowOpacity);
        Assert.Equal("测试本人昵称", loaded.SelfNickname);
        Assert.Equal(@"E:\xwechat_files\wxid_test_1234", loaded.WeChatDbRoot);
        Assert.Equal("encrypted_key_payload", loaded.EncryptedWeChatDbKey);
        Assert.Equal(@"C:\wcdb\win32\x64", loaded.NativeWcdbDllDir);
    }
}
