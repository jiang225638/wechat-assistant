namespace WeChatCopilot.Core.Models;

/// <summary>
/// M4 潜台词/意图分析结果（FR-5 意图面板）：六维结构化字段。
/// 由 <c>AiOutputParser.ParseSubtext</c> 按固定标签解析得到；缺失字段为空串。
/// </summary>
/// <param name="Literal">字面意思。</param>
/// <param name="Subtext">潜台词（话外之意）。</param>
/// <param name="Emotion">情绪状态。</param>
/// <param name="Intent">真实意图。</param>
/// <param name="DesiredResponse">想要的回应类型。</param>
/// <param name="Strategy">建议策略。</param>
public sealed record SubtextAnalysis(
    string Literal,
    string Subtext,
    string Emotion,
    string Intent,
    string DesiredResponse,
    string Strategy)
{
    /// <summary>意图面板展示文本：固定六行；未解析出的维度标注"（未解析）"。</summary>
    public string Format()
    {
        static string Or(string v) => string.IsNullOrWhiteSpace(v) ? "（未解析）" : v;
        return
            "字面意思：" + Or(Literal) + "\n" +
            "潜台词：" + Or(Subtext) + "\n" +
            "情绪状态：" + Or(Emotion) + "\n" +
            "真实意图：" + Or(Intent) + "\n" +
            "想要的回应：" + Or(DesiredResponse) + "\n" +
            "建议策略：" + Or(Strategy);
    }
}
