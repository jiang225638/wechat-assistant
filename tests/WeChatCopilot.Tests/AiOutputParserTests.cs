using WeChatCopilot.Core.Ai;

namespace WeChatCopilot.Tests;

/// <summary>M4 AI 输出解析器测试：回复建议卡片解析 + 潜台词六维面板解析。</summary>
public class AiOutputParserTests
{
    [Fact]
    public void ParseReplySuggestions_WellFormedLines()
    {
        string raw =
            "[共情] 听起来今天真累，先歇会儿 | 理由：先接住对方情绪\n" +
            "[专业] 建议明天上午对齐一下进度 | 理由：给出可执行动作\n" +
            "[幽默] 你这节奏比周一还周一 | 理由：用玩笑缓和气氛";

        var list = AiOutputParser.ParseReplySuggestions(raw);

        Assert.Equal(3, list.Count);
        Assert.Equal("共情", list[0].Tone);
        Assert.Equal("听起来今天真累，先歇会儿", list[0].Text);
        Assert.Equal("先接住对方情绪", list[0].Reason);
        Assert.Equal("专业", list[1].Tone);
        Assert.Equal("用玩笑缓和气氛", list[2].Reason);
        Assert.Contains("理由：", list[0].ReasonLine);
    }

    [Fact]
    public void ParseReplySuggestions_StripsNumberPrefix()
    {
        var list = AiOutputParser.ParseReplySuggestions("1. [直接] 行，就这么定 | 理由：快速收敛");

        Assert.Single(list);
        Assert.Equal("直接", list[0].Tone);
        Assert.Equal("行，就这么定", list[0].Text);
    }

    [Fact]
    public void ParseReplySuggestions_FullWidthPipeAndColon()
    {
        var list = AiOutputParser.ParseReplySuggestions("[缓和] 别急，慢慢来｜理由：降低对抗感");

        Assert.Single(list);
        Assert.Equal("别急，慢慢来", list[0].Text);
        Assert.Equal("降低对抗感", list[0].Reason);
    }

    [Fact]
    public void ParseReplySuggestions_MalformedLineFallsBackToCandidate()
    {
        var list = AiOutputParser.ParseReplySuggestions("随便一行没有格式的回答");

        Assert.Single(list);
        Assert.Equal("候选", list[0].Tone);
        Assert.Equal("随便一行没有格式的回答", list[0].Text);
        Assert.Equal(string.Empty, list[0].Reason);
        Assert.Equal(string.Empty, list[0].ReasonLine);
    }

    [Fact]
    public void ParseReplySuggestions_ToneWithoutReason()
    {
        var list = AiOutputParser.ParseReplySuggestions("[幽默] 哈哈我也觉得");

        Assert.Single(list);
        Assert.Equal("幽默", list[0].Tone);
        Assert.Equal("哈哈我也觉得", list[0].Text);
        Assert.Equal(string.Empty, list[0].Reason);
    }

    [Fact]
    public void ParseReplySuggestions_NullOrBlank_ReturnsEmpty()
    {
        Assert.Empty(AiOutputParser.ParseReplySuggestions(null));
        Assert.Empty(AiOutputParser.ParseReplySuggestions("   \n  "));
    }

    [Fact]
    public void ParseSubtext_AllLabels()
    {
        string raw =
            "字面意思：说自己在忙\n" +
            "潜台词：希望你主动关心而不是追问\n" +
            "情绪状态：疲惫、略烦躁\n" +
            "真实意图：获得理解与空间\n" +
            "想要的回应：共情式简短回应\n" +
            "建议策略：先共情再约时间";

        var a = AiOutputParser.ParseSubtext(raw);

        Assert.Equal("说自己在忙", a.Literal);
        Assert.Equal("希望你主动关心而不是追问", a.Subtext);
        Assert.Equal("疲惫、略烦躁", a.Emotion);
        Assert.Equal("获得理解与空间", a.Intent);
        Assert.Equal("共情式简短回应", a.DesiredResponse);
        Assert.Equal("先共情再约时间", a.Strategy);
        Assert.Contains("字面意思：说自己在忙", a.Format());
    }

