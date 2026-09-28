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
    /// <summary>persona 七维（FR-3）。</summary>
    public static readonly string[] Dimensions =
        { "稳定属性", "语言风格", "性格心理", "情绪模式", "意图需求", "关系动态", "边界禁忌" };

    /// <summary>map 分块大小（条/块）。</summary>
    public const int DefaultChunkSize = 40;

    private const string DirectDistillSystemPrompt =
        "你是资深沟通心理学与人格画像专家。用户会提供与联系人的聊天记录（[对方]=对方消息，[我]=用户自己消息）。\n" +
        "请深入分析对方（[对方]）的沟通习惯、行为模式与特质，严格从以下七个维度提炼人格特质画像：\n" +
        "1. 稳定属性（职业/作息/生活习惯等事实推断）\n" +
        "2. 语言风格（常用词、句式长短、表情符号、语气态度）\n" +
        "3. 性格心理（外向/内向、直接/委婉、严谨/随意、防御心等）\n" +
        "4. 情绪模式（易焦虑/平和、受压反应、情绪波动触发点）\n" +
        "5. 意图需求（当下核心关切、利益诉求、沟通目标）\n" +
        "6. 关系动态（对我的信任度、亲疏远近、话语权与互动模式）\n" +
        "7. 边界禁忌（反感的雷区、敏感话题、交流禁忌）\n\n" +
        "输出格式要求（每项一行，严格使用竖线 | 分隔，不要输出开场白、问候语或任何总结说明）：\n" +
        "维度 | 特质描述 | 置信度: 0.0到1.0 | 证据: 对方原话引用\n" +
        "（说明：维度必须在七维中；置信度为0.0~1.0之间的小数；证据必须引用对方原话，多条用;;分隔）";

    private const string MapSystemPrompt =
        "你是对话观察提取器。用户会给你一段聊天记录（[对方]=对方消息，[我]=用户自己消息）。" +
        "请提取对方可观察的人格特质观察项。严格只按以下格式输出，每项一行，不要输出其他内容：\n" +
        "维度 | 特质描述 | 置信度: 0.0到1.0 | 证据: 对方原话引用\n" +
        "维度只能是：稳定属性/语言风格/性格心理/情绪模式/意图需求/关系动态/边界禁忌；" +
        "证据必须是对方原话，多条用;;分隔；只观察不推断过度。";

    private const string ReduceSystemPrompt =
        "你是人格画像蒸馏专家。用户会给你若干观察特质行（格式：维度 | 特质 | 置信度: x | 证据: 原话）。" +
        "请合并为结构化画像：同义去重、矛盾时保留置信度更高者、证据原话全部保留；" +
        "若提供已有画像则增量合并，保留仍然成立的历史结论。" +
        "严格只按以下格式输出，每项一行，不要输出其他内容：\n" +
        "维度 | 特质描述 | 置信度: 0.0到1.0 | 证据: 原话;;原话";

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
        Persona? existing = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("目标联系人：" + contactName);
        sb.AppendLine("聊天记录：");
        sb.AppendLine(Transcript(history));

        if (existing is not null && existing.Traits.Count > 0)
        {
            sb.AppendLine("已有画像（参考并增量合并）：");
            foreach (PersonaTrait t in existing.Traits)
            {
                sb.AppendLine(t.FormatLine());
            }
        }

        sb.AppendLine($"请输出针对「{contactName}」的结构化人格特质行：");
        return new AiRequest(DirectDistillSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>map 请求：从一块历史提取观察特质。</summary>
    public static AiRequest BuildMapRequest(AiSettings settings, IReadOnlyList<HistoryMessage> chunk) =>
        new(MapSystemPrompt,
            "聊天记录：\n" + Transcript(chunk) + "\n请提取对方特质观察项。",
            settings.Model,
            settings.Temperature);

    /// <summary>reduce 请求：合并观察项（+可选已有画像）为最终 persona。</summary>
    public static AiRequest BuildReduceRequest(
        AiSettings settings,
        string contactName,
        IReadOnlyList<PersonaTrait> observations,
        Persona? existing = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("联系人：" + contactName);
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

        sb.AppendLine("请输出合并后的画像。");
        return new AiRequest(ReduceSystemPrompt, sb.ToString(), settings.Model, settings.Temperature);
    }

    /// <summary>
    /// 超强容错特质解析器：兼容常规文本行、Markdown 表格、列表序号、中文字符与 JSON 数组。
    /// </summary>
    public static IReadOnlyList<PersonaTrait> ParseTraits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<PersonaTrait>();
        }

        // 1. 尝试 JSON 格式解析（部分模型倾向输出 JSON）
        var jsonTraits = TryParseJsonTraits(raw);
        if (jsonTraits is { Count: > 0 })
        {
            return jsonTraits;
        }

        var list = new List<PersonaTrait>();
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            // 跳过代码块标记与 Markdown 分隔线
            if (line.StartsWith("```") || Regex.IsMatch(line, @"^[\|\s\-:]+$"))
            {
                continue;
            }

            // 支持中文全角竖线 ｜
            line = line.Replace('｜', '|');

            // 如果是 Markdown 表格行，形如 "| 语言风格 | 特质描述 | 0.9 | 证据 |"
            if (line.StartsWith("|") && line.EndsWith("|") && line.Length > 2)
            {
                line = line[1..^1].Trim();
            }

            // 去除行首序号或无序列表标记，例如 "1. ", "- ", "* ", "• "
            line = Regex.Replace(line, @"^(\d+[\.、\)]|[-*•])\s*", "");

            string[] seg = line.Split('|', 4);
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
            if (seg.Length >= 3)
            {
                string c = seg[2].Trim();
                foreach (string prefix in new[] { "置信度:", "置信度：", "confidence:", "置信度" })
                {
                    if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        c = c[prefix.Length..].Trim();
                        break;
                    }
                }

                // 支持百分比 "90%" -> 0.9
                if (c.EndsWith("%") && double.TryParse(c[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    confidence = Math.Clamp(pct / 100.0, 0d, 1d);
                }
                else if (double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    confidence = Math.Clamp(v, 0d, 1d);
                }
            }

            var evidence = new List<string>();
            if (seg.Length >= 4)
            {
                string e = seg[3].Trim();
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

            list.Add(new PersonaTrait(dimension, attribute, confidence, evidence));
        }

        return list;
    }

    private static string MatchKnownDimension(string dim)
    {
        foreach (string known in Dimensions)
        {
            if (dim.Contains(known))
            {
                return known;
            }
        }

        return dim;
    }

    private static List<PersonaTrait>? TryParseJsonTraits(string raw)
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
                        double conf = GetPropDouble(item, 0.7, "confidence", "置信度", "conf");
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
                            list.Add(new PersonaTrait(MatchKnownDimension(dim), attr, conf, evList));
                        }
                    }

                    if (list.Count > 0)
                    {
                        return list;
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
                    return Math.Clamp(d, 0d, 1d);
                }

                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    return Math.Clamp(parsed, 0d, 1d);
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// 详细蒸馏方法：返回 <see cref="DistillResult"/>，包含成功与否及精确的错误诊断原因。
    /// 策略：优先选取最具代表性的最近 80 条历史进行单次全局蒸馏（3~5秒完成）；若解析为空则执行分块 Map-Reduce。
    /// </summary>
    public static async Task<DistillResult> DistillDetailedAsync(
        IAiProvider provider,
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing = null,
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
            AiReply map = await provider.CompleteAsync(BuildMapRequest(settings, chunk), cancellationToken);
            if (map.Success)
            {
                lastMapReply = map.Text;
                observations.AddRange(ParseTraits(map.Text));
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

        AiReply reduce = await provider.CompleteAsync(
            BuildReduceRequest(settings, contactName, observations, existing), cancellationToken);

        IReadOnlyList<PersonaTrait> merged = reduce.Success ? ParseTraits(reduce.Text) : Array.Empty<PersonaTrait>();
        if (merged.Count == 0)
        {
            merged = observations;
        }

        if (merged.Count > 0)
        {
            return new DistillResult(true, new Persona(contactName, merged, DateTime.Now, history.Count), null);
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
        var res = await DistillDetailedAsync(provider, settings, contactName, history, existing, cancellationToken);
        return res.Persona;
    }

    /// <summary>历史转写为 "[对方] xxx / [我] xxx" 多行文本（带可选时间戳前缀）。</summary>
    private static string Transcript(IReadOnlyList<HistoryMessage> history)
    {
        var sb = new StringBuilder();
        foreach (HistoryMessage m in history)
        {
            string who = m.Role switch
            {
                MessageRole.Incoming => "对方",
                MessageRole.Outgoing => "我",
                _ => "未知"
            };
            string ts = m.Timestamp is null ? string.Empty : m.Timestamp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ";
            sb.AppendLine($"[{who}] {ts}{m.Text.Replace("\n", " ")}");
        }

        return sb.ToString();
    }
}
