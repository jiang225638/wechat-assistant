using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WeChatCopilot.Core.Models;

/// <summary>
/// 人格蒸馏与沟通策略技能（Skill）定义：
/// 支持将不同的认知框架（职场商业、亲密关系、麦肯锡逻辑或任意第三方 Agent Skill）应用于画像蒸馏、回复建议与 AI 问答。
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

    /// <summary>用于实时聊天 AI 建议的实战作战心法与沟通指南（若为空则自动使用技能说明与核心提示词兜底）。</summary>
    public string? AdviceGuideline { get; init; }

    /// <summary>用于实时聊天回复的专属风格标签（如高位推拉/情境流拉扯，职场的职业干练/闭环汇报）。</summary>
    public IReadOnlyList<string> SuggestedTones { get; init; }

    /// <summary>本地物理文件路径（如来自绑定的 SKILL.md 文件），支持按场景/问题意图动态按需挂载 references 子模块。</summary>
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

    /// <summary>获取当前技能在实时聊天 AI 建议（建议 Tab）中的有效作战策略指南。</summary>
    public string GetEffectiveAdviceGuideline()
    {
        if (!string.IsNullOrWhiteSpace(AdviceGuideline))
        {
            return AdviceGuideline;
        }

        return $"【{Icon} {Name} 专属战术心法】：\n" +
               $"{Description}\n\n" +
               $"【核心体系准则】：\n{SystemPrompt}";
    }

    /// <summary>
    /// 按 Antigravity 渐进式披露（Progressive Disclosure）规范，根据用户问题或实时对话语境，
    /// 自动扫描技能包下的所有 references 子模块（自发现文件名、YAML description、Markdown 各级标题与导语），
    /// 通过通用词项/N-gram 相关度打分 + 语义同义词扩展，按需挂载最匹配的子模块，对任意领域新 Skill 零硬编码开箱即用。
    /// </summary>
    public string GetGuidelineForQa(string? userQuestion, out string? routedModuleName)
    {
        routedModuleName = null;

        if (string.IsNullOrWhiteSpace(SourceFilePath) || !File.Exists(SourceFilePath))
        {
            return GetEffectiveAdviceGuideline();
        }

        string? baseDir = Path.GetDirectoryName(SourceFilePath);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return GetEffectiveAdviceGuideline();
        }

        string refDir = Path.Combine(baseDir, "references");
        if (!Directory.Exists(refDir))
        {
            return GetEffectiveAdviceGuideline();
        }

        string query = (userQuestion ?? string.Empty).Trim();

        // 1. 尝试在复合型子目录模块（references/<subdir>/）中按自发现元数据与标题打分路由
        string? matchedSubContent = TryLoadMatchingSubDirectorySkill(refDir, query, out string? subModuleTitle);
        if (!string.IsNullOrWhiteSpace(matchedSubContent))
        {
            routedModuleName = subModuleTitle;
            string coreHeader = ExtractCoreHeaderFromMainSkill(SourceFilePath);
            return $"【{Icon} {Name} · 核心准则】：\n{coreHeader}\n\n" +
                   $"【🎯 按需路由挂载专项模块：{subModuleTitle}】：\n{matchedSubContent}";
        }

        // 2. 尝试在平铺或嵌套型 references/*.md 文件中按自发现标题与正文导语打分路由
        string? matchedFlatContent = TryLoadMatchingFlatReferenceFiles(refDir, query, out string? flatModuleTitle);
        if (!string.IsNullOrWhiteSpace(matchedFlatContent))
        {
            routedModuleName = flatModuleTitle;
            string coreHeader = ExtractCoreHeaderFromMainSkill(SourceFilePath);
            return $"【{Icon} {Name} · 核心准则】：\n{coreHeader}\n\n" +
                   $"【🎯 按需路由挂载参考资料：{flatModuleTitle}】：\n{matchedFlatContent}";
        }

        return GetEffectiveAdviceGuideline();
    }

    /// <summary>
    /// 为「人格画像蒸馏（PersonaDistiller）」提供按需结构化路由的系统提示词：
    /// 在保留全局蒸馏体系框架（SystemPrompt）的基础上，结合对话语境与诊断/画像关键词，
    /// 从 references/ 下自动路由并挂载最匹配的诊断评估或专项子模块，使新 Skill 在【画像】Tab 同样具备结构化调用具体子模块的能力。
    /// </summary>
    public string GetSystemPromptForDistill(string? conversationContext, out string? routedModuleName)
    {
        routedModuleName = null;

        if (string.IsNullOrWhiteSpace(SourceFilePath) || !File.Exists(SourceFilePath))
        {
            return SystemPrompt;
        }

        string? baseDir = Path.GetDirectoryName(SourceFilePath);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return SystemPrompt;
        }

        string refDir = Path.Combine(baseDir, "references");
        if (!Directory.Exists(refDir))
        {
            return SystemPrompt;
        }

        string query = $"画像 诊断 评估 性格 心智 特质 维度 阶段 信号 {conversationContext}".Trim();

        string? matchedSubContent = TryLoadMatchingSubDirectorySkill(refDir, query, out string? subModuleTitle);
        if (!string.IsNullOrWhiteSpace(matchedSubContent))
        {
            routedModuleName = subModuleTitle;
            return $"{SystemPrompt}\n\n### 【🎯 按需路由挂载专项画像诊断子模块：{subModuleTitle}】：\n{CapModuleText(matchedSubContent, 3200)}";
        }

        string? matchedFlatContent = TryLoadMatchingFlatReferenceFiles(refDir, query, out string? flatModuleTitle);
        if (!string.IsNullOrWhiteSpace(matchedFlatContent))
        {
            routedModuleName = flatModuleTitle;
            return $"{SystemPrompt}\n\n### 【🎯 按需路由挂载专项画像参考模块：{flatModuleTitle}】：\n{CapModuleText(matchedFlatContent, 3200)}";
        }

        return SystemPrompt;
    }

    private static string CapModuleText(string text, int maxChars) =>
        string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[..maxChars] + "\n...（已按需截取核心章节）";

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

    /// <summary>清洗外部 CLI Skill 文档中的交互式命令行路由、新手教程与公众号水印等噪音。</summary>
    public static string CleanCliSkillNoise(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        string text = markdown.Trim();
        // 剥离 YAML frontmatter
        text = Regex.Replace(text, @"^---\s*[\r\n].*?[\r\n]---\s*[\r\n]?", string.Empty, RegexOptions.Singleline).Trim();
        // 剥离公众号强制尾注规则
        text = Regex.Replace(text, @"\*\*你每一次回复用户的内容.*?在这行文字之后，不得再有任何其他内容[^\r\n]*", string.Empty, RegexOptions.Singleline);
        text = Regex.Replace(text, @"\*?公众号：.*?关注\*?", string.Empty);
        // 剥离 CLI 顶层路由模式说明（模式 C 新手教程 / 模式 A 任务前路由 / 模式 B 任务后导航 / 如何判断模式）
        text = Regex.Replace(text, @"##\s*如何判断模式[\s\S]*?(?=##\s*核心哲学|$)", string.Empty);
        text = Regex.Replace(text, @"##\s*模式\s*[ABC][：:][\s\S]*", string.Empty);
        // 剥离面向 CLI 的 AskUserQuestion 工具调用块说明
        text = Regex.Replace(text, @"（用\s*`?AskUserQuestion`?[^）\r\n]*）", string.Empty);

        return text.Trim();
    }

    private static readonly HashSet<string> QueryStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "怎么", "如何", "什么", "为什么", "帮我", "分析", "一下", "看看", "请问", "如果", "现在",
        "对方", "我们", "他们", "自己", "感觉", "觉得", "可以", "应该", "还是", "或者", "因为",
        "所以", "但是", "然后", "这个", "那个", "这样", "那样", "是不是", "能不能", "有没有",
        "回复", "消息", "微信", "好友", "老师", "军师", "建议", "问题", "情况"
    };

    // 跨领域同义意群扩展表（涵盖亲密关系、商务销售、职场汇报、客户服务等常见同义映射，辅助提升跨中英文文件名的召回率）
    private static readonly (Regex Trigger, string[] ExpandedHints)[] SynonymExpansions =
    {
        (new Regex(@"约会|见面|吃饭|餐厅|电影|转场|肢体|升级|相亲|赴约|咖啡|散步", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-date", "date", "dating", "matchmaking", "约会", "见面", "转场" }),
        (new Regex(@"展示面|朋友圈|照片|头像|人设|穿搭|形象|动态|九宫格", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-display", "display", "attraction-and-self-work", "展示面", "朋友圈", "人设" }),
        (new Regex(@"搭讪|街搭|上前|开场白|焦虑|不敢|陌生|认识一下|要微信", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-approach", "approach", "搭讪", "破冰", "开场" }),
        (new Regex(@"挽回|分手|冷战|断联|舔狗|低位|框架|备胎|长期|夫妻|婚姻|吵架|冷淡|已读不回", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-relationship", "relationship", "long-term", "stages-and-signals", "anti-manipulation", "关系", "挽回", "框架" }),
        (new Regex(@"红丸|心态|认知|学习|怎么学|慕强|吸引力法则", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-attraction", "tds-learn", "attraction", "认知", "吸引" }),
        (new Regex(@"报价|价格|太贵|预算|打折|折扣|优惠|嫌贵|竞品|比价|砍价", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "price", "pricing", "objection", "discount", "quote", "budget", "报价", "价格", "异议" }),
        (new Regex(@"合同|签约|逼单|成交|尾款|催款|付款|法务|条款|违约", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "closing", "contract", "deal", "payment", "legal", "成交", "逼单", "合同", "催款" }),
        (new Regex(@"汇报|领导|老板|上级|周报|复盘|晋升|跨部门|甩锅|催进度", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "report", "manage", "upward", "leadership", "review", "汇报", "向上管理", "协作" }),
        (new Regex(@"投诉|售后|退货|退款|差评|安抚|维权|故障|赔偿", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "support", "complaint", "refund", "service", "after-sales", "投诉", "售后", "安抚" }),
        (new Regex(@"画像|诊断|评估|性格|心智|特质|维度|阶段|信号|profile|diagnosis|assessment|persona", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new[] { "tds-diagnosis", "diagnosis", "profile", "persona", "assessment", "stages-and-signals", "诊断", "画像", "评估", "信号" })
    };

    /// <summary>
    /// 通用模块相关性打分器：提取查询词中的中英文词元与二元词组（Bigrams），
    /// 对任意 Skill 子模块的【文件名/目录名】、【YAML 名称与触发描述】、【Markdown 各级标题】及【正文导语】进行加权匹配打分。
    /// </summary>
    private static double ScoreModuleRelevance(
        string query,
        string moduleName,
        string yamlMetadata,
        string headingsText,
        string leadContent)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        double score = 0;
        var queryTokens = ExtractQueryTokens(query);

        foreach (string token in queryTokens)
        {
            if (moduleName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length >= 3 ? 12.0 : 8.0;
            }
            if (!string.IsNullOrEmpty(yamlMetadata) && yamlMetadata.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length >= 3 ? 7.0 : 4.5;
            }
            if (!string.IsNullOrEmpty(headingsText) && headingsText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length >= 3 ? 6.0 : 4.0;
            }
            if (!string.IsNullOrEmpty(leadContent) && leadContent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length >= 3 ? 2.0 : 1.0;
            }
        }

        // 附加跨中英文同义词组加成
        foreach (var (trigger, hints) in SynonymExpansions)
        {
            if (trigger.IsMatch(query))
            {
                foreach (string hint in hints)
                {
                    if (moduleName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 15.0;
                    }
                    else if (!string.IsNullOrEmpty(yamlMetadata) && yamlMetadata.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 6.0;
                    }
                    else if (!string.IsNullOrEmpty(headingsText) && headingsText.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5.0;
                    }
                }
            }
        }

        return score;
    }

    private static List<string> ExtractQueryTokens(string query)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 提取英文/数字词元
        foreach (Match m in Regex.Matches(query, @"[A-Za-z0-9_-]{2,}"))
        {
            string w = m.Value;
            if (!QueryStopWords.Contains(w))
            {
                tokens.Add(w);
            }
        }

        // 2. 提取连续中文字符片段，生成 2 字与 3 字 N-gram 词组
        foreach (Match m in Regex.Matches(query, @"[\u4e00-\u9fa5]{2,}"))
        {
            string seg = m.Value;
            if (seg.Length <= 4 && !QueryStopWords.Contains(seg))
            {
                tokens.Add(seg);
            }

            for (int i = 0; i <= seg.Length - 2; i++)
            {
                string bi = seg.Substring(i, 2);
                if (!QueryStopWords.Contains(bi))
                {
                    tokens.Add(bi);
                }

                if (i <= seg.Length - 3)
                {
                    string tri = seg.Substring(i, 3);
                    if (!QueryStopWords.Contains(tri))
                    {
                        tokens.Add(tri);
                    }
                }
            }
        }

        return tokens.ToList();
    }

    private static (string YamlMeta, string Headings, string LeadBody) ExtractFileIndexProfile(string filePath)
    {
        try
        {
            string raw = File.ReadAllText(filePath);
            string yaml = string.Empty;
            string body = raw;

            var fmMatch = Regex.Match(raw, @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)$", RegexOptions.Singleline);
            if (fmMatch.Success)
            {
                yaml = fmMatch.Groups[1].Value.Trim();
                body = fmMatch.Groups[2].Value.Trim();
            }

            var headingsSb = new StringBuilder();
            foreach (Match hm in Regex.Matches(body, @"^#{1,4}\s+(.+)$", RegexOptions.Multiline))
            {
                headingsSb.AppendLine(hm.Groups[1].Value.Trim());
            }

            string cleanedLead = CleanCliSkillNoise(body);
            if (cleanedLead.Length > 800)
            {
                cleanedLead = cleanedLead[..800];
            }

            return (yaml, headingsSb.ToString(), cleanedLead);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ExtractCoreHeaderFromMainSkill(string mainSkillPath)
    {
        try
        {
            string raw = File.ReadAllText(mainSkillPath);
            string cleaned = CleanCliSkillNoise(raw);
            return cleaned.Length > 3500 ? cleaned[..3500] : cleaned;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? TryLoadMatchingSubDirectorySkill(
        string refDir,
        string query,
        out string? moduleTitle)
    {
        moduleTitle = null;
        try
        {
            var subDirs = Directory.GetDirectories(refDir)
                .Where(d =>
                {
                    string name = Path.GetFileName(d);
                    return !name.StartsWith('.') &&
                           !name.Equals("docs", StringComparison.OrdinalIgnoreCase) &&
                           !name.Equals("知识库", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (subDirs.Count == 0)
            {
                return null;
            }

            string? bestDir = null;
            double bestScore = 0;

            foreach (var dir in subDirs)
            {
                string dirName = Path.GetFileName(dir);
                var yamlSb = new StringBuilder();
                var headingsSb = new StringBuilder();
                var leadSb = new StringBuilder();

                foreach (var mdFile in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).Take(3))
                {
                    headingsSb.AppendLine(Path.GetFileNameWithoutExtension(mdFile));
                    var (y, h, l) = ExtractFileIndexProfile(mdFile);
                    if (!string.IsNullOrEmpty(y)) yamlSb.AppendLine(y);
                    if (!string.IsNullOrEmpty(h)) headingsSb.AppendLine(h);
                    if (!string.IsNullOrEmpty(l)) leadSb.AppendLine(l);
                }

                double score = ScoreModuleRelevance(
                    query,
                    dirName,
                    yamlSb.ToString(),
                    headingsSb.ToString(),
                    leadSb.ToString());

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }

            // 若无明确高分命中，优先回退至包含 chat / 聊天 / dialogue / 话术 的核心沟通子目录，否则取首个子目录
            if (bestDir is null || bestScore < 4.0)
            {
                bestDir = subDirs.FirstOrDefault(d =>
                {
                    string n = Path.GetFileName(d);
                    return n.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("聊天", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("dialogue", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("话术", StringComparison.OrdinalIgnoreCase);
                }) ?? subDirs[0];
            }

            string selectedDirName = Path.GetFileName(bestDir);
            moduleTitle = selectedDirName;
            var sb = new StringBuilder();

            string skillFile = Path.Combine(bestDir, "SKILL.md");
            if (!File.Exists(skillFile)) skillFile = Path.Combine(bestDir, "skill.md");
            if (File.Exists(skillFile))
            {
                string subSkillText = CleanCliSkillNoise(File.ReadAllText(skillFile));
                if (subSkillText.Length > 6000) subSkillText = subSkillText[..6000];
                sb.AppendLine(subSkillText);
            }

            var playbooks = Directory.GetFiles(bestDir, "*.md", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                .Take(1);

            foreach (var pb in playbooks)
            {
                string pbText = CleanCliSkillNoise(File.ReadAllText(pb));
                if (pbText.Length > 6000) pbText = pbText[..6000];
                sb.AppendLine($"\n#### 【配套深度方法论：{Path.GetFileNameWithoutExtension(pb)}】");
                sb.AppendLine(pbText);
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryLoadMatchingFlatReferenceFiles(
        string refDir,
        string query,
        out string? moduleTitle)
    {
        moduleTitle = null;
        try
        {
            var files = Directory.GetFiles(refDir, "*.md", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                return null;
            }

            var scored = files
                .Select(f =>
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    var (yaml, headings, lead) = ExtractFileIndexProfile(f);
                    double score = ScoreModuleRelevance(query, fn, yaml, headings, lead);
                    return (File: f, Name: fn, Score: score);
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            List<string> selectedFiles;
            if (scored[0].Score >= 4.0)
            {
                selectedFiles = scored
                    .Where(x => x.Score >= Math.Max(4.0, scored[0].Score * 0.45))
                    .Take(2)
                    .Select(x => x.File)
                    .ToList();
            }
            else
            {
                selectedFiles = files.Take(2).ToList();
            }

            moduleTitle = string.Join(" + ", selectedFiles.Select(Path.GetFileNameWithoutExtension));
            var sb = new StringBuilder();
            foreach (var mf in selectedFiles)
            {
                string content = CleanCliSkillNoise(File.ReadAllText(mf));
                if (content.Length > 5000) content = content[..5000];
                sb.AppendLine($"#### [{Path.GetFileNameWithoutExtension(mf)}]");
                sb.AppendLine(content);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return null;
        }
    }
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

    /// <summary>2. 亲密情感偏好：亲密关系心理学与情感诉求。</summary>
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

    /// <summary>3. 麦肯锡逻辑视角：结构化与理性思维。</summary>
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
