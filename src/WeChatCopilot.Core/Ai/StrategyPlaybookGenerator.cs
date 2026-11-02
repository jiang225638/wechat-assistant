using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Diagnostics;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;

namespace WeChatCopilot.Core.Ai;

/// <summary>策略手册中的一张卡片：标题 + 正文（多行文本）。</summary>
public sealed record StrategySection(string Title, string Content);

/// <summary>
/// AI 生成的「实战沟通破局策略与避坑指南」，对应放大看板第三个 Tab 的四张卡片。
/// </summary>
public sealed record StrategyPlaybook(
    StrategySection Mindset,
    StrategySection Leverage,
    StrategySection Taboos,
    StrategySection Scripts,
    string? Model = null,
    DateTime? GeneratedAt = null);

/// <summary>
/// 基于画像维度、证据、技能体系、终极目的与最近对话，调用大模型生成策略手册。
/// 生成失败时由调用方回退到本地模板；本类只负责提示词装配、结果容错解析与缓存键计算。
/// </summary>
public static class StrategyPlaybookGenerator
{
    private const int RecentMessageLimit = 12;

    /// <summary>构建生成请求。</summary>
    public static AiRequest BuildRequest(
        AiSettings settings,
        Persona persona,
        IReadOnlyList<PersonaTrait> traits,
        DistillSkill skill,
        string sceneName,
        IReadOnlyList<HistoryMessage>? history)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(skill);

        const string system =
            "你是资深的沟通策略与心理分析专家。用户会给出某位联系人的多维心智画像（含量化评分、可信度、原话证据）、" +
            "所采用的技能体系、交往身份、终极目的以及最近的对话片段。请据此输出一份【针对这个具体的人】的实战沟通策略与避坑指南。\n\n" +
            "硬性要求：\n" +
            "1. 只输出一个合法的 JSON 对象，不要任何开场白、解释或 Markdown 代码围栏。\n" +
            "2. JSON 结构必须为：{\"mindset\":{\"title\":\"\",\"content\":\"\"},\"leverage\":{\"title\":\"\",\"content\":\"\"}," +
            "\"taboos\":{\"title\":\"\",\"content\":\"\"},\"scripts\":{\"title\":\"\",\"content\":\"\"}}。content 为纯文本，多条要点用 \\n 分隔，每条以“• ”或“1. ”开头。\n" +
            "3. 【重要 JSON 转义纪律】：title 和 content 字符串值内部如需引用原话、话术或专有名词，请一律使用中文书名号/引号「」或“”，严禁在字符串值内部直接写未转义的英文半角双引号 \"，也严禁在字符串值内直接硬回车换行！\n" +
            "4. mindset=核心心智与诉求底色；leverage=可顺势借力的抓手（2~3 条，要具体可执行）；" +
            "taboos=沟通雷区与避坑红线（2~4 条，说明为什么）；scripts=2 条可直接发送的示范话术，每条附一句“心法点拨”。\n" +
            "5. 结论必须锚定画像证据，引用维度名与对方原话；话术要贴合最近对话的语境与对方的说话方式，不得套用万能模板。\n" +
            "6. 可信度低（<0.5）、或特质描述写“无法判断/信息不足/暂无”的维度，只代表证据不足，" +
            "严禁当作对方的性格缺陷、防御或脆弱点；应明确指出“信息不足”，并建议如何自然地补充观察。\n" +
            "7. 不要夸大分数含义：分数中等或偏低不等于“极强”，措辞要与分数、可信度相称。\n" +
            "8. 不要输出任何恭维、鸡汤或空泛套话；title 需含具体维度名，简洁（20 字以内，可带一个 emoji）。\n" +
            "9. 用中文回答。";

