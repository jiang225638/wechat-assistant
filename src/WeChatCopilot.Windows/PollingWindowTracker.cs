using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 以定时器轮询方式跟踪目标窗口位置/状态变化（M0 探针用）。
/// 仅在发生变化时触发 <see cref="TargetChanged"/>，事件可能在线程池线程回调。
/// 后续（M1）可替换为 SetWinEventHook 事件驱动实现，接口不变。
/// </summary>
public sealed class PollingWindowTracker : IWindowTracker
{
    private readonly int _intervalMs;
    private System.Threading.Timer? _timer;
    private WindowInfo? _target;
    private WindowInfo? _last;

    public event Action<WindowInfo?>? TargetChanged;

    public PollingWindowTracker(int intervalMs = 60)
    {
        _intervalMs = intervalMs;
    }

    public void Start(WindowInfo target)
    {
        _target = target;
        _last = null;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, _intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public WindowInfo? GetCurrent() => _target;

    private void Tick()
    {
        var t = _target;
        if (t is null)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(t.Handle, out var rect))
        {
            _target = null;
            _last = null;
            TargetChanged?.Invoke(null);
            return;
        }

        var bounds = new WindowBounds(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);

        bool minimized = NativeMethods.IsIconic(t.Handle);
        bool visible = NativeMethods.IsWindowVisible(t.Handle);

        _target = t with { Bounds = bounds, IsMinimized = minimized, IsVisible = visible };

        if (_last is not null
            && _last.Bounds == bounds
            && _last.IsMinimized == minimized
            && _last.IsVisible == visible)
        {
            return; // 无变化，避免无谓的 UI 更新
        }

        _last = _target;
        TargetChanged?.Invoke(_target);
    }

    public void Dispose() => Stop();
}
