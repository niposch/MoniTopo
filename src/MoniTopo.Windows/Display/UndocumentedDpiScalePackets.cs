using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

// Windows does not document these DisplayConfig request packets. Keep the contract isolated here.
// Cross-checked 2026-07-20 against:
// - https://searchfox.org/firefox-main/source/widget/windows/WinUtils.cpp
// - https://github.com/MartinGC94/DisplayConfig/tree/main/src/DisplayConfig/Native/Structs
// - https://github.com/lihas/windows-DPI-scaling-sample/blob/master/DPIHelper/DpiHelper.h
internal static class UndocumentedDpiScaleContract
{
    internal const int GetRequestType = -3;
    internal const int SetRequestType = -4;

    internal static readonly int[] ScalePercentages = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDpiScaleGet
{
    internal NativeDeviceInfoHeader Header;
    internal int MinimumRelativeScale;
    internal int CurrentRelativeScale;
    internal int MaximumRelativeScale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDpiScaleSet
{
    internal NativeDeviceInfoHeader Header;
    internal int RelativeScale;
}
