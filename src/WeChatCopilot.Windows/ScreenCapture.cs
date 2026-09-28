using System.Runtime.InteropServices;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 用 GDI BitBlt 从屏幕抓取一块物理像素区域，输出中性的 <see cref="CapturedImage"/>（顶向下 32bpp BGRA）。
/// 零外部依赖；坐标与 GetWindowRect 一致（进程已 PerMonitorV2 DPI 感知，均为物理像素）。
/// 注意：抓取的是“屏幕上呈现”的像素，若目标区域被其它窗口遮挡或超出屏幕，将捕获到遮挡内容/黑边。
/// </summary>
public static class ScreenCapture
{
    /// <summary>抓取指定物理像素区域；区域非法或任一 GDI 调用失败时返回 <c>null</c>。</summary>
    public static CapturedImage? CaptureRegion(WindowBounds region)
    {
        if (!region.IsValid)
        {
            return null;
        }

        int width = region.Width;
        int height = region.Height;

        nint screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            return null;
        }

        nint memDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint oldBitmap = nint.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero)
            {
                return null;
            }

            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero)
            {
                return null;
            }

            oldBitmap = NativeMethods.SelectObject(memDc, bitmap);

            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, region.X, region.Y, NativeMethods.SRCCOPY))
            {
                return null;
            }

            // 取回像素前先取消选中位图（GetDIBits 要求目标位图未选入 DC）。
            NativeMethods.SelectObject(memDc, oldBitmap);
            oldBitmap = nint.Zero;

            byte[] pixels = new byte[width * height * 4];
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负值 = 顶向下行序
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            int lines = NativeMethods.GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref header, NativeMethods.DIB_RGB_COLORS);
            if (lines != height)
            {
                return null;
            }

            // GDI 抓屏的 alpha 通道未定义（常为 0）；置为不透明，避免下游按预乘 alpha 处理时把 RGB 清零。
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            return new CapturedImage(width, height, pixels);
        }
        finally
        {
            if (oldBitmap != nint.Zero && memDc != nint.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }

            if (bitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memDc != nint.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    /// <summary>抓取指定窗口的整体矩形（含标题栏与边框）。</summary>
    public static CapturedImage? CaptureWindow(nint hwnd)
    {
        if (hwnd == nint.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return CaptureRegion(bounds);
    }
}
