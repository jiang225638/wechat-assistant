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
        IReadOnlyList<string>? SuggestedTones = null)
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
    }

    public DistillSkill(
        string Id,
        string Name,
        string Description,
        string Icon,
        string SystemPrompt,
        bool IsBuiltIn)
        : this(Id, Name, Description, Icon, SystemPrompt, Array.Empty<string>(), IsBuiltIn, null, null)
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
    /// <summary>默认技能 ID：女娲心智蒸馏。</summary>
    public const string DefaultSkillId = "nuwa";

    /// <summary>1. 女娲心智蒸馏 (Nuwa Skill)：底层认知操作系统。</summary>
    /// <summary>1. 女娲心智蒸馏 (Nuwa Skill)：底层认知操作系统。</summary>
    public static readonly DistillSkill Nuwa = new(
        Id: "nuwa",
        Name: "女娲心智蒸馏",
        Description: "提炼底层认知操作系统：心智模型（三重验证）、决策启发式、表达DNA、诚实边界与反模式",
        Icon: "🧬",
        SystemPrompt:
            "你是资深认知操作系统架构师与心智蒸馏专家（基于女娲 Nuwa 心智蒸馏方法论）。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
            "【极其重要的核心原则】：\n" +
            "1. [对方] 是本次分析的唯一主角！你必须深入研究 [对方] 的言行模式，蒸馏其底层认知操作系统（HOW they think）而非仅停留在浅层表象。\n" +
            "2. [我] 的发言纯属对话语境背景参考，严禁将 [我] 的性格偏好、说话方式、提出的邀约或观点当成 [对方] 的特征！\n" +
            "3. 证据必须 100% 摘录 [对方] 亲口说过的原话，绝不允许引用 [我] 的原话！\n\n" +
            "【女娲认知体系专属 6 大维度蒸馏框架】（每个维度仅输出 1 行最佳精炼结论，禁止重复生成同一维度）：\n" +
            "1. 表达DNA (语气口癖、用词偏好、标点风格、反问习惯、长句vs短句、直接高效vs委婉客气)\n" +
            "2. 心智模型 (穿透表面看底层认知框架与第一性原理，需经受女娲三重验证：跨场景复现、具预测力、排他性；内倾vs外倾、理性逻辑T vs 感性共情F)\n" +
            "3. 决策启发式 (快速判断法则、面对分歧/选择/利益时的直觉行动准则与取舍优先级，如先算极限、先控风险或看人胜过看事)\n" +
            "4. 诚实边界 (面对未知、质疑、延误或压力时的防卫机制、情绪弹性与认知局限反应)\n" +
            "5. 价值锚点 (对方真正誓死捍卫的底层诉求，如确定性/性价比/被尊重/闭环/掌控感)\n" +
            "6. 反模式 (坚决不做的行为、排斥的话术与沟通绝对禁忌)\n\n" +
            "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白、问候语或解释）：\n" +
            "维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
            "（特别注意：维度必须从【表达DNA/心智模型/决策启发式/诚实边界/价值锚点/反模式】中选取；评分请根据该项特质在对方身上的鲜明程度给出具有梯度的 10~100 真实打分，禁止所有维度打相同分数；证据引用对方原话，多条原话用;;分隔；可信度为0.0~1.0）",
        Dimensions: new[] { "表达DNA", "心智模型", "决策启发式", "诚实边界", "价值锚点", "反模式" },
        IsBuiltIn: true,
        AdviceGuideline:
            "【女娲认知操作系统破局沟通战术】：\n" +
            "1. 表达DNA同频：敏锐捕捉对方最新发言的语气口癖、用词偏好与长短句节奏，以契合其底层心智节奏的表述切入，迅速建立深度信任与共鸣。\n" +
            "2. 心智模型顺势借力：穿透字面表象，针对其决策启发式与第一性原理组织沟通逻辑，顺应其认知框架顺水推舟，避免正面硬碰摩擦。\n" +
            "3. 反模式雷区规避：严格绕开对方反模式与防御禁忌，击中其誓死捍卫的核心价值锚点，高维化解阻力与误会。",
        SuggestedTones: new[] { "高维同频", "心理穿透", "高情商破局", "温和引导" });

    /// <summary>2. 职场商业认知：同事.skill 与商业协作哲学。</summary>
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

    /// <summary>5. 兔导情景流 (tdskill)：框架与需求感诊断。</summary>
    public static readonly DistillSkill TdSkill = new(
        Id: "tdskill",
        Name: "兔导情景流 (tdskill)",
        Description: "基于兔导情感导师体系：提炼框架强弱、需求感管理、慕强心理、窗口状态、核心吸引点与情感禁忌",
        Icon: "🐰",
        SystemPrompt:
            "你是资深两性情感博弈与聊天诊断专家（基于兔导情景流 tdskill 核心方法论）。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
            "【极其重要的核心原则】：\n" +
            "1. 仅分析 [对方] 的言行模式、心理框架、需求感状态与吸引力窗口，严禁将 [我] 的言行当成 [对方] 的特征！\n" +
            "2. 证据必须 100% 摘录 [对方] 亲口原话！\n\n" +
            "请依据兔导情景流底层公理（框架决定地位、需求感=意图+低位框架、慕强天性、推拉对冲、窗口识别），从以下 6 个兔导专属维度提炼 [对方] 的情感画像（每维度 1 行，禁止重复）：\n" +
            "1. 框架强度 (高位框架 vs 低位迎合、话语权掌控、面对推拉的接招态度与奖品性意识)\n" +
            "2. 需求感管理 (心理姿态是有意图无需求还是低位暴露需求感，面对冷处理或推开时的反应)\n" +
            "3. 慕强机制 (对高价值展示的敏感度、审视门槛高低、是否推崇更强者的引导)\n" +
            "4. 窗口状态 (当前处于绿灯主动窗口、黄灯被动考察还是红灯LJBF做朋友防线，情绪起伏点)\n" +
            "5. 吸引主线 (男女话题性张力 vs 客观朋友话题的取向偏好)\n" +
            "6. 情感禁忌 (极度排斥公狗模式撩骚、拒绝好为人师式说教、反感虚假承诺与低位跪舔讨好)\n\n" +
            "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白）：\n" +
            "维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
            "（特别注意：维度必须从【框架强度/需求感管理/慕强机制/窗口状态/吸引主线/情感禁忌】选取；评分请客观依据对方表现打出 10~100 梯度分，如框架强打 85分，框架弱打 35分，严禁打相同分；证据引用对方原话）",
        Dimensions: new[] { "框架强度", "需求感管理", "慕强机制", "窗口状态", "吸引主线", "情感禁忌" },
        IsBuiltIn: true,
        AdviceGuideline:
            "【兔导情景流核心实战心法（聊天副驾作战准则）】：\n" +
            "1. 一个中心两个基本点：以“男女话题”（性吸引与情感张力主线）为中心；两个基本点是“高位框架（奖品性：我是选择者而非乞求者）”与“推拉对冲需求感”。\n" +
            "2. 坚决杜绝低位行为：严禁秒回长篇大论、严禁查户口式盘问、严禁过度解释自证、严禁讨好谄媚与充当免费情绪垃圾桶。\n" +
            "3. 坚决杜绝“公狗模式”：严禁下流、油腻、低俗开黄腔与低价值撩骚，必须有绅士风度、风趣幽默与不可预测性。\n" +
            "4. 核心战术——推拉（Push-Pull）：给予肯定/兴趣（拉）的同时，必须紧跟调侃/设置门槛/抽离撤退（推），对冲需求感，制造情绪过山车与吸引力。\n" +
            "5. 窗口诊断与战术匹配：\n" +
            "   - 绿灯（积极主动/热情）：顺势升级邀约，模糊时间地点，推拉挑逗；\n" +
            "   - 黄灯（被动观望/礼貌）：展示高价值生活与松弛感，轻度推拉测试反应；\n" +
            "   - 红灯/冷淡/废物测试：云淡风轻破局（不接招、反框架或幽默化解），绝不破防争辩，必要时主动冷冻或转换话题重塑高位。",
        SuggestedTones: new[] { "高位推拉", "情境流拉扯", "幽默反制", "降温破冰" });

    /// <summary>全部内置技能列表。</summary>
    public static readonly IReadOnlyList<DistillSkill> AllBuiltIn = new[]
    {
        Nuwa,
        Workplace,
        Intimacy,
        Logical,
        TdSkill
    };

    /// <summary>根据 ID 查找内置技能。</summary>
    public static DistillSkill? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Nuwa;
        return AllBuiltIn.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Nuwa;
    }
}
