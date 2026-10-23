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

    /// <summary>
    /// <summary>
    /// 解析一体化 AI 建议响应：同时提取意图剖析（原话、字面、潜台词、心理、策略）、动态博弈维度与多风格回复建议（含多维战术得分）。
    /// 无论模型输出是否严格遵守分块标记，均能强力容错解析。
    /// </summary>
    public static UnifiedAiAdvice ParseUnifiedAdvice(string? raw, string? fallbackTargetQuote = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new UnifiedAiAdvice(fallbackTargetQuote ?? string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<ReplySuggestion>());
        }

        string targetQuote = fallbackTargetQuote ?? string.Empty;
        string literal = string.Empty;
        string subtext = string.Empty;
        string intent = string.Empty;
        string strategy = string.Empty;
        string contextFlow = string.Empty;
        var dynamicDims = new List<string>();

        var suggestions = new List<ReplySuggestion>();

        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("===") || line.StartsWith("---") || line.StartsWith("```"))
            {
                continue;
            }

            // 1. 尝试匹配意图剖析与维度键值
            int colonIdx = line.IndexOfAny(new[] { ':', '：' });
            if (colonIdx > 0)
            {
                string prefix = line[..colonIdx].Trim();
                string val = line[(colonIdx + 1)..].Trim(' ', '"', '“', '”', '‘', '’');

                if (prefix is "评估维度" or "动态维度" or "博弈维度" or "动态评估维度" or "战术维度")
                {
                    var splitDims = val.Split(new[] { ',', '，', '、', '|', '｜', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var d in splitDims)
                    {
                        if (!string.IsNullOrWhiteSpace(d) && !dynamicDims.Contains(d))
                        {
                            dynamicDims.Add(d);
                        }
                    }
                    continue;
                }
                if (prefix is "战局态势" or "战略态势" or "战略阶段" or "战略阶段与语境态势" or "语境脉络" or "近期语境" or "对话走向" or "情感走向" or "语境走向" or "脉络走向")
                {
                    contextFlow = val;
                    continue;
                }
                if (prefix is "目标原话" or "原话")
                {
                    if (!string.IsNullOrWhiteSpace(val)) targetQuote = val;
                    continue;
                }
                if (prefix is "字面意思" or "字面")
                {
                    literal = val;
                    continue;
                }
                if (prefix is "潜台词洞察" or "潜台词" or "弦外之音" or "深层潜台词")
                {
                    subtext = val;
                    continue;
                }
                if (prefix is "画像透视" or "画像透视与真实动机" or "画像心智" or "真实心理" or "真实意图" or "心理诉求" or "核心诉求" or "意图")
                {
                    intent = val;
                    continue;
                }
                if (prefix is "破局战术" or "破局心法" or "破局战术与避坑指南" or "应对策略" or "建议策略" or "破局策略" or "沟通策略" or "策略")
                {
                    strategy = val;
                    continue;
                }
            }

            // 2. 尝试匹配回复建议格式: [战术风格] 内容 | 理由：xxx | 得分：维度1=88... | 推进力=86, 安全度=90
            string cleaned = NumberPrefix.Replace(line, string.Empty);
            Match m = SuggestionLine.Match(cleaned);
            if (m.Success)
            {
                string tone = m.Groups["tone"].Value.Trim();
                string rest = m.Groups["rest"].Value.Trim();

                string text = string.Empty;
                string reason = string.Empty;
                var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                double goalPush = double.NaN;
                double personaSafety = double.NaN;

                var pipes = rest.Split(new[] { '|', '｜' }, StringSplitOptions.TrimEntries);
                if (pipes.Length > 0)
                {
                    text = pipes[0];
                    for (int p = 1; p < pipes.Length; p++)
                    {
                        string segment = pipes[p];
                        if (segment.StartsWith("理由") || segment.StartsWith("原因") || segment.StartsWith("Reason", StringComparison.OrdinalIgnoreCase))
                        {
                            int scIdx = segment.IndexOfAny(new[] { ':', '：' });
                            reason = scIdx >= 0 ? segment[(scIdx + 1)..].Trim() : segment;
                        }
                        else if (segment.StartsWith("得分") || segment.StartsWith("维度得分") || segment.StartsWith("量化"))
                        {
                            int scIdx = segment.IndexOfAny(new[] { ':', '：' });
                            string scoreStr = scIdx >= 0 ? segment[(scIdx + 1)..].Trim() : segment;
                            ParseKeyValues(scoreStr, scores, ref goalPush, ref personaSafety);
                        }
                        else if (segment.Contains('=') || segment.Contains('＝'))
                        {
                            ParseKeyValues(segment, scores, ref goalPush, ref personaSafety);
                        }
                        else if (string.IsNullOrEmpty(reason))
                        {
                            reason = segment;
                        }
                    }
                }
                else
                {
                    text = rest;
                }

                suggestions.Add(new ReplySuggestion(tone, text, reason, scores, goalPush, personaSafety));
            }
        }

        if (string.IsNullOrWhiteSpace(targetQuote) && !string.IsNullOrWhiteSpace(fallbackTargetQuote))
        {
            targetQuote = fallbackTargetQuote;
        }

        // 若解析出的回复包含维度分，但顶部未声明动态维度列表，自动从所有回复的字典键汇集维度
        if (dynamicDims.Count == 0 && suggestions.Count > 0)
        {
            foreach (var s in suggestions)
            {
                if (s.DimensionScores != null)
                {
                    foreach (var k in s.DimensionScores.Keys)
                    {
                        if (!dynamicDims.Contains(k)) dynamicDims.Add(k);
                    }
                }
            }
        }

        return new UnifiedAiAdvice(
            targetQuote,
            literal,
            subtext,
            intent,
            strategy,
            suggestions,
            contextFlow,
            dynamicDims.Count > 0 ? dynamicDims : null);
    }

    private static void ParseKeyValues(string str, Dictionary<string, double> dict, ref double goalPush, ref double personaSafety)
    {
        var tokens = str.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tok in tokens)
        {
            int eq = tok.IndexOfAny(new[] { '=', '＝', ':', '：' });
            if (eq <= 0) continue;

            string k = tok[..eq].Trim();
            string vStr = tok[(eq + 1)..].Trim();
            if (double.TryParse(vStr, out double v))
            {
                if (k.Contains("推进") || k.Contains("目标") || k.Equals("GoalPush", StringComparison.OrdinalIgnoreCase))
                {
                    goalPush = Math.Clamp(v, 0, 100);
                }
                else if (k.Contains("安全") || k.Contains("稳健") || k.Equals("PersonaSafety", StringComparison.OrdinalIgnoreCase))
                {
                    personaSafety = Math.Clamp(v, 0, 100);
                }
                else
                {
                    dict[k] = Math.Clamp(v, 0, 100);
                }
            }
        }
    }
}
