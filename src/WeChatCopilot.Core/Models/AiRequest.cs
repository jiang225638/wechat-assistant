namespace WeChatCopilot.Core.Models;

/// <summary>
/// 多轮对话中的单条历史消息（Role 取值通常为 "user" 或 "assistant"）。
/// </summary>
public sealed record AiChatMessage(string Role, string Content);

/// <summary>
/// 一次 AI 补全请求（M3）：系统提示 + 用户提示 + 生成参数 + 可选多轮对话历史。
/// 不携带 API Key（密钥由提供商持有），避免密钥随请求对象扩散。
/// </summary>
/// <param name="SystemPrompt">系统提示（角色/规则）。</param>
/// <param name="UserPrompt">用户提示（本次任务输入，如对话转写）。</param>
/// <param name="Model">模型名（如 gpt-4o-mini / deepseek-chat）。</param>
/// <param name="Temperature">采样温度，0 更确定、1 更发散。</param>
/// <param name="MaxTokens">最大生成 token 数（默认 2048，避免多维度/长建议被截断）。</param>
/// <param name="HistoryMessages">可选的此前多轮对话历史（按时间升序排列的 user / assistant 消息）。</param>
public sealed record AiRequest(
    string SystemPrompt,
    string UserPrompt,
    string Model,
    double Temperature = 0.7,
    int MaxTokens = 2048,
    IReadOnlyList<AiChatMessage>? HistoryMessages = null);
