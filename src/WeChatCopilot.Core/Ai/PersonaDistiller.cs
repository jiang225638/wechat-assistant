using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Ai;

/// <summary>
/// 蒸馏结果对象：包含执行成功状态、Persona 以及诊断错误信息。
/// </summary>
public sealed record DistillResult(bool Success, Persona Persona, string? ErrorMessage);

/// <summary>
/// M5 人格蒸馏器（FR-3/FR-7）：
/// 支持单次全局精细蒸馏（高质量、低延迟）与分块 Map-Reduce（超长文本兜底），
/// 具备高容错解析器，兼容 Markdown 表格、编号列表、JSON 数组及中文标点。
/// </summary>
public static class PersonaDistiller
{
    /// <summary>专业六维 + 兼容经典七维。</summary>
    public static readonly string[] Dimensions =
    {
        "沟通风格", "性格能量", "决策模式", "情绪阈值", "价值锚点", "隐形雷区",
        "稳定属性", "语言风格", "性格心理", "情绪模式", "意图需求", "关系动态", "边界禁忌"
    };

    /// <summary>专业六维核心体系。</summary>
    public static readonly string[] StandardDimensions =
    {
        "沟通风格", "性格能量", "决策模式", "情绪阈值", "价值锚点", "隐形雷区"
    };

    /// <summary>map 分块大小（条/块）。</summary>
    public const int DefaultChunkSize = 40;

    private const string DirectDistillSystemPrompt =
        "你是资深沟通心理学与人格画像专家。用户会提供与联系人的真实聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
        "【极其重要的核心原则】：\n" +
        "1. [对方] 是本次分析的唯一主角！你必须深入研究 [对方] 的措辞、语调、诉求、反应速度与态度，提炼对方的人格特质。\n" +
        "2. [我] 的发言纯属对话语境背景参考，严禁将 [我] 的性格偏好、说话方式、提出的邀约或观点当成 [对方] 的特征！\n" +
        "3. 证据必须 100% 摘录 [对方] 亲口说过的原话，绝不允许引用 [我] 的原话！\n\n" +
        "请从以下 6 个专业维度深度提炼 [对方] 的多维画像（每个维度仅输出 1 行最佳精炼结论，禁止重复生成同一维度）：\n" +
        "1. 沟通风格 (DISC倾向：支配D/影响I/稳健S/服从C，直接高效 vs 委婉客气，长句 vs 短句)\n" +
        "2. 性格能量 (MBTI特质：内倾 vs 外倾，理性逻辑(T) vs 感性共情(F)，防备心与边界感)\n" +
        "3. 决策模式 (果断冲动 vs 审慎严密，事实证据导向 vs 人情信任导向)\n" +
        "4. 情绪阈值 (平和稳定 vs 敏感焦虑，面对延误/催促/分歧时的压力反应)\n" +
        "5. 价值锚点 (底层核心关切与利益诉求，如效率/性价比/被尊重/安全感/省心)\n" +
        "6. 隐形雷区 (沟通敏感点、抵触的表达方式、排斥的行为与绝对禁忌)\n\n" +
        "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白、问候语或解释）：\n" +
        "维度 | 特质描述 | 评分: 0到100的量化分 | 置信度: 0.0到1.0 | 证据: 对方原话引用\n" +
        "（说明：评分请根据该项特质在该维度上的突出/明显程度打出真实的0~100量化分；证据引用对方原话，多条原话用;;分隔；置信度为0.0~1.0之间的小数）";

    private const string MapSystemPrompt =
        "你是对话观察提取器。用户会给你一段聊天记录（[对方]=联系人发言，[我]=用户本人发言）。\n" +
        "【注意】：只观察和提炼 [对方] 的言行特征，严禁把 [我] 的发言当成对方的特质！证据必须全部引用 [对方] 原话！\n" +
        "严格只按以下格式输出，每项一行，不要输出其他内容：\n" +
        "维度 | 特质描述 | 评分: 0到100 | 置信度: 0.0到1.0 | 证据: 对方原话引用\n" +
        "维度从：沟通风格/性格能量/决策模式/情绪阈值/价值锚点/隐形雷区/稳定属性/语言风格/性格心理/情绪模式/意图需求/关系动态/边界禁忌 中选取（优先选取前 6 项专业维度）。";

