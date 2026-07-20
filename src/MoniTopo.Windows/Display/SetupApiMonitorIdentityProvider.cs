using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Display;

internal interface IMonitorIdentityProvider
{
    MonitorIdentityFingerprint Resolve(NativeTargetDeviceName targetName, DisplaySize? preferredMode);
}

internal sealed class SetupApiMonitorIdentityProvider : IMonitorIdentityProvider
{
    private static readonly Guid MonitorInterfaceClass = new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");
    private const uint PresentDevice = 0x2;
    private const uint DeviceInterface = 0x10;
    private const uint GlobalScope = 0x1;
    private const uint DeviceRegistryKey = 0x1;
    private const uint ReadAccess = 0x20019;
    private static readonly nint InvalidHandle = new(-1);

    public MonitorIdentityFingerprint Resolve(NativeTargetDeviceName targetName, DisplaySize? preferredMode)
    {
        var setupIdentity = FindSetupIdentity(targetName.DevicePath);
        var parsedEdid = EdidParser.Parse(setupIdentity.Edid);
        return new MonitorIdentityFingerprint(
            MonitorDevicePath: EmptyToNull(targetName.DevicePath),
            DeviceInstanceId: setupIdentity.DeviceInstanceId,
            DeviceContainerId: null,
            EdidSerial: parsedEdid.Serial,
            EdidManufacturerId: parsedEdid.ManufacturerId ?? DecodeCcdManufacturer(targetName.EdidManufacturerId),
            EdidProductCode: parsedEdid.ProductCode ?? targetName.EdidProductCode,
            FriendlyModelName: parsedEdid.ModelName ?? EmptyToNull(targetName.FriendlyName),
            PhysicalWidthMillimeters: parsedEdid.PhysicalWidthMillimeters,
            PhysicalHeightMillimeters: parsedEdid.PhysicalHeightMillimeters,
            OutputTechnology: CcdCaptureService.MapOutputTechnology(targetName.OutputTechnology),
            ConnectorInstance: targetName.ConnectorInstance,
            PreferredMode: preferredMode,
            SupportedModeSignature: parsedEdid.ModeSignature);
    }

    private static unsafe (string? DeviceInstanceId, byte[]? Edid) FindSetupIdentity(string monitorPath)
    {
        var deviceSet = SetupApiNativeMethods.GetClassDevices(
            MonitorInterfaceClass,
            nint.Zero,
            nint.Zero,
            PresentDevice | DeviceInterface);
        if (deviceSet == InvalidHandle)
        {
            return default;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new NativeDeviceInterfaceData
                {
                    Size = checked((uint)Marshal.SizeOf<NativeDeviceInterfaceData>()),
                };
                if (!SetupApiNativeMethods.EnumerateDeviceInterfaces(
                    deviceSet,
                    nint.Zero,
                    MonitorInterfaceClass,
                    index,
                    ref interfaceData))
                {
                    break;
                }

                var deviceInfo = new NativeDeviceInfoData
                {
                    Size = checked((uint)Marshal.SizeOf<NativeDeviceInfoData>()),
                };
                _ = SetupApiNativeMethods.GetDeviceInterfaceDetail(
                    deviceSet,
                    ref interfaceData,
                    nint.Zero,
                    0,
                    out var requiredSize,
                    ref deviceInfo);
                var detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupApiNativeMethods.GetDeviceInterfaceDetail(
                        deviceSet,
                        ref interfaceData,
                        detailBuffer,
                        requiredSize,
                        out _,
                        ref deviceInfo))
                    {
                        continue;
                    }

                    var interfacePath = Marshal.PtrToStringUni(detailBuffer + sizeof(uint));
                    if (!string.Equals(interfacePath, monitorPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var instanceIdBuffer = new char[512];
                    fixed (char* instanceIdPointer = instanceIdBuffer)
                    {
                        var hasInstanceId = SetupApiNativeMethods.GetDeviceInstanceId(
                            deviceSet,
                            ref deviceInfo,
                            instanceIdPointer,
                            checked((uint)instanceIdBuffer.Length),
                            out _);
                        var instanceId = hasInstanceId ? new string(instanceIdPointer) : null;
                        return (instanceId, ReadEdid(deviceSet, ref deviceInfo));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }

            return default;
        }
        finally
        {
            _ = SetupApiNativeMethods.DestroyDeviceInfoList(deviceSet);
        }
    }

    private static byte[]? ReadEdid(nint deviceSet, ref NativeDeviceInfoData deviceInfo)
    {
        var registryHandle = SetupApiNativeMethods.OpenDeviceRegistryKey(
            deviceSet,
            ref deviceInfo,
            GlobalScope,
            hardwareProfile: 0,
            DeviceRegistryKey,
            ReadAccess);
        if (registryHandle == InvalidHandle)
        {
            return null;
        }

        using var safeHandle = new SafeRegistryHandle(registryHandle, ownsHandle: true);
        using var key = RegistryKey.FromHandle(safeHandle);
        return key.GetValue("EDID") as byte[];
    }

    private static string? DecodeCcdManufacturer(ushort rawValue)
    {
        var parsed = EdidParser.Parse(
        [
            0, 0, 0, 0, 0, 0, 0, 0,
            (byte)(rawValue >> 8), (byte)rawValue,
            .. new byte[118],
        ]);
        return parsed.ManufacturerId;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
