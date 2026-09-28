using System.Globalization;
using System.Windows;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;

namespace WeChatCopilot.App;

/// <summary>
/// M3 AI 设置窗口：编辑 endpoint/model/温度/API Key。
/// Key 用 DPAPI 加密后随 <see cref="AiSettings"/> 落盘；保存后可编辑结果经 <see cref="Saved"/> 回传。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store = new();

    /// <summary>保存成功后的最新设置；取消时保持默认。</summary>
    public AiSettings Saved { get; private set; } = new();

    public SettingsWindow()
    {
        InitializeComponent();

        AiSettings s = _store.Load();
        EndpointBox.Text = s.Endpoint;
        ModelBox.Text = s.Model;
        TempBox.Text = s.Temperature.ToString(CultureInfo.InvariantCulture);

        // 回填解密后的 Key 便于查看/修改（仅内存与输入框，不落明文盘）
        KeyBox.Password = DpapiProtector.Unprotect(s.EncryptedApiKey);
        TraceMemoBox.Text = s.TraceMemoBaseUrl;
        Saved = s;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        double temp = double.TryParse(TempBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.7;

        // Key 留空 = 保留原密文（允许只改其他字段而不重输 Key）
        string encrypted = string.IsNullOrEmpty(KeyBox.Password)
            ? _store.Load().EncryptedApiKey
            : DpapiProtector.Protect(KeyBox.Password);

        Saved = new AiSettings
        {
            Endpoint = EndpointBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            Temperature = temp,
            EncryptedApiKey = encrypted,
            TraceMemoBaseUrl = TraceMemoBox.Text.Trim()
        };

        _store.Save(Saved);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
