namespace WeChatCopilot.Core.Models;

/// <summary>
/// M4 回复建议候选（FR-4）：一条语气候选 = 语气 + 回复文本 + "为什么这么说"的理由。
/// 由 <c>AiOutputParser.ParseReplySuggestions</c> 从模型输出解析得到，供悬浮窗卡片展示与一键复制。
/// </summary>
/// <param name="Tone">语气/战术风格标签（如：价值锚定反制/高位推拉/情绪托底；解析失败时为"候选"）。</param>
/// <param name="Text">回复正文（一键复制的内容）。</param>
/// <param name="Reason">理由（为什么这么说、战术动作依据）；模型未给则为空串。</param>
/// <param name="DimensionScores">针对当前战局动态提炼维度的量化得分字典（维度名 -> 0~100 分值）。</param>
/// <param name="GoalPushScore">战略目的推进力分值（0~100分，用于双轴象限图纵轴）。</param>
/// <param name="PersonaSafetyScore">画像安全稳健度分值（0~100分，用于双轴象限图横轴）。</param>
public sealed record ReplySuggestion(
    string Tone,
    string Text,
    string Reason,
    IReadOnlyDictionary<string, double>? DimensionScores = null,
    double GoalPushScore = 75,
    double PersonaSafetyScore = 80)
{
    /// <summary>卡片上的理由行文本；无理由时返回空串（卡片不显示该行）。</summary>
    public string ReasonLine => string.IsNullOrWhiteSpace(Reason) ? string.Empty : "理由：" + Reason;
}
