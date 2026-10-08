namespace WeChatCopilot.Core.Models;

/// <summary>
/// 画像特质（FR-3）：persona 的最小单元＝维度 + 特质描述 + 置信度 + 证据原话 + 量化评分。
/// 观察层（证据原话）与推断层（特质+置信度+量化分）分离，结论锚定证据。
/// </summary>
/// <param name="Dimension">专业维度（沟通风格/性格能量/决策模式/情绪阈值/价值锚点/隐形雷区）。</param>
/// <param name="Attribute">特质描述（推断层）。</param>
/// <param name="Confidence">置信度 0~1。</param>
/// <param name="Evidence">证据原话列表（观察层）。</param>
/// <param name="Score">该维度量化评分（0~100，用于绘制雷达图），缺省根据置信度计算。</param>
public sealed record PersonaTrait(
    string Dimension,
    string Attribute,
    double Confidence,
    IReadOnlyList<string> Evidence,
    double Score = 0.0)
{
    private readonly double _score = Score;

    /// <summary>该维度量化评分（0~100）。若未显式指定则依据置信度动态推导。</summary>
    public double Score
    {
        get => _score > 0 ? _score : Math.Clamp(Math.Round(Confidence * 100), 35, 95);
        init => _score = value;
    }
    /// <summary>卡片右侧置信度文本。</summary>
    public string ConfidenceText => Confidence.ToString("0.00");

    /// <summary>置信度百分比整数 (0~100)。</summary>
    public int ConfidencePercent => (int)Math.Clamp(Math.Round(Confidence * 100), 0, 100);

    /// <summary>置信度百分比文本，如 "85%"。</summary>
    public string ConfidencePercentText => $"{ConfidencePercent}%";

    /// <summary>量化评分整数 (0~100)。</summary>
    public int ScoreInt => (int)Math.Clamp(Math.Round(Score), 10, 100);

    /// <summary>量化评分显示文本，如 "88分"。</summary>
    public string ScoreText => $"{ScoreInt}分";

    /// <summary>是否有证据。</summary>
    public bool HasEvidence => Evidence is { Count: > 0 };

    /// <summary>证据摘要，例如 "3 条原话证据"。</summary>
    public string EvidenceSummary => HasEvidence ? $"{Evidence.Count} 条原话证据" : "无直接证据引用";

    /// <summary>卡片证据行：每条原话一行带引号；无证据时空串。</summary>
    public string EvidenceText =>
        Evidence is null || Evidence.Count == 0
            ? string.Empty
            : string.Join("\n", Evidence.Select(q => "“" + q + "”"));

    /// <summary>
    /// 女娲心智蒸馏分层标签（Nuwa Skill 认知操作系统对应层级）：
    /// 表达DNA / 心智模型 / 决策启发式 / 诚实边界 / 价值锚点 / 反模式雷区。
    /// </summary>
    public string NuwaTag => NormalizeNuwaTag(Dimension, Attribute);

    /// <summary>
    /// 根据维度与特质描述归类到女娲 5 层认知操作系统。
    /// </summary>
    public static string NormalizeNuwaTag(string dimension, string attribute)
    {
        string text = (dimension + " " + attribute).ToLowerInvariant();
        if (text.Contains("表达dna") || text.Contains("表达风格") || text.Contains("沟通风格") || text.Contains("语言风格"))
            return "🧬 表达DNA";
        if (text.Contains("心智模型") || text.Contains("性格能量") || text.Contains("性格心理") || text.Contains("mbti"))
            return "🧠 心智模型";
        if (text.Contains("决策启发式") || text.Contains("决策模式") || text.Contains("启发式") || text.Contains("决断"))
            return "⚡ 决策启发式";
        if (text.Contains("诚实边界") || text.Contains("情绪阈值") || text.Contains("情绪模式") || text.Contains("压力应对"))
            return "🛡️ 诚实边界";
        if (text.Contains("反模式") || text.Contains("隐形雷区") || text.Contains("边界禁忌") || text.Contains("雷区"))
            return "⚠️ 反模式·雷区";
        if (text.Contains("价值锚点") || text.Contains("意图需求") || text.Contains("核心关切"))
            return "🎯 价值锚点";
        return "🧩 认知操作系统";
    }

    /// <summary>提示词/解析共用行格式：维度 | 特质 | 置信度: x | 证据: 原话;;原话。</summary>
    public string FormatLine() =>
        Dimension + " | " + Attribute +
        " | 置信度: " + ConfidenceText +
        " | 证据: " + (Evidence is null ? string.Empty : string.Join(";;", Evidence));
}

