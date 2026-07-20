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
}
