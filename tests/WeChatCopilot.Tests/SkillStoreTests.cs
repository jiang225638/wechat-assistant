using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.Tests;

/// <summary>
/// 人格蒸馏 SkillStore 单元测试：验证内置预设加载、自定义技能读写、当前技能激活持久化与防篡改规则。
/// </summary>
public sealed class SkillStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly SkillStore _store;

    public SkillStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "WeChatCopilotTests_Skills_" + Guid.NewGuid().ToString("N"));
        _store = new SkillStore(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // 忽略测试清理异常
        }
    }

    [Fact]
    public void GetAllSkills_IncludesAllBuiltInPresetsByDefault()
    {
        var skills = _store.GetAllSkills();

        Assert.True(skills.Count >= 4);
        Assert.Contains(skills, s => s.Id == DistillSkillPresets.DefaultSkillId);
        Assert.Contains(skills, s => s.Id == "workplace");
        Assert.Contains(skills, s => s.Id == "intimacy");
        Assert.Contains(skills, s => s.Id == "logical");
    }

    [Fact]
    public void GetActiveSkillId_DefaultsToNuwa_AndPersistsUpdate()
    {
        Assert.Equal(DistillSkillPresets.DefaultSkillId, _store.GetActiveSkillId());

        _store.SetActiveSkillId("workplace");
        Assert.Equal("workplace", _store.GetActiveSkillId());

        var store2 = new SkillStore(_testDir);
        Assert.Equal("workplace", store2.GetActiveSkillId());
    }

    [Fact]
    public void SaveCustomSkill_PersistsAndCanBeLoadedAndDeleted()
    {
        var custom = new DistillSkill(
            Id: "custom_sales",
            Name: "商务谈判",
            Description: "分析对方痛点与预算",
            Icon: "🤝",
            SystemPrompt: "你是谈判心理学专家...",
            IsBuiltIn: false
        );

        _store.SaveCustomSkill(custom);

        var loaded = _store.GetSkill("custom_sales");
        Assert.NotNull(loaded);
        Assert.Equal("商务谈判", loaded.Name);
        Assert.Equal("🤝", loaded.Icon);
        Assert.False(loaded.IsBuiltIn);

        // 新建实例验证已持久化至磁盘
        var store2 = new SkillStore(_testDir);
        var loaded2 = store2.GetSkill("custom_sales");
        Assert.Equal("商务谈判", loaded2.Name);

        // 删除自定义技能
        bool deleted = store2.DeleteCustomSkill("custom_sales");
        Assert.True(deleted);

        var afterDelete = store2.GetSkill("custom_sales");
        // 回退至默认女娲
        Assert.Equal(DistillSkillPresets.DefaultSkillId, afterDelete.Id);
    }

    [Fact]
    public void SaveCustomSkill_CannotOverwriteBuiltInSkill()
    {
        var forgedNuwa = new DistillSkill(
            Id: "nuwa",
            Name: "伪造女娲",
            Description: "篡改测试",
            Icon: "❌",
            SystemPrompt: "恶意提示词",
            IsBuiltIn: false
        );

        Assert.Throws<InvalidOperationException>(() => _store.SaveCustomSkill(forgedNuwa));

        var skill = _store.GetSkill("nuwa");
        Assert.Equal("女娲心智蒸馏", skill.Name);
        Assert.True(skill.IsBuiltIn);
    }

    [Fact]
    public void DeleteCustomSkill_CannotDeleteBuiltInSkill()
    {
        bool deleted = _store.DeleteCustomSkill("nuwa");
        Assert.False(deleted);

        var skill = _store.GetSkill("nuwa");
        Assert.Equal(DistillSkillPresets.DefaultSkillId, skill.Id);
    }

    [Fact]
    public void ScanWorkspaceSkills_DiscoversWorkspaceSkillPackages()
    {
        var scanned = _store.ScanWorkspaceSkills();
        Assert.NotEmpty(scanned);
        Assert.Contains(scanned, s => s.Id.Contains("td") || s.Id.Contains("chat"));
    }
}
