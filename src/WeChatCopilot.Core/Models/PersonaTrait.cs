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
    public int ConfidencePercent
    {
        get => (int)Math.Clamp(Math.Round(Confidence * 100), 0, 100);
        set { }
    }

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
    /// 多技能认知识别标签（支持女娲、兔导情景流、职场商业、亲密关系、麦肯锡等）：
    /// 自动根据所属技能维度与特质描述动态生成专业高亮徽章。
    /// </summary>
    public string NuwaTag => CategoryTag;

    /// <summary>专属技能方法论分类标签。</summary>
    public string CategoryTag => NormalizeCategoryTag(Dimension, Attribute);

    /// <summary>
    /// 根据维度与特质描述归类到对应技能的核心方法论标签。
    /// </summary>
    public static string NormalizeCategoryTag(string dimension, string attribute)
    {
        string text = (dimension + " " + attribute).ToLowerInvariant();

        // 0. 女娲认知核心识别（若显式指定女娲六大维度，优先赋予女娲专属高亮徽章）
        if (text.Contains("表达dna"))
            return "🧬 表达DNA";
        if (text.Contains("心智模型"))
            return "🧠 心智模型";
        if (text.Contains("决策启发式"))
            return "⚡ 决策启发式";
        if (text.Contains("诚实边界"))
            return "🛡️ 诚实边界";
        if (text.Contains("反模式"))
            return "⚠️ 反模式·雷区";
        if (text.Contains("价值锚点"))
            return "🎯 价值锚点";

        // 1. 兔导情景流 (tdskill)
        if (text.Contains("框架") || text.Contains("话语权") || text.Contains("地位") || text.Contains("迎合"))
            return "👑 高位框架";
        if (text.Contains("需求感") || text.Contains("姿态") || text.Contains("跪舔"))
            return "⚖️ 需求感管理";
        if (text.Contains("慕强") || text.Contains("吸引力") || text.Contains("奖品性") || text.Contains("高价值"))
            return "🧲 慕强吸引力";
        if (text.Contains("窗口") || text.Contains("绿灯") || text.Contains("黄灯") || text.Contains("红灯") || text.Contains("防线"))
            return "🚦 窗口状态";
        if (text.Contains("吸引主线") || text.Contains("男女话题") || text.Contains("性张力") || text.Contains("拉扯"))
            return "💘 吸引力主线";
        if (text.Contains("情感禁忌") || text.Contains("公狗") || text.Contains("撩骚") || text.Contains("说教"))
            return "🚫 情感禁忌";

        // 2. 职场商业 (workplace)
        if (text.Contains("交付") || text.Contains("汇报") || text.Contains("结果导向") || text.Contains("bluf"))
            return "📋 交付导向";
        if (text.Contains("能动性") || text.Contains("主动推进") || text.Contains("闭环"))
            return "🚀 推进能动性";
        if (text.Contains("权衡") || text.Contains("roi") || text.Contains("产出比") || text.Contains("商业决断"))
            return "📊 商业权衡";
        if (text.Contains("职业防备") || text.Contains("推诿") || text.Contains("抗压") || text.Contains("防御"))
            return "🛡️ 职业防守";
        if (text.Contains("利益诉求") || text.Contains("kpi") || text.Contains("背书") || text.Contains("认可"))
            return "🎯 利益诉求";
        if (text.Contains("职场禁忌") || text.Contains("甩锅") || text.Contains("越级") || text.Contains("职场大忌"))
            return "🚫 职场雷区";

        // 3. 亲密关系 (intimacy)
        if (text.Contains("情感表达") || text.Contains("直球") || text.Contains("矜持") || text.Contains("示好"))
            return "💌 情感表达";
        if (text.Contains("依恋") || text.Contains("安全感") || text.Contains("焦虑型") || text.Contains("回避型"))
            return "💞 依恋安全";
        if (text.Contains("推进节奏") || text.Contains("慢热") || text.Contains("审慎"))
            return "⏳ 推进节奏";
        if (text.Contains("敏感") || text.Contains("情绪价值") || text.Contains("易感") || text.Contains("已读不回"))
            return "💓 情绪敏感";
        if (text.Contains("核心需求") || text.Contains("陪伴") || text.Contains("报备") || text.Contains("独立空间"))
            return "💖 核心需求";
        if (text.Contains("亲密雷区") || text.Contains("敷衍") || text.Contains("欺骗") || text.Contains("冷暴力"))
            return "🚫 亲密雷区";

        // 4. 麦肯锡逻辑 (logical)
        if (text.Contains("金字塔") || text.Contains("结构化") || text.Contains("结论先行"))
            return "🔺 结构表达";
        if (text.Contains("事实求证") || text.Contains("客观事实") || text.Contains("数据佐证"))
            return "🔍 事实求证";
        if (text.Contains("因果严密") || text.Contains("因果推导") || text.Contains("逻辑推导"))
            return "⚙️ 因果推导";
        if (text.Contains("辩论") || text.Contains("抗逆") || text.Contains("理智攻防"))
            return "🛡️ 辩论抗逆";
        if (text.Contains("自洽") || text.Contains("mece"))
            return "🔄 逻辑闭环";
        if (text.Contains("逻辑反模式") || text.Contains("偷换概念") || text.Contains("漏洞") || text.Contains("以偏概全"))
            return "🚫 逻辑反模式";

        // 5. 女娲心智泛化匹配 (nuwa)
        if (text.Contains("表达风格") || text.Contains("沟通风格") || text.Contains("语言风格") || text.Contains("口癖"))
            return "🧬 表达DNA";
        if (text.Contains("性格能量") || text.Contains("性格心理") || text.Contains("mbti") || text.Contains("认知框架"))
            return "🧠 心智模型";
        if (text.Contains("决策模式") || text.Contains("启发式") || text.Contains("决断"))
            return "⚡ 决策启发式";
        if (text.Contains("情绪阈值") || text.Contains("情绪模式") || text.Contains("心理防御"))
            return "🛡️ 诚实边界";
        if (text.Contains("隐形雷区") || text.Contains("边界禁忌") || text.Contains("雷区"))
            return "⚠️ 反模式·雷区";
        if (text.Contains("意图需求") || text.Contains("核心关切") || text.Contains("底层信仰"))
            return "🎯 价值锚点";

        return "🧩 " + (dimension.Length > 0 ? dimension : "认知切片");
    }

    /// <summary>
    /// 是否具有专属分类方法论标签（若只是简单重复维度名称本身则不显示第二徽章，节省横向空间）。
    /// </summary>
    public bool HasDistinctCategoryTag =>
        !string.IsNullOrWhiteSpace(CategoryTag) &&
        !CategoryTag.StartsWith("🧩") &&
        !CategoryTag.Equals(Dimension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用于卡片第二徽章的显示/隐藏（避免重复显示维度名）。
    /// </summary>
    public string CategoryTagVisibility => HasDistinctCategoryTag ? "Visible" : "Collapsed";

    /// <summary>保持与老版本兼容的标签获取方法。</summary>
    public static string NormalizeNuwaTag(string dimension, string attribute) => NormalizeCategoryTag(dimension, attribute);

    /// <summary>提示词/解析共用行格式：维度 | 特质 | 可信度: x | 证据: 原话;;原话。</summary>
    public string FormatLine() =>
        Dimension + " | " + Attribute +
        " | 可信度: " + ConfidenceText +
        " | 证据: " + (Evidence is null ? string.Empty : string.Join(";;", Evidence));
}

