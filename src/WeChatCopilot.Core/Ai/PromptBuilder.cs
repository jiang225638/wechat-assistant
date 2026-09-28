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
        "你是微信聊天潜台词分析副驾。用户会给你一段聊天记录（[对方]=对方消息，[我]=用户自己消息）。" +
        "请用中文分析对方最近一条消息。严格只按以下固定格式输出，每项一行，不要输出其他内容，不要代发消息：\n" +
        "字面意思：…\n潜台词：…\n情绪状态：…\n真实意图：…\n想要的回应：…\n建议策略：…";

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
    /// 每条一行，格式 "[语气] 回复内容 | 理由：为什么这么说"。
    /// </summary>
    public static AiRequest BuildReplySuggestions(AiSettings settings, IReadOnlyList<ChatMessage> messages, int count = 3)
    {
        int n = Math.Clamp(count, 1, ReplyTones.Length);
        string system =
            "你是微信聊天回复副驾。用户会给你一段聊天记录（[对方]=对方消息，[我]=用户自己消息）。" +
            $"请基于上下文生成 {n} 条语气不同、简洁自然口语化的中文回复建议，语气从以下集合选取：{string.Join("/", ReplyTones)}。" +
            "严格只按以下格式输出，每条一行，不要输出其他内容，不要代发消息：\n" +
            "[语气] 回复内容 | 理由：为什么这么说的一句话";

        return new AiRequest(system,
            "聊天记录：\n" + BuildTranscript(messages) + "\n请给出 " + n + " 条回复建议。",
            settings.Model,
            settings.Temperature);
    }

    /// <summary>构建"潜台词分析"请求：六项标签固定格式，与 <see cref="AiOutputParser.ParseSubtext"/> 对应。</summary>
    public static AiRequest BuildSubtextAnalysis(AiSettings settings, IReadOnlyList<ChatMessage> messages) =>
        new(SubtextSystemPrompt,
            "聊天记录：\n" + BuildTranscript(messages) + "\n请分析对方最近一条消息的潜台词。",
            settings.Model,
            settings.Temperature);
}
