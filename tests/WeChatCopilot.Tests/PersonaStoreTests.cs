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
}
