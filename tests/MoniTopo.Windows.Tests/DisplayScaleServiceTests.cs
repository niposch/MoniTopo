using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplayScaleServiceTests
{
    [Fact]
    public void RelativeDpiIndexesMapToWindowsScalePercentages()
    {
        var native = new FakeDisplayNative
        {
            DpiPacket = new NativeDpiScaleGet
            {
                MinimumRelativeScale = -2,
                CurrentRelativeScale = 1,
                MaximumRelativeScale = 5,
            },
        };
        var service = new DisplayScaleService(native);

        var result = service.Query(new DisplaySourceAddress(1, 0, 2));

        Assert.True(result.IsSupported);
        Assert.Equal(175, result.CurrentPercent);
        Assert.Equal(150, result.RecommendedPercent);
        Assert.Equal([100, 125, 150, 175, 200, 225, 250, 300], result.SupportedPercentages);
    }

    [Fact]
    public void UnsupportedDpiPacketReturnsCompatibilityError()
    {
        var service = new DisplayScaleService(new FakeDisplayNative { DpiErrorCode = 50 });

        var result = service.Query(new DisplaySourceAddress(1, 0, 2));

        Assert.False(result.IsSupported);
        Assert.Contains("not supported", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutOfRangeDpiPacketIsContained()
    {
        var native = new FakeDisplayNative
        {
            DpiPacket = new NativeDpiScaleGet
            {
                MinimumRelativeScale = -20,
                CurrentRelativeScale = 0,
                MaximumRelativeScale = 20,
            },
        };

        var result = new DisplayScaleService(native).Query(new DisplaySourceAddress(1, 0, 2));

        Assert.False(result.IsSupported);
        Assert.Contains("unrecognized", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesiredPercentageMapsBackToRelativeSetterIndex()
    {
        var packet = new NativeDpiScaleGet
        {
            MinimumRelativeScale = -2,
            CurrentRelativeScale = 0,
            MaximumRelativeScale = 5,
        };

        var supported = DisplayScaleService.TryResolveRelativeScale(packet, 175, out var relativeScale);
        var unsupported = DisplayScaleService.TryResolveRelativeScale(packet, 500, out _);
        var malformedPacket = new NativeDpiScaleGet
        {
            MinimumRelativeScale = -20,
            CurrentRelativeScale = 0,
            MaximumRelativeScale = 20,
        };
        var malformed = DisplayScaleService.TryResolveRelativeScale(malformedPacket, 100, out _);

        Assert.True(supported);
        Assert.Equal(1, relativeScale);
        Assert.False(unsupported);
        Assert.False(malformed);
    }
}
