using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

public class OverlayLayoutTests
{
    [Fact]
    public void SnapRight_AlignTop_PlacesAtRightEdge()
    {
        var target = new WindowBounds(100, 50, 800, 600);
        var overlay = new OverlaySize(340, 220);

        var (x, y) = OverlayLayout.ComputeSnapPosition(target, overlay, SnapSide.Right, gap: 0, alignTop: true);

        Assert.Equal(900, x); // 100 + 800
        Assert.Equal(50, y);  // 顶部对齐
    }

    [Fact]
    public void SnapLeft_AlignTop_PlacesAtLeftEdgeMinusOverlayWidth()
    {
        var target = new WindowBounds(100, 50, 800, 600);
        var overlay = new OverlaySize(340, 220);

        var (x, y) = OverlayLayout.ComputeSnapPosition(target, overlay, SnapSide.Left, gap: 0, alignTop: true);

        Assert.Equal(-240, x); // 100 - 340
        Assert.Equal(50, y);
    }

    [Fact]
    public void SnapRight_CenterVertically_ComputesMidpoint()
    {
        var target = new WindowBounds(0, 0, 800, 600);
        var overlay = new OverlaySize(340, 200);

        var (_, y) = OverlayLayout.ComputeSnapPosition(target, overlay, SnapSide.Right, gap: 8, alignTop: false);

        Assert.Equal(200, y); // (600 - 200) / 2
    }

    [Fact]
    public void Gap_IsApplied_OnRightSide()
    {
        var target = new WindowBounds(0, 0, 800, 600);
        var overlay = new OverlaySize(340, 200);

        var (x, _) = OverlayLayout.ComputeSnapPosition(target, overlay, SnapSide.Right, gap: 12, alignTop: true);

        Assert.Equal(812, x); // 800 + 12
    }
}
