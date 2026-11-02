using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;
using Xunit;

namespace WeChatCopilot.Tests;

public class StrategyPlaybookGeneratorTests
{
    private const string ValidJson =
        "{\"mindset\":{\"title\":\"🎯 A\",\"content\":\"• x\"},\"leverage\":{\"title\":\"✨ B\",\"content\":[\"• y1\",\"• y2\"]}," +
        "\"taboos\":{\"title\":\"🚫 C\",\"content\":\"• z\"},\"scripts\":{\"title\":\"💬 D\",\"content\":\"• w\"}}";

    private static Persona MakePersona() => new(
        "张琼",
        new[] { new PersonaTrait("礼貌回应", "能以简短问候回应", 0.85, new[] { "你好" }, 58) },
        new DateTime(2026, 10, 21, 14, 40, 0), 8, "intimacy", "邀约见面");

    [Fact]
    public void Parse_ValidJson_ReturnsFourSections()
    {
        var pb = StrategyPlaybookGenerator.Parse(ValidJson);

        Assert.NotNull(pb);
        Assert.Equal("🎯 A", pb!.Mindset.Title);
        Assert.Equal("• y1\n• y2", pb.Leverage.Content);
        Assert.Equal("• w", pb.Scripts.Content);
    }

    [Fact]
    public void Parse_CodeFenceAndChatter_IsTolerated()
    {
        var pb = StrategyPlaybookGenerator.Parse("好的：\n```json\n" + ValidJson + "\n```\n以上");

        Assert.NotNull(pb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"mindset\":{\"title\":\"a\",\"content\":\"b\"}}")]
    [InlineData("{\"mindset\":{\"title\":\"a\",\"content\":\"\"},\"leverage\":{\"content\":\"b\"},\"taboos\":{\"content\":\"c\"},\"scripts\":{\"content\":\"d\"}}")]
    public void Parse_InvalidOrIncomplete_ReturnsNull(string? raw)
    {
        Assert.Null(StrategyPlaybookGenerator.Parse(raw));
    }

    [Fact]
    public void BuildRequest_IncludesTraitsGoalAndRecentHistory()
    {
        var persona = MakePersona();
        var history = new[]
        {
            new HistoryMessage(MessageRole.Incoming, "你好"),
            new HistoryMessage(MessageRole.Outgoing, "在吗")
        };

        var req = StrategyPlaybookGenerator.BuildRequest(
            new AiSettings { Model = "m" }, persona, persona.Traits, DistillSkillPresets.Intimacy, "亲密交往对象", history);

        Assert.Equal("m", req.Model);
        Assert.Contains("礼貌回应", req.UserPrompt);
        Assert.Contains("邀约见面", req.UserPrompt);
        Assert.Contains("[对方] 你好", req.UserPrompt);
        Assert.Contains("[我] 在吗", req.UserPrompt);
        Assert.Contains("信息不足", req.SystemPrompt);
    }

    [Fact]
    public void ReplySuggestion_MissingScores_AreNotFabricated()
    {
        var s = new ReplySuggestion("t", "text", "r");

        Assert.False(s.HasGoalPushScore);
        Assert.False(s.HasPersonaSafetyScore);
        Assert.Equal("—", s.GoalPushText);
        Assert.Equal("—", s.PersonaSafetyText);
    }

    [Fact]
    public void ParseTraits_BareScoreColumn_And_InsufficientEvidenceScore()
    {
        var traits = PersonaDistiller.ParseTraits(
            "沟通风格 | 简洁高效 | 85 | 0.90 | 好的\n" +
            "框架与需求感 | 无法判断对方是否主导互动 | 置信度: 0.85");

        Assert.Equal(2, traits.Count);
        Assert.Equal(85, traits[0].Score);
        Assert.DoesNotContain("85", traits[0].Evidence);
        Assert.Equal(35, traits[1].Score);
    }

    [Fact]
    public void CacheKey_ChangesWhenGoalChanges_AndStableOtherwise()
    {
        var p1 = MakePersona();
        var p2 = p1 with { UltimateGoal = "别的目的" };
        var skill = DistillSkillPresets.Intimacy;

        string k1 = StrategyPlaybookGenerator.ComputeCacheKey(p1, skill, "s", null);
        string k1b = StrategyPlaybookGenerator.ComputeCacheKey(p1, skill, "s", null);
        string k2 = StrategyPlaybookGenerator.ComputeCacheKey(p2, skill, "s", null);

        Assert.Equal(k1, k1b);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void Parse_JsonWithUnescapedInnerQuotesAndRawNewlines_ParsesSuccessfully()
    {
        string malformedLlmOutput = @"```json
{
  ""mindset"": {
    ""title"": ""🎯 节奏控制权与""条件前置""底色"",
    ""content"": ""• 第一核心主导：她用""报备式收线""（如""我骑车回家 等会跟你说哈""）掌握主动权。
• 第二支柱：不用""拒绝""这个词来完成拒绝，而是改成""条件前置""。""
  },
  ""leverage"": {
    ""title"": ""✨ 顺势借力抓手"",
    ""content"": ""• 抓手1：顺应她的""报备式收线""，干脆利落收尾。
• 抓手2：以推拉回应她的""条件前置""。""
  },
  ""taboos"": {
    ""title"": ""🚫 沟通雷区与避坑红线"",
    ""content"": ""• 雷区1：严禁在她说""我得早点睡觉""后继续追问。
• 雷区2：切忌低位讨好。""
  },
  ""scripts"": {
    ""title"": ""💬 实战破局示范话术"",
    ""content"": ""• 话术1：""去吧，正好我也去忙手头的事，回头聊~""（心法点拨：镜像收线）
• 话术2：""下个月周末怕是要排号了，先给你留个VIP位""（心法点拨：幽默化解""条件前置""）""
  }
}
```";

        var pb = StrategyPlaybookGenerator.Parse(malformedLlmOutput);

        Assert.NotNull(pb);
        Assert.Contains("节奏控制权", pb!.Mindset.Title);
        Assert.Contains("报备式收线", pb.Mindset.Content);
        Assert.Contains("条件前置", pb.Leverage.Content);
        Assert.Contains("我得早点睡觉", pb.Taboos.Content);
        Assert.Contains("下个月周末怕是要排号了", pb.Scripts.Content);
    }

    [Fact]
    public void Parse_MarkdownFourSectionsFallback_ParsesSuccessfully()
    {
        string markdownOutput = @"
### 🎯 核心心智与诉求底色
• 在【节奏控制权】占据高位。

### ✨ 顺势借力抓手
• 借力抓手 1：顺应其节奏。

### 🚫 沟通雷区与避坑红线
• 避坑 1：切忌纠缠。

### 💬 示范话术与心法点拨
• 话术 1：早点休息，明儿聊。
";

        var pb = StrategyPlaybookGenerator.Parse(markdownOutput);

        Assert.NotNull(pb);
        Assert.Contains("节奏控制权", pb!.Mindset.Content);
        Assert.Contains("顺应其节奏", pb.Leverage.Content);
        Assert.Contains("切忌纠缠", pb.Taboos.Content);
        Assert.Contains("早点休息", pb.Scripts.Content);
    }
}
