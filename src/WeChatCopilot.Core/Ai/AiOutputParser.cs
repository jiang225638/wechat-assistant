using System.Text.Json;
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
    // 剥离推理模型（如 DeepSeek-R1 / QwQ）输出的 <think>...</think> 思考块（含未闭合尾部）
    private static readonly Regex ThinkBlockRegex = new(
        @"<\s*(?:think|thinking)\b[^>]*>[\s\S]*?(?:<\s*/\s*(?:think|thinking)\s*>|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 行首编号（1. / 1、 / 1) ）
    private static readonly Regex NumberPrefix = new(@"^\d+\s*[\.、\)]\s*", RegexOptions.Compiled);

    // [语气] 其余内容
    private static readonly Regex SuggestionLine = new(
        @"^(?:[\*\-\+]|\d+[\.、\)])?\s*(?:\*\*)?[\[【](?<tone>[^\]】]+)[\]】](?:\*\*)?\s*[:：]?\s*(?<rest>.*)$",
        RegexOptions.Compiled);

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

    /// <summary>剥离推理模型输出的 &lt;think&gt;...&lt;/think&gt; 思考过程块，防止内部草稿污染结构化解析。</summary>
    public static string StripThinkingBlocks(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return ThinkBlockRegex.Replace(raw, string.Empty).Trim();
    }

    /// <summary>解析回复建议：每行一条候选；不合格式的行降级为 Tone="候选"。</summary>
    public static IReadOnlyList<ReplySuggestion> ParseReplySuggestions(string? raw)
    {
        string cleanedRaw = StripThinkingBlocks(raw);
        if (string.IsNullOrWhiteSpace(cleanedRaw))
        {
            return Array.Empty<ReplySuggestion>();
        }

        var list = new List<ReplySuggestion>();
        foreach (string rawLine in cleanedRaw.Split('\n'))
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
        string cleanedRaw = StripThinkingBlocks(raw);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(cleanedRaw))
        {
            foreach (string rawLine in cleanedRaw.Split('\n'))
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
    /// 解析一体化 AI 建议响应：同时提取意图剖析（原话、字面、潜台词、心理、策略）、动态博弈维度与多风格回复建议（含多维战术得分）。
    /// 无论模型输出是否严格遵守分块标记，均能强力容错解析。
    /// </summary>
    public static UnifiedAiAdvice ParseUnifiedAdvice(string? raw, string? fallbackTargetQuote = null, int? maxSuggestions = null)
    {
        // 优先按 JSON 契约解析（提示词要求模型只输出 JSON 对象）；失败时回退到逐行文本解析，兼容旧格式与不守约的模型
        var jsonAdvice = TryParseUnifiedAdviceJson(raw, fallbackTargetQuote, maxSuggestions);
        if (jsonAdvice is not null)
        {
            return jsonAdvice;
        }

        return ParseUnifiedAdviceFromText(raw, fallbackTargetQuote, maxSuggestions);
    }

    /// <summary>
    /// 按 JSON 契约解析一体化建议：自动剥离思考块与 Markdown 代码围栏、截取首个 { 到末个 }，
    /// 允许尾随逗号/注释，并修复字符串内的裸换行。无法得到 JSON 对象时返回 null。
    /// </summary>
    public static UnifiedAiAdvice? TryParseUnifiedAdviceJson(string? raw, string? fallbackTargetQuote = null, int? maxSuggestions = null)
    {
        string cleaned = StripThinkingBlocks(raw);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        string slice = cleaned[start..(end + 1)];

        JsonDocument? doc = TryParseJson(slice) ?? TryParseJson(EscapeRawNewlinesInStrings(slice));
        if (doc is null) return null;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string fallbackQuote = PromptBuilder.CleanMessageTextForAi(fallbackTargetQuote);
            string targetQuote = ReadString(root, "target_quote", "targetQuote", "quote");
            targetQuote = PromptBuilder.CleanMessageTextForAi(string.IsNullOrWhiteSpace(targetQuote) ? fallbackQuote : targetQuote);

            var dims = new List<string>();
            if (TryGetProperty(root, out var dimsEl, "dimensions", "dims", "evaluation_dimensions") && dimsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dimsEl.EnumerateArray())
                {
                    string name = d.ValueKind == JsonValueKind.String ? (d.GetString() ?? string.Empty).Trim() : string.Empty;
                    if (name.Length > 0 && !dims.Contains(name)) dims.Add(name);
                }
            }

            var suggestions = new List<ReplySuggestion>();
            if (TryGetProperty(root, out var sugEl, "suggestions", "replies", "candidates") && sugEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sugEl.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    string replyText = ReadString(s, "text", "reply", "content");
                    if (string.IsNullOrWhiteSpace(replyText)) continue;

                    string tone = ReadString(s, "style", "tone", "label");
                    var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    if (TryGetProperty(s, out var scoresEl, "scores", "dimension_scores") && scoresEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in scoresEl.EnumerateObject())
                        {
                            if (TryReadNumber(p.Value, out double v)) scores[p.Name.Trim()] = Math.Clamp(v, 0, 100);
                        }
                    }

                    double goalPush = TryGetProperty(s, out var gp, "goal_push", "goalPush", "push") && TryReadNumber(gp, out double g) ? Math.Clamp(g, 0, 100) : double.NaN;
                    double safety = TryGetProperty(s, out var sf, "safety", "persona_safety", "personaSafety") && TryReadNumber(sf, out double sv) ? Math.Clamp(sv, 0, 100) : double.NaN;

                    suggestions.Add(new ReplySuggestion(
                        string.IsNullOrWhiteSpace(tone) ? "候选" : tone.Trim(),
                        replyText.Trim(),
                        ReadString(s, "reason", "why").Trim(),
                        scores,
                        goalPush,
                        safety));
                }
            }

            if (dims.Count == 0)
            {
                foreach (var s in suggestions)
                {
                    if (s.DimensionScores is null) continue;
                    foreach (var k in s.DimensionScores.Keys)
                    {
                        if (!dims.Contains(k)) dims.Add(k);
                    }
                }
            }

            if (maxSuggestions is > 0 && suggestions.Count > maxSuggestions.Value)
            {
                suggestions = suggestions.Take(maxSuggestions.Value).ToList();
            }

            return new UnifiedAiAdvice(
                targetQuote,
                StripLabel(ReadString(root, "literal"), "字面意思"),
                StripLabel(ReadString(root, "subtext"), "潜台词洞察", "潜台词"),
                StripLabel(ReadString(root, "intent", "psychology"), "真实心理"),
                StripLabel(ReadString(root, "strategy"), "破局心法"),
                suggestions,
                StripLabel(ReadString(root, "context_flow", "contextFlow", "context"), "语境脉络"),
                dims.Count > 0 ? dims : null);
        }
    }

    private static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把 JSON 字符串字面量内部的裸回车/换行转义为 \n，修复模型在字符串里硬换行导致的解析失败。</summary>
    private static string EscapeRawNewlinesInStrings(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;
        foreach (char c in json)
        {
            if (inString)
            {
                if (escaped) { escaped = false; sb.Append(c); continue; }
                if (c == '\\') { escaped = true; sb.Append(c); continue; }
                if (c == '"') { inString = false; sb.Append(c); continue; }
                if (c == '\n') { sb.Append("\\n"); continue; }
                if (c == '\r') continue;
                if (c == '\t') { sb.Append("\\t"); continue; }
                sb.Append(c);
            }
            else
            {
                if (c == '"') inString = true;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out value)) return true;
        }
        foreach (var p in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadString(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var el, names)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.Array => string.Join("；", el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
            _ => string.Empty
        };
    }

    private static bool TryReadNumber(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out value);
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = (el.GetString() ?? string.Empty).Trim().TrimEnd('分', '%');
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    /// <summary>去掉模型照抄示例时带上的“字面意思：”等前缀标签。</summary>
    private static string StripLabel(string value, params string[] labels)
    {
        string v = value.Trim();
        foreach (var label in labels)
        {
            if (v.StartsWith(label + "：", StringComparison.Ordinal) || v.StartsWith(label + ":", StringComparison.Ordinal))
            {
                return v[(label.Length + 1)..].Trim();
            }
        }
        return v;
    }

    /// <summary>逐行文本解析（旧格式兜底）：意图剖析键值行 + “[风格] 内容 | 理由：xxx | 得分：... | 推进力=xx, 安全度=yy”建议行。</summary>
    private static UnifiedAiAdvice ParseUnifiedAdviceFromText(string? raw, string? fallbackTargetQuote, int? maxSuggestions)
    {
        string cleanedRaw = StripThinkingBlocks(raw);
        fallbackTargetQuote = PromptBuilder.CleanMessageTextForAi(fallbackTargetQuote);
        if (string.IsNullOrWhiteSpace(cleanedRaw))
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

        bool inSuggestionsSection = false;

        foreach (string rawLine in cleanedRaw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            if (line.Contains("回复建议") || line.Contains("候选回复") || line.Contains("建议回复"))
            {
                inSuggestionsSection = true;
                continue;
            }

            if (line.StartsWith("===") || line.StartsWith("---") || line.StartsWith("```"))
            {
                continue;
            }

            // 过滤回复建议段落末尾常见的总结、说明、提示类非建议行
            if (inSuggestionsSection)
            {
                if (Regex.IsMatch(line, @"^(?:[\*\-\+•\s]|\d+[\.、\)])*\s*(?:\*\*)?(?:提示|说明|注意|总结|结语|备注|注|PS|温馨提示|实战提醒|行动建议|避坑提醒)[:：]", RegexOptions.IgnoreCase))
                {
                    continue;
                }
                if (line.StartsWith("提示") || line.StartsWith("说明") || line.StartsWith("注意") || line.StartsWith("总结") || line.StartsWith("注：") || line.StartsWith("注:") || line.StartsWith("PS：") || line.StartsWith("PS:"))
                {
                    continue;
                }
            }

            // 过滤回复建议段落末尾常见的总结、说明、提示类非建议行
            if (inSuggestionsSection)
            {
                if (Regex.IsMatch(line, @"^(?:[\*\-\+•\s]|\d+[\.、\)])*\s*(?:\*\*)?(?:提示|说明|注意|总结|结语|备注|注|PS|温馨提示|实战提醒|行动建议|避坑提醒)[:：]", RegexOptions.IgnoreCase))
                {
                    continue;
                }
                if (line.StartsWith("提示") || line.StartsWith("说明") || line.StartsWith("注意") || line.StartsWith("总结") || line.StartsWith("注：") || line.StartsWith("注:") || line.StartsWith("PS：") || line.StartsWith("PS:"))
                {
                    continue;
                }
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
                    if (!string.IsNullOrWhiteSpace(val)) targetQuote = PromptBuilder.CleanMessageTextForAi(val);
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
            string cleaned = NumberPrefix.Replace(line, string.Empty).TrimStart('*', '-', '+', ' ', '\t');
            Match m = SuggestionLine.Match(cleaned);
            string tone = string.Empty;
            string rest = string.Empty;

            if (m.Success)
            {
                tone = m.Groups["tone"].Value.Trim('*', ' ');
                rest = m.Groups["rest"].Value.Trim();
            }
            else if (inSuggestionsSection || cleaned.Contains('|') || cleaned.Contains('｜'))
            {
                var colonMatch = Regex.Match(cleaned, @"^(?:[\*\-\+]|\d+[\.、\)])?\s*(?<tone>[\u4e00-\u9fa5a-zA-Z0-9_\-\s]{2,12})[:：]\s*(?<rest>.*)$");
                if (colonMatch.Success && (colonMatch.Groups["rest"].Value.Contains('|') || colonMatch.Groups["rest"].Value.Contains('｜')))
                {
                    tone = colonMatch.Groups["tone"].Value.Trim();
                    rest = colonMatch.Groups["rest"].Value.Trim();
                }
                else if (cleaned.Contains('|') || cleaned.Contains('｜'))
                {
                    tone = "破局建议";
                    rest = cleaned;
                }
                else if (inSuggestionsSection && (Regex.IsMatch(line, @"^(?:[\*\-\+•]|\d+[\.、\)])\s*") || cleaned.StartsWith("“") || cleaned.StartsWith("\"")) && cleaned.Length >= 6 && !cleaned.StartsWith('#'))
                {
                    tone = "破局建议";
                    rest = cleaned;
                }
            }

            if (!string.IsNullOrEmpty(tone) && !string.IsNullOrEmpty(rest))
            {
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

        if (string.IsNullOrWhiteSpace(targetQuote) ||
            ((targetQuote == "[动画表情]" || targetQuote == "[图片]") &&
             !string.IsNullOrWhiteSpace(fallbackTargetQuote) &&
             fallbackTargetQuote != "[动画表情]" && fallbackTargetQuote != "[图片]"))
        {
            if (!string.IsNullOrWhiteSpace(fallbackTargetQuote))
            {
                targetQuote = fallbackTargetQuote;
            }
        }
        targetQuote = PromptBuilder.CleanMessageTextForAi(targetQuote);

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

        if (maxSuggestions.HasValue && maxSuggestions.Value > 0 && suggestions.Count > maxSuggestions.Value)
        {
            suggestions = suggestions.Take(maxSuggestions.Value).ToList();
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
