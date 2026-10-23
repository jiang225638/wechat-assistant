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

    [Fact]
    public void Load_MigratesExistingJsonToSqlite()
    {
        string dir = TempDir();
        var store = new PersonaStore(dir);

        // 手工写入一个 legacy JSON 文件
        string jsonPath = Path.Combine(dir, "老王.json");
        var legacy = new Persona("老王", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 20, "intimacy", "老友相聚");
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(legacy));

        // SQLite 中目前无记录
        Assert.Null(store.SqliteStorage.GetPersona("老王"));

        // 调用 Load 触发自动迁移
        var loaded = store.Load("老王");
        Assert.NotNull(loaded);
        Assert.Equal("老王", loaded.ContactName);
        Assert.Equal("老友相聚", loaded.UltimateGoal);

        // 验证 SQLite 中已经自动建档
        var sqlitePersona = store.SqliteStorage.GetPersona("老王");
        Assert.NotNull(sqlitePersona);
        Assert.Equal("老王", sqlitePersona.ContactName);
        Assert.Equal("老友相聚", sqlitePersona.UltimateGoal);
    }

    [Fact]
    public void Delete_CleansBothSqliteAndJson()
    {
        string dir = TempDir();
        var store = new PersonaStore(dir);
        var persona = new Persona("赵六", Array.Empty<PersonaTrait>(), DateTime.Now, 5);
        store.Save(persona);

        Assert.NotNull(store.Load("赵六"));
        Assert.NotNull(store.SqliteStorage.GetPersona("赵六"));

        bool deleted = store.Delete("赵六");
        Assert.True(deleted);

        Assert.Null(store.Load("赵六"));
        Assert.Null(store.SqliteStorage.GetPersona("赵六"));
    }

    [Fact]
    public void GetAllPersonas_MergesSqliteAndUnmigratedJsonFiles()
    {
        string dir = TempDir();
        var store = new PersonaStore(dir);

        // 1. 先通过 Save 写入一条 SQLite + JSON 记录
        store.Save(new Persona("新联系人", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), 10));

        // 2. 再在目录下放入一条仅存在于旧版 JSON（尚未进入 SQLite）的画像文件
        string legacyJsonPath = Path.Combine(dir, "旧版联系人.json");
        var legacy = new Persona("旧版联系人", Array.Empty<PersonaTrait>(), new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), 8);
        File.WriteAllText(legacyJsonPath, System.Text.Json.JsonSerializer.Serialize(legacy));

        // 验证 List() 与 GetAllPersonas() 数量与成员完全一致
        var listNames = store.List();
        var allPersonas = store.GetAllPersonas();

        Assert.Equal(2, listNames.Count);
        Assert.Equal(2, allPersonas.Count);
        Assert.Contains(allPersonas, p => p.ContactName == "新联系人");
        Assert.Contains(allPersonas, p => p.ContactName == "旧版联系人");
    }
}
