using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using WeChatCopilot.AI;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;

namespace WeChatCopilot.App;

/// <summary>
/// M3 AI 设置窗口：编辑 endpoint/model/温度/API Key。
/// Key 用 DPAPI 加密后随 <see cref="AiSettings"/> 落盘；保存后可编辑结果经 <see cref="Saved"/> 回传。
/// 支持单次快速连接测试，验证模型与接口是否可用。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store = new();
    private CancellationTokenSource? _testCts;

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

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        string endpoint = EndpointBox.Text.Trim();
        string model = ModelBox.Text.Trim();
        string key = KeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = DpapiProtector.Unprotect(_store.Load().EncryptedApiKey);
        }

        double temp = double.TryParse(TempBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.7;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowTestResult(false, "缺少接口地址", "请先输入接口基址（例如 https://api.openai.com/v1 或中转站地址）。", 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            ShowTestResult(false, "缺少模型名", "请先输入模型名称（例如 gpt-4o、gpt-5.5、deepseek-chat 等）。", 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            ShowTestResult(false, "缺少 API Key", "请先在上方输入 API Key。", 0);
            return;
        }

        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowTestResult(false, "接口地址格式错误", "接口基址必须以 http:// 或 https:// 开头（例如 https://yostoken.top/v1）。", 0);
            return;
        }

        TestButton.IsEnabled = false;
        TestButton.Content = "⏳ 测试中...";
        ShowTestingStatus(endpoint, model);

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = _testCts.Token;

        var sw = Stopwatch.StartNew();
        try
        {
            using var provider = new OpenAiCompatibleProvider(endpoint, () => key);
            var req = new AiRequest(
                SystemPrompt: "你是一个智能测试助手。请只回复一句话：“连接成功，AI接口与模型正常运作中！”。不要有多余的文字。",
                UserPrompt: "测试当前 AI 接口是否可用，请回复确认信息。",
                Model: model,
                Temperature: temp,
                MaxTokens: 80);

            AiReply reply = await provider.CompleteAsync(req, token);
            sw.Stop();

            if (reply.Success)
            {
                string text = reply.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowTestResult(false, "模型返回内容为空",
                        "接口返回成功状态码，但模型回复内容为空。\n可能原因：该模型不支持当前请求格式，或中转服务未正确透传模型回复。",
                        sw.ElapsedMilliseconds);
                }
                else
                {
                    ShowTestResult(true, "测试成功！AI 正常响应",
                        $"模型回复：\n{text}",
                        sw.ElapsedMilliseconds);
                }
            }
            else
            {
                string diagnosis = DiagnoseError(reply.Error ?? "未知错误", endpoint, model);
                ShowTestResult(false, "测试失败：无法获取 AI 响应",
                    $"{reply.Error}\n\n{diagnosis}",
                    sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            ShowTestResult(false, "请求超时",
                $"在 25 秒内未收到服务商响应。\n💡 排查建议：\n1. 请检查网络连接或系统代理是否正常。\n2. 检查接口域名是否被防火墙阻拦，或服务商服务器响应缓慢。",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string diagnosis = DiagnoseError(ex.Message, endpoint, model);
            ShowTestResult(false, "连接发生异常",
                $"{ex.Message}\n\n{diagnosis}",
                sw.ElapsedMilliseconds);
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "⚡ 测试 AI 响应";
        }
    }

    private void ShowTestingStatus(string endpoint, string model)
    {
        TestResultBorder.Visibility = Visibility.Visible;
        TestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        TestStatusIcon.Text = "⏳";
        TestStatusTitle.Text = "正在请求 AI 接口...";
        TestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        TestLatencyText.Text = string.Empty;
        TestDetailBox.Text = $"正在向 [{endpoint}] 发送测试请求，模型：[{model}]，请稍候...";
    }

    private void ShowTestResult(bool success, string title, string detail, long elapsedMs)
    {
        TestResultBorder.Visibility = Visibility.Visible;
        if (success)
        {
            TestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            TestStatusIcon.Text = "✅";
            TestStatusTitle.Text = title;
            TestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            TestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }
        else
        {
            TestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TestStatusIcon.Text = "❌";
            TestStatusTitle.Text = title;
            TestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }

        TestDetailBox.Text = detail;
    }

    private static string DiagnoseError(string error, string endpoint, string model)
    {
        var sb = new StringBuilder("💡 诊断与排查建议：\n");

        if (error.Contains("401") || error.Contains("Unauthorized"))
        {
            sb.AppendLine("• 身份验证失败 (401)：API Key 错误、已过期或余额受限，请确认 Key 是否复制完整。");
        }
        else if (error.Contains("404") || error.Contains("Not Found"))
        {
            sb.AppendLine("• 路径或模型未找到 (404)：");
            if (!endpoint.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  - 当前接口基址为「{endpoint}」，未以 /v1 结尾。OpenAI 兼容接口通常需以 /v1 结尾（例如 {endpoint.TrimEnd('/')}/v1）。");
            }
            sb.AppendLine($"  - 服务商可能不支持当前模型名「{model}」，请到服务商官网确认模型名拼写。");
        }
        else if (error.Contains("429"))
        {
            sb.AppendLine("• 请求超限或欠费 (429)：API 额度已用尽、账户欠费，或触发了服务商的请求频率限制。");
        }
        else if (error.Contains("400") || error.Contains("Bad Request"))
        {
            sb.AppendLine($"• 请求无效 (400)：模型名「{model}」可能不被支持或请求参数与此模型不兼容。");
        }
        else if (error.Contains("refused") || error.Contains("No such host") || error.Contains("积极拒绝") || error.Contains("远程主机强迫关闭"))
        {
            sb.AppendLine("• 无法建立网络连接：请检查接口网址是否正确，以及本机网络或代理（VPN）设置。");
        }
        else
        {
            sb.AppendLine("• 请检查接口地址、模型名以及 API Key 是否与服务商提供的信息一致。");
        }

        return sb.ToString().TrimEnd();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();

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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _testCts?.Cancel();
        _testCts?.Dispose();
    }
}
