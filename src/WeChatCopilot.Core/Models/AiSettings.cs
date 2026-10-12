namespace WeChatCopilot.Core.Models;

/// <summary>
/// AI 设置（M3）：endpoint/model/温度 + 经 DPAPI 加密的 API Key（Base64）。
/// 明文密钥不落盘；<see cref="EncryptedApiKey"/> 仅能在同一 Windows 用户下解密。
/// </summary>
public sealed record AiSettings
{
    /// <summary>OpenAI 兼容接口基址（含 /v1），如 https://api.deepseek.com/v1。</summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>模型名。</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>采样温度。</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>DPAPI 加密后的 API Key（Base64）；空串表示未配置。</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>TraceMemo Local HTTP API 基址（M5 冷链路）；默认 http://127.0.0.1:6131。</summary>
    public string TraceMemoBaseUrl { get; set; } = "http://127.0.0.1:6131";

    /// <summary>请求超时时间（秒），默认 180 秒（长文本蒸馏与深度思考模型推荐 180~300 秒）。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>对话语境分析跨度（条数），默认 10 条（约 3~6 轮完整交互，兼顾情感走向与即时性）。</summary>
    public int ContextMessageCount { get; set; } = 10;

    /// <summary>外观主题模式："Dark" (默认深色), "Light" (浅色), "System" (跟随系统)。</summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>用户调整记忆的悬浮窗宽度，默认 430。</summary>
    public double WindowWidth { get; set; } = 430.0;

    /// <summary>用户调整记忆的悬浮窗高度，默认 690。</summary>
    public double WindowHeight { get; set; } = 690.0;
}

