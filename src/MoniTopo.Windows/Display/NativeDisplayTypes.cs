using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeAdapterId(uint LowPart, int HighPart)
{
    public string StableKey => $"{unchecked((uint)HighPart):X8}{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRational(uint Numerator, uint Denominator);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativePoint(int X, int Y);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRegion(uint Width, uint Height);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSourceMode
{
    internal uint Width;
    internal uint Height;
    internal uint PixelFormat;
    internal NativePoint Position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoSignalInfo
{
    internal ulong PixelRate;
    internal NativeRational HorizontalSyncFrequency;
    internal NativeRational VerticalSyncFrequency;
    internal NativeRegion ActiveSize;
    internal NativeRegion TotalSize;
    internal uint VideoStandard;
    internal uint ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTargetMode
{
    internal NativeVideoSignalInfo TargetVideoSignalInfo;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct NativeModeUnion
{
    [FieldOffset(0)]
    internal NativeTargetMode TargetMode;

    [FieldOffset(0)]
    internal NativeSourceMode SourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeModeInfo
{
    internal uint InfoType;
    internal uint Id;
    internal NativeAdapterId AdapterId;
    internal NativeModeUnion Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathSourceInfo
{
    internal NativeAdapterId AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint StatusFlags;

    internal readonly uint CloneGroupId => ModeInfoIndex & 0xFFFF;

    internal readonly uint SourceModeInfoIndex => ModeInfoIndex >> 16;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathTargetInfo
{
    internal NativeAdapterId AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint OutputTechnology;
    internal uint Rotation;
    internal uint Scaling;
    internal NativeRational RefreshRate;
    internal uint ScanLineOrdering;
    internal int TargetAvailable;
    internal uint StatusFlags;

    internal readonly uint TargetModeInfoIndex => ModeInfoIndex >> 16;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathInfo
{
    internal NativePathSourceInfo SourceInfo;
    internal NativePathTargetInfo TargetInfo;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeviceInfoHeader
{
    internal int Type;
    internal uint Size;
    internal NativeAdapterId AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NativeTargetDeviceName
{
    internal NativeDeviceInfoHeader Header;
    internal uint Flags;
    internal uint OutputTechnology;
    internal ushort EdidManufacturerId;
    internal ushort EdidProductCode;
    internal uint ConnectorInstance;
    private fixed char _friendlyName[64];
    private fixed char _devicePath[128];

    internal readonly string FriendlyName
    {
        get
        {
            fixed (char* value = _friendlyName)
            {
                return new string(value);
            }
        }
    }

    internal readonly string DevicePath
    {
        get
        {
            fixed (char* value = _devicePath)
            {
                return new string(value);
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NativeSourceDeviceName
{
    internal NativeDeviceInfoHeader Header;
    private fixed char _gdiDeviceName[32];

    internal readonly string GdiDeviceName
    {
        get
        {
            fixed (char* value = _gdiDeviceName)
            {
                return new string(value);
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTargetPreferredMode
{
    internal NativeDeviceInfoHeader Header;
    internal uint Width;
    internal uint Height;
    internal NativeTargetMode TargetMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdvancedColorInfo
{
    internal NativeDeviceInfoHeader Header;
    internal uint Value;
    internal uint ColorEncoding;
    internal uint BitsPerColorChannel;

    internal readonly bool IsSupported => (Value & 0x1) != 0;

    internal readonly bool IsEnabled => (Value & 0x2) != 0;

    internal readonly bool IsForceDisabled => (Value & 0x8) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdvancedColorStateSet
{
    internal NativeDeviceInfoHeader Header;
    internal uint Value;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NativeDisplayDevice
{
    internal uint Size;
    private fixed char _deviceName[32];
    private fixed char _deviceString[128];
    internal uint StateFlags;
    private fixed char _deviceId[128];
    private fixed char _deviceKey[128];

    internal readonly string DeviceName
    {
        get
        {
            fixed (char* value = _deviceName)
            {
                return new string(value);
            }
        }
    }
}
