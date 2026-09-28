using System.Text.RegularExpressions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Ai;

/// <summary>
/// M4 AI 输出解析器：把模型自由文本解析为结构化结果，格式约定与 <see cref="PromptBuilder"/> 的提示词一一对应。
/// - 回复建议行格式：[语气] 回复内容 | 理由：xxx（兼容全角竖线、行首编号、缺理由）；
/// - 潜台词六项标签：字面意思/潜台词/情绪状态/真实意图/想要的回应/建议策略（兼容简称标签）。
/// 解析尽量宽容：单行不合格式时降级为"候选"卡片，不丢内容。
/// </summary>
public static class AiOutputParser
{
    // 行首编号（1. / 1、 / 1) ）
    private static readonly Regex NumberPrefix = new(@"^\d+\s*[\.、\)]\s*", RegexOptions.Compiled);

    // [语气] 其余内容
    private static readonly Regex SuggestionLine = new(@"^\[(?<tone>[^\]]+)\]\s*(?<rest>.*)$", RegexOptions.Compiled);

    // 回复正文 | 理由：xxx（竖线半角/全角，冒号半角/全角）
    private static readonly Regex ReasonSplit = new(
        @"^(?<text>.*?)\s*[|｜]\s*(?:理由|原因|Reason)\s*[:：]\s*(?<reason>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 潜台词标签 -> 字段键（长标签在前，避免"情绪"抢先匹配"情绪状态"）
    private static readonly (string Prefix, string Key)[] SubtextLabels =
    {
        ("字面意思", "Literal"),
        ("潜台词", "Subtext"),
        ("情绪状态", "Emotion"),
        ("情绪", "Emotion"),
        ("真实意图", "Intent"),
        ("意图", "Intent"),
        ("想要的回应", "DesiredResponse"),
        ("回应类型", "DesiredResponse"),
        ("建议策略", "Strategy"),
        ("策略", "Strategy"),
    };

    /// <summary>解析回复建议：每行一条候选；不合格式的行降级为 Tone="候选"。</summary>
    public static IReadOnlyList<ReplySuggestion> ParseReplySuggestions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<ReplySuggestion>();
        }

        var list = new List<ReplySuggestion>();
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = NumberPrefix.Replace(rawLine.Trim('\r', ' '), string.Empty);
            if (line.Length == 0)
            {
                continue;
            }

            Match m = SuggestionLine.Match(line);
            if (!m.Success)
            {
                // 无 [语气] 前缀：整行作为回复正文保留
                list.Add(new ReplySuggestion("候选", line, string.Empty));
                continue;
            }

            string tone = m.Groups["tone"].Value.Trim();
            string rest = m.Groups["rest"].Value.Trim();
            Match r = ReasonSplit.Match(rest);
            if (r.Success)
            {
                list.Add(new ReplySuggestion(tone, r.Groups["text"].Value.Trim(), r.Groups["reason"].Value.Trim()));
            }
            else
            {
                list.Add(new ReplySuggestion(tone, rest, string.Empty));
            }
        }

        return list;
    }

    /// <summary>解析潜台词六项标签；未出现的字段为空串。</summary>
    public static SubtextAnalysis ParseSubtext(string? raw)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (string rawLine in raw.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ');
                foreach ((string prefix, string key) in SubtextLabels)
                {
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string rest = line[prefix.Length..].TrimStart(' ', ':', '：').Trim();
                    if (rest.Length > 0)
                    {
                        values[key] = rest;
                    }

                    break;
                }
            }
        }

        string Get(string key) => values.TryGetValue(key, out string? v) ? v : string.Empty;
        return new SubtextAnalysis(
            Get("Literal"), Get("Subtext"), Get("Emotion"),
            Get("Intent"), Get("DesiredResponse"), Get("Strategy"));
    }
}
