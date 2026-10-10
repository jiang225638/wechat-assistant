using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.Tests;

/// <summary>M5 画像存储测试：JSON 往返/缺失返回 null/文件名净化/列表。</summary>
public class PersonaStoreTests
{
    private static string TempDir() =>
        Path.Combine(AppContext.BaseDirectory, "persona-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var store = new PersonaStore(TempDir());
        var persona = new Persona(
            "张三",
            new[] { new PersonaTrait("语言风格", "短句", 0.8, new[] { "嗯", "好" }) },
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            42);

        store.Save(persona);
        Persona? loaded = store.Load("张三");

        Assert.NotNull(loaded);
        Assert.Equal("张三", loaded.ContactName);
        Assert.Single(loaded.Traits);
        Assert.Equal("短句", loaded.Traits[0].Attribute);
        Assert.Equal(0.8, loaded.Traits[0].Confidence);
        Assert.Equal(2, loaded.Traits[0].Evidence.Count);
        Assert.Equal(42, loaded.SourceMessageCount);
        Assert.Equal(persona.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public void Load_Missing_ReturnsNull()
    {
        var store = new PersonaStore(TempDir());
        Assert.Null(store.Load("不存在"));
    }

    [Fact]
    public void Save_SanitizesInvalidFileNameChars()
    {
        var store = new PersonaStore(TempDir());
        store.Save(new Persona("a/b:c", Array.Empty<PersonaTrait>(), DateTime.Now, 0));

        // 非法字符被替换后仍能按原名取回
        Assert.NotNull(store.Load("a/b:c"));
        Assert.Contains("a_b_c", store.List());
    }

    [Fact]
    public void List_ReturnsSavedNames()
    {
        var store = new PersonaStore(TempDir());
        store.Save(new Persona("甲", Array.Empty<PersonaTrait>(), DateTime.Now, 0));
        store.Save(new Persona("乙", Array.Empty<PersonaTrait>(), DateTime.Now, 0));

        Assert.Equal(2, store.List().Count);
        Assert.Contains("甲", store.List());
    }

    [Fact]
    public void SaveLoad_WithUltimateGoal_RoundTrip()
    {
        var store = new PersonaStore(TempDir());
        var persona = new Persona(
            "李四",
            Array.Empty<PersonaTrait>(),
            DateTime.Now,
            15,
            SkillId: "tdskill",
            UltimateGoal: "确立恋爱关系并邀约线下看电影");

        store.Save(persona);
        var loaded = store.Load("李四");

        Assert.NotNull(loaded);
        Assert.Equal("李四", loaded.ContactName);
        Assert.Equal("tdskill", loaded.SkillId);
        Assert.Equal("确立恋爱关系并邀约线下看电影", loaded.UltimateGoal);
    }

    [Fact]
    public void UpdateUltimateGoal_PersistsAndUpdatesExistingPersona()
    {
        var store = new PersonaStore(TempDir());
        var persona = new Persona("王五", Array.Empty<PersonaTrait>(), DateTime.Now, 10);
        store.Save(persona);

        // 更新目的
        bool ok = store.UpdateUltimateGoal("王五", "争取更有利的商务合同条款");
        Assert.True(ok);

        var loaded = store.Load("王五");
        Assert.NotNull(loaded);
        Assert.Equal("争取更有利的商务合同条款", loaded.UltimateGoal);

        // 清空目的
        store.UpdateUltimateGoal("王五", "   ");
        var cleared = store.Load("王五");
        Assert.NotNull(cleared);
        Assert.Null(cleared.UltimateGoal);

        // 不存在的联系人返回 false
        Assert.False(store.UpdateUltimateGoal("不存在的人", "目标"));
    }
}
