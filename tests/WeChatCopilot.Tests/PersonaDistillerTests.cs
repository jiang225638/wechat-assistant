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

        foreach (string dim in PersonaDistiller.Dimensions)
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

        Assert.Contains("语言风格 | 短句 | 置信度: 0.70 | 证据: 嗯", req.UserPrompt);
        Assert.Contains("稳定属性 | 夜班 | 置信度: 0.90 | 证据: 又熬夜", req.UserPrompt);
        Assert.Contains("已有画像", req.UserPrompt);
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
