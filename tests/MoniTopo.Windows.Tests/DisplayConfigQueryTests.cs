using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplayConfigQueryTests
{
    [Fact]
    public void QueryRetriesWhenDisplayBuffersChange()
    {
        var api = new ChangingBufferApi();

        var snapshot = new DisplayConfigNativeFacade(api).Query(flags: 1);

        Assert.Equal(2, api.BufferSizeCalls);
        Assert.Equal(2, api.QueryCalls);
        Assert.Single(snapshot.Paths);
        Assert.Single(snapshot.Modes);
    }

    [Fact]
    public void NativeErrorIsTranslatedWithoutRawCodeAsPrimaryMessage()
    {
        var exception = Assert.Throws<DisplayCaptureException>(() =>
            new DisplayConfigNativeFacade(new FailingQueryApi()).Query(flags: 1));

        Assert.Equal(5, exception.NativeErrorCode);
        Assert.DoesNotContain("5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Native error 5", exception.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows11HdrQueryUsesUserEnabledBitNotGenericAdvancedColorActivity()
    {
        var deviceInfo = new FakeDeviceInfoApi { AdvancedColorInfo2Value = 0x12 };
        var facade = new DisplayConfigNativeFacade(new UnusedQueryApi(), deviceInfo);

        var state = facade.GetHdrState(new NativeAdapterId(1, 0), 2);

        Assert.True(state.IsSupported);
        Assert.False(state.IsEnabled);
        Assert.Equal(15, deviceInfo.Info2RequestType);
        Assert.False(deviceInfo.LegacyCalled);
    }

    [Fact]
    public void Windows11HdrQueryReportsUserEnabledState()
    {
        var deviceInfo = new FakeDeviceInfoApi { AdvancedColorInfo2Value = 0x30 };
        var facade = new DisplayConfigNativeFacade(new UnusedQueryApi(), deviceInfo);

        var state = facade.GetHdrState(new NativeAdapterId(1, 0), 2);

        Assert.True(state.IsSupported);
        Assert.True(state.IsEnabled);
    }

    [Fact]
    public void HdrQueryFallsBackToLegacyAdvancedColorOnOlderWindows()
    {
        var deviceInfo = new FakeDeviceInfoApi
        {
            Info2Result = DisplayConfigNativeFacade.ErrorInvalidParameter,
            LegacyValue = 0x3,
        };
        var facade = new DisplayConfigNativeFacade(new UnusedQueryApi(), deviceInfo);

        var state = facade.GetHdrState(new NativeAdapterId(1, 0), 2);

        Assert.True(state.IsSupported);
        Assert.True(state.IsEnabled);
        Assert.True(deviceInfo.LegacyCalled);
    }

    private sealed class ChangingBufferApi : IDisplayConfigQueryApi
    {
        public int BufferSizeCalls { get; private set; }

        public int QueryCalls { get; private set; }

        public int GetBufferSizes(uint flags, out uint pathCount, out uint modeCount)
        {
            BufferSizeCalls++;
            pathCount = 1;
            modeCount = 1;
            return 0;
        }

        public int Query(uint flags, ref uint pathCount, NativePathInfo[] paths, ref uint modeCount, NativeModeInfo[] modes)
        {
            QueryCalls++;
            return QueryCalls == 1 ? DisplayConfigNativeFacade.ErrorInsufficientBuffer : 0;
        }
    }

    private sealed class FailingQueryApi : IDisplayConfigQueryApi
    {
        public int GetBufferSizes(uint flags, out uint pathCount, out uint modeCount)
        {
            pathCount = 0;
            modeCount = 0;
            return 5;
        }

        public int Query(uint flags, ref uint pathCount, NativePathInfo[] paths, ref uint modeCount, NativeModeInfo[] modes) =>
            throw new InvalidOperationException();
    }

    private sealed class UnusedQueryApi : IDisplayConfigQueryApi
    {
        public int GetBufferSizes(uint flags, out uint pathCount, out uint modeCount) =>
            throw new InvalidOperationException();

        public int Query(uint flags, ref uint pathCount, NativePathInfo[] paths, ref uint modeCount, NativeModeInfo[] modes) =>
            throw new InvalidOperationException();
    }

    private sealed class FakeDeviceInfoApi : IDisplayConfigDeviceInfoApi
    {
        internal uint AdvancedColorInfo2Value { get; init; }

        internal uint LegacyValue { get; init; }

        internal int Info2Result { get; init; }

        internal int Info2RequestType { get; private set; }

        internal bool LegacyCalled { get; private set; }

        public int GetAdvancedColorInfo(ref NativeAdvancedColorInfo packet)
        {
            LegacyCalled = true;
            packet.Value = LegacyValue;
            return 0;
        }

        public int GetAdvancedColorInfo2(ref NativeAdvancedColorInfo2 packet)
        {
            Info2RequestType = packet.Header.Type;
            packet.Value = AdvancedColorInfo2Value;
            return Info2Result;
        }
    }
}
