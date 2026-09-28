using System.Diagnostics;
using System.Text;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 通过进程名定位微信主窗口。
/// 微信 4.x 进程名为 <c>Weixin.exe</c>（且为多进程：一个主进程 + 若干辅助进程），3.x 为 <c>WeChat.exe</c>；两者都尝试。
/// 不依赖易变的窗口类名，按优先级打分挑选主窗口：
/// ① 有标题（真正的主窗口标题为当前聊天/昵称，辅助窗口多为空标题）优先；
/// ② 非最小化优先（注意 <c>IsWindowVisible</c> 对最小化窗口仍返回 true，故需单独用 <c>IsIconic</c> 判定）；
/// ③ 面积最大（排除小弹窗/悬浮按钮）。
/// 如此即便微信被最小化，也能锁定“那个有标题的主窗口”并正确上报 <see cref="WindowInfo.IsMinimized"/>，
/// 而不会误选到空的辅助窗口。
/// </summary>
public sealed class WeChatWindowLocator : IWeChatWindowLocator
{
    private static readonly string[] ProcessNames = { "Weixin", "WeChat" };

    public WindowInfo? FindWeChatMainWindow()
    {
        var pids = CollectWeChatPids();
        if (pids.Count == 0)
        {
            return null;
        }

        WindowInfo? best = null;
        bool bestHasTitle = false;
        bool bestNotMinimized = false;
        long bestArea = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains(pid))
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area <= 0)
            {
                return true;
            }

            bool minimized = NativeMethods.IsIconic(hwnd);
            var bounds = new WindowBounds(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
            string title = GetText(hwnd, NativeMethods.GetWindowText);
            bool hasTitle = !string.IsNullOrWhiteSpace(title);
            bool notMinimized = !minimized;

            // 优先级：有标题 > 非最小化 > 面积大
            bool better = best is null
                || (hasTitle && !bestHasTitle)
                || (hasTitle == bestHasTitle && notMinimized && !bestNotMinimized)
                || (hasTitle == bestHasTitle && notMinimized == bestNotMinimized && area > bestArea);

            if (better)
            {
                best = new WindowInfo(
                    hwnd,
                    title,
                    GetText(hwnd, NativeMethods.GetClassName),
                    bounds,
                    minimized,
                    true);
                bestHasTitle = hasTitle;
                bestNotMinimized = notMinimized;
                bestArea = area;
            }

            return true;
        }, nint.Zero);

        return best;
    }

    private static HashSet<uint> CollectWeChatPids()
    {
        var pids = new HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    pids.Add((uint)p.Id);
                }
                catch
                {
                    // 进程可能已退出，忽略
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return pids;
    }

    private static string GetText(nint hwnd, Func<nint, StringBuilder, int, int> getter)
    {
        var sb = new StringBuilder(512);
        getter(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
