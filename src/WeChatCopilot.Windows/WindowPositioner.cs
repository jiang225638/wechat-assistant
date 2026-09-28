using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 以物理像素坐标定位窗口，规避 WPF 的 DIP/DPI 换算，保证与 GetWindowRect 坐标一致。
/// </summary>
public static class WindowPositioner
{
    /// <summary>将窗口移动到指定物理像素坐标并保持置顶（不改变尺寸、不激活）。</summary>
    public static void MoveToTopmost(nint hwnd, int x, int y)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