    private const string ReduceSystemPrompt =
        "你是人格画像蒸馏专家。用户会给你若干关于 [对方] 的观察特质行（格式：维度 | 特质 | 评分: s | 置信度: x | 证据: 原话）。\n" +
        "请合并为关于 [对方] 的结构化深度画像：\n" +
        "1. 严格归纳合并为 6 个专业维度（沟通风格、性格能量、决策模式、情绪阈值、价值锚点、隐形雷区），【每个维度只输出 1 行结论，禁止重复】；\n" +
        "2. 同义去重，矛盾时保留置信度更高者，证据原话全部保留并去重，必须 100% 属于对方原话；\n" +
        "3. 综合评估给出明确的 0~100 评分（不同维度根据特质鲜明程度给出差异化评分，禁止全部打相同分值）；\n" +
        "4. 若提供已有画像则增量合并。\n" +
        "严格只按以下格式输出，每项一行，不要输出其他内容：\n" +
        "维度 | 特质描述 | 评分: 0到100 | 置信度: 0.0到1.0 | 证据: 原话;;原话";

    /// <summary>把历史按 chunkSize 分块（最后一块可能不足）。</summary>
    public static IReadOnlyList<IReadOnlyList<HistoryMessage>> Chunk(
        IReadOnlyList<HistoryMessage> history, int chunkSize = DefaultChunkSize)
    {
        if (history is null || history.Count == 0 || chunkSize <= 0)
        {
            return Array.Empty<IReadOnlyList<HistoryMessage>>();
        }

        var chunks = new List<IReadOnlyList<HistoryMessage>>();
        for (int i = 0; i < history.Count; i += chunkSize)
        {
            int n = Math.Min(chunkSize, history.Count - i);
            var block = new HistoryMessage[n];
            for (int j = 0; j < n; j++)
            {
                block[j] = history[i + j];
            }

            chunks.Add(block);
        }

        return chunks;
    }

    /// <summary>直接单次全局蒸馏请求（适用于聊天记录较集中或取最近历史场景）。</summary>
    public static AiRequest BuildDirectDistillRequest(
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing = null) =>
        BuildDirectDistillRequest(settings, contactName, history, existing, null);

