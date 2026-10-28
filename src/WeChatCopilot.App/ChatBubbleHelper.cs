using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.App;

/// <summary>
/// 对话气泡富文本与多媒体呈现引擎：
/// 1. 微信自带小黄脸表情智能替换为彩色 Segoe UI Emoji；
/// 2. 文本普通网址自动转换为可点击 Hyperlink 并调起系统浏览器；
/// 3. 分享卡片（Type 49）呈现为精致可点击交互卡片（深浅色完美自适应）；
/// 4. 斗图大表情包（Type 47）自动从本地缓存/CDN 加载动画与图片，纯文本降级为精致胶囊，杜绝空框与浅色看不清；
/// 5. 聊天图片（Type 3）自动解密呈现缩略图，支持点击大图或定位微信图片目录。
/// </summary>
public static class ChatBubbleHelper
{
    public static readonly DependencyProperty FormattedMessageProperty =
        DependencyProperty.RegisterAttached(
            "FormattedMessage",
            typeof(string),
            typeof(ChatBubbleHelper),
            new PropertyMetadata(null, OnFormattedMessageChanged));

    public static string? GetFormattedMessage(DependencyObject obj) => (string?)obj.GetValue(FormattedMessageProperty);
    public static void SetFormattedMessage(DependencyObject obj, string? value) => obj.SetValue(FormattedMessageProperty, value);

    private static readonly Regex s_urlRegex = new(@"https?://[^\s<>""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_emojiPattern = new(@"^\[动画表情:\s*(?:md5=([^&|\]]*))?(?:&cdn=([^\]]*))?\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_imgPattern = new(@"^\[图片:\s*md5=([^\]]*)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_cardPattern = new(@"^\[分享卡片:\s*url=([^|]*)\|title=([^|]*)\|des=([^\]]*)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? s_cachedAccountRoot;

    /// <summary>更新全局解析引用的微信数据根目录。</summary>
    public static void SetAccountRoot(string? root) => s_cachedAccountRoot = root;

    private static void OnFormattedMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        tb.Inlines.Clear();
        string rawText = (e.NewValue as string ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rawText))
        {
            return;
        }

        // 1. 斗图大表情包 (Type 47)
        Match mEmoji = s_emojiPattern.Match(rawText);
        if (mEmoji.Success)
        {
            string md5 = mEmoji.Groups[1].Value.Trim();
            string cdn = mEmoji.Groups[2].Value.Trim();

            // 若有实体 md5 或 cdn 链接，尝试加载真实大表情图
            if (!string.IsNullOrEmpty(md5) || !string.IsNullOrEmpty(cdn))
            {
                tb.Inlines.Add(new InlineUIContainer(CreateEmojiBubbleElement(md5, cdn)));
                return;
            }
        }

        // 纯文本 [动画表情] 历史记录（无 md5/cdn）：降级为内联精致表情标签胶囊，杜绝难看的空框
        if (rawText.Equals("[动画表情]", StringComparison.OrdinalIgnoreCase) ||
            rawText.StartsWith("[动画表情:", StringComparison.OrdinalIgnoreCase))
        {
            tb.Inlines.Add(new InlineUIContainer(CreateSimpleEmojiPill()));
            return;
        }

        // 2. 聊天图片 (Type 3)
        Match mImg = s_imgPattern.Match(rawText);
        if (mImg.Success || rawText.Equals("[图片]", StringComparison.OrdinalIgnoreCase))
        {
            string md5 = mImg.Success ? mImg.Groups[1].Value.Trim() : string.Empty;
            tb.Inlines.Add(new InlineUIContainer(CreateImageBubbleElement(md5)));
            return;
        }

        // 3. 分享卡片 (Type 49)
        Match mCard = s_cardPattern.Match(rawText);
        if (mCard.Success)
        {
            string url = mCard.Groups[1].Value.Trim();
            string title = mCard.Groups[2].Value.Trim();
            string des = mCard.Groups[3].Value.Trim();
            tb.Inlines.Add(new InlineUIContainer(CreateShareCardElement(url, title, des)));
            return;
        }

