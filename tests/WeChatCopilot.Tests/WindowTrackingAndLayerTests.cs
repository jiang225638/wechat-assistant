using System.IO;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows;
using Xunit;

namespace WeChatCopilot.Tests;

public class WindowTrackingAndLayerTests
{
    [Fact]
    public void WindowInfo_DefaultIsForeground_IsFalse()
    {
        var bounds = new WindowBounds(100, 200, 800, 600);
        var info = new WindowInfo(
            Handle: 12345,
            Title: "微信",
            ClassName: "WeChatMainWndForPC",
            Bounds: bounds,
            IsMinimized: false,
            IsVisible: true);

        Assert.False(info.IsForeground);
        Assert.True(info.IsVisible);
        Assert.False(info.IsMinimized);
    }

    [Fact]
    public void WindowInfo_ExplicitIsForeground_IsPreserved()
    {
        var bounds = new WindowBounds(0, 0, 1024, 768);
        var info = new WindowInfo(
            Handle: 8888,
            Title: "测试聊天",
            ClassName: "ChatWnd",
            Bounds: bounds,
            IsMinimized: false,
            IsVisible: true,
            IsForeground: true);

        Assert.True(info.IsForeground);

        var updated = info with { IsForeground = false };
        Assert.False(updated.IsForeground);
        Assert.True(updated.IsVisible);
    }

    [Fact]
    public void OverlayWindowXaml_MustNotHaveHardcodedTopmostTrue()
    {
        string currentDir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WeChatCopilot.slnx")) || File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);
        string xamlPath = Path.Combine(repoRoot, "src", "WeChatCopilot.App", "OverlayWindow.xaml");
        Assert.True(File.Exists(xamlPath));

        string content = File.ReadAllText(xamlPath);

        // 验证不再是 Topmost="True"，而是 Topmost="False"
        Assert.Contains(@"Topmost=""False""", content);
        Assert.DoesNotMatch(@"<Window[^>]*Topmost=""True""", content);

        // 验证 ShowActivated="False" 以避免抢夺微信输入焦点
        Assert.Contains(@"ShowActivated=""False""", content);
    }

    [Fact]
    public void WindowPositioner_ExposesCompanionAndForegroundMethods()
    {
        // 验证 WindowPositioner.GetForegroundWindow() 返回有效或空指针且不抛异常
        nint fg = WindowPositioner.GetForegroundWindow();
        Assert.True(fg >= 0);

        // 验证在空句柄上调用不崩溃
        WindowPositioner.MoveKeepZOrder(0, 0, 0);
        WindowPositioner.RemoveTopmost(0);
        WindowPositioner.MoveRelativeTo(0, 0, 0, 0);
        WindowPositioner.MoveCompanion(0, 0, 0, 0);
    }

    [Fact]
    public void CompanionSnapPosition_SnapRightWithZeroGap_CoordinatesCorrect()
    {
        var target = new WindowBounds(200, 150, 900, 700);
        var overlay = new OverlaySize(420, 680);

        var (x, y) = OverlayLayout.ComputeSnapPosition(target, overlay, SnapSide.Right, gap: 0, alignTop: true);

        Assert.Equal(1100, x); // 200 + 900
        Assert.Equal(150, y);  // alignTop = true
    }
}