    [Fact]
    public void ParseSubtext_MissingLabels_FormatMarksUnparsed()
    {
        var a = AiOutputParser.ParseSubtext("潜台词：有点不高兴");

        Assert.Equal("有点不高兴", a.Subtext);
        Assert.Equal(string.Empty, a.Literal);
        Assert.Contains("（未解析）", a.Format());
        Assert.DoesNotContain("潜台词：（未解析）", a.Format());
    }

    [Fact]
    public void ParseSubtext_ShortAliasLabels()
    {
        var a = AiOutputParser.ParseSubtext("情绪：低落\n意图：想被安慰\n策略：温和回应");

        Assert.Equal("低落", a.Emotion);
        Assert.Equal("想被安慰", a.Intent);
        Assert.Equal("温和回应", a.Strategy);
    }

    [Fact]
    public void ParseSubtext_Null_ReturnsAllEmpty()
    {
        var a = AiOutputParser.ParseSubtext(null);

        Assert.Equal(string.Empty, a.Literal);
        Assert.Equal(string.Empty, a.Strategy);
    }

    [Fact]
    public void ParseUnifiedAdvice_WellFormedOutput()
    {
        string raw =
            "=== 意图剖析 ===\n" +
            "目标原话：行吧，尽快给我答复。\n" +
            "字面意思：要求尽快给出方案结果。\n" +
            "潜台词洞察：对拖延零容忍，目前正在等待你的承诺而非借口。\n" +
            "真实心理：希望掌控推进节点，追求高效闭环。\n" +
            "应对策略：给出明确的时间承诺，不要解释过多客观理由。\n" +
            "\n" +
            "=== 回复建议 ===\n" +
            "[高情商] 收到！我正在敲定最后两项核心细节，今天下午16点前准时发送给您。 | 理由：给出精准确定时刻，打消焦虑\n" +
            "[直接] 明白，方案已进入汇总收尾，下午下班前给到。 | 理由：干脆利落不拖泥带水\n" +
            "[专业] 已梳理到第4阶段，预计16:00整提交最终定稿。 | 理由：体现流程把控力";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw, fallbackTargetQuote: "兜底原话");

        Assert.Equal("行吧，尽快给我答复。", advice.TargetQuote);
        Assert.Equal("要求尽快给出方案结果。", advice.Literal);
        Assert.Equal("对拖延零容忍，目前正在等待你的承诺而非借口。", advice.Subtext);
        Assert.Equal("希望掌控推进节点，追求高效闭环。", advice.Intent);
        Assert.Equal("给出明确的时间承诺，不要解释过多客观理由。", advice.Strategy);
        Assert.Equal(3, advice.Suggestions.Count);
        Assert.Equal("高情商", advice.Suggestions[0].Tone);
        Assert.Equal("收到！我正在敲定最后两项核心细节，今天下午16点前准时发送给您。", advice.Suggestions[0].Text);
        Assert.Equal("给出精准确定时刻，打消焦虑", advice.Suggestions[0].Reason);
        Assert.Equal("直接", advice.Suggestions[1].Tone);
        Assert.Equal("专业", advice.Suggestions[2].Tone);
    }

    [Fact]
    public void ParseUnifiedAdvice_FallbackTargetQuote_WhenMissingInRaw()
    {
        string raw =
            "潜台词洞察：想尽快结束对话\n" +
            "真实心理：感到烦躁\n" +
            "[缓和] 好的，你先忙，稍后再联系 | 理由：退一步给对方空间";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw, fallbackTargetQuote: "你先忙吧");

        Assert.Equal("你先忙吧", advice.TargetQuote);
        Assert.Equal("想尽快结束对话", advice.Subtext);
        Assert.Equal("感到烦躁", advice.Intent);
        Assert.Single(advice.Suggestions);
        Assert.Equal("缓和", advice.Suggestions[0].Tone);
    }

    [Fact]
    public void ParseUnifiedAdvice_NullOrEmpty_ReturnsFallbackQuoteAndEmptySuggestions()
    {
        var advice = AiOutputParser.ParseUnifiedAdvice(null, "默认发言");

        Assert.Equal("默认发言", advice.TargetQuote);
        Assert.Empty(advice.Literal);
        Assert.Empty(advice.Subtext);
        Assert.Empty(advice.Suggestions);
    }
}
