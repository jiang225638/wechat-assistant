using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

public class PromptBuilderTests
{
    private static ChatMessage M(MessageRole r, string t) => new(r, t);

    [Fact]
    public void BuildTranscript_FormatsRoles()
    {
        var transcript = PromptBuilder.BuildTranscript(new[]
        {
            M(MessageRole.Incoming, "你好"),
            M(MessageRole.Outgoing, "在的"),
        });

        Assert.Contains("[对方] 你好", transcript);
        Assert.Contains("[我] 在的", transcript);
    }

    [Fact]
    public void BuildTranscript_NewlinesFlattened()
    {
        var transcript = PromptBuilder.BuildTranscript(new[] { M(MessageRole.Incoming, "a\nb") });
        Assert.Contains("[对方] a b", transcript);
        Assert.DoesNotContain("\nb", transcript);
    }

    [Fact]
    public void BuildTranscript_RespectsMaxMessages()
    {
        var msgs = Enumerable.Range(0, 10).Select(i => M(MessageRole.Incoming, "m" + i)).ToList();
        var transcript = PromptBuilder.BuildTranscript(msgs, maxMessages: 3);

        Assert.DoesNotContain("m0", transcript);
        Assert.Contains("m7", transcript);
        Assert.Contains("m9", transcript);
    }

    [Fact]
    public void BuildTranscript_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PromptBuilder.BuildTranscript(Array.Empty<ChatMessage>()));
    }

    [Fact]
    public void BuildReplySuggestions_UsesSettingsModelAndTemp()
    {
        var settings = new AiSettings { Model = "deepseek-chat", Temperature = 0.3 };
        var req = PromptBuilder.BuildReplySuggestions(settings, new[] { M(MessageRole.Incoming, "在吗") }, count: 3);

        Assert.Equal("deepseek-chat", req.Model);
        Assert.Equal(0.3, req.Temperature);
        Assert.Contains("[对方] 在吗", req.UserPrompt);
        Assert.NotEmpty(req.SystemPrompt);
    }

    [Fact]
    public void BuildReplySuggestions_CountAndTonesInPrompt()
    {
        var settings = new AiSettings();
        var msgs = new[] { M(MessageRole.Incoming, "在吗") };

        var req3 = PromptBuilder.BuildReplySuggestions(settings, msgs, count: 3);
        var req5 = PromptBuilder.BuildReplySuggestions(settings, msgs, count: 5);

        Assert.Contains("3 条", req3.SystemPrompt);
        Assert.Contains("5 条", req5.SystemPrompt);
        foreach (string tone in PromptBuilder.ReplyTones)
        {
            Assert.Contains(tone, req3.SystemPrompt);
        }

        // 输出格式约定：与 AiOutputParser 的行格式一致
        Assert.Contains("[语气]", req3.SystemPrompt);
        Assert.Contains("理由：", req3.SystemPrompt);
    }

    [Fact]
    public void BuildReplySuggestions_CountClampedToToneCount()
    {
        var req = PromptBuilder.BuildReplySuggestions(new AiSettings(), new[] { M(MessageRole.Incoming, "x") }, count: 99);
        Assert.Contains(PromptBuilder.ReplyTones.Length + " 条", req.SystemPrompt);
    }

    [Fact]
    public void BuildSubtextAnalysis_SixLabelFormat()
    {
        var req = PromptBuilder.BuildSubtextAnalysis(new AiSettings(), new[] { M(MessageRole.Incoming, "哦") });

        Assert.Contains("字面意思：", req.SystemPrompt);
        Assert.Contains("潜台词：", req.SystemPrompt);
        Assert.Contains("情绪状态：", req.SystemPrompt);
        Assert.Contains("真实意图：", req.SystemPrompt);
        Assert.Contains("想要的回应：", req.SystemPrompt);
        Assert.Contains("建议策略：", req.SystemPrompt);
    }

    [Fact]
    public void BuildReplySuggestions_WithPersona_IncludesPersonaContext()
    {
        var settings = new AiSettings();
        var msgs = new[] { M(MessageRole.Incoming, "合同发过来了") };
        var persona = new Persona("张三", new[]
        {
            new PersonaTrait("沟通风格", "喜欢直入主题", 0.9, new[] { "直接发我" })
        }, DateTime.Now, 10);

        var req = PromptBuilder.BuildReplySuggestions(settings, msgs, count: 3, persona: persona);

        Assert.Contains("对方画像参考：", req.UserPrompt);
        Assert.Contains("喜欢直入主题", req.UserPrompt);
    }

    [Fact]
    public void BuildSubtextAnalysis_WithPersona_IncludesPersonaContext()
    {
        var settings = new AiSettings();
        var msgs = new[] { M(MessageRole.Incoming, "好的") };
        var persona = new Persona("李四", new[]
        {
            new PersonaTrait("性格心理", "谨慎克制", 0.85, new[] { "我再想想" })
        }, DateTime.Now, 15);

        var req = PromptBuilder.BuildSubtextAnalysis(settings, msgs, persona: persona);

        Assert.Contains("对方已知画像参考：", req.UserPrompt);
        Assert.Contains("谨慎克制", req.UserPrompt);
    }

    [Fact]
    public void BuildUnifiedAdvice_IncludesPersonaAndTargetQuote()
    {
        var settings = new AiSettings { Model = "deepseek-chat", Temperature = 0.4 };
        var msgs = new[]
        {
            M(MessageRole.Incoming, "你觉得这个方案怎么样？"),
            M(MessageRole.Outgoing, "我正在看"),
            M(MessageRole.Incoming, "行吧，尽快给我答复。")
        };
        var persona = new Persona("王五", new[]
        {
            new PersonaTrait("沟通风格", "雷厉风行，追求效率", 0.92, new[] { "尽快" })
        }, DateTime.Now, 20);

        var req = PromptBuilder.BuildUnifiedAdvice(settings, msgs, count: 4, persona: persona, contactName: "王五");

        Assert.Equal("deepseek-chat", req.Model);
        Assert.Equal(0.4, req.Temperature);
        Assert.Contains("=== 意图剖析 ===", req.SystemPrompt);
        Assert.Contains("=== 回复建议 ===", req.SystemPrompt);
        Assert.Contains("当前对话对象：王五", req.UserPrompt);
        Assert.Contains("雷厉风行，追求效率", req.UserPrompt);
        Assert.Contains("92分", req.UserPrompt);
        Assert.Contains("行吧，尽快给我答复。", req.UserPrompt);
        Assert.Contains("4 条", req.UserPrompt);
    }
}

