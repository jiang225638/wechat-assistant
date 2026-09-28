namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一次屏幕区域截图的中性表示：顶向下（top-down）32bpp BGRA 原始像素。
/// 由 GDI BitBlt 采集，供 OCR 引擎消费，避免耦合任何具体位图库。
/// </summary>
/// <param name="Width">像素宽度。</param>
/// <param name="Height">像素高度。</param>
/// <param name="BgraPixels">长度为 Width*Height*4 的 BGRA 字节缓冲（行序自上而下）。</param>
public sealed record CapturedImage(int Width, int Height, byte[] BgraPixels)
{
    public bool IsValid => Width > 0 && Height > 0 && BgraPixels.Length == Width * Height * 4;

    /// <summary>
    /// 最近邻整数倍放大（factor &lt;= 1 时原样返回）。
    /// Windows.Media.Ocr 对偏小的中文字误识率高（如把"你"识成"亻 尔"），
    /// 放大 2x 后再识别可显著提升准确率；返回新实例，不修改原图。
    /// </summary>
    public CapturedImage ScaleNearest(int factor)
    {
        if (factor <= 1 || !IsValid)
        {
            return this;
        }

        int w = Width * factor;
        int h = Height * factor;
        var dst = new byte[w * h * 4];
        int srcStride = Width * 4;

        for (int y = 0; y < h; y++)
        {
            int srcRow = (y / factor) * srcStride;
            int dstRow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int si = srcRow + (x / factor) * 4;
                int di = dstRow + x * 4;
                dst[di] = BgraPixels[si];
                dst[di + 1] = BgraPixels[si + 1];
                dst[di + 2] = BgraPixels[si + 2];
                dst[di + 3] = BgraPixels[si + 3];
            }
        }

        return new CapturedImage(w, h, dst);
    }
}
