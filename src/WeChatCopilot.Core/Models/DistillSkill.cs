using System.Text.Json.Serialization;

namespace WeChatCopilot.Core.Models;

/// <summary>
/// 人格蒸馏技能（Skill）定义：
/// 支持将不同的认知框架（女娲心智、职场商业、亲密关系、麦肯锡逻辑或用户自定义）应用于联系人画像蒸馏。
/// </summary>
public sealed record DistillSkill
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public string Icon { get; init; }
    public string SystemPrompt { get; init; }
    public IReadOnlyList<string> Dimensions { get; init; }
    public bool IsBuiltIn { get; init; }

    /// <summary>用于实时聊天 AI 建议的实战作战心法与沟通指南（若为空则自动使用技能说明与提示词兜底）。</summary>
    public string? AdviceGuideline { get; init; }

    /// <summary>用于实时聊天回复的专属风格标签（如兔导的高位推拉/情境流拉扯，职场的职业干练/闭环汇报）。</summary>
    public IReadOnlyList<string> SuggestedTones { get; init; }

    /// <summary>本地物理文件路径（如来自绑定的 SKILL.md 文件）。</summary>
    public string? SourceFilePath { get; init; }

    [JsonConstructor]
    public DistillSkill(
        string Id,
        string Name,
        string Description,
        string Icon,
        string SystemPrompt,
        IReadOnlyList<string>? Dimensions = null,
        bool IsBuiltIn = false,
        string? AdviceGuideline = null,
        IReadOnlyList<string>? SuggestedTones = null,
        string? SourceFilePath = null)
    {
        this.Id = Id;
        this.Name = Name;
        this.Description = Description;
        this.Icon = Icon;
        this.SystemPrompt = SystemPrompt;
        this.Dimensions = Dimensions ?? Array.Empty<string>();
        this.IsBuiltIn = IsBuiltIn;
        this.AdviceGuideline = AdviceGuideline;
        this.SuggestedTones = SuggestedTones ?? Array.Empty<string>();
        this.SourceFilePath = SourceFilePath;
    }

    public DistillSkill(
        string Id,
        string Name,
        string Description,
        string Icon,
        string SystemPrompt,
        bool IsBuiltIn)
        : this(Id, Name, Description, Icon, SystemPrompt, Array.Empty<string>(), IsBuiltIn, null, null, null)
    {
    }

    /// <summary>获取当前技能在实时聊天 AI 建议中的有效作战策略指南。</summary>
    public string GetEffectiveAdviceGuideline()
    {
        if (!string.IsNullOrWhiteSpace(AdviceGuideline))
        {
            return AdviceGuideline;
        }

        // 自定义或导入的外部技能：自动提取其核心提示词与说明作为作战指导
        return $"【{Icon} {Name} 专属战术心法】：\n" +
               $"{Description}\n\n" +
               $"【核心体系准则】：\n{SystemPrompt}";
    }

    /// <summary>获取当前技能在实时聊天 AI 建议中的推荐风格标签（若未定义则使用通用风格）。</summary>
    public IReadOnlyList<string> GetEffectiveSuggestedTones()
    {
        if (SuggestedTones is { Count: > 0 })
        {
            return SuggestedTones;
        }

        return new[] { "高情商", "直接", "专业", "缓和", "幽默" };
    }

    /// <summary>下拉框展示标题：图标 + 名称。</summary>
    public string DisplayTitle => $"{Icon} {Name}";

    /// <summary>简明摘要展示。</summary>
    public string SummaryText => $"{Icon} {Name} · {Description}";
}

/// <summary>
/// 内置官方蒸馏技能预设集。
/// </summary>
public static class DistillSkillPresets
{
    /// <summary>默认技能 ID：亲密关系与情感偏好 (intimacy)。</summary>
    public const string DefaultSkillId = "intimacy";

