using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

/// <summary>M5 人格蒸馏器测试：分块/map-reduce 提示词/特质行解析/编排。</summary>
public class PersonaDistillerTests
{
    private static HistoryMessage H(MessageRole r, string t) => new(r, t);

    [Fact]
    public void Chunk_SplitsBySize()
    {
        var history = Enumerable.Range(0, 5).Select(i => H(MessageRole.Incoming, "m" + i)).ToList();

        var chunks = PersonaDistiller.Chunk(history, chunkSize: 2);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(2, chunks[0].Count);
        Assert.Single(chunks[2]);
    }

    [Fact]
    public void Chunk_EmptyHistory_ReturnsEmpty()
    {
        Assert.Empty(PersonaDistiller.Chunk(Array.Empty<HistoryMessage>()));
    }

    [Fact]
    public void ParseTraits_WellFormedLine()
    {
        var list = PersonaDistiller.ParseTraits(
            "语言风格 | 爱用表情包和短句 | 置信度: 0.8 | 证据: 哈哈;;[捂脸]");

        Assert.Single(list);
        Assert.Equal("语言风格", list[0].Dimension);
        Assert.Equal("爱用表情包和短句", list[0].Attribute);
        Assert.Equal(0.8, list[0].Confidence);
        Assert.Equal(2, list[0].Evidence.Count);
        Assert.Contains("0.80", list[0].ConfidenceText);
        Assert.Contains("“哈哈”", list[0].EvidenceText);
    }

    [Fact]
    public void ParseTraits_ClampsConfidence_AndDefaults()
    {
        var clamped = PersonaDistiller.ParseTraits("情绪模式 | 易焦虑 | 置信度: 9 | 证据: x");
        var defaulted = PersonaDistiller.ParseTraits("情绪模式 | 易焦虑");

        Assert.Equal(1.0, clamped[0].Confidence);
        Assert.Equal(0.5, defaulted[0].Confidence);
        Assert.Empty(defaulted[0].Evidence);
    }

    [Fact]
    public void ParseTraits_SkipsMalformedLines()
    {
        var list = PersonaDistiller.ParseTraits("单段无分隔\n| 空维度 | 置信度: 0.5\n稳定属性 |  | 置信度: 0.5\n稳定属性 | 有效行 | 置信度: 0.6");

        Assert.Single(list);
        Assert.Equal("有效行", list[0].Attribute);
    }

    [Fact]
    public void BuildMapRequest_ContainsDimensionsAndFormat()
    {
        var req = PersonaDistiller.BuildMapRequest(new AiSettings(), new[] { H(MessageRole.Incoming, "在吗") });

        foreach (string dim in DistillSkillPresets.Nuwa.Dimensions)
        {
            Assert.Contains(dim, req.SystemPrompt);
        }

        Assert.Contains("[对方] 在吗", req.UserPrompt);
        Assert.Contains("证据:", req.SystemPrompt);
    }

    [Fact]
    public void BuildReduceRequest_IncludesObservationsAndExisting()
    {
        var obs = new[] { new PersonaTrait("语言风格", "短句", 0.7, new[] { "嗯" }) };
        var existing = new Persona("张三", new[] { new PersonaTrait("稳定属性", "夜班", 0.9, new[] { "又熬夜" }) }, DateTime.Now, 10);

        var req = PersonaDistiller.BuildReduceRequest(new AiSettings(), "张三", obs, existing);

        Assert.Contains("语言风格 | 短句 | 可信度: 0.70 | 证据: 嗯", req.UserPrompt);
        Assert.Contains("稳定属性 | 夜班 | 可信度: 0.90", req.UserPrompt);
        Assert.Contains("已有画像基线", req.UserPrompt);
    }

    [Fact]
    public async Task DistillAsync_MapThenReduce_ReduceWins()
    {
        var provider = new StubProvider(
            "性格心理 | 观察项 | 置信度: 0.5 | 证据: a",                 // map
            "性格心理 | 合并后特质 | 置信度: 0.9 | 证据: a;;b");          // reduce

        var history = new[] { H(MessageRole.Incoming, "在吗") };
        Persona persona = await PersonaDistiller.DistillAsync(provider, new AiSettings(), "张三", history);

        Assert.Equal("张三", persona.ContactName);
        Assert.Single(persona.Traits);
        Assert.Equal("合并后特质", persona.Traits[0].Attribute);
        Assert.Equal(1, persona.SourceMessageCount);
    }

