using System.Text;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Ai;

/// <summary>
/// M4 提示词构建器：把对话缓冲转写为文本，并组装两类任务请求——
/// 多候选回复建议（FR-4：N 条不同语气 + 理由）与结构化潜台词分析（FR-5：六项标签意图面板）。
/// 输出格式约定与 <see cref="AiOutputParser"/> 一一对应，便于解析成卡片/面板。
/// 只读 + 建议，不代发；提示词中明确约束 AI 只输出建议文本。
/// </summary>
public static class PromptBuilder
{
    /// <summary>回复候选的语气集合（FR-4）；候选数上限即其长度。</summary>
    public static readonly string[] ReplyTones = { "共情", "专业", "幽默", "直接", "缓和" };

    private const string SubtextSystemPrompt =
        "你是一位精通人际交往心理学、微表情与对话潜台词的顶级沟通大师。\n" +
        "你需要根据提供的对话上下文以及【对方已知的人格特质画像】（如其性格倾向、沟通风格、决策模式、情绪阈值与沟通雷区），穿透字面表象，深度剖析对方最新发言背后的真实意图。\n" +
        "严格只按以下固定格式输出6项，每项一行，不要输出其他开场白、总结，不要代发消息：\n" +
        "字面意思：对方表面上说了什么\n" +
        "潜台词：结合其性格特征，分析对方不敢明说、委婉暗示或隐藏在背后的真实弦外之音\n" +
        "情绪状态：对方此时此刻的真实情绪与心理防备程度\n" +
        "真实意图：对方核心想推动什么、防范什么或试探什么\n" +
        "想要的回应：结合其价值锚点，对方潜意识里最希望得到什么样的答复与承诺\n" +
        "建议策略：针对其性格特征与雷区，建议我方采取的最佳沟通与破解策略（具体到语气、切入点与措辞）";