    /// <summary>1. 职场商业认知：同事.skill 与商业协作哲学。</summary>
    public static readonly DistillSkill Workplace = new(
        Id: "workplace",
        Name: "职场与商业认知",
        Description: "聚焦职场与商业协同：提炼工作交付偏好、沟通协作习惯、权责边界感、利益考量导向与汇报习惯",
        Icon: "💼",
        SystemPrompt:
            "你是资深职场沟通与商业组织行为学专家。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
            "【核心原则】：仅分析 [对方] 作为职场/商业协作伙伴的行为特征与交付风格，严禁引用 [我] 的原话，证据必须 100% 摘录 [对方] 亲口原话！\n\n" +
            "请从以下 6 个职场商业专业维度提炼 [对方] 的画像（每维度 1 行，禁止重复）：\n" +
            "1. 交付导向 (结果导向 vs 过程同步，长文排版 vs 零碎快报，推崇的沟通工具与沟通效率)\n" +
            "2. 推进能动性 (职场能动性：主动推进型 vs 被动响应型，闭环意识，抗压与担当)\n" +
            "3. 权衡决策法 (方案权衡：先看可行性还是先算ROI投入产出比？看重数据佐证 vs 领导背书)\n" +
            "4. 职业防备心 (面对需求突变、工期延误、责任推诿时的反应，职场防备心与压力应对方式)\n" +
            "5. 利益诉求导向 (利益诉求：最在乎的是KPI达标、风险规避、工作量透明、个人成就感还是组织认可)\n" +
            "6. 职场禁忌 (职场大忌：极度反感的行为，如甩锅推诿、越级汇报、非工作时间突袭、未对齐擅自决定等)\n\n" +
            "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白）：\n" +
            "维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
            "（特别注意：维度必须从【交付导向/推进能动性/权衡决策法/职业防备心/利益诉求导向/职场禁忌】选取；评分请给出有梯度的真实量化分）",
        Dimensions: new[] { "交付导向", "推进能动性", "权衡决策法", "职业防备心", "利益诉求导向", "职场禁忌" },
        IsBuiltIn: true,
        AdviceGuideline:
            "【职场与商业协同作战准则】：\n" +
            "1. BLUF 原则（Bottom Line Up Front）：结论先行，开门见山汇报结果或提炼方案，再提供支撑事实与数据。\n" +
            "2. 权责边界与闭环意识：明确职责归属、时间节点（DDL）与明确的下一步行动项（Next Steps），形成完整行动闭环。\n" +
            "3. 专业克制与不卑不亢：情绪脱敏、就事论事、给出可落地选项（给选择题而非填空题），严谨留痕且捍卫工作边界。",
        SuggestedTones: new[] { "职业干练", "闭环汇报", "边界防守", "方案推动" });

    /// <summary>3. 亲密情感偏好：亲密关系心理学与情感诉求。</summary>
    public static readonly DistillSkill Intimacy = new(
        Id: "intimacy",
        Name: "亲密关系与情感偏好",
        Description: "聚焦人际交往与亲密心理：提炼依恋倾向（安全/焦虑/回避）、情绪价值敏感点、爱与认可的表达偏好、亲密雷区",
        Icon: "❤️",
        SystemPrompt:
            "你是一位精通亲密关系心理学、依恋理论与情感沟通的情感大师。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
            "【核心原则】：仅分析 [对方] 的情感依恋特质、心理防御与真实情感诉求，证据必须 100% 摘录 [对方] 亲口原话！\n\n" +
            "请从以下 6 个亲密关系维度提炼 [对方] 的情感偏好画像（每维度 1 行，禁止重复）：\n" +
            "1. 情感表达 (直球示好 vs 矜持被动，依赖分享欲 vs 保持独立，撒娇/傲娇/理智克制倾向)\n" +
            "2. 依恋安全感 (安全型/焦虑型/回避型倾向，在关系中的心理安全感与信任门槛)\n" +
            "3. 推进节奏 (慢热审慎 vs 凭感觉冲动，面对冲突时倾向于冷处理、即刻沟通还是寻求安慰)\n" +
            "4. 情绪敏感度 (对已读不回、语气冷淡、话题冷场时的敏感脆弱度，面对关怀的接纳程度)\n" +
            "5. 核心情感需求 (极度看重的是情绪价值、时刻报备、现实陪伴、共同成长还是个人独立空间)\n" +
            "6. 亲密禁忌雷区 (情感底线：绝对零容忍的行为，如敷衍应付、欺骗隐瞒、过度说教打压、中央空调等)\n\n" +
            "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白）：\n" +
            "维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
            "（特别注意：维度必须从【情感表达/依恋安全感/推进节奏/情绪敏感度/核心情感需求/亲密禁忌雷区】选取；评分给出梯度分值）",
        Dimensions: new[] { "情感表达", "依恋安全感", "推进节奏", "情绪敏感度", "核心情感需求", "亲密禁忌雷区" },
        IsBuiltIn: true,
        AdviceGuideline:
            "【亲密关系与情感沟通准则】：\n" +
            "1. 情绪先行，事实在后：优先接住对方的情绪颗粒度与感受，给予坚定的情绪价值与心理安全感，切忌讲大道理说教。\n" +
            "2. 非暴力沟通（NVC）：清晰觉察【观察、感受、需要、请求】，放下防卫与冷战，温和坦陈真实在乎。\n" +
            "3. 脆弱表达与台阶修补：用真诚化解距离，主动递台阶，避免胜负欲对抗与好为人师。",
        SuggestedTones: new[] { "深层共情", "真诚直球", "温情化解", "趣味调侃" });

