using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WeChatCopilot.App;

/// <summary>
/// 高性能本地 WPF Markdown 富文本预览渲染器。
/// 将 AI 问答返回的 Markdown 文本结构化解析并渲染为原生 WPF 视觉元素（标题、引用卡片、分割线、加粗高亮、代码块等），
/// 自动自适应当前深浅色主题，并提供话术与代码一键复制功能。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily s_monoFont = new("Cascadia Code, Consolas, Courier New, monospace");
    private static readonly Regex s_inlineRegex = new(
        @"(\*\*(?:[^*]|\*(?!\*))+\*\*)|(`[^`\r\n]+`)|(\*(?:[^*]|\*\*(?!\*))+\*)|([^*`]+|\*|`)",
        RegexOptions.Compiled);

    /// <summary>
    /// 将 Markdown 文本渲染为一个 StackPanel 容器。
    /// </summary>
    public static FrameworkElement Render(string markdownText, FrameworkElement? resourceHost = null)
    {
        var container = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return container;
        }

        string normalized = markdownText.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] rawLines = normalized.Split('\n');

        int i = 0;
        int lineCount = rawLines.Length;

        while (i < lineCount)
        {
            string line = rawLines[i];
            string trimmed = line.Trim();

            // 1. 空行
            if (string.IsNullOrEmpty(trimmed))
            {
                i++;
                continue;
            }

            // 2. 代码块 (```)
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                string lang = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                var codeLines = new List<string>();
                i++;
                while (i < lineCount && !rawLines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(rawLines[i]);
                    i++;
                }
                if (i < lineCount) i++; // 跳过结束的 ```

                string codeText = string.Join("\n", codeLines);
                container.Children.Add(CreateCodeBlockElement(codeText, lang));
                continue;
            }

            // 3. 分割线 (--- / *** / ___)
            if (IsHorizontalRule(trimmed))
            {
                container.Children.Add(CreateDividerElement());
                i++;
                continue;
            }

            // 4. 标题 (#, ##, ###, ####)
            if (trimmed.StartsWith('#'))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }

                if (level < trimmed.Length && trimmed[level] == ' ')
                {
                    string headerText = trimmed[level..].Trim();
                    container.Children.Add(CreateHeadingElement(headerText, level));
                    i++;
                    continue;
                }
            }

            // 5. 引用块 (Blockquote: > ...)
            if (trimmed.StartsWith('>'))
            {
                var quoteLines = new List<string>();
                while (i < lineCount)
                {
                    string qLine = rawLines[i].Trim();
                    if (!qLine.StartsWith('>'))
                    {
                        break;
                    }
                    string content = qLine.Length > 1 && qLine[1] == ' ' ? qLine[2..] : qLine[1..];
                    quoteLines.Add(content);
                    i++;
                }

                container.Children.Add(CreateQuoteCardElement(quoteLines));
                continue;
            }

            // 6. 无序列表 (- / * / +) 与有序列表 (1. / 2.)
            if (IsListItem(trimmed, out string marker, out string itemContent))
            {
                container.Children.Add(CreateListItemElement(marker, itemContent));
                i++;
                continue;
            }

            // 7. 常规段落 (合并直到空行、标题、引用、列表或分割线)
            var paraLines = new List<string> { line };
            i++;
            while (i < lineCount)
            {
                string nextLine = rawLines[i];
                string nextTrimmed = nextLine.Trim();

                if (string.IsNullOrEmpty(nextTrimmed) ||
                    nextTrimmed.StartsWith("```", StringComparison.Ordinal) ||
                    IsHorizontalRule(nextTrimmed) ||
                    (nextTrimmed.StartsWith('#') && nextTrimmed.Contains(' ')) ||
                    nextTrimmed.StartsWith('>') ||
                    IsListItem(nextTrimmed, out _, out _))
                {
                    break;
                }

                paraLines.Add(nextLine);
                i++;
            }

            string paraText = string.Join("\n", paraLines);
            container.Children.Add(CreateParagraphElement(paraText));
        }

        return container;
    }

    private static bool IsHorizontalRule(string line)
    {
        if (line.Length < 3) return false;
        char c = line[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (char ch in line)
        {
            if (ch != c && ch != ' ') return false;
        }
        return true;
    }

    private static bool IsListItem(string line, out string marker, out string content)
    {
        marker = "";
        content = "";

        if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
        {
            marker = "•";
            content = line[2..].Trim();
            return true;
        }

        var match = Regex.Match(line, @"^(\d+)\.\s+(.*)$");
        if (match.Success)
        {
            marker = match.Groups[1].Value + ".";
            content = match.Groups[2].Value.Trim();
            return true;
        }

        return false;
    }

    private static UIElement CreateHeadingElement(string text, int level)
    {
        double fontSize = level switch
        {
            1 => 14.5,
            2 => 13.5,
            3 => 12.5,
            _ => 12.0
        };

        var margin = level switch
        {
            1 => new Thickness(0, 10, 0, 4),
            2 => new Thickness(0, 8, 0, 4),
            3 => new Thickness(0, 6, 0, 3),
            _ => new Thickness(0, 4, 0, 2)
        };

        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.5,
            Margin = margin
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, level <= 2 ? "AccentBrush" : "TextPrimaryBrush");

        AppendFormattedInlines(tb, text);
        return tb;
    }

    private static UIElement CreateDividerElement()
    {
        var border = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.7
        };
        border.SetResourceReference(Border.BackgroundProperty, "BorderLightBrush");
        return border;
    }

    private static UIElement CreateQuoteCardElement(List<string> lines)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(10, 7, 8, 7),
            Margin = new Thickness(0, 4, 0, 8)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        border.SetResourceReference(Border.BackgroundProperty, "CardSubtleBgBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        string fullQuote = string.Join("\n", lines);
        AppendFormattedInlines(tb, fullQuote);
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);

        // 话术一键复制按钮
        var copyBtn = new Button
        {
            Content = "📋 复制话术",
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "一键将此段推荐示范话术复制到剪贴板"
        };
        copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");

        copyBtn.Click += (s, e) =>
        {
            try
            {
                Clipboard.SetText(fullQuote);
                copyBtn.Content = "✅ 已复制";
                _ = Task.Delay(1500).ContinueWith(_ => copyBtn.Dispatcher.Invoke(() => copyBtn.Content = "📋 复制话术"));
            }
            catch
            {
                // 忽略非致命剪贴板访问冲突
            }
        };
        var pasteBtn = new Button
        {
            Content = "📤 填入",
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "一键复制到剪贴板并自动填入微信输入框（不自动发送）"
        };
        pasteBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
        pasteBtn.Click += async (s, e) =>
        {
            try
            {
                Clipboard.SetText(fullQuote);
                bool pasted = await WeChatCopilot.Windows.WindowPositioner.TryPasteToWeChatInputAsync();
                pasteBtn.Content = pasted ? "✅ 已填入" : "✔ 已复制";
                _ = Task.Delay(1500).ContinueWith(_ => pasteBtn.Dispatcher.Invoke(() => pasteBtn.Content = "📤 填入"));
            }
            catch
            {
            }
        };

        copyBtn.Margin = new Thickness(4, 0, 0, 0);
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top
        };
        btnPanel.Children.Add(pasteBtn);
        btnPanel.Children.Add(copyBtn);
        Grid.SetColumn(btnPanel, 1);
        grid.Children.Add(btnPanel);

        border.Child = grid;
        return border;
    }

    private static UIElement CreateListItemElement(string marker, string content)
    {
        var grid = new Grid
        {
            Margin = new Thickness(4, 2, 0, 3)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var markerTb = new TextBlock
        {
            Text = marker,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        markerTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(markerTb, 0);
        grid.Children.Add(markerTb);

        var contentTb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Top
        };
        contentTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        AppendFormattedInlines(contentTb, content);
        Grid.SetColumn(contentTb, 1);
        grid.Children.Add(contentTb);

        return grid;
    }

    private static UIElement CreateParagraphElement(string text)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 6)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        AppendFormattedInlines(tb, text);
        return tb;
    }

    private static UIElement CreateCodeBlockElement(string code, string lang)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 8)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardSubtleBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderLightBrush");

        var panel = new StackPanel();

        if (!string.IsNullOrWhiteSpace(lang))
        {
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var langTb = new TextBlock
            {
                Text = lang.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            langTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(langTb, 0);
            headerGrid.Children.Add(langTb);

            var copyBtn = new Button
            {
                Content = "📋 复制",
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 9.5,
                Padding = new Thickness(6, 1, 6, 1),
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center
            };
            copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(code);
                    copyBtn.Content = "✅ 已复制";
                    _ = Task.Delay(1500).ContinueWith(_ => copyBtn.Dispatcher.Invoke(() => copyBtn.Content = "📋 复制"));
                }
                catch (Exception ex)
                {
                    WeChatCopilot.Core.Diagnostics.AppLog.Warn("MarkdownRenderer", "复制代码块到剪贴板失败", ex);
                }
            };
            Grid.SetColumn(copyBtn, 1);
            headerGrid.Children.Add(copyBtn);

            panel.Children.Add(headerGrid);
        }

        var tb = new TextBlock
        {
            Text = code,
            FontFamily = s_monoFont,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(tb);

        border.Child = panel;
        return border;
    }

    /// <summary>
    /// 解析行内样式并填充到 TextBlock.Inlines：**粗体**、`行内代码`、*斜体* 与普通文本。
    /// </summary>
    public static void AppendFormattedInlines(TextBlock textBlock, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var matches = s_inlineRegex.Matches(text);
        foreach (Match m in matches)
        {
            string val = m.Value;
            if (string.IsNullOrEmpty(val)) continue;

            // 加粗 **bold**
            if (val.Length >= 4 && val.StartsWith("**", StringComparison.Ordinal) && val.EndsWith("**", StringComparison.Ordinal))
            {
                string boldContent = val[2..^2];
                var run = new Run(boldContent)
                {
                    FontWeight = FontWeights.Bold
                };
                textBlock.Inlines.Add(run);
            }
            // 行内代码 `code`
            else if (val.Length >= 2 && val.StartsWith('`') && val.EndsWith('`'))
            {
                string codeContent = val[1..^1];
                var run = new Run(codeContent)
                {
                    FontFamily = s_monoFont,
                    FontWeight = FontWeights.Medium
                };
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                textBlock.Inlines.Add(run);
            }
            // 斜体 *italic*
            else if (val.Length >= 2 && val.StartsWith('*') && val.EndsWith('*'))
            {
                string italicContent = val[1..^1];
                var run = new Run(italicContent)
                {
                    FontStyle = FontStyles.Italic
                };
                textBlock.Inlines.Add(run);
            }
            // 普通文本
            else
            {
                textBlock.Inlines.Add(new Run(val));
            }
        }
    }
}
