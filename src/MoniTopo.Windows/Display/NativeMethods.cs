using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    internal static unsafe partial int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        NativePathInfo* paths,
        ref uint modeCount,
        NativeModeInfo* modes,
        nint currentTopologyId);

    [LibraryImport("user32.dll")]
    internal static unsafe partial int SetDisplayConfig(
        uint pathCount,
        NativePathInfo* paths,
        uint modeCount,
        NativeModeInfo* modes,
        uint flags);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static partial int GetTargetDeviceName(ref NativeTargetDeviceName packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static partial int GetSourceDeviceName(ref NativeSourceDeviceName packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static partial int GetTargetPreferredMode(ref NativeTargetPreferredMode packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static partial int GetAdvancedColorInfo(ref NativeAdvancedColorInfo packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static partial int GetDpiScale(ref NativeDpiScaleGet packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    internal static partial int SetDpiScale(ref NativeDpiScaleSet packet);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    internal static partial int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayDevices(
        nint deviceName,
        uint deviceIndex,
        ref NativeDisplayDevice displayDevice,
        uint flags);
}