    /// <summary>把消息列表转写为 "[对方] xxx / [我] xxx" 的多行文本（取最近 maxMessages 条，自动携带时间戳与长断联间隔标记）。</summary>
    public static string BuildTranscript(IReadOnlyList<ChatMessage> messages, int maxMessages = 30)
    {
        if (messages is null || messages.Count == 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, messages.Count - maxMessages);
        var sb = new StringBuilder();
        DateTime? prevTimestamp = null;

        for (int i = start; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (prevTimestamp.HasValue && msg.Timestamp.HasValue)
            {
                TimeSpan gap = msg.Timestamp.Value - prevTimestamp.Value;
                if (gap.TotalHours >= 2)
                {
                    string gapDesc = gap.TotalDays >= 1
                        ? $"间隔 {(int)gap.TotalDays} 天 {gap.Hours} 小时"
                        : $"间隔 {gap.TotalHours:F1} 小时";
                    sb.AppendLine($"--- （{gapDesc}） ---");
                }
            }

            if (msg.Timestamp.HasValue)
            {
                prevTimestamp = msg.Timestamp;
            }

            string who;
            if (msg.Role == MessageRole.Incoming)
            {
                who = !string.IsNullOrWhiteSpace(msg.SenderName) && msg.SenderName != "对方"
                    ? $"对方({msg.SenderName.Trim()})"
                    : "对方";
            }
            else if (msg.Role == MessageRole.Outgoing)
            {
                who = !string.IsNullOrWhiteSpace(msg.SenderName) && msg.SenderName != "我"
                    ? $"我({msg.SenderName.Trim()})"
                    : "我";
            }
            else
            {
                who = "未知";
            }

            string timePrefix = string.Empty;
            if (msg.Timestamp.HasValue)
            {
                var dt = msg.Timestamp.Value.Kind == DateTimeKind.Utc
                    ? msg.Timestamp.Value.ToLocalTime()
                    : msg.Timestamp.Value;
                timePrefix = $"[{dt:MM-dd HH:mm}] ";
            }

            sb.AppendLine($"{timePrefix}[{who}] {CleanMessageTextForAi(msg.Text)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从最近消息列表中提取对方最新的连续发言回合（自动合并同一轮中的多条文本、图片与表情，如“俺妥妥老实人 / 老江套路我😡 [动画表情]”），
    /// 以及紧随其后由[我]发出的后续消息列表（用于判断时序先后与跟进状态）。
    /// </summary>
    public static string ExtractLatestIncomingTurn(
        IReadOnlyList<ChatMessage> messages,
        out DateTime? latestIncomingTime,
        out IReadOnlyList<ChatMessage> trailingOutgoing,
        out DateTime? trailingOutgoingTime)
    {
        latestIncomingTime = null;
        trailingOutgoingTime = null;

        if (messages == null || messages.Count == 0)
        {
            trailingOutgoing = Array.Empty<ChatMessage>();
            return "（无具体消息）";
        }

        // 1. 收集对话末尾连续由[我]发出的消息（trailing outgoing）
        var trailingList = new List<ChatMessage>();
        int lastIncomingIndex = -1;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Incoming)
            {
                lastIncomingIndex = i;
                break;
            }
            if (messages[i].Role == MessageRole.Outgoing)
            {
                trailingList.Insert(0, messages[i]);
            }
        }

        trailingOutgoing = trailingList;
        if (trailingList.Count > 0)
        {
            trailingOutgoingTime = trailingList.Last().Timestamp;
        }

        // 2. 若完全没有对方消息，回退到最后一条消息
        if (lastIncomingIndex < 0)
        {
            var last = messages.Last();
            latestIncomingTime = last.Timestamp;
            return CleanMessageTextForAi(last.Text);
        }

        // 3. 回溯收集属于同一轮次（连续发送且时间间隔小于30分钟，最多取最近5条）的对方消息序列
        var incomingBurst = new List<ChatMessage> { messages[lastIncomingIndex] };
        latestIncomingTime = messages[lastIncomingIndex].Timestamp;

        for (int j = lastIncomingIndex - 1; j >= 0 && incomingBurst.Count < 5; j--)
        {
            var prev = messages[j];
            if (prev.Role != MessageRole.Incoming)
            {
                break;
            }

            if (prev.Timestamp.HasValue && incomingBurst[0].Timestamp.HasValue)
            {
                var diff = incomingBurst[0].Timestamp!.Value - prev.Timestamp.Value;
                if (Math.Abs(diff.TotalMinutes) > 30)
                {
                    break;
                }
            }

            incomingBurst.Insert(0, prev);
        }

        var cleanedSegments = incomingBurst
            .Select(m => CleanMessageTextForAi(m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (cleanedSegments.Count == 0)
        {
            return "（无具体消息）";
        }

        return string.Join(" / ", cleanedSegments);
    }

    /// <summary>
    /// 构建"多候选回复建议"请求：count 条不同语气候选（语气取自 <see cref="ReplyTones"/>），
    /// 可选传入对方 <paramref name="persona"/> 融入回复偏好。
    /// 每条一行，格式 "[语气] 回复内容 | 理由：为什么这么说"。
    /// </summary>
    public static AiRequest BuildReplySuggestions(AiSettings settings, IReadOnlyList<ChatMessage> messages, int count = 3, Persona? persona = null)
    {
        int n = Math.Clamp(count, 1, ReplyTones.Length);
        string system =
            "你是微信聊天回复副驾。用户会给你一段聊天记录（[对方]=对方消息，[我]=用户自己消息）。\n" +
            $"请基于上下文生成 {n} 条语气不同、极具真人微信聊天感、短小精炼且口语化的中文回复建议，语气从以下集合选取：{string.Join("/", ReplyTones)}。\n" +
            "【微信真人感核心准则】：\n" +
            "1. 严格控制字数：日常微信交流绝非写小作文，每条回复内容字数严格控制在 8~25 字以内（坚决杜绝长句与复合大长句），像真人随手在微信敲出来的日常口语；\n" +
            "2. 自然松弛接地气：去油腻、去客服感、拒绝生硬背段子与长篇大论，多用日常口头语与生活化短句，随性留白让对方轻松秒回。\n" +
            "严格只按以下格式输出，每条一行，不要输出其他内容，不要代发消息：\n" +
            "[语气] 回复内容 | 理由：为什么这么说的一句话";

        string userPrompt = "聊天记录：\n" + BuildTranscript(messages);
        if (persona is { Traits.Count: > 0 })
        {
            userPrompt += "\n\n对方画像参考：\n" + string.Join("\n", persona.Traits.Select(t => $"- {t.Dimension}: {t.Attribute}"));
        }
        userPrompt += $"\n请给出 {n} 条贴合上下文、极具真人微信打字感的简短回复建议（每条 8~25 字）。";

        return new AiRequest(system, userPrompt, settings.Model, settings.Temperature);
    }

    /// <summary>构建"潜台词分析"请求：六项标签固定格式，与 <see cref="AiOutputParser.ParseSubtext"/> 对应。</summary>
    public static AiRequest BuildSubtextAnalysis(AiSettings settings, IReadOnlyList<ChatMessage> messages, Persona? persona = null)
    {
        string targetStatement = ExtractLatestIncomingTurn(
            messages,
            out DateTime? latestIncomingTime,
            out _,
            out _);

        var sb = new StringBuilder();
        sb.AppendLine("完整对话流背景（[对方]=对方消息，[我]=用户自己消息）：");
        sb.AppendLine(BuildTranscript(messages));

        if (persona is { Traits.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"对方已知画像参考：{persona.ContactName}");
            foreach (var t in persona.Traits)
            {
                sb.AppendLine($"- {t.Dimension}: {t.Attribute} (可信度: {t.ConfidenceText})");
            }
        }

        sb.AppendLine();
        string timeHint = latestIncomingTime.HasValue
            ? $" (发送时间: {(latestIncomingTime.Value.Kind == DateTimeKind.Utc ? latestIncomingTime.Value.ToLocalTime() : latestIncomingTime.Value):MM-dd HH:mm})"
            : string.Empty;
        sb.AppendLine($"【🎯 本次重点深度剖析的目标发言】：\n“{targetStatement}”{timeHint}");
        sb.AppendLine("请结合上述对话背景与对方的人格特征，穿透字面表象，严格按格式输出 6 项深层潜台词剖析：");

        return new AiRequest(SubtextSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>获取不同社交关系定位下的专属沟通准则指导。</summary>
    public static string GetRelationshipGuideline(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains("智能推断"))
        {
            return "智能研判：请结合聊天上下文语气与对方画像，自动研判双方当下的社交距离（是暧昧、职场、好友还是初识），输出契合该场景尺度的建议。";
        }
        if (rel.Contains("追求") || rel.Contains("心仪") || rel.Contains("恋爱") || rel.Contains("暧昧"))
        {
            return "追求/恋爱心仪对象：重在情绪价值、趣味拉扯与情感共鸣。避免直男说教、查户口或终结式回答，制造轻松舒适的互动氛围与下一次交流契机。";
        }
        if (rel.Contains("职场") || rel.Contains("同事") || rel.Contains("领导") || rel.Contains("工作"))
        {
            return "职场同事/领导：注重清晰的责任边界、确定性、闭环汇报与时间节点。语气专业克制、不卑不亢，切忌使用过于轻浮的表述。";
        }
        if (rel.Contains("好友") || rel.Contains("闺蜜") || rel.Contains("死党") || rel.Contains("亲密"))
        {
            return "亲密好友/死党：重在松弛感、接地气与真实陪伴。表达自然真诚，可适度幽默互怼或同频共情，切忌官方客套或生分说教。";
        }
        if (rel.Contains("商务") || rel.Contains("客户") || rel.Contains("合作"))
        {
            return "商务合作/客户：注重利益共赢、专业信任感与礼貌距离。严谨周全，以解决实际问题与推动合作为导向。";
        }
        if (rel.Contains("冷战") || rel.Contains("挽回") || rel.Contains("矛盾") || rel.Contains("吵架"))
        {
            return "矛盾/冷战挽回情境：重在破冰降温、真诚接住情绪与自我反思。切忌争辩对错、推卸责任或翻旧账，给出合理台阶与温暖关怀。";
        }
        if (rel.Contains("相亲") || rel.Contains("初识") || rel.Contains("刚加"))
        {
            return "相亲初识情境：重在展示良好教养、真诚好奇与共同话题发掘。保持轻松幽默与适度分寸，避免查户口式盘问与过度自嗨。";
        }
        if (rel.Contains("网友") || rel.Contains("陌生人") || rel.Contains("普通"))
        {
            return "普通网友/泛泛之交：保持适度礼貌、安全边界与分寸感，既不冷漠敷衍，也不过分热情窥探隐私。";
        }
        return $"用户自定义专属关系与情境（{rel}）：请深度契合用户指定的这一具体情境背景与心理预期，拿捏最精准的社交边界、潜台词洞察与语气分寸！";
    }

    /// <summary>
    /// 构建全能 AI 建议请求（合并潜台词剖析与多候选回复建议）：
    /// 深度融合当前指导作战技能 (Skill)、对方人格画像、用户期望的回复方式与终极战略目的，一键完成“多轮语境脉络 + 意图穿透 + 心理洞察 + 避坑策略 + 动态多风格候选回复”。
    /// </summary>
    public static AiRequest BuildUnifiedAdvice(
        AiSettings settings,
        IReadOnlyList<ChatMessage> messages,
        int count = 3,
        Persona? persona = null,
        string? contactName = null,
        string? relationship = null,
        DistillSkill? skill = null,
        string? userIntent = null,
        string? ultimateGoal = null,
        int? contextCount = null)
    {
        int n = Math.Clamp(count, 1, 5);
        settings ??= new AiSettings();
        skill ??= DistillSkillPresets.Intimacy;

        int effectiveContextCount = contextCount ?? (settings?.ContextMessageCount ?? 20);
        if (effectiveContextCount <= 0) effectiveContextCount = 20;

        string targetStatement = ExtractLatestIncomingTurn(
            messages,
            out DateTime? latestIncomingTime,
            out IReadOnlyList<ChatMessage> trailingOutgoing,
            out DateTime? trailingOutgoingTime);

        string skillName = skill.Name;
        string skillIcon = skill.Icon;
        string tactics = skill.GetEffectiveAdviceGuideline();
        var tones = skill.GetEffectiveSuggestedTones();
        string? effectiveGoal = !string.IsNullOrWhiteSpace(ultimateGoal) ? ultimateGoal.Trim() : persona?.UltimateGoal?.Trim();

        // 1. 动态编排对应 Skill 体系的专属 SystemPrompt
        var sysSb = new StringBuilder();
        sysSb.AppendLine($"你是一位兼具人际博弈学、深度心理学与战略沟通智慧的全域对话军师与高维沟通副驾，当前深度激活并贯彻【{skillIcon} {skillName}】作战体系。");
        sysSb.AppendLine("你的核心使命是穿透对话表象，结合【当前对话对象的终极战略目的】、【对方已知的人格画像特质】与【当前作战Skill】，进行真正分层立体的战局研判与战术推演。");
        sysSb.AppendLine($"你需要综合研判近期多轮对话脉络与博弈演进，穿透对方最新发言的字面表象，深度剖析其弦外之音与真实意图，并结合【{skillName}】的核心作战策略给出承前启后、量身定制的多风格回复建议。");
        sysSb.AppendLine("严禁任何千篇一律、死板说教或浮于表面的无效闲聊！必须根据上述核心要素，实时动态提炼针对本场博弈的专属评估维度，并量化推演每一条回复方案的话语力量与战略推进力。");
        sysSb.AppendLine();
        sysSb.AppendLine("【⚠️ 角色立场与发言归属核心铁律（绝不可混淆颠倒）】：");
        sysSb.AppendLine("1. 对话中的【对方】代表微信好友（被分析对象）；【我】代表当前使用微信的用户本人（你所辅助的咨询者）。");
        sysSb.AppendLine("2. 你的唯一立场是充当【我（用户本人）】的贴身军师，深度分析【对方】发来的话语心理，并为【我】生成发给【对方】的回复建议。");
        sysSb.AppendLine("3. 严禁角色颠倒！严禁把【我】发出的言语当成【对方】的话去剖析，严禁把【我】的话当成被分析主体，严禁为对方生成回复来回答【我】！");
        sysSb.AppendLine();
        sysSb.AppendLine($"【⚔️ 本作战流派的核心战略准则】：\n{tactics}");

        if (!string.IsNullOrWhiteSpace(effectiveGoal))
        {
            sysSb.AppendLine();
            sysSb.AppendLine($"【👑 终极战略导向】：用户为该对话对象设定了终极战略目的【{effectiveGoal}】！你的所有意图剖析与回复生成必须作为战略军师，引导对话向该目标稳步靠拢与顺势推进。");
            sysSb.AppendLine("（⚠️ 极为关键的真人感法则：战略与目的必须深藏在幕后！最高明的猎手往往以最随意的闲聊出现。战术动作和心理分析写在【理由】中，但最终生成的【回复内容】必须极其生活化、接地气、短小精悍，严禁把战略目的或多选题直接写在台词上，严禁让对方感受到目的性与压迫感！）");
        }

        if (!string.IsNullOrWhiteSpace(userIntent))
        {
            sysSb.AppendLine();
            sysSb.AppendLine($"【🎯 用户期望的回复方式 / 当前具体意图】：用户明确要求【{userIntent.Trim()}】。请在严格贯彻该意图方式的同时，巧妙兼顾战略推进！");
        }

        sysSb.AppendLine();
        sysSb.AppendLine("【📦 输出格式铁律】：只输出一个合法的 JSON 对象，JSON 之外不要输出任何文字（不要开场白、不要总结、不要 Markdown 代码块标记），严禁代发消息。结构如下：");
        sysSb.AppendLine("{");
        sysSb.AppendLine("  \"context_flow\": \"语境脉络：结合双方最近多轮交互与终极战略目的，研判当前博弈阶段、话题演进轨迹与双方互动态势\",");
        sysSb.AppendLine("  \"literal\": \"字面意思：对方表面上说了什么\",");
        sysSb.AppendLine("  \"subtext\": \"潜台词洞察：结合近期语境、对方性格特征与心智模型，剖析对方背后的弦外之音、潜意识动因或心理防线\",");
        sysSb.AppendLine("  \"intent\": \"真实心理：对方在当前战局中真正想推动什么、防范什么，以及对我方意图的态度倾向\",");
        sysSb.AppendLine($"  \"strategy\": \"破局心法：基于【{skillName}】心法、对方画像雷区与我方战略目标，给出最佳破局战术与避坑指南\",");
        sysSb.AppendLine("  \"dimensions\": [\"维度A\", \"维度B\", \"维度C\"], // 评估维度：动态博弈评估维度");
        sysSb.AppendLine("  \"suggestions\": [");
        sysSb.AppendLine("    {");
        sysSb.AppendLine("      \"style\": \"由你根据当下情境自拟的战术风格标签（4~8字）\",");
        sysSb.AppendLine("      \"text\": \"可直接复制发送给对方的回复原文\",");
        sysSb.AppendLine("      \"reason\": \"针对对方画像特质与战略目的的战术动作依据\",");
        sysSb.AppendLine("      \"scores\": {\"维度A\": 85, \"维度B\": 78, \"维度C\": 90},");
        sysSb.AppendLine("      \"goal_push\": 80,");
        sysSb.AppendLine("      \"safety\": 88");
        sysSb.AppendLine("    }");
        sysSb.AppendLine("  ]");
        sysSb.AppendLine("}");
        sysSb.AppendLine();
        sysSb.AppendLine("【重要要求】：");
        sysSb.AppendLine($" 1. suggestions 数组必须恰好包含 {n} 个元素，不多不少；");
        sysSb.AppendLine(" 2. dimensions（动态博弈评估维度）：由你根据终极战略目的、对方画像特征与作战Skill，量身提炼 3~5 个专属博弈维度（如破冰社交：舒适安全感、情绪共鸣、价值吸引力、邀约契机；商务合作：商业主导权、筹码锁定、成单推进力），严禁千篇一律；每条建议的 scores 键名必须与 dimensions 完全一致，分值为 0~100 的整数；");
        sysSb.AppendLine(" 3. goal_push（推进力，对终极目的的实际推进力度）与 safety（安全度，对对方画像雷区与关系的稳健防御度）均为 0~100 的整数；");
        sysSb.AppendLine($" 4. 战术风格完全由你自由决定与动态生成：{n} 条建议的 style 必须彼此明显不同、各有侧重，整体覆盖从稳妥守势到积极推进的梯度；风格标签要贴合当下语境与对方画像动态生成，严禁照搬'高情商/直接/专业/缓和'等刻板标签，也不要每次都用同一套标签；");
        if (tones.Count > 0)
        {
            sysSb.AppendLine($"    （【{skillName}】流派风格可供参考，但不必照搬：{string.Join("、", tones)}）");
        }
        sysSb.AppendLine(" 5. 【🔥 微信真人感与「接招+抛钩」对话法则】：");
        sysSb.AppendLine("    - 拒绝“句号式死话”：严禁只扔一句孤立的冷俏皮话、评价或段子，避免对方无法接话导致冷场；");
        sysSb.AppendLine("    - 贯彻「接招+抛钩」两段式律动：前半句轻松接住对方的情绪、梗或互动点，后半句顺势抛出轻巧好接的开放式反问、幽默好奇或下一步行动台阶；");
        sysSb.AppendLine("    - 真实生活化字数：每条 text 自然控制在 15~35 字，像真人随手打字一样松弛、接地气；");
        sysSb.AppendLine("    - 严禁油腻段子与客服问卷：坚决禁止多选式问卷（'你是更喜欢A、B还是C'）或销售话术；");
        sysSb.AppendLine("    - 终极战略目的体现在抛钩推进中（如敲定见面细节、建立私密默契、推进关系升温），但严禁直接暴露目的性；");
        sysSb.AppendLine(" 6. JSON 字符串内如需引用原话，请使用中文引号“”或「」，严禁使用未转义的英文双引号；字符串内不要换行。");

        // 2. 编排 UserPrompt
        var sb = new StringBuilder();
        string targetName = !string.IsNullOrWhiteSpace(contactName) ? contactName : (persona?.ContactName ?? "对方");
        sb.AppendLine($"当前对话对象：{targetName}（对方）");
        sb.AppendLine($"咨询者立场：我（用户本人）");
        sb.AppendLine($"当前指导作战技能：【{skillIcon} {skillName}】");

        if (!string.IsNullOrWhiteSpace(effectiveGoal))
        {
            sb.AppendLine($"【🚩 本次聊天的终极战略目的】：{effectiveGoal}");
            sb.AppendLine("（⚠️ 最高战略指令：所有候选回复方案与战术剖析，都必须以推进实现上述终极战略目的为最高导向与推进锚点！严禁没有结果的无效闲聊或偏离航向！）");
        }

        if (!string.IsNullOrWhiteSpace(userIntent))
        {
            sb.AppendLine($"【🎯 用户期望的回复方式 / 当前具体意图】：{userIntent.Trim()}");
            sb.AppendLine("（⚠️ 核心意图要求：请严格顺应用户期望的这一回复方式/战术风格展开回复构思，并巧妙结合终极战略目的推进！）");
        }

        if (!string.IsNullOrWhiteSpace(relationship))
        {
            string relGuideline = GetRelationshipGuideline(relationship);
            string relTitle = relationship;
            sb.AppendLine($"双方关系定位：【{relTitle}】");
            sb.AppendLine($"【👥 关系社交准则】：{relGuideline}");
        }

        sb.AppendLine();
        sb.AppendLine($"聊天上下文（基于最近 {effectiveContextCount} 条多轮交互分析，[对方]=对方消息，[我]=用户自己消息）：");
        sb.AppendLine(BuildTranscript(messages, effectiveContextCount));

        if (persona is { Traits.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"【🎯 对方已知人格特质画像（{persona.ContactName}）】：");
            foreach (var t in persona.Traits)
            {
                sb.AppendLine($"- 【{t.Dimension}】[{t.NuwaTag}]: {t.Attribute} (量化评分: {t.ScoreInt}分, 可信度: {t.ConfidenceText})");
            }
            sb.AppendLine($"（重要约束：意图剖析与候选回复必须高度契合上述画像特征，并严格贯彻【{skillName}】战术心法，切忌千篇一律的通用套话！）");
        }

        sb.AppendLine();
        string incomingTimeStr = string.Empty;
        if (latestIncomingTime.HasValue)
        {
            var dt = latestIncomingTime.Value.Kind == DateTimeKind.Utc ? latestIncomingTime.Value.ToLocalTime() : latestIncomingTime.Value;
            incomingTimeStr = $" (发送时间: {dt:MM-dd HH:mm})";
        }

        sb.AppendLine($"【🎯 对方（{targetName}）最新发言回合】：\n“{CleanMessageTextForAi(targetStatement)}”{incomingTimeStr}");

        if (trailingOutgoing.Count > 0)
        {
            string outgoingTimeStr = string.Empty;
            if (trailingOutgoingTime.HasValue)
            {
                var dt = trailingOutgoingTime.Value.Kind == DateTimeKind.Utc ? trailingOutgoingTime.Value.ToLocalTime() : trailingOutgoingTime.Value;
                outgoingTimeStr = $" (发送时间: {dt:MM-dd HH:mm})";
            }

            string myLastSent = string.Join(" / ", trailingOutgoing.Select(m => CleanMessageTextForAi(m.Text)));
            sb.AppendLine($"【⚡ 战局时序先后脉络（严格按发生先后顺序研判，最后一条消息由[我]发出，绝不可颠倒混乱！）】：");
            sb.AppendLine($"  1️⃣ 第一步：【对方（{targetName}）】先发出了上述最新发言：“{CleanMessageTextForAi(targetStatement)}”{incomingTimeStr}；");
            sb.AppendLine($"  2️⃣ 第二步：随后【我（用户本人）】回复了：“{myLastSent}”{outgoingTimeStr}（目前对方暂未回）；");
            sb.AppendLine($"  3️⃣ 战术推演任务：请站在【我（用户本人）】的立场，先深度剖析第 1️⃣ 步中【对方（{targetName}）】该发言回合的真实潜台词与深层心理；再结合第 2️⃣ 步【我】刚发出的内容，为【我】量身提供后续自然的破冰接茬、话题跟进或推进终极目的的高情商回复建议（⚠️ 严禁角色颠倒，严禁反向分析【我】自己的话，严禁替对方回答【我】）！");
        }
        else
        {
            sb.AppendLine("（战局状态：对方刚发来上述发言，【我】尚未回复。请站在【我（用户本人）】的立场，深度剖析对方心理并为【我】生成回复【对方】的建议。）");
        }

        sb.AppendLine();
        sb.AppendLine($"请综合研判上述 {effectiveContextCount} 条完整聊天语境脉络与情感演进（切勿孤立只看最后一句话），按系统要求的 JSON 结构输出意图剖析、专属博弈维度与恰好 {n} 条风格各异、承前启后并量化赋能战略目的的回复建议（严格执行微信真人感 15~35 字「接招+抛钩」对话准则）。只输出 JSON 对象本身：");

        return new AiRequest(sysSb.ToString(), sb.ToString(), settings?.Model ?? "gpt-4o-mini", settings?.Temperature ?? 0.7, MaxTokens: 3500);
    }

    /// <summary>清洗单条消息中的多媒体标记（如结构化表情/图片/分享卡片），转换为简洁自然文本供 AI 分析。</summary>
    public static string CleanMessageTextForAi(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;
        string res = rawText.Replace("\r", "").Replace("\n", " ").Trim();
        if (res.Contains("<msg", StringComparison.OrdinalIgnoreCase) && res.Contains("<emoji", StringComparison.OrdinalIgnoreCase))
        {
            res = System.Text.RegularExpressions.Regex.Replace(res, @"<msg>[\s\S]*?<emoji[\s\S]*?</msg>", "[动画表情]");
        }
        else if (res.Contains("<msg", StringComparison.OrdinalIgnoreCase) && res.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            res = System.Text.RegularExpressions.Regex.Replace(res, @"<msg>[\s\S]*?<img[\s\S]*?</msg>", "[图片]");
        }
        res = System.Text.RegularExpressions.Regex.Replace(res, @"\[动画表情(?::\s*[^\]]*)?\]", "[动画表情]");
        res = System.Text.RegularExpressions.Regex.Replace(res, @"\[图片(?::\s*[^\]]*)?\]", "[图片]");
        res = System.Text.RegularExpressions.Regex.Replace(res, @"\[分享卡片:\s*url=[^|]*\|title=([^|]*)\|[^\]]*\]", "[分享: $1]");
        return res;
    }
}
