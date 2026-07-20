using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

internal sealed record NativeDisplaySnapshot(NativePathInfo[] Paths, NativeModeInfo[] Modes);

internal interface IDisplayConfigNativeFacade
{
    NativeDisplaySnapshot Query(uint flags);

    NativeTargetDeviceName GetTargetName(NativeAdapterId adapterId, uint targetId);

    string GetSourceName(NativeAdapterId adapterId, uint sourceId);

    NativeTargetPreferredMode GetTargetPreferredMode(NativeAdapterId adapterId, uint targetId);

    NativeAdvancedColorInfo GetAdvancedColorInfo(NativeAdapterId adapterId, uint targetId);

    (int ErrorCode, NativeDpiScaleGet Packet) TryGetDpiScale(NativeAdapterId adapterId, uint sourceId);

    bool IsPrimaryGdiDevice(string gdiDeviceName);
}

internal interface IDisplayConfigQueryApi
{
    int GetBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    int Query(uint flags, ref uint pathCount, NativePathInfo[] paths, ref uint modeCount, NativeModeInfo[] modes);
}

internal sealed class User32DisplayConfigQueryApi : IDisplayConfigQueryApi
{
    public int GetBufferSizes(uint flags, out uint pathCount, out uint modeCount) =>
        NativeMethods.GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);

    public unsafe int Query(uint flags, ref uint pathCount, NativePathInfo[] paths, ref uint modeCount, NativeModeInfo[] modes)
    {
        fixed (NativePathInfo* pathPointer = paths)
        fixed (NativeModeInfo* modePointer = modes)
        {
            return NativeMethods.QueryDisplayConfig(
                flags,
                ref pathCount,
                pathPointer,
                ref modeCount,
                modePointer,
                nint.Zero);
        }
    }
}

internal sealed class DisplayConfigNativeFacade : IDisplayConfigNativeFacade
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;
    private const int MaximumQueryAttempts = 5;
    private const uint DisplayDevicePrimary = 0x4;
    private readonly IDisplayConfigQueryApi _queryApi;

    internal DisplayConfigNativeFacade()
        : this(new User32DisplayConfigQueryApi())
    {
    }

    internal DisplayConfigNativeFacade(IDisplayConfigQueryApi queryApi)
    {
        _queryApi = queryApi;
    }

    public NativeDisplaySnapshot Query(uint flags)
    {
        for (var attempt = 0; attempt < MaximumQueryAttempts; attempt++)
        {
            var result = _queryApi.GetBufferSizes(flags, out var pathCount, out var modeCount);
            ThrowIfFailed(result, "Windows could not determine the display snapshot size.");

            var paths = new NativePathInfo[pathCount];
            var modes = new NativeModeInfo[modeCount];
            result = _queryApi.Query(flags, ref pathCount, paths, ref modeCount, modes);

            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            ThrowIfFailed(result, "Windows could not query the display configuration.");
            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return new NativeDisplaySnapshot(paths, modes);
        }

        throw new DisplayCaptureException(
            ErrorInsufficientBuffer,
            "The display configuration kept changing while MoniTopo was reading it. Try again after the displays settle.");
    }

    public NativeTargetDeviceName GetTargetName(NativeAdapterId adapterId, uint targetId)
    {
        var packet = new NativeTargetDeviceName
        {
            Header = CreateHeader(type: 2, Marshal.SizeOf<NativeTargetDeviceName>(), adapterId, targetId),
        };
        ThrowIfFailed(NativeMethods.GetTargetDeviceName(ref packet), "Windows could not read a display name.");
        return packet;
    }

    public string GetSourceName(NativeAdapterId adapterId, uint sourceId)
    {
        var packet = new NativeSourceDeviceName
        {
            Header = CreateHeader(type: 1, Marshal.SizeOf<NativeSourceDeviceName>(), adapterId, sourceId),
        };
        ThrowIfFailed(NativeMethods.GetSourceDeviceName(ref packet), "Windows could not read a display source name.");
        return packet.GdiDeviceName;
    }

    public NativeTargetPreferredMode GetTargetPreferredMode(NativeAdapterId adapterId, uint targetId)
    {
        var packet = new NativeTargetPreferredMode
        {
            Header = CreateHeader(type: 3, Marshal.SizeOf<NativeTargetPreferredMode>(), adapterId, targetId),
        };
        ThrowIfFailed(NativeMethods.GetTargetPreferredMode(ref packet), "Windows could not read a display's preferred mode.");
        return packet;
    }

    public NativeAdvancedColorInfo GetAdvancedColorInfo(NativeAdapterId adapterId, uint targetId)
    {
        var packet = new NativeAdvancedColorInfo
        {
            Header = CreateHeader(type: 9, Marshal.SizeOf<NativeAdvancedColorInfo>(), adapterId, targetId),
        };
        ThrowIfFailed(NativeMethods.GetAdvancedColorInfo(ref packet), "Windows could not read the HDR state.");
        return packet;
    }

    public (int ErrorCode, NativeDpiScaleGet Packet) TryGetDpiScale(NativeAdapterId adapterId, uint sourceId)
    {
        var packet = new NativeDpiScaleGet
        {
            Header = CreateHeader(
                UndocumentedDpiScaleContract.GetRequestType,
                Marshal.SizeOf<NativeDpiScaleGet>(),
                adapterId,
                sourceId),
        };
        var errorCode = NativeMethods.GetDpiScale(ref packet);
        return (errorCode, packet);
    }

    public bool IsPrimaryGdiDevice(string gdiDeviceName)
    {
        for (uint index = 0; ; index++)
        {
            var device = new NativeDisplayDevice { Size = (uint)Marshal.SizeOf<NativeDisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(nint.Zero, index, ref device, flags: 0))
            {
                return false;
            }

            if (string.Equals(device.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return (device.StateFlags & DisplayDevicePrimary) != 0;
            }
        }
    }

    private static NativeDeviceInfoHeader CreateHeader(int type, int size, NativeAdapterId adapterId, uint id) => new()
    {
        Type = type,
        Size = checked((uint)size),
        AdapterId = adapterId,
        Id = id,
    };

    private static void ThrowIfFailed(int errorCode, string message)
    {
        if (errorCode != ErrorSuccess)
        {
            throw new DisplayCaptureException(errorCode, message);
        }
    }
}

public sealed class DisplayCaptureException(int nativeErrorCode, string message) : Exception(message)
{
    public int NativeErrorCode { get; } = nativeErrorCode;
}