    /// <summary>4. 麦肯锡逻辑视角：结构化与理性思维。</summary>
    public static readonly DistillSkill Logical = new(
        Id: "logical",
        Name: "麦肯锡逻辑视角",
        Description: "聚焦结构化分析与理性推演：提炼结论先行偏好、事实推导风格、因果论证严密性、思维盲点与逻辑防备心",
        Icon: "🎯",
        SystemPrompt:
            "你是麦肯锡资深结构化思维与逻辑分析顾问。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
            "【核心原则】：穿透言语表象，提炼 [对方] 的底层逻辑思考框架与因果论证习惯，证据必须 100% 摘录 [对方] 亲口原话！\n\n" +
            "请从以下 6 个结构化思维维度提炼 [对方] 的逻辑认知画像（每维度 1 行，禁止重复）：\n" +
            "1. 结构化表达 (金字塔结构结论先行 vs 漫谈铺陈，事实与观点是否清晰区分，用词精确度)\n" +
            "2. 事实求证度 (客观事实驱动 vs 直觉信念驱动，遇到反例时的开放包容度)\n" +
            "3. 因果推导严密 (第一性原理拆解 vs 归纳法对齐，对边界条件和关键变量的严谨审视)\n" +
            "4. 辩论抗逆力 (面对逻辑质疑与漏洞指出时的防御机制，理智辩论 vs 情绪化反击)\n" +
            "5. 逻辑闭环度 (崇尚逻辑自洽、真实有效、严密闭环与数据说服力)\n" +
            "6. 逻辑反模式 (逻辑禁忌：极度排斥逻辑漏洞、偷换概念、以偏概全、空洞口号与无数据支撑的武断结论)\n\n" +
            "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白）：\n" +
            "维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
            "（特别注意：维度必须从【结构化表达/事实求证度/因果推导严密/辩论抗逆力/逻辑闭环度/逻辑反模式】选取；评分给出梯度分值）",
        Dimensions: new[] { "结构化表达", "事实求证度", "因果推导严密", "辩论抗逆力", "逻辑闭环度", "逻辑反模式" },
        IsBuiltIn: true,
        AdviceGuideline:
            "【麦肯锡结构化与理性思维准则】：\n" +
            "1. 金字塔原理：自上而下、逻辑自洽、中心论点明确，支撑论据层层递进。\n" +
            "2. MECE 原则：相互独立、完全穷尽，结构化拆解问题要素，严格区分客观事实与主观观点。\n" +
            "3. 因果严密与第一性原理：击破虚假归因与思维漏洞，从根本变量推导结论，提供具备说服力的数据或逻辑支撑。",
        SuggestedTones: new[] { "金字塔结论", "结构拆解", "事实论证", "理性攻防" });

    /// <summary>全部内置技能列表。</summary>
    public static readonly IReadOnlyList<DistillSkill> AllBuiltIn = new[]
    {
        Workplace,
        Intimacy,
        Logical
    };

    /// <summary>根据 ID 查找内置技能。</summary>
    public static DistillSkill? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Intimacy;
        return AllBuiltIn.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Intimacy;
    }
}
