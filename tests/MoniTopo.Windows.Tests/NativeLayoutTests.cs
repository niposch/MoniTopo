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
        Assert.Equal(40, Marshal.SizeOf<NativeDesktopImageInfo>());
        Assert.Equal(16, Marshal.SizeOf<NativeRect>());
        Assert.Equal(20, Marshal.SizeOf<NativeDeviceInfoHeader>());
        Assert.Equal(420, Marshal.SizeOf<NativeTargetDeviceName>());
        Assert.Equal(80, Marshal.SizeOf<NativeTargetPreferredMode>());
        Assert.Equal(32, Marshal.SizeOf<NativeAdvancedColorInfo>());
        Assert.Equal(36, Marshal.SizeOf<NativeAdvancedColorInfo2>());
        Assert.Equal(24, Marshal.SizeOf<NativeAdvancedColorStateSet>());
        Assert.Equal(24, Marshal.SizeOf<NativeHdrStateSet>());
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
        Assert.Equal(8, Marshal.OffsetOf<NativeDesktopImageInfo>(nameof(NativeDesktopImageInfo.DesktopImageRegion)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeDesktopImageInfo>(nameof(NativeDesktopImageInfo.DesktopImageClip)).ToInt32());
    }

    [Fact]
    public void VirtualModeIndexesDecodeFromDocumentedBitfieldUnion()
    {
        var source = new NativePathSourceInfo { ModeInfoIndex = (7u << 16) | 3u };
        var target = new NativePathTargetInfo { ModeInfoIndex = (11u << 16) | 5u };

        Assert.Equal(3u, source.CloneGroupId);
        Assert.Equal(7u, source.SourceModeInfoIndex);
        Assert.Equal(5u, target.DesktopModeInfoIndex);
        Assert.Equal(11u, target.TargetModeInfoIndex);
    }

    [Fact]
    public void PathModeIndexesResolveDirectAndPackedDriverRepresentations()
    {
        var adapter = new NativeAdapterId(1, 0);
        var modes = new[]
        {
            new NativeModeInfo { InfoType = 2, AdapterId = adapter, Id = 2 },
            new NativeModeInfo { InfoType = 1, AdapterId = adapter, Id = 1 },
        };
        var direct = new NativePathInfo
        {
            SourceInfo = new NativePathSourceInfo { AdapterId = adapter, Id = 1, ModeInfoIndex = 1 },
            TargetInfo = new NativePathTargetInfo { AdapterId = adapter, Id = 2, ModeInfoIndex = 0 },
        };
        var packedWithoutCapabilityFlag = new NativePathInfo
        {
            SourceInfo = new NativePathSourceInfo
            {
                AdapterId = adapter,
                Id = 1,
                ModeInfoIndex = (1u << 16) | 0xFFFFu,
            },
            TargetInfo = new NativePathTargetInfo
            {
                AdapterId = adapter,
                Id = 2,
                ModeInfoIndex = 0x0000FFFFu,
            },
        };

        Assert.True(direct.TryResolveSourceModeInfoIndex(modes, out var directSource));
        Assert.True(direct.TryResolveTargetModeInfoIndex(modes, out var directTarget));
        Assert.True(packedWithoutCapabilityFlag.TryResolveSourceModeInfoIndex(modes, out var packedSource));
        Assert.True(packedWithoutCapabilityFlag.TryResolveTargetModeInfoIndex(modes, out var packedTarget));
        Assert.Equal(1u, directSource);
        Assert.Equal(0u, directTarget);
        Assert.Equal(1u, packedSource);
        Assert.Equal(0u, packedTarget);
    }

    [Fact]
    public void DesktopImageIndexRequiresCorrectTypeAdapterAndEndpoint()
    {
        var adapter = new NativeAdapterId(1, 0);
        var modes = new[]
        {
            new NativeModeInfo { InfoType = 2, AdapterId = adapter, Id = 2 },
            new NativeModeInfo { InfoType = 3, AdapterId = adapter, Id = 2 },
        };
        var path = new NativePathInfo
        {
            TargetInfo = new NativePathTargetInfo { AdapterId = adapter, Id = 2, ModeInfoIndex = 1 },
            Flags = NativeDisplayConfigPathFlags.SupportVirtualMode,
        };

        Assert.True(path.TryResolveDesktopModeInfoIndex(modes, out var desktop));
        Assert.Equal(1u, desktop);
        modes[1].Id = 3;
        Assert.False(path.TryResolveDesktopModeInfoIndex(modes, out _));
        modes[1].Id = 2;
        modes[1].AdapterId = new NativeAdapterId(2, 0);
        Assert.False(path.TryResolveDesktopModeInfoIndex(modes, out _));
    }
}
