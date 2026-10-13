using Microsoft.Data.Sqlite;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.Tests;

public sealed class SqliteStorageTests : IDisposable
{
    private readonly string _testDbPath;

    public SqliteStorageTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"copilot_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp directory
        }
    }

    [Fact]
    public void InitializesSchemaSuccessfully()
    {
        using var storage = new SqliteStorage(_testDbPath);
        Assert.True(File.Exists(_testDbPath));
    }

    [Fact]
    public void SaveMessages_DeduplicatesAndRetrievesInOrder()
    {
        using var storage = new SqliteStorage(_testDbPath);
        string contact = "小敏";

        var t1 = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 10, 10, 10, 5, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 10, 10, 10, 10, 0, DateTimeKind.Utc);

        var batch1 = new[]
        {
            new HistoryMessage(MessageRole.Incoming, "你好呀", t1),
            new HistoryMessage(MessageRole.Outgoing, "哈喽，今天天气真好", t2),
        };

        int inserted1 = storage.SaveMessages(contact, batch1);
        Assert.Equal(2, inserted1);

        // 重复插入相同消息 + 1条新消息
        var batch2 = new[]
        {
            new HistoryMessage(MessageRole.Incoming, "你好呀", t1), // 重复
            new HistoryMessage(MessageRole.Outgoing, "哈喽，今天天气真好", t2), // 重复
            new HistoryMessage(MessageRole.Incoming, "周末有空去喝咖啡吗？", t3), // 新消息
        };

        int inserted2 = storage.SaveMessages(contact, batch2);
        Assert.Equal(1, inserted2); // 仅新增 1 条

        var all = storage.GetMessages(contact);
        Assert.Equal(3, all.Count);
        Assert.Equal("你好呀", all[0].Text);
        Assert.Equal(MessageRole.Incoming, all[0].Role);
        Assert.Equal(t1, all[0].Timestamp);

        Assert.Equal("哈喽，今天天气真好", all[1].Text);
        Assert.Equal(MessageRole.Outgoing, all[1].Role);

        Assert.Equal("周末有空去喝咖啡吗？", all[2].Text);
        Assert.Equal(MessageRole.Incoming, all[2].Role);
    }

    [Fact]
    public void GetLatestMessageTimestamp_ReturnsLatest()
    {
        using var storage = new SqliteStorage(_testDbPath);
        string contact = "Bob";

        Assert.Null(storage.GetLatestMessageTimestamp(contact));

        var t1 = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        storage.SaveMessages(contact, new[]
        {
            new HistoryMessage(MessageRole.Incoming, "Msg1", t1),
            new HistoryMessage(MessageRole.Outgoing, "Msg2", t2),
        });

        var latest = storage.GetLatestMessageTimestamp(contact);
        Assert.NotNull(latest);
        Assert.Equal(t2, latest.Value);
    }

    [Fact]
    public void Persona_SaveAndLoad_RoundTrip()
    {
        using var storage = new SqliteStorage(_testDbPath);
        string contact = "Alice";

        var persona = new Persona(
            ContactName: contact,
            Traits: new[]
            {
                new PersonaTrait("情感表达", "直接坦诚", 0.9, new[] { "我很喜欢你" }, 85),
                new PersonaTrait("依恋安全感", "高安全感", 0.95, new[] { "我随时都在" }, 90)
            },
            UpdatedAt: new DateTime(2026, 10, 13, 15, 30, 0, DateTimeKind.Utc),
            SourceMessageCount: 42,
            SkillId: "intimacy",
            UltimateGoal: "增进好感，周末约会"
        );

        storage.SavePersona(persona);

        var loaded = storage.GetPersona(contact);
        Assert.NotNull(loaded);
        Assert.Equal(contact, loaded.ContactName);
        Assert.Equal(2, loaded.Traits.Count);
        Assert.Equal("情感表达", loaded.Traits[0].Dimension);
        Assert.Equal("直接坦诚", loaded.Traits[0].Attribute);
        Assert.Equal(85, loaded.Traits[0].Score);
        Assert.Equal(0.9, loaded.Traits[0].Confidence);
        Assert.Contains("我很喜欢你", loaded.Traits[0].Evidence);
        Assert.Equal(42, loaded.SourceMessageCount);
        Assert.Equal("intimacy", loaded.SkillId);
        Assert.Equal("增进好感，周末约会", loaded.UltimateGoal);

        var names = storage.ListPersonaNames();
        Assert.Contains(contact, (IEnumerable<string>)names);
    }

    [Fact]
    public void Persona_UpdateUltimateGoal_ModifiesExisting()
    {
        using var storage = new SqliteStorage(_testDbPath);
        string contact = "Charlie";

        var persona = new Persona(
            ContactName: contact,
            Traits: Array.Empty<PersonaTrait>(),
            UpdatedAt: DateTime.UtcNow,
            SourceMessageCount: 10,
            SkillId: "workplace",
            UltimateGoal: "初始目标"
        );
        storage.SavePersona(persona);

        bool updated = storage.UpdateUltimateGoal(contact, "推进项目合同签约");
        Assert.True(updated);

        var loaded = storage.GetPersona(contact);
        Assert.NotNull(loaded);
        Assert.Equal("推进项目合同签约", loaded.UltimateGoal);

        bool updatedNonExistent = storage.UpdateUltimateGoal("UnknownUser", "Goal");
        Assert.False(updatedNonExistent);
    }

    [Fact]
    public void GetAllPersonas_ReturnsAllSortedByUpdatedAt()
    {
        using var storage = new SqliteStorage(_testDbPath);

        var p1 = new Persona("User1", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 5);
        var p2 = new Persona("User2", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), 10);
        var p3 = new Persona("User3", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), 15);

        storage.SavePersona(p1);
        storage.SavePersona(p2);
        storage.SavePersona(p3);

        var list = storage.GetAllPersonas();
        Assert.Equal(3, list.Count);
        Assert.Equal("User2", list[0].ContactName);
        Assert.Equal("User3", list[1].ContactName);
        Assert.Equal("User1", list[2].ContactName);
    }

    [Fact]
    public void Settings_GetAndSet()
    {
        using var storage = new SqliteStorage(_testDbPath);

        Assert.Equal("default", storage.GetSetting("theme", "default"));

        storage.SetSetting("theme", "dark");
        Assert.Equal("dark", storage.GetSetting("theme"));

        storage.SetSetting("theme", "light");
        Assert.Equal("light", storage.GetSetting("theme"));
    }

    [Fact]
    public void Skill_SaveAndLoad_RoundTrip()
    {
        using var storage = new SqliteStorage(_testDbPath);

        var skill = new DistillSkill(
            Id: "custom_skill_1",
            Name: "自定义销售攻坚",
            Description: "提炼商业销售线索",
            Icon: "💰",
            SystemPrompt: "销售提示词",
            Dimensions: new[] { "客户痛点", "预算规模" },
            IsBuiltIn: false,
            AdviceGuideline: "销售作战心法",
            SuggestedTones: new[] { "商务礼貌", "攻心推进" },
            SourceFilePath: "C:\\skills\\sale.md"
        );

        storage.SaveSkill(skill);

        var loaded = storage.GetSkill("custom_skill_1");
        Assert.NotNull(loaded);
        Assert.Equal("custom_skill_1", loaded.Id);
        Assert.Equal("自定义销售攻坚", loaded.Name);
        Assert.Equal("💰", loaded.Icon);
        Assert.Equal("销售作战心法", loaded.AdviceGuideline);
        Assert.Equal(2, loaded.Dimensions.Count);
        Assert.Equal(2, loaded.SuggestedTones.Count);
        Assert.Equal("C:\\skills\\sale.md", loaded.SourceFilePath);

        var allSkills = storage.GetAllSkills();
        Assert.Single(allSkills);

        bool deleted = storage.DeleteSkill("custom_skill_1");
        Assert.True(deleted);
        Assert.Null(storage.GetSkill("custom_skill_1"));
    }
}
