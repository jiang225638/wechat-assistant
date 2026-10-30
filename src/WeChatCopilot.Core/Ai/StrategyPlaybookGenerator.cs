using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

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
            "1. 只输出一个 JSON 对象，不要任何开场白、解释或 Markdown 代码围栏。\n" +
            "2. JSON 结构必须为：{\"mindset\":{\"title\":\"\",\"content\":\"\"},\"leverage\":{\"title\":\"\",\"content\":\"\"}," +
            "\"taboos\":{\"title\":\"\",\"content\":\"\"},\"scripts\":{\"title\":\"\",\"content\":\"\"}}。content 为纯文本，多条要点用 \\n 分隔，每条以“• ”或“1. ”开头。\n" +
            "3. mindset=核心心智与诉求底色；leverage=可顺势借力的抓手（2~3 条，要具体可执行）；" +
            "taboos=沟通雷区与避坑红线（2~4 条，说明为什么）；scripts=2 条可直接发送的示范话术，每条附一句“心法点拨”。\n" +
            "4. 结论必须锚定画像证据，引用维度名与对方原话；话术要贴合最近对话的语境与对方的说话方式，不得套用万能模板。\n" +
            "5. 可信度低（<0.5）、或特质描述写“无法判断/信息不足/暂无”的维度，只代表证据不足，" +
            "严禁当作对方的性格缺陷、防御或脆弱点；应明确指出“信息不足”，并建议如何自然地补充观察。\n" +
            "6. 不要夸大分数含义：分数中等或偏低不等于“极强”，措辞要与分数、可信度相称。\n" +
            "7. 不要输出任何恭维、鸡汤或空泛套话；title 需含具体维度名，简洁（20 字以内，可带一个 emoji）。\n" +
            "8. 用中文回答。";

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
                ? string.Join(";;", t.Evidence.Take(3).Select(e => Truncate(e, 80)))
                : "无";
            sb.AppendLine($"- {t.Dimension} | {t.Attribute} | {t.ScoreInt}分 | 可信度 {t.ConfidenceText} | 证据: {evidence}");
        }

        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【最近对话（[对方]=联系人，[我]=用户本人）】");
            foreach (var m in recent)
            {
                string who = m.Role == MessageRole.Incoming ? "[对方]" : "[我]";
                sb.AppendLine($"{who} {Truncate(m.Text.Replace('\n', ' '), 120)}");
            }
        }

        return new AiRequest(system, sb.ToString(), settings.Model, 0.6, 3000);
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
            return (null, "大模型未按约定的 JSON 格式输出策略手册。");
        }

        return (playbook with { Model = reply.Model ?? settings.Model, GeneratedAt = DateTime.Now }, null);
    }

    /// <summary>容错解析：兼容代码围栏、前后多余文字、content 为数组等情况。四张卡片缺一即视为失败。</summary>
    public static StrategyPlaybook? Parse(string? raw)
    {
        string cleanedRaw = AiOutputParser.StripThinkingBlocks(raw);
        if (string.IsNullOrWhiteSpace(cleanedRaw)) return null;

        int start = cleanedRaw.IndexOf('{');
        int end = cleanedRaw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(cleanedRaw[start..(end + 1)]);
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
