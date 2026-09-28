namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一次 AI 补全请求（M3）：系统提示 + 用户提示 + 生成参数。
/// 不携带 API Key（密钥由提供商持有），避免密钥随请求对象扩散。
/// </summary>
/// <param name="SystemPrompt">系统提示（角色/规则）。</param>
/// <param name="UserPrompt">用户提示（本次任务输入，如对话转写）。</param>
/// <param name="Model">模型名（如 gpt-4o-mini / deepseek-chat）。</param>
/// <param name="Temperature">采样温度，0 更确定、1 更发散。</param>
/// <param name="MaxTokens">最大生成 token 数。</param>
public sealed record AiRequest(
    string SystemPrompt,
    string UserPrompt,
    string Model,
    double Temperature = 0.7,
    int MaxTokens = 512);