    [Fact]
    public async Task DistillAsync_ReduceFails_FallsBackToObservations()
    {
        var provider = new StubProvider(
            "情绪模式 | 观察项 | 置信度: 0.6 | 证据: a",
            "模型乱答没有格式");  // reduce 解析为空 → 回退观察项

        var history = new[] { H(MessageRole.Incoming, "x") };
        Persona persona = await PersonaDistiller.DistillAsync(provider, new AiSettings(), "李四", history);

        Assert.Single(persona.Traits);
        Assert.Equal("观察项", persona.Traits[0].Attribute);
    }

    [Fact]
    public void ParseTraits_MarkdownTable_ParsesCorrectly()
    {
        string table = """
            | 维度 | 特质描述 | 置信度 | 证据 |
            | :--- | :--- | :--- | :--- |
            | 沟通偏好 | 喜欢发语音短句与表情包 | 0.92 | "好的马上来";;[微笑] |
            | 情绪模式 | 情绪稳定，语气温和 | 85% | 好的没问题 |
            """;

        var traits = PersonaDistiller.ParseTraits(table);

        Assert.Equal(2, traits.Count);
        Assert.Equal("沟通偏好", traits[0].Dimension);
        Assert.Equal("喜欢发语音短句与表情包", traits[0].Attribute);
        Assert.Equal(0.92, traits[0].Confidence);
        Assert.Equal(2, traits[0].Evidence.Count);

        Assert.Equal("情绪模式", traits[1].Dimension);
        Assert.Equal("情绪稳定，语气温和", traits[1].Attribute);
        Assert.Equal(0.85, traits[1].Confidence);
    }

    [Fact]
    public void ParseTraits_NumberedList_And_JsonFallback_ParseCorrectly()
    {
        string list = """
            1. 互动动力 | 倾向于主动发起话题 | 置信度: 0.88 | 证据: 今天天气不错
            - 价值取向 | 看重效率与及时回复 | 0.75 | 麻烦快一点
            """;

        var listTraits = PersonaDistiller.ParseTraits(list);
        Assert.Equal(2, listTraits.Count);
        Assert.Equal("互动动力", listTraits[0].Dimension);
        Assert.Equal("倾向于主动发起话题", listTraits[0].Attribute);

        string json = """
            以下是为你生成的画像：
            [
              {"dimension": "社交关系", "attribute": "礼貌客气，保持边界感", "confidence": 0.8, "evidence": ["谢谢您", "麻烦了"]}
            ]
            """;

        var jsonTraits = PersonaDistiller.ParseTraits(json);
        Assert.Single(jsonTraits);
        Assert.Equal("社交关系", jsonTraits[0].Dimension);
        Assert.Equal("礼貌客气，保持边界感", jsonTraits[0].Attribute);
        Assert.Equal(2, jsonTraits[0].Evidence.Count);
    }

    [Fact]
    public void ParseTraits_WithExplicitScore_ParsesScore()
    {
        string text = "沟通风格 | 简洁明了 | 评分: 88 | 置信度: 0.9 | 证据: 收到";
        var traits = PersonaDistiller.ParseTraits(text);

        Assert.Single(traits);
        Assert.Equal(88, traits[0].Score);
        Assert.Equal(0.9, traits[0].Confidence);
        Assert.Equal("沟通风格", traits[0].Dimension);
    }

    [Fact]
    public void PersonaTrait_Score_DerivesDynamicallyFromConfidence_WhenUnset()
    {
        var traitLow = new PersonaTrait("沟通风格", "内敛", 0.4, new[] { "嗯" });
        var traitHigh = new PersonaTrait("沟通风格", "外向", 0.92, new[] { "哈哈" });
        var traitExplicit = new PersonaTrait("沟通风格", "热情", 0.5, new[] { "好呀" }, Score: 85);

        Assert.Equal(40, traitLow.Score);
        Assert.Equal(92, traitHigh.Score);
        Assert.Equal(85, traitExplicit.Score);
    }

