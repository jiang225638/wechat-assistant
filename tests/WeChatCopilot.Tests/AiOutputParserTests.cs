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
        Assert.Empty(advice.Suggestions);
    }

    [Fact]
    public void ParseUnifiedAdvice_ParsesContextFlowSummary()
    {
        string raw =
            "语境脉络：双方前期探讨周末露营话题，氛围轻松活跃，对方因临时加班产生打扰愧疚感，整体处于稳步升温期的轻度防备拉扯。\n" +
            "目标原话：“我周末可能要加班，晚点回复你哈”\n" +
            "字面意思：说明周末可能有工作安排\n" +
            "潜台词洞察：怕你误会冷落，主动打预防针\n" +
            "真实心理：希望保留自我节奏的同时不切断互动\n" +
            "应对策略：轻松托底，不追问不施压\n" +
            "[轻松托底] 没事你先忙，手头事情搞定再说 | 理由：卸下对方回复压力";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw);

        Assert.Equal("双方前期探讨周末露营话题，氛围轻松活跃，对方因临时加班产生打扰愧疚感，整体处于稳步升温期的轻度防备拉扯。", advice.ContextFlowSummary);
        Assert.Equal("我周末可能要加班，晚点回复你哈", advice.TargetQuote);
        Assert.Equal("怕你误会冷落，主动打预防针", advice.Subtext);
        Assert.Single(advice.Suggestions);
    }

    [Fact]
    public void ParseUnifiedAdvice_ParsesDynamicDimensionsAndScores()
    {
        string raw =
            "=== 动态博弈评估维度 ===\n" +
            "评估维度：商业主导权, 筹码锁定, 成单推进力, 信任赋能, 让步防守\n" +
            "=== 意图剖析 ===\n" +
            "战局态势：双方处于商务报价的关键博弈期，对方试图通过试探底线压低价格。\n" +
            "目标原话：“这个报价有点超出预算了，别家给的更优惠”\n" +
            "字面意思：表示预算不足并提及竞品价格优势。\n" +
            "潜台词洞察：并非否定合作，而是习惯性议价博弈，试探是否有谈判空间。\n" +
            "画像透视：务实谨慎型决策者，极度注重性价比与风险兜底。\n" +
            "破局战术：坚守核心价值锚点，不直接降价，提供差异化服务包作为让步置换。\n" +
            "=== 回复建议 ===\n" +
            "[价值锚定反制] 明白您的考量！咱们方案包含7*24h定制保障与首期免费升级，这些竞品通常单独计费。若预算有硬性红线，我可申请为您优化配置方案。 | 理由：亮出隐形价值打破竞品价格假象 | 得分：商业主导权=88, 筹码锁定=92, 成单推进力=85, 信任赋能=90, 让步防守=82 | 推进力=86, 安全度=90\n" +
            "[以退为进探底] 理解！如果方便的话，您重点关注的是首期成本还是长期运维？我们也有精简版方案可供选择。 | 理由：不急于降价，试探其真实预算痛点 | 得分：商业主导权=75, 筹码锁定=80, 成单推进力=70, 信任赋能=95, 让步防守=90 | 推进力=72, 安全度=94";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw);

        Assert.NotNull(advice.DynamicDimensions);
        Assert.Equal(5, advice.DynamicDimensions.Count);
        Assert.Contains("商业主导权", advice.DynamicDimensions);
        Assert.Contains("成单推进力", advice.DynamicDimensions);

        Assert.Equal(2, advice.Suggestions.Count);
        var s1 = advice.Suggestions[0];
        Assert.Equal("价值锚定反制", s1.Tone);
        Assert.Equal(86, s1.GoalPushScore);
        Assert.Equal(90, s1.PersonaSafetyScore);
        Assert.NotNull(s1.DimensionScores);
        Assert.Equal(88, s1.DimensionScores["商业主导权"]);
        Assert.Equal(92, s1.DimensionScores["筹码锁定"]);

        var s2 = advice.Suggestions[1];
        Assert.Equal(72, s2.GoalPushScore);
        Assert.Equal(94, s2.PersonaSafetyScore);
        Assert.NotNull(s2.DimensionScores);
        Assert.Equal(95, s2.DimensionScores["信任赋能"]);
    }

    [Fact]
    public void ParseUnifiedAdvice_ChineseBracketsAndBullets_ParsedSuccessfully()
    {
        string raw =
            "=== 意图剖析 ===\n" +
            "目标原话：好呀\n" +
            "字面意思：表示同意\n" +
            "\n" +
            "=== 回复建议 ===\n" +
            "1. 【幽默推拉】那我也不能落后，下周听你安排。 | 理由：接梗幽默\n" +
            "* [借势推进] 行，你负责有饭，我负责有心，分工明确。那下周哪天赏脸？ | 理由：推进见面 | 推进力=80, 安全度=85\n" +
            "3. 顺水推舟：没问题，周六见 | 理由：直接确认";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw);

        Assert.Equal(3, advice.Suggestions.Count);
        Assert.Equal("幽默推拉", advice.Suggestions[0].Tone);
        Assert.Equal("那我也不能落后，下周听你安排。", advice.Suggestions[0].Text);
        Assert.Equal("借势推进", advice.Suggestions[1].Tone);
        Assert.Equal("行，你负责有饭，我负责有心，分工明确。那下周哪天赏脸？", advice.Suggestions[1].Text);
        Assert.Equal(80, advice.Suggestions[1].GoalPushScore);
        Assert.Equal("顺水推舟", advice.Suggestions[2].Tone);
        Assert.Equal("没问题，周六见", advice.Suggestions[2].Text);
    }

    [Fact]
    public void ParseUnifiedAdvice_WithThinkBlocks_StripsReasoningDraftAndParsesCleanly()
    {
        string raw =
            "<think>\n" +
            "用户在和她聊周末安排，我先草拟一下：\n" +
            "[错误草稿] 这是一条思考中的废案 | 理由：思考草稿\n" +
            "潜台词洞察：这是思考过程中的错误判断\n" +
            "</think>\n" +
            "=== 意图剖析 ===\n" +
            "字面意思：答应周末见面\n" +
            "潜台词洞察：期待你主动安排具体地点\n" +
            "=== 回复建议 ===\n" +
            "[借势推进] 那周六下午我带你去家宝藏咖啡馆 | 理由：直接敲定行程 | 推进力=90, 安全度=88";

        var advice = AiOutputParser.ParseUnifiedAdvice(raw, "好呀");

        Assert.Equal("期待你主动安排具体地点", advice.Subtext);
        Assert.Single(advice.Suggestions);
        Assert.Equal("借势推进", advice.Suggestions[0].Tone);
        Assert.DoesNotContain(advice.Suggestions, s => s.Tone == "错误草稿");
    }
}
