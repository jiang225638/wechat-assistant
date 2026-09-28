using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Layout;

/// <summary>
/// 依据 <see cref="ChatRegionOptions"/> 从微信窗口矩形裁剪出聊天消息区（纯函数、可单测）。
/// 结果仍为物理像素矩形，可直接交给截屏与 OCR。
/// </summary>
public static class ChatRegionCropper
{
    /// <summary>计算聊天消息区矩形；对无效窗口或极端比例做了下限保护（宽高至少为 1）。</summary>
    public static WindowBounds ComputeChatRegion(WindowBounds window, ChatRegionOptions? options = null)
    {
        if (!window.IsValid)
        {
            return window;
        }

        options ??= new ChatRegionOptions();

        int trimLeft = (int)Math.Round(window.Width * Clamp(options.TrimLeftRatio));
        int trimTop = (int)Math.Round(window.Height * Clamp(options.TrimTopRatio));
        int trimRight = (int)Math.Round(window.Width * Clamp(options.TrimRightRatio));
        int trimBottom = (int)Math.Round(window.Height * Clamp(options.TrimBottomRatio));

        int x = window.X + trimLeft;
        int y = window.Y + trimTop;
        int w = window.Width - trimLeft - trimRight;
        int h = window.Height - trimTop - trimBottom;

        return new WindowBounds(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static double Clamp(double ratio) => ratio < 0 ? 0 : ratio > 1 ? 1 : ratio;
}