    [Fact]
    public void DeduplicateByDimension_MergesSameDimensionAndAggregatesEvidence()
    {
        var t1 = new PersonaTrait("沟通风格", "简短利落", 0.7, new[] { "好的" }, Score: 70);
        var t2 = new PersonaTrait("沟通风格", "喜欢用感叹号与短句", 0.9, new[] { "好嘞！" }, Score: 88);

        var deduplicated = PersonaDistiller.DeduplicateByDimension(new[] { t1, t2 });

        Assert.Single(deduplicated);
        Assert.Equal("沟通风格", deduplicated[0].Dimension);
        Assert.Equal("喜欢用感叹号与短句", deduplicated[0].Attribute);
        Assert.Equal(0.9, deduplicated[0].Confidence);
        Assert.Equal(88, deduplicated[0].Score);
        Assert.Equal(2, deduplicated[0].Evidence.Count);
    }

    [Fact]
    public void FilterEvidence_RemovesUserOutgoingQuotes()
    {
        var history = new[]
        {
            new HistoryMessage(MessageRole.Outgoing, "下午3点一起喝杯咖啡呀"),
            new HistoryMessage(MessageRole.Incoming, "好的没问题，准时到")
        };

        var rawEvidence = new[] { "下午3点一起喝杯咖啡呀", "好的没问题，准时到" };
        var filtered = PersonaDistiller.FilterEvidence(rawEvidence, history);

        // 己方发言应被自动剔除，只保留对方亲口发言
        Assert.Single(filtered);
        Assert.Equal("好的没问题，准时到", filtered[0]);
    }

    [Fact]
    public void NuwaTag_CategorizesCognitiveLayersCorrectly()
    {
        var dna = new PersonaTrait("沟通风格", "【表达DNA】短句口语化，高频反问", 0.9, new[] { "为什么不呢？" });
        var mental = new PersonaTrait("性格能量", "【心智模型】「第一性原理」：凡事先算极限，抗拒流程冗余", 0.95, new[] { "先算物理极限" });
        var heuristic = new PersonaTrait("决策模式", "【决策启发式】「先控风险再谈收益」", 0.88, new[] { "风险点在哪" });
        var boundary = new PersonaTrait("情绪阈值", "【诚实边界】遇到未知保持适度怀疑，不轻易承诺", 0.82, new[] { "这个我还不确定" });
        var antiPattern = new PersonaTrait("隐形雷区", "【反模式】极度反感空洞承诺与推诿借口", 0.91, new[] { "别扯借口" });
        var anchor = new PersonaTrait("价值锚点", "极度追求确定性与闭环反馈", 0.9, new[] { "必须今天定下来" });

        Assert.Equal("🧬 表达DNA", dna.NuwaTag);
        Assert.Equal("🧠 心智模型", mental.NuwaTag);
        Assert.Equal("⚡ 决策启发式", heuristic.NuwaTag);
        Assert.Equal("🛡️ 诚实边界", boundary.NuwaTag);
        Assert.Equal("⚠️ 反模式·雷区", antiPattern.NuwaTag);
        Assert.Equal("🎯 价值锚点", anchor.NuwaTag);
    }

    [Fact]
    public void NormalizeDimension_NormalizesNuwaCognitiveDimensions()
    {
        Assert.Equal("沟通风格", PersonaDistiller.NormalizeDimension("表达DNA"));
        Assert.Equal("沟通风格", PersonaDistiller.NormalizeDimension("沟通风格(表达DNA)"));
        Assert.Equal("性格能量", PersonaDistiller.NormalizeDimension("心智模型"));
        Assert.Equal("决策模式", PersonaDistiller.NormalizeDimension("决策启发式"));
        Assert.Equal("情绪阈值", PersonaDistiller.NormalizeDimension("诚实边界"));
        Assert.Equal("隐形雷区", PersonaDistiller.NormalizeDimension("反模式"));
        Assert.Equal("价值锚点", PersonaDistiller.NormalizeDimension("价值底线"));
    }

    [Fact]
    public void BuildDirectDistillRequest_IncludesNuwaMethodologyGuidelines()
    {
        var req = PersonaDistiller.BuildDirectDistillRequest(new AiSettings(), "乔布斯", new[]
        {
            new HistoryMessage(MessageRole.Incoming, "Stay hungry, stay foolish.")
        });

        Assert.Contains("女娲", req.SystemPrompt);
        Assert.Contains("心智模型", req.SystemPrompt);
        Assert.Contains("决策启发式", req.SystemPrompt);
        Assert.Contains("表达DNA", req.SystemPrompt);
        Assert.Contains("三重验证", req.SystemPrompt);
        Assert.Contains("乔布斯", req.UserPrompt);
    }

