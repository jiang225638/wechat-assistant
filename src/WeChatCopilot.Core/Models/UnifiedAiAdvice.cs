namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一体化 AI 建议分析结果：将“潜台词深层意图剖析”与“多候选回复建议”深度整合，
/// 依据对方的人格画像与当前最新对话，一键呈现穿透式心理洞察与针对性回复方案。
/// </summary>
/// <param name="TargetQuote">本次重点剖析的对方最新原话。</param>
/// <param name="Literal">字面意思表象。</param>
/// <param name="Subtext">结合性格特征剖析的真实潜台词（弦外之音）。</param>
/// <param name="Intent">对方的核心心理动机与真实诉求。</param>
/// <param name="Strategy">建议采取的破局沟通策略与避坑指南。</param>
/// <param name="Suggestions">结合对方人格定制的多种风格回复建议列表。</param>
public sealed record UnifiedAiAdvice(
    string TargetQuote,
    string Literal,
    string Subtext,
    string Intent,
    string Strategy,
    IReadOnlyList<ReplySuggestion> Suggestions)
{
    /// <summary>是否存在有效的潜台词或意图剖析。</summary>
    public bool HasSubtext => !string.IsNullOrWhiteSpace(Subtext) || !string.IsNullOrWhiteSpace(Intent);

    /// <summary>是否存在有效的候选回复建议。</summary>
    public bool HasSuggestions => Suggestions is { Count: > 0 };
}