    /// <summary>直接单次全局蒸馏请求（带己方昵称标注）。</summary>
    public static AiRequest BuildDirectDistillRequest(
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing,
        string? selfName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("目标联系人（对方）：" + contactName);
        if (!string.IsNullOrWhiteSpace(selfName))
        {
            sb.AppendLine("用户本人（己方）：" + selfName);
        }
        sb.AppendLine("聊天记录：");
        sb.AppendLine(Transcript(history, contactName, selfName));

        if (existing is not null && existing.Traits.Count > 0)
        {
            sb.AppendLine("已有画像（参考并增量合并）：");
            foreach (PersonaTrait t in existing.Traits)
            {
                sb.AppendLine(t.FormatLine());
            }
        }

        sb.AppendLine($"请输出针对「{contactName}」的结构化人格特质行。每行格式：维度 | 特质描述 | 评分: 0到100 | 置信度: 0.0到1.0 | 证据: 对方原话引用。严禁将用户的提议/言行当成「{contactName}」的特质，证据必须100%摘录「{contactName}」的原话。");
        return new AiRequest(DirectDistillSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>map 请求：从一块历史提取观察特质。</summary>
    public static AiRequest BuildMapRequest(AiSettings settings, IReadOnlyList<HistoryMessage> chunk) =>
        BuildMapRequest(settings, chunk, string.Empty, null);

    /// <summary>map 请求：从一块历史提取观察特质（带联系人与己方标识）。</summary>
    public static AiRequest BuildMapRequest(
        AiSettings settings,
        IReadOnlyList<HistoryMessage> chunk,
        string contactName,
        string? selfName = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(contactName))
        {
            sb.AppendLine("目标联系人（对方）：" + contactName);
        }
        if (!string.IsNullOrWhiteSpace(selfName))
        {
            sb.AppendLine("用户本人（己方）：" + selfName);
        }
        sb.AppendLine("聊天记录：");
        sb.AppendLine(Transcript(chunk, contactName, selfName));
        sb.AppendLine("请提取对方特质观察项。证据必须全部引用对方亲口原话，严禁引用用户原话。格式：维度 | 特质描述 | 评分: 0到100 | 置信度: 0.0到1.0 | 证据: 对方原话");
        return new AiRequest(MapSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>reduce 请求：合并观察项（+可选已有画像）为最终 persona。</summary>
    public static AiRequest BuildReduceRequest(
        AiSettings settings,
        string contactName,
        IReadOnlyList<PersonaTrait> observations,
        Persona? existing = null) =>
        BuildReduceRequest(settings, contactName, observations, existing, null);

    /// <summary>reduce 请求：合并观察项为最终 persona（带己方标识）。</summary>
    public static AiRequest BuildReduceRequest(
        AiSettings settings,
        string contactName,
        IReadOnlyList<PersonaTrait> observations,
        Persona? existing,
        string? selfName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("联系人：" + contactName);
        if (!string.IsNullOrWhiteSpace(selfName))
        {
            sb.AppendLine("用户本人：" + selfName);
        }
        sb.AppendLine("观察特质：");
        foreach (PersonaTrait t in observations)
        {
            sb.AppendLine(t.FormatLine());
        }

        if (existing is not null && existing.Traits.Count > 0)
        {
            sb.AppendLine("已有画像（增量合并，保留仍成立结论）：");
            foreach (PersonaTrait t in existing.Traits)
            {
                sb.AppendLine(t.FormatLine());
            }
        }

        sb.AppendLine($"请输出合并后的画像。每个维度仅输出 1 行，评分必须明确给出0到100分，证据100%引用对方原话。");
        return new AiRequest(ReduceSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>
    /// 超强容错特质解析器：兼容常规文本行、Markdown 表格、列表序号、中文字符与 JSON 数组。
    /// 自动清洗冒充对方原话的己方发言，并根据置信度与模型评分生成合理评分。
    /// </summary>
    public static IReadOnlyList<PersonaTrait> ParseTraits(string? raw) =>
        ParseTraits(raw, null);

    /// <summary>带聊天历史校对的特质解析器：严格过滤己方（Outgoing）原话并归纳去重。</summary>
    public static IReadOnlyList<PersonaTrait> ParseTraits(string? raw, IReadOnlyList<HistoryMessage>? history)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<PersonaTrait>();
        }

        // 1. 尝试 JSON 格式解析（部分模型倾向输出 JSON）
        var jsonTraits = TryParseJsonTraits(raw, history);
        if (jsonTraits is { Count: > 0 })
        {
            return DeduplicateByDimension(jsonTraits, history);
        }

        var list = new List<PersonaTrait>();
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("```") || Regex.IsMatch(line, @"^[\|\s\-:]+$"))
            {
                continue;
            }

            // 支持中文全角竖线 ｜
            line = line.Replace('｜', '|');

            // 如果是 Markdown 表格行，形如 "| 语言风格 | 特质描述 | 80 | 0.9 | 证据 |"
            if (line.StartsWith("|") && line.EndsWith("|") && line.Length > 2)
            {
                line = line[1..^1].Trim();
            }

            // 去除行首序号或无序列表标记，例如 "1. ", "- ", "* ", "• "
            line = Regex.Replace(line, @"^(\d+[\.、\)]|[-*•])\s*", "");

            string[] seg = line.Split('|');
            if (seg.Length < 2)
            {
                continue;
            }

            string dimension = seg[0].Trim();
            string attribute = seg[1].Trim();

            // 跳过表头 "维度 | 特质"
            if (dimension is "维度" or "Dimension" or "dim" || attribute is "特质" or "特质描述" or "Attribute")
            {
                continue;
            }

            if (dimension.Length == 0 || attribute.Length == 0)
            {
                continue;
            }

            // 规范化维度名
            dimension = MatchKnownDimension(dimension);

            double confidence = 0.5;
            double score = 0;
            var evidence = new List<string>();

            for (int i = 2; i < seg.Length; i++)
            {
                string s = seg[i].Trim();
                if (s.Length == 0) continue;

                // 评分解析 (如 "评分: 85", "85分", "score: 85", "得分: 90")
                var mScore = Regex.Match(s, @"(?:评分|得分|score)[:：]?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (mScore.Success && double.TryParse(mScore.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedScore))
                {
                    score = Math.Clamp(parsedScore, 10, 100);
                    continue;
                }

                var mSuffix = Regex.Match(s, @"^(\d{1,3})\s*分$");
                if (mSuffix.Success && double.TryParse(mSuffix.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSuffix))
                {
                    score = Math.Clamp(parsedSuffix, 10, 100);
                    continue;
                }

                // 置信度解析 (如 "置信度: 0.85", "85%", "置信度: 9")
                var mConf = Regex.Match(s, @"(?:置信度|confidence)[:：]?\s*(\d+(?:\.\d+)?%?)", RegexOptions.IgnoreCase);
                if (mConf.Success)
                {
                    string cStr = mConf.Groups[1].Value;
                    if (cStr.EndsWith("%") && double.TryParse(cStr[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    {
                        confidence = Math.Clamp(pct / 100.0, 0d, 1d);
                        continue;
                    }
                    if (double.TryParse(cStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double cVal))
                    {
                        confidence = cVal > 10.0 ? Math.Clamp(cVal / 100.0, 0d, 1d) : Math.Clamp(cVal, 0d, 1d);
                        continue;
                    }
                }
                else if (s.EndsWith("%") && double.TryParse(s[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    confidence = Math.Clamp(pct / 100.0, 0d, 1d);
                    continue;
                }
                else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double numVal) && numVal <= 1.0 && numVal >= 0.0)
                {
                    confidence = numVal;
                    continue;
                }

                // 证据解析
                string e = s;
                foreach (string prefix in new[] { "证据:", "证据：", "evidence:", "证据" })
                {
                    if (e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        e = e[prefix.Length..].Trim();
                        break;
                    }
                }

                evidence.AddRange(e.Split(";;")
                    .Select(q => q.Trim().Trim('"', '“', '”', '`', '\''))
                    .Where(q => q.Length > 0 && q != "无" && q != "None"));
            }

            if (score <= 0)
            {
                score = Math.Clamp(Math.Round(confidence * 100), 35, 95);
            }

            var filteredEvidence = FilterEvidence(evidence, history);
            list.Add(new PersonaTrait(dimension, attribute, confidence, filteredEvidence, score));
        }

        return DeduplicateByDimension(list, history);
    }

    /// <summary>
    /// 严防证据污染：对比历史消息收发方向，坚决剔除任何属于己方（Outgoing）的发言原话。
    /// </summary>
    public static List<string> FilterEvidence(
        IEnumerable<string>? rawEvidence,
        IReadOnlyList<HistoryMessage>? history)
    {
        if (rawEvidence is null)
        {
            return new List<string>();
        }

        var list = rawEvidence
            .Select(q => q.Trim().Trim('"', '“', '”', '`', '\''))
            .Where(q => q.Length > 0 && q != "无" && q != "None")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (history is null || history.Count == 0 || list.Count == 0)
        {
            return list;
        }

        var incomingTexts = history
            .Where(m => m.Role == MessageRole.Incoming && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text.Trim())
            .ToList();

        var outgoingTexts = history
            .Where(m => m.Role == MessageRole.Outgoing && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text.Trim())
            .ToList();

        var valid = new List<string>();
        foreach (var quote in list)
        {
            // 检查是否属于用户自己的发言
            bool matchesUser = outgoingTexts.Any(outText =>
                outText.Contains(quote, StringComparison.OrdinalIgnoreCase) ||
                quote.Contains(outText, StringComparison.OrdinalIgnoreCase));

            // 检查是否属于对方发言
            bool matchesOther = incomingTexts.Any(inText =>
                inText.Contains(quote, StringComparison.OrdinalIgnoreCase) ||
                quote.Contains(inText, StringComparison.OrdinalIgnoreCase));

            // 如果明确只匹配用户自己的发言，而不匹配对方发言，坚决剔除！
            if (matchesUser && !matchesOther)
            {
                continue;
            }

            valid.Add(quote);
        }

        return valid;
    }

    /// <summary>
    /// 按维度去重与合并：合并同维度特质，聚合真实证据，消除重复维度。
    /// </summary>
    public static IReadOnlyList<PersonaTrait> DeduplicateByDimension(
        IReadOnlyList<PersonaTrait> traits,
        IReadOnlyList<HistoryMessage>? history = null)
    {
        if (traits is null || traits.Count == 0)
        {
            return Array.Empty<PersonaTrait>();
        }

        var result = new List<PersonaTrait>();
        var groups = traits.GroupBy(t => NormalizeDimension(t.Dimension));

        foreach (var group in groups)
        {
            string normDim = group.Key;
            var best = group.OrderByDescending(t => t.Confidence).First();

            var allEvidence = group.SelectMany(t => t.Evidence);
            var filteredEvidence = FilterEvidence(allEvidence, history);

            // 若条目本身评分 > 0 则沿用，否则根据置信度动态推导（避免千篇一律 75 分）
            double score = best.Score > 0 ? best.Score : Math.Clamp(Math.Round(best.Confidence * 100), 35, 95);

            // 保留原本匹配度最高的维度名
            string dimToUse = group.Any(t => StandardDimensions.Contains(t.Dimension))
                ? normDim
                : best.Dimension;

            result.Add(new PersonaTrait(dimToUse, best.Attribute, best.Confidence, filteredEvidence, score));
        }

        return result;
    }

    /// <summary>
    /// 将任意历史维度或别名归一化映射到标准六大专业维度。
    /// </summary>
    public static string NormalizeDimension(string dim)
    {
        if (string.IsNullOrWhiteSpace(dim)) return "沟通风格";
        dim = dim.Trim();

        if (dim.Contains("沟通风格") || dim.Contains("语言风格") || dim.Contains("沟通偏好") || dim.Contains("表达风格"))
            return "沟通风格";
        if (dim.Contains("性格能量") || dim.Contains("性格心理") || dim.Contains("性格特质") || dim.Contains("MBTI") || dim.Contains("互动动力"))
            return "性格能量";
        if (dim.Contains("决策模式") || dim.Contains("决策偏好") || dim.Contains("决断风格") || dim.Contains("价值取向"))
            return "决策模式";
        if (dim.Contains("情绪阈值") || dim.Contains("情绪模式") || dim.Contains("心理阈值") || dim.Contains("压力应对"))
            return "情绪阈值";
        if (dim.Contains("价值锚点") || dim.Contains("意图需求") || dim.Contains("核心关切") || dim.Contains("稳定属性"))
            return "价值锚点";
        if (dim.Contains("隐形雷区") || dim.Contains("边界禁忌") || dim.Contains("关系动态") || dim.Contains("沟通禁忌") || dim.Contains("社交关系") || dim.Contains("雷区"))
            return "隐形雷区";

        foreach (string known in StandardDimensions)
        {
            if (dim.Contains(known) || known.Contains(dim)) return known;
        }

        return dim;
    }

    private static string MatchKnownDimension(string dim)
    {
        foreach (string known in Dimensions)
        {
            if (dim.Equals(known, StringComparison.OrdinalIgnoreCase) || dim.Contains(known))
            {
                return known;
            }
        }

        return dim;
    }

    private static List<PersonaTrait>? TryParseJsonTraits(string raw, IReadOnlyList<HistoryMessage>? history = null)
    {
        try
        {
            int start = raw.IndexOf('[');
            int end = raw.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                string json = raw.Substring(start, end - start + 1);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PersonaTrait>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        string dim = GetPropString(item, "dimension", "维度", "dim");
                        string attr = GetPropString(item, "attribute", "特质", "特质描述", "desc");
                        double conf = GetPropDouble(item, 0.75, "confidence", "置信度", "conf");
                        double score = GetPropDouble(item, Math.Round(conf * 100), "score", "评分", "得分", "scoreInt");
                        var evList = new List<string>();

                        if (item.TryGetProperty("evidence", out var ev) || item.TryGetProperty("证据", out ev))
                        {
                            if (ev.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var e in ev.EnumerateArray())
                                {
                                    string s = e.ToString().Trim();
                                    if (s.Length > 0)
                                    {
                                        evList.Add(s);
                                    }
                                }
                            }
                            else if (ev.ValueKind == JsonValueKind.String)
                            {
                                evList.AddRange(ev.GetString()!.Split(";;").Select(s => s.Trim()).Where(s => s.Length > 0));
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(dim) && !string.IsNullOrWhiteSpace(attr))
                        {
                            var filteredEvidence = FilterEvidence(evList, history);
                            list.Add(new PersonaTrait(MatchKnownDimension(dim), attr, conf, filteredEvidence, score));
                        }
                    }

                    if (list.Count > 0)
                    {
                        return DeduplicateByDimension(list, history).ToList();
                    }
                }
            }
        }
        catch
        {
            // 忽略非 JSON
        }

        return null;
    }

    private static string GetPropString(JsonElement el, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (el.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String)
            {
                return v.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static double GetPropDouble(JsonElement el, double fallback, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (el.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
                {
                    return d;
                }

                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// 详细蒸馏方法：返回 <see cref="DistillResult"/>，包含成功与否及精确的错误诊断原因。
    /// 策略：分块 Map-Reduce 处理历史消息，严格区分收发双方，并在聚合阶段消除重复维度。
    /// </summary>
    public static Task<DistillResult> DistillDetailedAsync(
        IAiProvider provider,
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing = null,
        CancellationToken cancellationToken = default) =>
        DistillDetailedAsync(provider, settings, contactName, history, existing, null, cancellationToken);

    /// <summary>带己方名称的详细蒸馏方法。</summary>
    public static async Task<DistillResult> DistillDetailedAsync(
        IAiProvider provider,
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing,
        string? selfName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);

        if (history is null || history.Count == 0)
        {
            return new DistillResult(false, new Persona(contactName, Array.Empty<PersonaTrait>(), DateTime.Now, 0), "聊天历史记录为空。");
        }

        // 选取最具代表性的最近 100 条历史进行分块分析（避免超长记录导致串行请求爆炸）
        var sampleHistory = history.Count > 100 ? history.TakeLast(100).ToList() : history;
        var chunks = Chunk(sampleHistory, chunkSize: 50);

        var observations = new List<PersonaTrait>();
        string lastError = string.Empty;
        string lastMapReply = string.Empty;

        foreach (IReadOnlyList<HistoryMessage> chunk in chunks)
        {
            AiReply map = await provider.CompleteAsync(BuildMapRequest(settings, chunk, contactName, selfName), cancellationToken);
            if (map.Success)
            {
                lastMapReply = map.Text;
                observations.AddRange(ParseTraits(map.Text, history));
            }
            else
            {
                lastError = map.Error ?? "大模型调用失败";
            }
        }

        if (observations.Count == 0 && existing is null)
        {
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                return new DistillResult(false, new Persona(contactName, Array.Empty<PersonaTrait>(), DateTime.Now, history.Count),
                    $"模型调用失败: {lastError}");
            }

            string preview = string.IsNullOrWhiteSpace(lastMapReply)
                ? "无返回内容"
                : (lastMapReply.Length > 80 ? lastMapReply[..80] + "..." : lastMapReply).Replace('\n', ' ');

            return new DistillResult(false, new Persona(contactName, Array.Empty<PersonaTrait>(), DateTime.Now, history.Count),
                $"大模型未按约定格式输出特质行（模型回复片段: {preview}）");
        }

        // 先对观察项进行初次归纳去重
        var cleanObservations = DeduplicateByDimension(observations, history);

        AiReply reduce = await provider.CompleteAsync(
            BuildReduceRequest(settings, contactName, cleanObservations, existing, selfName), cancellationToken);

        IReadOnlyList<PersonaTrait> merged = reduce.Success ? ParseTraits(reduce.Text, history) : Array.Empty<PersonaTrait>();
        if (merged.Count == 0)
        {
            merged = cleanObservations;
        }

        // 最终保障：归一化合并，彻底杜绝重复维度与己方原话
        var finalTraits = DeduplicateByDimension(merged, history);

        if (finalTraits.Count > 0)
        {
            return new DistillResult(true, new Persona(contactName, finalTraits, DateTime.Now, history.Count), null);
        }

        string reducePreview = reduce.Success
            ? (reduce.Text.Length > 80 ? reduce.Text[..80] + "..." : reduce.Text).Replace('\n', ' ')
            : (reduce.Error ?? "调用失败");

        return new DistillResult(false, new Persona(contactName, Array.Empty<PersonaTrait>(), DateTime.Now, history.Count),
            $"合并特质失败: {reducePreview}");
    }

    /// <summary>
    /// 标准蒸馏入口：保留原有签名，直接返回 <see cref="Persona"/>。
    /// </summary>
    public static async Task<Persona> DistillAsync(
        IAiProvider provider,
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing = null,
        CancellationToken cancellationToken = default)
    {
        var res = await DistillDetailedAsync(provider, settings, contactName, history, existing, null, cancellationToken);
        return res.Persona;
    }

    /// <summary>历史转写为带角色标签的多行文本（带可选时间戳前缀）。</summary>
    private static string Transcript(
        IReadOnlyList<HistoryMessage> history,
        string? contactName = null,
        string? selfName = null)
    {
        var sb = new StringBuilder();
        string otherTag = string.IsNullOrWhiteSpace(contactName) ? "对方" : $"对方({contactName})";
        string selfTag = string.IsNullOrWhiteSpace(selfName) ? "我" : $"我({selfName})";

        foreach (HistoryMessage m in history)
        {
            string who = m.Role switch
            {
                MessageRole.Incoming => otherTag,
                MessageRole.Outgoing => selfTag,
                _ => "未知"
            };
            string ts = m.Timestamp is null ? string.Empty : m.Timestamp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ";
            sb.AppendLine($"[{who}] {ts}{m.Text.Replace("\n", " ")}");
        }

        return sb.ToString();
    }
}
