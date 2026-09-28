namespace WeChatCopilot.Core.Models;

/// <summary>
/// M4 回复建议候选（FR-4）：一条语气候选 = 语气 + 回复文本 + "为什么这么说"的理由。
/// 由 <c>AiOutputParser.ParseReplySuggestions</c> 从模型输出解析得到，供悬浮窗卡片展示与一键复制。
/// </summary>
/// <param name="Tone">语气标签（共情/专业/幽默/直接/缓和；解析失败时为"候选"）。</param>
/// <param name="Text">回复正文（一键复制的内容）。</param>
/// <param name="Reason">理由（为什么这么说）；模型未给则为空串。</param>
public sealed record ReplySuggestion(string Tone, string Text, string Reason)
{
    /// <summary>卡片上的理由行文本；无理由时返回空串（卡片不显示该行）。</summary>
    public string ReasonLine => string.IsNullOrWhiteSpace(Reason) ? string.Empty : "理由：" + Reason;
}
