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

    /// <summary>把消息列表转写为 "[对方] xxx / [我] xxx" 的多行文本（取最近 maxMessages 条）。</summary>
    public static string BuildTranscript(IReadOnlyList<ChatMessage> messages, int maxMessages = 30)
    {
        if (messages is null || messages.Count == 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, messages.Count - maxMessages);
        var sb = new StringBuilder();
        for (int i = start; i < messages.Count; i++)
        {
            string who = messages[i].Role switch
            {
                MessageRole.Incoming => "对方",
                MessageRole.Outgoing => "我",
                _ => "未知"
            };
            sb.AppendLine($"[{who}] {messages[i].Text.Replace("\n", " ")}");
        }

        return sb.ToString();
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
            "你是微信聊天回复副驾。用户会给你一段聊天记录（[对方]=对方消息，[我]=用户自己消息）。" +
            $"请基于上下文生成 {n} 条语气不同、简洁自然口语化的中文回复建议，语气从以下集合选取：{string.Join("/", ReplyTones)}。" +
            "严格只按以下格式输出，每条一行，不要输出其他内容，不要代发消息：\n" +
            "[语气] 回复内容 | 理由：为什么这么说的一句话";

        string userPrompt = "聊天记录：\n" + BuildTranscript(messages);
        if (persona is { Traits.Count: > 0 })
        {
            userPrompt += "\n\n对方画像参考：\n" + string.Join("\n", persona.Traits.Select(t => $"- {t.Dimension}: {t.Attribute}"));
        }
        userPrompt += $"\n请给出 {n} 条贴合上下文的回复建议。";

        return new AiRequest(system, userPrompt, settings.Model, settings.Temperature);
    }

    /// <summary>构建"潜台词分析"请求：六项标签固定格式，与 <see cref="AiOutputParser.ParseSubtext"/> 对应。</summary>
    public static AiRequest BuildSubtextAnalysis(AiSettings settings, IReadOnlyList<ChatMessage> messages, Persona? persona = null)
    {
        var latestIncoming = messages.LastOrDefault(m => m.Role == MessageRole.Incoming) ?? messages.LastOrDefault();
        string targetStatement = latestIncoming is not null ? latestIncoming.Text.Replace("\n", " ") : "（无具体消息）";

        var sb = new StringBuilder();
        sb.AppendLine("完整对话流背景（[对方]=对方消息，[我]=用户自己消息）：");
        sb.AppendLine(BuildTranscript(messages));

        if (persona is { Traits.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"对方已知画像参考：{persona.ContactName}");
            foreach (var t in persona.Traits)
            {
                sb.AppendLine($"- {t.Dimension}: {t.Attribute} (置信度: {t.ConfidenceText})");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"【🎯 本次重点深度剖析的目标发言】：\n“{targetStatement}”");
        sb.AppendLine("请结合上述对话背景与对方的人格特征，穿透字面表象，严格按格式输出 6 项深层潜台词剖析：");

        return new AiRequest(SubtextSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    private const string UnifiedAdviceSystemPrompt =
        "你是一位精通人际交往心理学、微表情与对话攻防的顶级高情商聊天副驾大师，深度结合了女娲(Nuwa)认知操作系统蒸馏方法论。\n" +
        "用户会提供一段微信聊天上下文，以及【对方已知的人格画像特质】（深度覆盖心智模型、决策启发式、表达DNA、诚实边界、价值锚点与反模式雷区）。\n" +
        "你需要穿透对方最新发言的字面表象，深度剖析其弦外之音与真实意图，并调用对方的认知操作系统底层逻辑（心智模型与决策启发式）给出量身定制的多风格回复建议。\n" +
        "严格按以下结构化格式输出，不要有额外寒暄、说明或多余开场白，严禁代发消息：\n\n" +
        "=== 意图剖析 ===\n" +
        "字面意思：对方表面上说了什么\n" +
        "潜台词洞察：结合其心智模型与表达DNA，分析对方不敢明说、委婉暗示或隐藏在背后的真实弦外之音\n" +
        "真实心理：结合其决策启发式与价值锚点，剖析对方潜意识里真正想推动什么、防范什么或此时情绪状态\n" +
        "应对策略：针对其性格特征与反模式雷区，建议我方采取的最佳破局沟通策略与避坑指南\n\n" +
        "=== 回复建议 ===\n" +
        "[高情商] 回复内容 | 理由：为什么这么说\n" +
        "[直接] 回复内容 | 理由：为什么这么说\n" +
        "[专业] 回复内容 | 理由：为什么这么说\n";

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
    /// 深度融合对方人格画像与双方关系定位，一键完成“意图穿透 + 心理洞察 + 避坑策略 + 多风格候选回复”。
    /// </summary>
    public static AiRequest BuildUnifiedAdvice(
        AiSettings settings,
        IReadOnlyList<ChatMessage> messages,
        int count = 3,
        Persona? persona = null,
        string? contactName = null,
        string? relationship = null)
    {
        int n = Math.Clamp(count, 1, ReplyTones.Length);
        var latestIncoming = messages.LastOrDefault(m => m.Role == MessageRole.Incoming) ?? messages.LastOrDefault();
        string targetStatement = latestIncoming is not null ? latestIncoming.Text.Replace("\n", " ") : "（无具体消息）";

        var sb = new StringBuilder();
        string targetName = !string.IsNullOrWhiteSpace(contactName) ? contactName : (persona?.ContactName ?? "对方");
        sb.AppendLine($"当前对话对象：{targetName}");

        string relGuideline = GetRelationshipGuideline(relationship);
        string relTitle = !string.IsNullOrWhiteSpace(relationship) ? relationship : "智能推断";
        sb.AppendLine($"双方关系定位：【{relTitle}】");
        sb.AppendLine($"【👥 关系社交准则】：{relGuideline}");
        sb.AppendLine("（重要约束：回复方案必须严格遵循该关系的交往边界、分寸感与沟通目标，切忌用词不合时宜！）");
        sb.AppendLine();
        sb.AppendLine("聊天上下文（[对方]=对方消息，[我]=用户自己消息）：");
        sb.AppendLine(BuildTranscript(messages));

        if (persona is { Traits.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"【🎯 对方已知人格特质画像（{persona.ContactName}，女娲认知模型）】：");
            foreach (var t in persona.Traits)
            {
                sb.AppendLine($"- 【{t.Dimension}】[{t.NuwaTag}]: {t.Attribute} (量化评分: {t.ScoreInt}分, 置信度: {t.ConfidenceText})");
            }
            sb.AppendLine("（重要约束：意图剖析与候选回复必须高度契合上述画像的心智模型、决策启发式与表达DNA特征，切忌千篇一律的套话！）");
        }

        sb.AppendLine();
        sb.AppendLine($"【🎯 本次重点深度剖析的目标发言】：\n“{targetStatement}”");
        sb.AppendLine();
        sb.AppendLine($"请严格按约定格式，先输出【意图剖析】，再输出 {n} 条风格差异鲜明的【回复建议】（建议语气：高情商/直接/专业/缓和/幽默）：");

        return new AiRequest(UnifiedAdviceSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }
}
