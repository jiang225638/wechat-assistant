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

        Assert.True(skills.Count >= 3);
        Assert.Contains(skills, s => s.Id == DistillSkillPresets.DefaultSkillId);
        Assert.Contains(skills, s => s.Id == "workplace");
        Assert.Contains(skills, s => s.Id == "intimacy");
        Assert.Contains(skills, s => s.Id == "logical");
    }

    [Fact]
    public void GetActiveSkillId_DefaultsToDefaultSkill_AndPersistsUpdate()
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
        // 回退至默认技能
        Assert.Equal(DistillSkillPresets.DefaultSkillId, afterDelete.Id);
    }

    [Fact]
    public void SaveCustomSkill_CannotOverwriteBuiltInSkill()
    {
        var forgedWorkplace = new DistillSkill(
            Id: "workplace",
            Name: "伪造职场",
            Description: "篡改测试",
            Icon: "❌",
            SystemPrompt: "恶意提示词",
            IsBuiltIn: false
        );

        Assert.Throws<InvalidOperationException>(() => _store.SaveCustomSkill(forgedWorkplace));

        var skill = _store.GetSkill("workplace");
        Assert.Equal("职场与商业认知", skill.Name);
        Assert.True(skill.IsBuiltIn);
    }

    [Fact]
    public void DeleteCustomSkill_CannotDeleteBuiltInSkill()
    {
        bool deleted = _store.DeleteCustomSkill("workplace");
        Assert.False(deleted);

        var skill = _store.GetSkill("workplace");
        Assert.Equal("workplace", skill.Id);
    }

    [Fact]
    public void ScanWorkspaceSkills_DiscoversWorkspaceSkillPackages()
    {
        string customDir = Path.Combine(_testDir, "test_workspace_skills");
        string skillDir = Path.Combine(customDir, "pkg1");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: 包1\ndescription: 包1的描述\n---\n提示词");
        _store.SetBoundSkillsDirectory(customDir);

        var scanned = _store.ScanWorkspaceSkills();
        Assert.NotEmpty(scanned);
    }

    [Fact]
    public void ExtractDimensionsFromSkill_ExtractsFromYamlAndMarkdown()
    {
        // 1. YAML 数组格式
        string yaml1 = "name: test\ndimensions: [框架强度, 需求感管理, 慕强机制, 窗口状态]";
        var dims1 = SkillStore.ExtractDimensionsFromSkill(yaml1, "");
        Assert.Equal(4, dims1.Count);
        Assert.Equal("框架强度", dims1[0]);
        Assert.Equal("窗口状态", dims1[3]);

        // 2. YAML 多行列表格式
        string yaml2 = "name: test\ndimensions:\n  - 表达DNA\n  - 心智模型\n  - 决策启发式";
        var dims2 = SkillStore.ExtractDimensionsFromSkill(yaml2, "");
        Assert.Equal(3, dims2.Count);
        Assert.Equal("表达DNA", dims2[0]);
        Assert.Equal("心智模型", dims2[1]);

        // 3. Markdown 正文中的显式行
        string body1 = "# 技能介绍\n核心评估维度：商务谈判、利益诉求、防备心理、底线试探\n详细指南...";
        var dims3 = SkillStore.ExtractDimensionsFromSkill(null, body1);
        Assert.Equal(4, dims3.Count);
        Assert.Equal("商务谈判", dims3[0]);
        Assert.Equal("底线试探", dims3[3]);
    }

    [Fact]
    public void SaveCustomSkill_PersistsDimensionsCorrectly()
    {
        var custom = new DistillSkill(
            Id: "custom_sales",
            Name: "商务谈判",
            Description: "分析对方痛点与预算",
            Icon: "🤝",
            SystemPrompt: "你是谈判心理学专家...",
            Dimensions: new[] { "商务谈判", "利益诉求", "风险控制", "出价策略" },
            IsBuiltIn: false
        );

        _store.SaveCustomSkill(custom);

        var loaded = _store.GetSkill("custom_sales");
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Dimensions.Count);
        Assert.Equal("商务谈判", loaded.Dimensions[0]);
        Assert.Equal("出价策略", loaded.Dimensions[3]);
    }

    [Fact]
    public void GetSkill_DoesNotMutateActiveSkill()
    {
        _store.SetActiveSkillId("workplace");
        Assert.Equal("workplace", _store.GetActiveSkillId());

        // 查询其他技能不应改变当前激活技能
        var other = _store.GetSkill("logical");
        Assert.Equal("workplace", _store.GetActiveSkillId());
    }

    [Fact]
    public void ScanWorkspaceSkills_PreservesDetailedDescriptionsAndDimensions()
    {
        string customDir = Path.Combine(_testDir, "test_workspace_skills_dims");
        string skillDir = Path.Combine(customDir, "pkg2");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: 详尽画像分析视角\ndescription: 这是详细描述测试，长度超过二十个字符用来验证保留\ndimensions: [维度A, 维度B]\n---\n提示词");
        _store.SetBoundSkillsDirectory(customDir);

        var scanned = _store.ScanWorkspaceSkills();
        Assert.NotEmpty(scanned);

        var first = scanned.First();
        Assert.False(string.IsNullOrWhiteSpace(first.Description));
        Assert.True(first.Description.Length > 20);
    }

    [Fact]
    public void ScanAndImportFolder_RecursivelyDiscoversAndSavesSkills()
    {
        string importFolder = Path.Combine(_testDir, "test_imports");
        string subDir1 = Path.Combine(importFolder, "negotiation");
        string subDir2 = Path.Combine(importFolder, "conflict_resolution");
        Directory.CreateDirectory(subDir1);
        Directory.CreateDirectory(subDir2);

        string md1 = "---\nname: 商务谈判\ndescription: 商业谈判专属技能\ndimensions: [议价底线, 利益权衡, 筹码控制]\n---\n这是谈判方法论正文...";
        string md2 = "---\nname: 冲突化解\ndescription: 情绪与矛盾化解\ndimensions: [共情接纳, 事实澄清, 解决方案]\n---\n这是冲突化解正文...";

        File.WriteAllText(Path.Combine(subDir1, "SKILL.md"), md1);
        File.WriteAllText(Path.Combine(subDir2, "skill.md"), md2);

        var imported = _store.ScanAndImportFolder(importFolder);

        Assert.Equal(2, imported.Count);
        Assert.Contains(imported, s => s.Name == "商务谈判" && s.Dimensions.Count == 3);
        Assert.Contains(imported, s => s.Name == "冲突化解" && s.Dimensions.Count == 3);

        // 验证已持久化至自定义技能库中
        var loaded1 = _store.GetSkill("negotiation");
        Assert.Equal("商务谈判", loaded1.Name);
        var loaded2 = _store.GetSkill("conflict_resolution");
        Assert.Equal("冲突化解", loaded2.Name);
    }

    [Fact]
    public void BoundSkillsDirectory_And_PhysicalDeletion_PreventsRescan()
    {
        string customDir = Path.Combine(_testDir, "my_custom_skills");
        string skillDir = Path.Combine(customDir, "tactics_skill");
        Directory.CreateDirectory(skillDir);

        string md = "---\nname: 实战战术\ndescription: 专属战术技能\ndimensions: [进攻, 防守]\n---\n战术正文...";
        string skillPath = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillPath, md);

        _store.SetBoundSkillsDirectory(customDir);
        Assert.Equal(customDir, _store.GetBoundSkillsDirectory());

        var all = _store.GetAllSkills();
        Assert.Contains(all, s => s.Id == "tactics_skill");

        // 删除自定义技能
        bool deleted = _store.DeleteCustomSkill("tactics_skill");
        Assert.True(deleted);

        // 验证物理文件已被移除
        Assert.False(File.Exists(skillPath));

        // 重新扫描后绝不会再次出现
        var allAfter = _store.GetAllSkills();
        Assert.DoesNotContain(allAfter, s => s.Id == "tactics_skill");
    }

    [Fact]
    public void ParseSkillMdFile_WithReferencesFolder_BundlesReferenceMarkdownContents()
    {
        string skillDir = Path.Combine(_testDir, "agent_pkg");
        string refDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(refDir);

        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: 关系师\ndescription: 关系测试\n---\n入门指引正文");
        File.WriteAllText(Path.Combine(refDir, "chat-reply.md"), "# 回复实战秘籍\n1. 先接情绪\n2. 再带方向");
        File.WriteAllText(Path.Combine(refDir, "signals.md"), "# 信号判断\n- 强信号\n- 弱信号");

        var parsed = SkillStore.ParseSkillMdFile(Path.Combine(skillDir, "SKILL.md"));
        Assert.NotNull(parsed);
        Assert.Equal("关系师", parsed.Name);

        // 验证 SystemPrompt 中已成功自动聚合注入 references/ 下的内容
        Assert.Contains("入门指引正文", parsed.SystemPrompt);
        Assert.Contains("【本技能配套实战策略与核心参考资料（References 知识库注入）】", parsed.SystemPrompt);
        Assert.Contains("回复实战秘籍", parsed.SystemPrompt);
        Assert.Contains("先接情绪", parsed.SystemPrompt);
        Assert.Contains("信号判断", parsed.SystemPrompt);
    }

    [Fact]
    public void ScanWorkspaceSkills_CompositeSkillWithSubModules_OnlyDiscoversRootSkill()
    {
        string customDir = Path.Combine(_testDir, "test_composite_skills");
        string rootSkillDir = Path.Combine(customDir, "tdskill");
        string refDir = Path.Combine(rootSkillDir, "references");
        string subSkillDir1 = Path.Combine(refDir, "tds-chat");
        string subSkillDir2 = Path.Combine(refDir, "tds-date");

        Directory.CreateDirectory(subSkillDir1);
        Directory.CreateDirectory(subSkillDir2);

        File.WriteAllText(Path.Combine(rootSkillDir, "SKILL.md"), "---\nname: tdskill\ndescription: 情感导师主入口\n---\n主路由提示词");
        File.WriteAllText(Path.Combine(subSkillDir1, "SKILL.md"), "---\nname: tds-chat\ndescription: 聊天子技能\n---\n聊天诊断");
        File.WriteAllText(Path.Combine(subSkillDir2, "SKILL.md"), "---\nname: tds-date\ndescription: 约会子技能\n---\n约会诊断");

        _store.SetBoundSkillsDirectory(customDir);
        var scanned = _store.ScanWorkspaceSkills();

        // 核心断言：tdskill 作为一个整体包，只应该产出 1 个根技能，references 内的子模块严禁作为独立技能打散
        Assert.Contains(scanned, s => s.Id == "tdskill");
        Assert.DoesNotContain(scanned, s => s.Id == "tds-chat");
        Assert.DoesNotContain(scanned, s => s.Id == "tds-date");
    }

    [Fact]
    public void ScanAndImportFolder_IgnoresSubSkillsInsideReferencesFolder()
    {
        string importFolder = Path.Combine(_testDir, "test_composite_import");
        string rootDir = Path.Combine(importFolder, "tdskill");
        string refSub = Path.Combine(rootDir, "references", "tds-approach");
        Directory.CreateDirectory(refSub);

        File.WriteAllText(Path.Combine(rootDir, "SKILL.md"), "---\nname: tdskill\ndescription: 情感总控\n---\n总控正文");
        File.WriteAllText(Path.Combine(refSub, "SKILL.md"), "---\nname: tds-approach\ndescription: 搭讪子技能\n---\n搭讪正文");

        var imported = _store.ScanAndImportFolder(importFolder);

        Assert.Single(imported);
        Assert.Equal("tdskill", imported[0].Id);
    }

    [Fact]
    public void ExtractDimensionsFromSkill_FiltersOutNoisyHeadersAndBlacklistedWords()
    {
        string noisyBody = @"# 技能说明
## ⚠️ 强制规则（最高优先级，不可违反）
**你每一次回复用户的内容，最后一行必须附上：**
*公众号：兔子舞的奇思妙想y*
- 无论回复内容是什么
- Step 1: 新手判断
- Step 2: 路由判断
- 关键原则：框架博弈
- 核心维度: 框架强度、需求感把控、情绪共鸣、边界感
";
        var dims = SkillStore.ExtractDimensionsFromSkill(null, noisyBody);

        Assert.DoesNotContain("公众号", dims);
        Assert.DoesNotContain("无论回复内容是什么", dims);
        Assert.DoesNotContain("Step 1", dims);
        Assert.DoesNotContain("原则", dims);
        Assert.Contains("框架强度", dims);
        Assert.Contains("需求感把控", dims);
        Assert.Contains("情绪共鸣", dims);
        Assert.Contains("边界感", dims);
    }
}
