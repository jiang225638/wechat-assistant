namespace WeChatCopilot.Core.Models;

/// <summary>
/// 画像特质（FR-3）：persona 的最小单元＝维度 + 特质描述 + 置信度 + 证据原话。
/// 观察层（证据原话）与推断层（特质+置信度）分离，结论锚定证据。
/// </summary>
/// <param name="Dimension">七维之一（稳定属性/语言风格/性格心理/情绪模式/意图需求/关系动态/边界禁忌）。</param>
/// <param name="Attribute">特质描述（推断层）。</param>
/// <param name="Confidence">置信度 0~1。</param>
/// <param name="Evidence">证据原话列表（观察层）。</param>
public sealed record PersonaTrait(
    string Dimension,
    string Attribute,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    /// <summary>卡片右侧置信度文本。</summary>
    public string ConfidenceText => Confidence.ToString("0.00");

    /// <summary>卡片证据行：每条原话一行带引号；无证据时空串。</summary>
    public string EvidenceText =>
        Evidence is null || Evidence.Count == 0
            ? string.Empty
            : string.Join("\n", Evidence.Select(q => "“" + q + "”"));

    /// <summary>提示词/解析共用行格式：维度 | 特质 | 置信度: x | 证据: 原话;;原话。</summary>
    public string FormatLine() =>
        Dimension + " | " + Attribute +
        " | 置信度: " + ConfidenceText +
        " | 证据: " + (Evidence is null ? string.Empty : string.Join(";;", Evidence));
}
