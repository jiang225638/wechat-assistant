using System.Globalization;
using System.Text;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Ai;

/// <summary>
/// M5 人格蒸馏器（FR-3/FR-7）：历史分块 map → 观察特质，再 reduce 合并为结构化 persona。
/// - map：每块历史提取"维度 | 特质 | 置信度 | 证据原话"观察行；
/// - reduce：去重同义、矛盾取高置信、增量合并已有画像（不丢历史结论）；
/// - 观察层（证据原话）与推断层（特质+置信度）分离，结论锚定证据。
/// 行格式约定与 <see cref="ParseTraits"/> 一一对应。
/// </summary>
public static class PersonaDistiller
{
    /// <summary>persona 七维（FR-3）。</summary>
    public static readonly string[] Dimensions =
        { "稳定属性", "语言风格", "性格心理", "情绪模式", "意图需求", "关系动态", "边界禁忌" };

    /// <summary>map 分块大小（条/块）。</summary>
    public const int DefaultChunkSize = 40;

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
    /// 解析特质行：维度 | 特质 | 置信度: x | 证据: q;;q。
    /// 宽容处理：缺置信度默认 0.5 并钳制 0~1；缺证据为空列表；段数不足 2 的行跳过。
    /// </summary>
    public static IReadOnlyList<PersonaTrait> ParseTraits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<PersonaTrait>();
        }

        var list = new List<PersonaTrait>();
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            string[] seg = line.Split('|', 4);
            if (seg.Length < 2)
            {
                continue;
            }

            string dimension = seg[0].Trim();
            string attribute = seg[1].Trim();
            if (dimension.Length == 0 || attribute.Length == 0)
            {
                continue;
            }

            double confidence = 0.5;
            if (seg.Length >= 3)
            {
                string c = seg[2].Trim();
                foreach (string prefix in new[] { "置信度:", "置信度：", "confidence:" })
                {
                    if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        c = c[prefix.Length..].Trim();
                        break;
                    }
                }

                if (double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    confidence = Math.Clamp(v, 0d, 1d);
                }
            }

            var evidence = new List<string>();
            if (seg.Length >= 4)
            {
                string e = seg[3].Trim();
                foreach (string prefix in new[] { "证据:", "证据：", "evidence:" })
                {
                    if (e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        e = e[prefix.Length..];
                        break;
                    }
                }

                evidence.AddRange(e.Split(";;").Select(q => q.Trim()).Where(q => q.Length > 0));
            }

            list.Add(new PersonaTrait(dimension, attribute, confidence, evidence));
        }

        return list;
    }

    /// <summary>
    /// 蒸馏编排：分块 map → reduce 合并 → persona。已有画像传入则增量合并。
    /// reduce 失败时回退为观察项直出；历史与画像皆空时返回空 persona。
    /// </summary>
    public static async Task<Persona> DistillAsync(
        IAiProvider provider,
        AiSettings settings,
        string contactName,
        IReadOnlyList<HistoryMessage> history,
        Persona? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);

        var observations = new List<PersonaTrait>();
        foreach (IReadOnlyList<HistoryMessage> chunk in Chunk(history))
        {
            AiReply map = await provider.CompleteAsync(BuildMapRequest(settings, chunk), cancellationToken);
            if (map.Success)
            {
                observations.AddRange(ParseTraits(map.Text));
            }
        }

        IReadOnlyList<PersonaTrait> merged = observations;
        if (observations.Count > 0 || existing is not null)
        {
            AiReply reduce = await provider.CompleteAsync(
                BuildReduceRequest(settings, contactName, observations, existing), cancellationToken);
            if (reduce.Success)
            {
                var parsed = ParseTraits(reduce.Text);
                if (parsed.Count > 0)
                {
                    merged = parsed;
                }
            }
        }

        return new Persona(contactName, merged, DateTime.Now, history?.Count ?? 0);
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
