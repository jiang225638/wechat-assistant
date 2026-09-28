namespace WeChatCopilot.Core.Models;

/// <summary>
/// AI 补全结果（M3）。失败不抛异常，用 Success=false + Error 表达，便于 UI 直接展示。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Text">成功时的生成文本。</param>
/// <param name="Error">失败时的错误描述。</param>
/// <param name="Model">实际使用的模型名。</param>
public sealed record AiReply(bool Success, string Text, string? Error = null, string? Model = null);
