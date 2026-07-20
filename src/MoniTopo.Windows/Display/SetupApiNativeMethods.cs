using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeviceInterfaceData
{
    internal uint Size;
    internal Guid InterfaceClassGuid;
    internal uint Flags;
    internal nint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeviceInfoData
{
    internal uint Size;
    internal Guid ClassGuid;
    internal uint DeviceInstance;
    internal nint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeDevicePropertyKey(Guid FormatId, uint PropertyId);

internal static partial class SetupApiNativeMethods
{
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    internal static partial nint GetClassDevices(in Guid classGuid, nint enumerator, nint parentWindow, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumerateDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        in Guid interfaceClassGuid,
        uint memberIndex,
        ref NativeDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref NativeDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref NativeDeviceInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetDeviceInstanceId(
        nint deviceInfoSet,
        ref NativeDeviceInfoData deviceInfoData,
        char* deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetDeviceProperty(
        nint deviceInfoSet,
        ref NativeDeviceInfoData deviceInfoData,
        in NativeDevicePropertyKey propertyKey,
        out uint propertyType,
        byte* propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDevRegKey", SetLastError = true)]
    internal static partial nint OpenDeviceRegistryKey(
        nint deviceInfoSet,
        ref NativeDeviceInfoData deviceInfoData,
        uint scope,
        uint hardwareProfile,
        uint keyType,
        uint access);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyDeviceInfoList(nint deviceInfoSet);
}
