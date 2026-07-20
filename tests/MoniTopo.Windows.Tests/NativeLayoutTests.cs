using System.Runtime.InteropServices;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class NativeLayoutTests
{
    [Fact]
    public void CriticalCcdStructuresMatchWindowsSdkLayouts()
    {
        Assert.Equal(8, Marshal.SizeOf<NativeAdapterId>());
        Assert.Equal(20, Marshal.SizeOf<NativePathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<NativePathTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<NativePathInfo>());
        Assert.Equal(64, Marshal.SizeOf<NativeModeInfo>());
        Assert.Equal(20, Marshal.SizeOf<NativeDeviceInfoHeader>());
        Assert.Equal(420, Marshal.SizeOf<NativeTargetDeviceName>());
        Assert.Equal(80, Marshal.SizeOf<NativeTargetPreferredMode>());
        Assert.Equal(32, Marshal.SizeOf<NativeAdvancedColorInfo>());
        Assert.Equal(32, Marshal.SizeOf<NativeDpiScaleGet>());
        Assert.Equal(24, Marshal.SizeOf<NativeDpiScaleSet>());
    }

    [Fact]
    public void CriticalCcdFieldOffsetsMatchWindowsSdkLayouts()
    {
        Assert.Equal(16, Marshal.OffsetOf<NativeModeInfo>(nameof(NativeModeInfo.Mode)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativePathSourceInfo>(nameof(NativePathSourceInfo.ModeInfoIndex)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<NativePathTargetInfo>(nameof(NativePathTargetInfo.RefreshRate)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<NativePathInfo>(nameof(NativePathInfo.Flags)).ToInt32());
    }

    [Fact]
    public void VirtualModeIndexesDecodeFromDocumentedBitfieldUnion()
    {
        var source = new NativePathSourceInfo { ModeInfoIndex = (7u << 16) | 3u };
        var target = new NativePathTargetInfo { ModeInfoIndex = (11u << 16) | 5u };

        Assert.Equal(3u, source.CloneGroupId);
        Assert.Equal(7u, source.SourceModeInfoIndex);
        Assert.Equal(11u, target.TargetModeInfoIndex);
    }
}
