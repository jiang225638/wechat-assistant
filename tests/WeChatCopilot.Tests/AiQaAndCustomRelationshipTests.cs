using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WeChatCopilot.Tests;

public class AiQaAndCustomRelationshipTests
{
    private static string GetRepoRoot()
    {
        string currentDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WeChatCopilot.slnx")) || File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found");
    }

    [Fact]
    public void PersonaAnalyticsWindow_ShouldContainEditableRelationshipInputAndPresets()
    {
        string repoRoot = GetRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "WeChatCopilot.App", "PersonaAnalyticsWindow.xaml");
        Assert.True(File.Exists(xamlPath));

        string content = File.ReadAllText(xamlPath);

        // 验证 TextBox 输入框
        Assert.Contains("x:Name=\"RelationshipIdentityInput\"", content);
        Assert.Contains("KeyDown=\"RelationshipIdentityInput_KeyDown\"", content);
        Assert.Contains("LostFocus=\"RelationshipIdentityInput_LostFocus\"", content);

        // 验证常用预设菜单与应用视角按钮
        Assert.Contains("x:Name=\"ApplyRelationshipBtn\"", content);
        Assert.Contains("Click=\"ApplyRelationshipBtn_Click\"", content);
        Assert.Contains("Click=\"RelationshipPreset_Click\"", content);
        Assert.Contains("Tag=\"亲密交往对象 (心仪伴侣)\"", content);
        Assert.Contains("Tag=\"相亲交往对象 (相亲发展)\"", content);
        Assert.Contains("Tag=\"职场管理对象 (直属领导)\"", content);
    }

    [Fact]
    public void OverlayWindow_ShouldContainTabQaAndAntigravityChatView()
    {
        string repoRoot = GetRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "WeChatCopilot.App", "OverlayWindow.xaml");
        Assert.True(File.Exists(xamlPath));

        string content = File.ReadAllText(xamlPath);

        // 验证 TabQa 存在
        Assert.Contains("x:Name=\"TabQa\"", content);
        Assert.Contains("Header=\"🤖 AI问答\"", content);

        // 验证 ViewQa 结构与 Antigravity 交互组件
        Assert.Contains("x:Name=\"ViewQa\"", content);
        Assert.Contains("x:Name=\"QaTargetContactText\"", content);
        Assert.Contains("x:Name=\"QaPersonaBadgeText\"", content);
        Assert.Contains("x:Name=\"QaHistoryBadgeText\"", content);
        Assert.Contains("x:Name=\"QaToggleHistoryBtn\"", content);
        Assert.Contains("x:Name=\"QaHistorySidebarOverlay\"", content);
        Assert.Contains("x:Name=\"QaSidebarNewSessionBtn\"", content);
        Assert.Contains("x:Name=\"QaSessionListPanel\"", content);
        Assert.Contains("x:Name=\"QaWelcomeCard\"", content);
        Assert.Contains("x:Name=\"QaThinkingPanel\"", content);
        Assert.Contains("x:Name=\"QaStopButton\"", content);
        Assert.Contains("x:Name=\"QaIncludePersonaCheck\"", content);
        Assert.Contains("x:Name=\"QaIncludeHistoryCheck\"", content);
        Assert.Contains("x:Name=\"QaInputBox\"", content);
        Assert.Contains("x:Name=\"QaSendButton\"", content);

        // 验证快捷指令探针
        Assert.Contains("Click=\"QaPromptPill_Click\"", content);
    }

    [Fact]
    public void QaSessionStore_ShouldSaveAndLoadSessionsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WeChatCopilot_Test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var store = new WeChatCopilot.Data.QaSessionStore(tempDir);

            var initialSessions = store.LoadAll();
            Assert.Empty(initialSessions);

            var session1 = new WeChatCopilot.Core.Models.QaSession
            {
                Title = "测试会话1",
                ContactName = "小美",
                Messages = new System.Collections.Generic.List<WeChatCopilot.Core.Models.QaSessionMessage>
                {
                    new(WeChatCopilot.Core.Models.MessageRole.Outgoing, "你好，她今天发了朋友圈怎么回？", DateTime.Now),
                    new(WeChatCopilot.Core.Models.MessageRole.Incoming, "建议从她的兴趣点切入点赞评论。", DateTime.Now)
                }
            };

            var session2 = new WeChatCopilot.Core.Models.QaSession
            {
                Title = "工作沟通复盘",
                ContactName = "张总"
            };

            var toSave = new System.Collections.Generic.List<WeChatCopilot.Core.Models.QaSession> { session1, session2 };
            store.SaveAll(toSave);

            var loaded = store.LoadAll();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("测试会话1", loaded[0].Title);
            Assert.Equal(2, loaded[0].Messages.Count);
            Assert.Equal("小美", loaded[0].ContactName);
            Assert.Equal("工作沟通复盘", loaded[1].Title);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void OverlayWindow_TabOrder_MustBeChatAiPersonaQaSettings()
    {
        string repoRoot = GetRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "WeChatCopilot.App", "OverlayWindow.xaml");
        string content = File.ReadAllText(xamlPath);

        int idxChat = content.IndexOf("x:Name=\"TabChat\"", StringComparison.Ordinal);
        int idxAi = content.IndexOf("x:Name=\"TabAi\"", StringComparison.Ordinal);
        int idxPersona = content.IndexOf("x:Name=\"TabPersona\"", StringComparison.Ordinal);
        int idxQa = content.IndexOf("x:Name=\"TabQa\"", StringComparison.Ordinal);
        int idxSettings = content.IndexOf("x:Name=\"TabSettings\"", StringComparison.Ordinal);

        Assert.True(idxChat >= 0, "TabChat not found");
        Assert.True(idxAi > idxChat, "TabAi must follow TabChat");
        Assert.True(idxPersona > idxAi, "TabPersona must follow TabAi");
        Assert.True(idxQa > idxPersona, "TabQa must follow TabPersona");
        Assert.True(idxSettings > idxQa, "TabSettings must follow TabQa");
    }
}
