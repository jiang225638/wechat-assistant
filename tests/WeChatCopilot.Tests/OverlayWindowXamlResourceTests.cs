using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WeChatCopilot.Tests;

public class OverlayWindowXamlResourceTests
{
    [Fact]
    public void OverlayWindow_AllStaticResourceReferences_MustBeDefined()
    {
        // 查找 src/WeChatCopilot.App/OverlayWindow.xaml
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
        Assert.True(File.Exists(xamlPath), $"XAML file not found at {xamlPath}");

        string xamlContent = File.ReadAllText(xamlPath);

        // 1. 提取所有定义了 x:Key 的资源名称
        var keyMatches = Regex.Matches(xamlContent, @"x:Key=""([^""]+)""");
        var definedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in keyMatches)
        {
            definedKeys.Add(m.Groups[1].Value.Trim());
        }

        // 2. 提取所有引用的 StaticResource
        var resourceMatches = Regex.Matches(xamlContent, @"\{StaticResource\s+([a-zA-Z0-9_]+)\}");
        var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
        var missingKeys = new List<string>();

        foreach (Match m in resourceMatches)
        {
            string key = m.Groups[1].Value.Trim();
            referencedKeys.Add(key);
            if (!definedKeys.Contains(key))
            {
                missingKeys.Add(key);
            }
        }

        // 断言：引用的每一个静态资源都必须在 Window.Resources 中严格存在
        Assert.True(referencedKeys.Count > 0, "Should have referenced resources");
        Assert.True(missingKeys.Count == 0,
            $"Found missing StaticResource references in OverlayWindow.xaml: {string.Join(", ", missingKeys.Distinct())}");
    }
}
