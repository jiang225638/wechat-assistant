using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Layout;

/// <summary>
/// 悬浮窗贴边吸附布局计算（纯函数，物理像素，便于单元测试）。
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// 依据目标窗口矩形计算悬浮窗左上角坐标，使其贴附在目标左/右缘外侧。
    /// </summary>
    /// <param name="target">目标窗口矩形（物理像素）。</param>
    /// <param name="overlay">悬浮窗尺寸（物理像素）。</param>
    /// <param name="side">贴附边（右/左）。</param>
    /// <param name="gap">与目标边缘的间距（物理像素，可为负表示轻微重叠）。</param>
    /// <param name="alignTop">true=顶部与目标对齐；false=相对目标垂直居中。</param>
    public static (int X, int Y) ComputeSnapPosition(
        WindowBounds target,
        OverlaySize overlay,
        SnapSide side,
        int gap = 0,
        bool alignTop = true)
    {
        int x = side switch
        {
            SnapSide.Left => target.Left - overlay.Width - gap,
            _ => target.Right + gap,
        };

        int y = alignTop
            ? target.Top
            : target.Top + (target.Height - overlay.Height) / 2;

        return (x, y);
    }
}