        var recent = (history ?? Array.Empty<HistoryMessage>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(RecentMessageLimit)
            .ToList();

        string routingQuery = string.Join(" ",
            traits.Take(6).Select(t => $"{t.Dimension} {t.Attribute}")
            .Concat(recent.TakeLast(5).Select(m => m.Text))
            .Append(persona.UltimateGoal ?? string.Empty)
            .Append(sceneName));
        string dynamicGuideline = skill.GetGuidelineForQa(routingQuery, out string? routedModule);

        var sb = new StringBuilder();
        sb.AppendLine($"【联系人】{persona.ContactName}");
        sb.AppendLine($"【交往身份】{sceneName}");
        sb.AppendLine($"【技能体系】{skill.Icon} {skill.Name}：{skill.Description}");
        if (!string.IsNullOrWhiteSpace(routedModule))
        {
            sb.AppendLine($"【按需路由挂载专项子模块】{routedModule}");
        }
        sb.AppendLine($"【终极目的】{(string.IsNullOrWhiteSpace(persona.UltimateGoal) ? "未设定" : persona.UltimateGoal)}");
        sb.AppendLine($"【样本规模】{persona.SourceMessageCount} 条历史消息");
        sb.AppendLine();
        sb.AppendLine("【技能方法论摘要（含结构化路由子模块）】");
        sb.AppendLine(Truncate(dynamicGuideline, 3200));
        sb.AppendLine();
        sb.AppendLine("【多维画像（维度 | 特质 | 评分 | 可信度 | 证据）】");
        foreach (var t in traits.OrderByDescending(t => t.Score))
        {
            string evidence = t.Evidence is { Count: > 0 }
                ? string.Join(";;", t.Evidence.Take(3).Select(e => Truncate(ReplaceAsciiQuotes(e), 80)))
                : "无";
            sb.AppendLine($"- {t.Dimension} | {ReplaceAsciiQuotes(t.Attribute)} | {t.ScoreInt}分 | 可信度 {t.ConfidenceText} | 证据: {evidence}");
        }

        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【最近对话（[对方]=联系人，[我]=用户本人）】");
            foreach (var m in recent)
            {
                string who = m.Role == MessageRole.Incoming ? "[对方]" : "[我]";
                sb.AppendLine($"{who} {Truncate(ReplaceAsciiQuotes(m.Text.Replace('\n', ' ')), 120)}");
            }
        }

