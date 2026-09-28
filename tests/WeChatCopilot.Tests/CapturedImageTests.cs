using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

public class CapturedImageTests
{
    private static CapturedImage Img(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = (byte)i;         // B
            px[i * 4 + 1] = (byte)(i * 2); // G
            px[i * 4 + 2] = (byte)(i * 3); // R
            px[i * 4 + 3] = 255;         // A
        }

        return new CapturedImage(w, h, px);
    }

    [Fact]
    public void ScaleNearest_FactorOne_ReturnsSameInstance()
    {
        var img = Img(2, 2);
        Assert.Same(img, img.ScaleNearest(1));
    }

    [Fact]
    public void ScaleNearest_FactorTwo_DoublesDimensions()
    {
        var img = Img(2, 2);
        var scaled = img.ScaleNearest(2);
        Assert.Equal(4, scaled.Width);
        Assert.Equal(4, scaled.Height);
        Assert.True(scaled.IsValid);
    }

    [Fact]
    public void ScaleNearest_FactorTwo_ReplicatesPixels()
    {
        var img = Img(2, 2);
        var scaled = img.ScaleNearest(2);

        // 源 (0,0) 的 B 值 = 0；放大后 (0,0)..(1,1) 都应为 0
        Assert.Equal(0, scaled.BgraPixels[0]);
        Assert.Equal(0, scaled.BgraPixels[4]);
        Assert.Equal(0, scaled.BgraPixels[4 * 4]);
        Assert.Equal(0, scaled.BgraPixels[4 * 4 + 4]);

        // 源 (1,1) 的索引=3，B=3；放大后右下角 (3,3) 应为 3
        int last = (3 * 4 + 3) * 4;
        Assert.Equal(3, scaled.BgraPixels[last]);
    }

    [Fact]
    public void ScaleNearest_InvalidImage_ReturnsSameInstance()
    {
        var bad = new CapturedImage(0, 0, Array.Empty<byte>());
        Assert.Same(bad, bad.ScaleNearest(2));
    }
}
