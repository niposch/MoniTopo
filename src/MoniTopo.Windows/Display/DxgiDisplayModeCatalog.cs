using System.Runtime.InteropServices;
using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Display;

internal interface IDisplayModeCatalog
{
    IReadOnlyList<AvailableDisplayMode> ReadModes(string gdiDeviceName);
}

// Read-only DXGI enumeration. No device, swap chain, ownership, or mode setter is created.
// https://learn.microsoft.com/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-getdisplaymodelist1
internal sealed unsafe partial class DxgiDisplayModeCatalog : IDisplayModeCatalog
{
    private const int NotFound = unchecked((int)0x887A0002);
    private const int MoreData = unchecked((int)0x887A0003);

    public IReadOnlyList<AvailableDisplayMode> ReadModes(string gdiDeviceName)
    {
        var factoryId = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"); // IDXGIFactory
        ThrowIfFailed(CreateDXGIFactory(in factoryId, out var factory));
        var matches = new List<HashSet<AvailableDisplayMode>>();
        try
        {
            for (uint adapterIndex = 0; adapterIndex < 64; adapterIndex++)
            {
                nint adapter;
                var result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 7))(factory, adapterIndex, &adapter);
                if (result == NotFound) break;
                ThrowIfFailed(result);
                try
                {
                    for (uint outputIndex = 0; outputIndex < 64; outputIndex++)
                    {
                        nint output;
                        result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7))(adapter, outputIndex, &output);
                        if (result == NotFound) break;
                        ThrowIfFailed(result);
                        try
                        {
                            NativeDxgiOutputDescription description;
                            ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, NativeDxgiOutputDescription*, int>)Slot(output, 7))(output, &description));
                            if (!string.Equals(description.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                            var outputId = new Guid("00cddea8-939b-4b83-a340-a685226666cc"); // IDXGIOutput1
                            nint output1;
                            ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(output, 0))(output, &outputId, &output1));
                            try { matches.Add(ReadOutput(output1).ToHashSet()); }
                            finally { Release(output1); }
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        if (matches.Count == 0) return [];
        // Cloned outputs can share a GDI name. Offer only modes common to all of them.
        foreach (var match in matches.Skip(1)) matches[0].IntersectWith(match);
        return matches[0].OrderByDescending(mode => (long)mode.Resolution.Width * mode.Resolution.Height)
            .ThenByDescending(mode => mode.RefreshRate.Hertz).ToArray();
    }

    private static AvailableDisplayMode[] ReadOutput(nint output)
    {
        var result = new List<AvailableDisplayMode>();
        foreach (var format in new uint[] { 28, 24 }) // RGBA8 and RGB10A2; no color-depth setting is changed.
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                var getModes = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, NativeDxgiMode*, int>)Slot(output, 19);
                var status = getModes(output, format, 2, &count, null); // DXGI_ENUM_MODES_SCALING
                if (status < 0 && status != MoreData) break;
                if (count == 0) break;
                if (count > 16384) throw new DisplayCaptureException(0, "Windows returned too many display modes.");
                var modes = new NativeDxgiMode[count];
                fixed (NativeDxgiMode* buffer = modes)
                {
                    status = getModes(output, format, 2, &count, buffer);
                }
                if (status == MoreData) continue;
                ThrowIfFailed(status);
                if (count > modes.Length) throw new DisplayCaptureException(0, "The display mode list changed. Try again.");
                result.AddRange(ConvertModes(modes.Take(checked((int)count))));
                break;
            }
        }
        return result.Distinct().ToArray();
    }

    internal static IEnumerable<AvailableDisplayMode> ConvertModes(IEnumerable<NativeDxgiMode> modes) => modes
        .Where(mode => mode.Width is > 0 and <= 32768 && mode.Height is > 0 and <= 32768 &&
            mode.Rate.Numerator > 0 && mode.Rate.Denominator > 0 && mode.ScanLineOrdering == 1 && mode.Stereo == 0)
        .Select(mode => new AvailableDisplayMode(new DisplaySize((int)mode.Width, (int)mode.Height),
            new RefreshRate(mode.Rate.Numerator, mode.Rate.Denominator).Normalize(), mode.ScanLineOrdering)).Distinct();

    private static void* Slot(nint instance, int slot) => (*(void***)instance)[slot];
    private static void Release(nint instance) => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    private static void ThrowIfFailed(int result)
    {
        if (result < 0) throw new DisplayCaptureException(result, "Windows could not enumerate the available display modes. Reconnect or activate the display and try again.");
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory(in Guid interfaceId, out nint factory);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDxgiMode
{
    internal uint Width;
    internal uint Height;
    internal NativeRational Rate;
    internal uint Format;
    internal uint ScanLineOrdering;
    internal uint Scaling;
    internal int Stereo;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDxgiOutputDescription
{
    private fixed char _deviceName[32];
    internal NativeRect DesktopCoordinates;
    internal int AttachedToDesktop;
    internal uint Rotation;
    internal nint Monitor;
    internal readonly string DeviceName { get { fixed (char* name = _deviceName) return new string(name, 0, 32).TrimEnd('\0'); } }
}