        return new AiRequest(system, sb.ToString(), settings.Model, 0.6, 4096);
    }

    /// <summary>调用提供商生成策略手册；失败返回 (null, 错误信息)。</summary>
    public static async Task<(StrategyPlaybook? Playbook, string? Error)> GenerateAsync(
        IAiProvider provider,
        AiSettings settings,
        Persona persona,
        IReadOnlyList<PersonaTrait> traits,
        DistillSkill skill,
        string sceneName,
        IReadOnlyList<HistoryMessage>? history,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(settings, persona, traits, skill, sceneName, history);
        var reply = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
        {
            return (null, reply.Error ?? "大模型调用失败");
        }

        var playbook = Parse(reply.Text);
        if (playbook is null)
        {
            string preview = string.IsNullOrWhiteSpace(reply.Text)
                ? "<empty>"
                : Truncate(reply.Text.Replace('\r', ' ').Replace('\n', ' '), 200);
            AppLog.Warn("StrategyPlaybookGenerator", $"策略手册解析失败，模型原始输出片段: {preview}");
            return (null, "大模型未按约定的 JSON 格式输出策略手册。");
        }

        return (playbook with { Model = reply.Model ?? settings.Model, GeneratedAt = DateTime.Now }, null);
    }

    /// <summary>
    /// 高容错解析：
    /// 1. 优先使用允许尾随逗号/注释的 JsonDocument 解析；
    /// 2. 若大模型在引用原话/话术时写入了未转义的英文双引号 " 或直接硬回车换行导致严格 JSON 报错，自动启用结构化块级容错提取；
    /// 3. 若大模型直接输出了四段式 Markdown 标题，也支持回退解析。
    /// 四张卡片缺一即视为失败。
    /// </summary>
    public static StrategyPlaybook? Parse(string? raw)
    {
        string cleanedRaw = AiOutputParser.StripThinkingBlocks(raw);
        if (string.IsNullOrWhiteSpace(cleanedRaw)) return null;

        int start = cleanedRaw.IndexOf('{');
        int end = cleanedRaw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            string jsonSlice = cleanedRaw[start..(end + 1)];

            // 层级 1：标准 JSON 解析（允许尾随逗号与注释）
            var strictResult = TryParseJsonDocument(jsonSlice);
            if (strictResult is not null)
            {
                return strictResult;
            }

            // 层级 2：针对字符串内含未转义双引号（如引用原话 "报备式收线"）或直接硬回车换行的块级结构化容错提取
            var relaxedResult = TryParseRelaxedJsonSections(jsonSlice);
            if (relaxedResult is not null)
            {
                return relaxedResult;
            }
        }
        else if (start >= 0)
        {
            // 处理末尾因 token 截断缺少闭合 } 的 JSON
            var truncatedResult = TryParseRelaxedJsonSections(cleanedRaw[start..]);
            if (truncatedResult is not null)
            {
                return truncatedResult;
            }
        }

        // 层级 3：Markdown 标题分节兜底解析
        return TryParseMarkdownSections(cleanedRaw);
    }

    private static StrategyPlaybook? TryParseJsonDocument(string jsonSlice)
    {
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(jsonSlice, options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var mindset = ReadSection(root, "mindset");
            var leverage = ReadSection(root, "leverage");
            var taboos = ReadSection(root, "taboos");
            var scripts = ReadSection(root, "scripts");
            if (mindset is null || leverage is null || taboos is null || scripts is null) return null;

            return new StrategyPlaybook(mindset, leverage, taboos, scripts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Regex SectionKeyRegex = new(
        @"""(?<key>mindset|leverage|taboos|scripts)""\s*:\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static StrategyPlaybook? TryParseRelaxedJsonSections(string text)
    {
        var matches = SectionKeyRegex.Matches(text);
        if (matches.Count < 4)
        {
            return null;
        }

        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            string key = matches[i].Groups["key"].Value.ToLowerInvariant();
            int blockStart = matches[i].Index + matches[i].Length;
            int blockEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
            if (blockEnd > blockStart && !blocks.ContainsKey(key))
            {
                blocks[key] = text[blockStart..blockEnd];
            }
        }

        if (!blocks.TryGetValue("mindset", out string? bMindset) ||
            !blocks.TryGetValue("leverage", out string? bLeverage) ||
            !blocks.TryGetValue("taboos", out string? bTaboos) ||
            !blocks.TryGetValue("scripts", out string? bScripts))
        {
            return null;
        }

        var mindset = ExtractRelaxedSection(bMindset);
        var leverage = ExtractRelaxedSection(bLeverage);
        var taboos = ExtractRelaxedSection(bTaboos);
        var scripts = ExtractRelaxedSection(bScripts);

        if (mindset is null || leverage is null || taboos is null || scripts is null)
        {
            return null;
        }

        return new StrategyPlaybook(mindset, leverage, taboos, scripts);
    }

    private static readonly Regex TitleFieldRegex = new(
        @"""title""\s*:\s*""(?<val>[\s\S]*?)""\s*,\s*""content""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContentAfterTitleRegex = new(
        @"""content""\s*:\s*(?<val>\[[\s\S]*?\]|""[\s\S]*?"")\s*\}?\s*,?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContentBeforeTitleRegex = new(
        @"""content""\s*:\s*(?<cval>\[[\s\S]*?\]|""[\s\S]*?"")\s*,\s*""title""\s*:\s*""(?<tval>[\s\S]*?)""\s*\}?\s*,?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static StrategySection? ExtractRelaxedSection(string rawBlock)
    {
        string trimmed = rawBlock.Trim();
        // 剥离块末尾多余的顶层闭合花括号与逗号
        trimmed = Regex.Replace(trimmed, @"\}\s*,?\s*\}?\s*$", "}").Trim();

        string title = string.Empty;
        string contentRaw = string.Empty;

        var titleMatch = TitleFieldRegex.Match(trimmed);
        if (titleMatch.Success)
        {
            title = UnescapeRelaxedJsonText(titleMatch.Groups["val"].Value);
            string afterTitle = trimmed[(titleMatch.Index + titleMatch.Length - "\"content\"".Length)..];
            var contentMatch = ContentAfterTitleRegex.Match(afterTitle);
            if (contentMatch.Success)
            {
                contentRaw = contentMatch.Groups["val"].Value.Trim();
            }
            else
            {
                int colonIdx = afterTitle.IndexOf(':');
                if (colonIdx >= 0)
                {
                    contentRaw = afterTitle[(colonIdx + 1)..].Trim().TrimEnd(',', '}').Trim();
                }
            }
        }
        else
        {
            var revMatch = ContentBeforeTitleRegex.Match(trimmed);
            if (revMatch.Success)
            {
                contentRaw = revMatch.Groups["cval"].Value.Trim();
                title = UnescapeRelaxedJsonText(revMatch.Groups["tval"].Value);
            }
            else
            {
                // 仅含 content 字段的情况
                var cOnly = ContentAfterTitleRegex.Match(trimmed);
                if (cOnly.Success)
                {
                    contentRaw = cOnly.Groups["val"].Value.Trim();
                }
            }
        }

        string content = ParseRelaxedContentValue(contentRaw);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return new StrategySection(title.Trim(), content.Trim());
    }

    private static string ParseRelaxedContentValue(string contentRaw)
    {
        if (string.IsNullOrWhiteSpace(contentRaw))
        {
            return string.Empty;
        }

        if (contentRaw.StartsWith('['))
        {
            string inner = contentRaw.TrimStart('[').TrimEnd(']').Trim();
            var lines = new List<string>();
            foreach (Match m in Regex.Matches(inner, @"""((?:\\.|[^""\\])*)"""))
            {
                string item = UnescapeRelaxedJsonText(m.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(item))
                {
                    lines.Add(item);
                }
            }
            if (lines.Count > 0)
            {
                return string.Join("\n", lines);
            }
        }

        if (contentRaw.StartsWith('"'))
        {
            contentRaw = contentRaw[1..];
        }
        if (contentRaw.EndsWith('"'))
        {
            contentRaw = contentRaw[..^1];
        }

        return UnescapeRelaxedJsonText(contentRaw);
    }

    private static string UnescapeRelaxedJsonText(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        return s
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\n")
            .Replace("\\t", " ")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Trim();
    }

    private static StrategyPlaybook? TryParseMarkdownSections(string text)
    {
        var sectionRegex = new Regex(
            @"(?:^|\n)\s*(?:#{1,4}\s*|\*\*|【)(?<header>[^】\n*]*?(?:mindset|核心心智|主导心智|leverage|顺势借力|借力抓手|taboos|沟通雷区|避坑红线|敏感死穴|scripts|示范话术|实战话术)[^】\n*]*)(?:】|\*\*)?\s*\n(?<body>[\s\S]*?)(?=(?:\n\s*(?:#{1,4}\s*|\*\*|【)[^】\n*]*?(?:mindset|核心心智|主导心智|leverage|顺势借力|借力抓手|taboos|沟通雷区|避坑红线|敏感死穴|scripts|示范话术|实战话术))|$)",
            RegexOptions.IgnoreCase);

        var matches = sectionRegex.Matches(text);
        if (matches.Count < 4)
        {
            return null;
        }

        StrategySection? mindset = null, leverage = null, taboos = null, scripts = null;
        foreach (Match m in matches)
        {
            string h = m.Groups["header"].Value.Trim().Trim('#', '*', ' ', ':', '：');
            string body = m.Groups["body"].Value.Trim();
            if (string.IsNullOrWhiteSpace(body)) continue;

            if (mindset is null && Regex.IsMatch(h, @"mindset|核心心智|主导心智", RegexOptions.IgnoreCase))
            {
                mindset = new StrategySection(h, body);
            }
            else if (leverage is null && Regex.IsMatch(h, @"leverage|顺势借力|借力抓手", RegexOptions.IgnoreCase))
            {
                leverage = new StrategySection(h, body);
            }
            else if (taboos is null && Regex.IsMatch(h, @"taboos|沟通雷区|避坑红线|敏感死穴", RegexOptions.IgnoreCase))
            {
                taboos = new StrategySection(h, body);
            }
            else if (scripts is null && Regex.IsMatch(h, @"scripts|示范话术|实战话术", RegexOptions.IgnoreCase))
            {
                scripts = new StrategySection(h, body);
            }
        }

        if (mindset is null || leverage is null || taboos is null || scripts is null)
        {
            return null;
        }

        return new StrategyPlaybook(mindset, leverage, taboos, scripts);
    }

    private static string ReplaceAsciiQuotes(string? input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('"'))
        {
            return input ?? string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        bool open = true;
        foreach (char c in input)
        {
            if (c == '"')
            {
                sb.Append(open ? '「' : '」');
                open = !open;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 缓存键：联系人 + 画像更新时间 + 技能 + 交往身份 + 终极目的 + 最近对话尾部。
    /// 任一变化都会使旧缓存失效。
    /// </summary>
    public static string ComputeCacheKey(
        Persona persona,
        DistillSkill skill,
        string sceneName,
        IReadOnlyList<HistoryMessage>? history)
    {
        string tail = history is { Count: > 0 }
            ? string.Join("\n", history.TakeLast(RecentMessageLimit).Select(m => $"{m.Role}:{m.Text}"))
            : string.Empty;
        string material = string.Join("|",
            persona.ContactName, persona.UpdatedAt.Ticks, skill.Id, skill.Name, skill.Description,
            sceneName, persona.UltimateGoal ?? string.Empty, tail);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    private static StrategySection? ReadSection(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;

        string title = ReadText(el, "title");
        string content = ReadText(el, "content");
        if (string.IsNullOrWhiteSpace(content)) return null;
        return new StrategySection(title.Trim(), content.Trim());
    }

    private static string ReadText(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", v.EnumerateArray().Select(x =>
                x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString())),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => v.ToString()
        };
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
