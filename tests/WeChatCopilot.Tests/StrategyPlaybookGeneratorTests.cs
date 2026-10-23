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
}