    [Fact]
    public void DeduplicateSubstrings_RemovesShorterSubstringsAndPreservesDistinct()
    {
        var rawQuotes = new[]
        {
            "到时间问我姐我妹她们",
            "到时间问我姐我妹她们看看她们去不去",
            "另外一个完全不相关的长句子",
            "另外一个完全不相关"
        };

        var deduped = PersonaDistiller.DeduplicateSubstrings(rawQuotes);

        Assert.Equal(2, deduped.Count);
        Assert.Contains("到时间问我姐我妹她们看看她们去不去", deduped);
        Assert.Contains("另外一个完全不相关的长句子", deduped);
        Assert.DoesNotContain("到时间问我姐我妹她们", deduped);
        Assert.DoesNotContain("另外一个完全不相关", deduped);
    }

    [Fact]
    public void FilterEvidence_FiltersSubstringsAndCapsAt4()
    {
        var history = new[]
        {
            new HistoryMessage(MessageRole.Incoming, "到时间问我姐我妹她们看看她们去不去"),
            new HistoryMessage(MessageRole.Incoming, "第二句有效证据话语"),
            new HistoryMessage(MessageRole.Incoming, "第三句有效证据话语"),
            new HistoryMessage(MessageRole.Incoming, "第四句有效证据话语"),
            new HistoryMessage(MessageRole.Incoming, "第五句有效证据话语")
        };

        var raw = new[]
        {
            "到时间问我姐我妹她们", // 短子串
            "到时间问我姐我妹她们看看她们去不去", // 长原句
            "第二句有效证据话语",
            "第三句有效证据话语",
            "第四句有效证据话语",
            "第五句有效证据话语"
        };

        var filtered = PersonaDistiller.FilterEvidence(raw, history);

        // 验证短句已被去重，且数量被限制在最多 4 条最具代表性的证据
        Assert.True(filtered.Count <= 4);
        Assert.Contains("到时间问我姐我妹她们看看她们去不去", filtered);
        Assert.DoesNotContain("到时间问我姐我妹她们", filtered);
    }

    [Fact]
    public void BuildDirectDistillRequest_SupportsCustomSkill_AndFreshDistill()
    {
        var customSkill = new DistillSkill("workplace", "职场商业", "关注权责分工", "💼", "你是麦肯锡职场专家系统提示词", false);
        var existing = new Persona("老李", new[]
        {
            new PersonaTrait("沟通风格", "讲求闭环", 0.9, new[] { "老旧的原话证据不要出现" })
        }, DateTime.Now, 10);

        var history = new[] { new HistoryMessage(MessageRole.Incoming, "今天下班前同步进展") };

        // 1. Fresh distill = true: 验证不包含旧原话证据，且系统提示词使用 customSkill
        var reqFresh = PersonaDistiller.BuildDirectDistillRequest(
            new AiSettings(), "老李", history, existing, "我本人", customSkill, freshDistill: true);

        Assert.Equal("你是麦肯锡职场专家系统提示词", reqFresh.SystemPrompt);
        Assert.Contains("【职场商业】", reqFresh.UserPrompt);
        Assert.Contains("沟通风格 | 讲求闭环", reqFresh.UserPrompt);
        Assert.DoesNotContain("老旧的原话证据不要出现", reqFresh.UserPrompt);

        // 2. Fresh distill = false: 验证旧原话被完整保留作为格式行
        var reqIncremental = PersonaDistiller.BuildDirectDistillRequest(
            new AiSettings(), "老李", history, existing, "我本人", customSkill, freshDistill: false);

        Assert.Contains("老旧的原话证据不要出现", reqIncremental.UserPrompt);
    }

    [Fact]
    public void AiSettings_TimeoutSeconds_DefaultsTo180()
    {
        var settings = new AiSettings();
        Assert.Equal(180, settings.TimeoutSeconds);

        var custom = new AiSettings { TimeoutSeconds = 300 };
        Assert.Equal(300, custom.TimeoutSeconds);
    }

    /// <summary>按队列返回固定文本的测试用 Provider。</summary>
    private sealed class StubProvider : IAiProvider
    {
        private readonly Queue<string> _replies;

        public StubProvider(params string[] replies) => _replies = new Queue<string>(replies);

        public string Name => "stub";

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiReply(true, _replies.Count > 0 ? _replies.Dequeue() : string.Empty));
    }
}