        // 4. 普通文字消息（Emoji 转换 + 超链接识别）
        RenderRichTextWithEmojisAndLinks(tb, rawText);
    }

    /// <summary>
    /// 无图时的纯文本动画表情胶囊：与气泡高度协调，深浅色均清晰。
    /// </summary>
    private static UIElement CreateSimpleEmojiPill()
    {
        bool isLight = ThemeManager.IsLightMode;

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center
        };

        var tb = new TextBlock
        {
            Text = "🎭 动画表情",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            VerticalAlignment = VerticalAlignment.Center
        };

        border.Child = tb;
        return border;
    }

    /// <summary>
    /// 将普通文本渲染为带彩色 Emoji 和超链接的富文本。
    /// </summary>
    private static void RenderRichTextWithEmojisAndLinks(TextBlock tb, string rawText)
    {
        // 先替换微信小黄脸表情为彩色 Emoji
        string text = WeChatEmojiTable.ReplaceWeChatEmojis(rawText);

        var matches = s_urlRegex.Matches(text);
        if (matches.Count == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        int lastIdx = 0;
        foreach (Match m in matches)
        {
            if (m.Index > lastIdx)
            {
                tb.Inlines.Add(new Run(text[lastIdx..m.Index]));
            }

            string url = m.Value;
            var link = new Hyperlink(new Run(url))
            {
                Cursor = Cursors.Hand,
                ToolTip = "点击在浏览器中打开: " + url
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) => OpenBrowser(url);

            tb.Inlines.Add(link);
            lastIdx = m.Index + m.Length;
        }

        if (lastIdx < text.Length)
        {
            tb.Inlines.Add(new Run(text[lastIdx..]));
        }
    }

    /// <summary>
    /// 构建斗图大表情包内嵌控件（支持本地直接读取与 CDN 异步下载）。
    /// </summary>
    private static UIElement CreateEmojiBubbleElement(string md5, string cdnUrl)
    {
        bool isLight = ThemeManager.IsLightMode;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0x15, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            Padding = new Thickness(4),
            MaxWidth = 130,
            MaxHeight = 130,
            Cursor = Cursors.Hand,
            ToolTip = "自定义表情包（点击查看大图）"
        };

        var grid = new Grid();
        var placeholder = new TextBlock
        {
            Text = "🎭 [动画表情]",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6))
        };
        grid.Children.Add(placeholder);

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 120,
            MaxHeight = 120,
            Visibility = Visibility.Collapsed
        };
        grid.Children.Add(image);
        border.Child = grid;

        string? loadedPath = null;

        // 异步加载表情包
        _ = Task.Run(async () =>
        {
            string? localPath = await WeChatCdnMediaDownloader.EnsureEmojiCachedAsync(md5, cdnUrl, s_cachedAccountRoot);
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                loadedPath = localPath;
                border.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(localPath, UriKind.Absolute);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();

                        image.Source = bi;
                        image.Visibility = Visibility.Visible;
                        placeholder.Visibility = Visibility.Collapsed;

                        // 图片加载成功后移除外框背景，直接呈现纯粹表情
                        border.BorderThickness = new Thickness(0);
                        border.Background = Brushes.Transparent;
                    }
                    catch
                    {
                        // 降级为清晰占位
                    }
                });
            }
        });

        border.MouseLeftButtonUp += (_, _) =>
        {
            if (!string.IsNullOrEmpty(loadedPath) && File.Exists(loadedPath))
            {
                OpenFileInDefaultViewer(loadedPath);
            }
        };

        return border;
    }

    /// <summary>
    /// 构建图片气泡控件（支持单字节 XOR 解密与点击定位，深浅色自适应）。
    /// </summary>
    private static UIElement CreateImageBubbleElement(string md5)
    {
        bool isLight = ThemeManager.IsLightMode;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0x15, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            Padding = new Thickness(6),
            MaxWidth = 230,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 210,
            MaxHeight = 210,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(image);

        var placeholder = new TextBlock
        {
            Text = "🖼️ [图片] 点击查看 / 定位微信目录 ↗",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A))
                : new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            ToolTip = "点击定位微信本地图片存储目录"
        };
        stack.Children.Add(placeholder);

        border.Child = stack;

        string? resolvedImagePath = null;

        // 异步尝试解密本地图片
        _ = Task.Run(() =>
        {
            string? localImage = WeChatDatImageDecoder.TryResolveImage(md5, null, s_cachedAccountRoot);
            if (!string.IsNullOrEmpty(localImage) && File.Exists(localImage))
            {
                resolvedImagePath = localImage;
                border.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(localImage, UriKind.Absolute);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();

                        image.Source = bi;
                        image.Visibility = Visibility.Visible;
                        placeholder.Text = "🔍 点击查看大图";
                        placeholder.FontSize = 10;
                        placeholder.Foreground = isLight
                            ? new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69))
                            : new SolidColorBrush(Colors.LightGray);

                        border.BorderThickness = new Thickness(0);
                        border.Background = Brushes.Transparent;
                    }
                    catch
                    {
                        // 降级为占位
                    }
                });
            }
        });

        border.MouseLeftButtonUp += (_, _) =>
        {
            if (!string.IsNullOrEmpty(resolvedImagePath) && File.Exists(resolvedImagePath))
            {
                OpenFileInDefaultViewer(resolvedImagePath);
            }
            else
            {
                string? imgDir = WeChatDatImageDecoder.GetImageStorageDirectory(s_cachedAccountRoot);
                if (!string.IsNullOrEmpty(imgDir) && Directory.Exists(imgDir))
                {
                    OpenExplorerDirectory(imgDir);
                }
            }
        };

        return border;
    }

    /// <summary>
    /// 构建微信分享卡片内嵌控件（深浅色自适应）。
    /// </summary>
    private static UIElement CreateShareCardElement(string url, string title, string des)
    {
        bool isLight = ThemeManager.IsLightMode;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = isLight
                ? new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC))
                : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = isLight
                ? new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1))
                : new SolidColorBrush(Color.FromArgb(60, 147, 197, 253)),
            Padding = new Thickness(8, 6, 8, 6),
            MaxWidth = 300,
            Cursor = Cursors.Hand,
            ToolTip = "点击在浏览器中打开: " + url
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var badgeText = new TextBlock
        {
            Text = "🌐 微信分享",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
                : new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            Margin = new Thickness(0, 0, 0, 2)
        };
        stack.Children.Add(badgeText);

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A))
                : new SolidColorBrush(Colors.White),
            LineHeight = 16
        };
        stack.Children.Add(titleTb);

        if (!string.IsNullOrWhiteSpace(des))
        {
            var desTb = new TextBlock
            {
                Text = des,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                Foreground = isLight
                    ? new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69))
                    : new SolidColorBrush(Color.FromRgb(200, 200, 210)),
                Margin = new Thickness(0, 3, 0, 0)
            };
            stack.Children.Add(desTb);
        }

        var hintTb = new TextBlock
        {
            Text = "点击在浏览器中打开 ↗",
            FontSize = 9.5,
            Foreground = isLight
                ? new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69))
                : new SolidColorBrush(Color.FromRgb(110, 231, 183)),
            Margin = new Thickness(0, 4, 0, 0)
        };
        stack.Children.Add(hintTb);

        border.Child = stack;

        border.MouseLeftButtonUp += (_, _) => OpenBrowser(url);

        return border;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略打开浏览器异常
        }
    }

    private static void OpenFileInDefaultViewer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch
        {
            // 忽略看图异常
        }
    }

    private static void OpenExplorerDirectory(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略打开目录异常
        }
    }
}
