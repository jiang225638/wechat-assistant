using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Tests;

public class ChatRegionCropperTests
{
    [Fact]
    public void ComputeChatRegion_DefaultTrims_CutsSidebarHeaderAndInput()
    {
        var window = new WindowBounds(100, 50, 1000, 800);

        var region = ChatRegionCropper.ComputeChatRegion(window);

        // trimLeft=300, trimTop=80, trimRight=0, trimBottom=200
        Assert.Equal(new WindowBounds(400, 130, 700, 520), region);
    }

    [Fact]
    public void ComputeChatRegion_ZeroTrims_ReturnsWholeWindow()
    {
        var window = new WindowBounds(10, 20, 800, 600);
        var opts = new ChatRegionOptions
        {
            TrimLeftRatio = 0,
            TrimTopRatio = 0,
            TrimRightRatio = 0,
            TrimBottomRatio = 0
        };

        var region = ChatRegionCropper.ComputeChatRegion(window, opts);

        Assert.Equal(window, region);
    }

    [Fact]
    public void ComputeChatRegion_AllFourTrims_AppliedCorrectly()
    {
        var window = new WindowBounds(0, 0, 1000, 1000);
        var opts = new ChatRegionOptions
        {
            TrimLeftRatio = 0.1,
            TrimTopRatio = 0.2,
            TrimRightRatio = 0.3,
            TrimBottomRatio = 0.4
        };

        var region = ChatRegionCropper.ComputeChatRegion(window, opts);

        // x=100, y=200, w=1000-100-300=600, h=1000-200-400=400
        Assert.Equal(new WindowBounds(100, 200, 600, 400), region);
    }

    [Fact]
    public void ComputeChatRegion_ExtremeRatios_ClampToAtLeastOnePixel()
    {
        var window = new WindowBounds(0, 0, 100, 100);
        var opts = new ChatRegionOptions
        {
            TrimLeftRatio = 0.9,
            TrimRightRatio = 0.9,
            TrimTopRatio = 0.9,
            TrimBottomRatio = 0.9
        };

        var region = ChatRegionCropper.ComputeChatRegion(window, opts);

        Assert.True(region.IsValid);
        Assert.True(region.Width >= 1);
        Assert.True(region.Height >= 1);
    }

    [Fact]
    public void ComputeChatRegion_InvalidWindow_ReturnedAsIs()
    {
        var window = new WindowBounds(0, 0, 0, 0);

        var region = ChatRegionCropper.ComputeChatRegion(window);

        Assert.Equal(window, region);
        Assert.False(region.IsValid);
    }
}
