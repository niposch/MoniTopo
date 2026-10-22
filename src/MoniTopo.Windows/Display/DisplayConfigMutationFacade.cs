using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MoniTopo.Core.Activation;

namespace MoniTopo.Windows.Display;

internal static class SetDisplayConfigFlags
{
    internal const uint TopologyExtend = 0x4;
    internal const uint UseSuppliedDisplayConfig = 0x20;
    internal const uint Validate = 0x40;
    internal const uint Apply = 0x80;
    internal const uint SaveToDatabase = 0x200;
    internal const uint PathPersistIfRequired = 0x800;
    internal const uint VirtualModeAware = 0x8000;
    internal const uint VirtualRefreshRateAware = 0x20000;
}

internal interface IDisplayConfigMutationApi
{
    int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags);

    int SetDpiScale(ref NativeDpiScaleSet packet);

    int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet);

    int SetHdrState(ref NativeHdrStateSet packet);
}

internal sealed class User32DisplayConfigMutationApi : IDisplayConfigMutationApi
{
    public unsafe int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
    {
        fixed (NativePathInfo* pathPointer = paths)
        fixed (NativeModeInfo* modePointer = modes)
        {
            return NativeMethods.SetDisplayConfig(
                checked((uint)paths.Length),
                paths.Length == 0 ? null : pathPointer,
                checked((uint)modes.Length),
                modes.Length == 0 ? null : modePointer,
                flags);
        }
    }

    public int SetDpiScale(ref NativeDpiScaleSet packet) => NativeMethods.SetDpiScale(ref packet);

    public int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet) =>
        NativeMethods.SetAdvancedColorState(ref packet);

    public int SetHdrState(ref NativeHdrStateSet packet) => NativeMethods.SetHdrState(ref packet);
}

internal sealed class DisplayConfigMutationFacade
{
    private readonly IDisplayConfigMutationApi _api;
    private Action? _ensureWriterOwnership;

    internal void SetMutationGuard(Action? ensureWriterOwnership) => _ensureWriterOwnership = ensureWriterOwnership;

    internal DisplayConfigMutationFacade(DisplayMutationAuthorization authorization)
        : this(authorization, new User32DisplayConfigMutationApi())
    {
    }

    internal DisplayConfigMutationFacade(
        DisplayMutationAuthorization authorization,
        IDisplayConfigMutationApi api)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _api = api;
    }

    internal void Validate(NativeDisplaySnapshot plan) => SetCore(
        plan,
        SetDisplayConfigFlags.UseSuppliedDisplayConfig |
        SetDisplayConfigFlags.Validate |
        SetDisplayConfigFlags.VirtualModeAware |
        SetDisplayConfigFlags.VirtualRefreshRateAware,
        "activation.validation.rejected",
        "Windows rejected this display configuration before it was applied.");

    internal void ApplyTemporary(NativeDisplaySnapshot plan) => SetCore(
        plan,
        SetDisplayConfigFlags.UseSuppliedDisplayConfig |
        SetDisplayConfigFlags.Apply |
        SetDisplayConfigFlags.VirtualModeAware |
        SetDisplayConfigFlags.VirtualRefreshRateAware,
        "activation.topology.failed",
        "Windows could not apply the saved display topology and modes.");

    internal void Persist(NativeDisplaySnapshot plan) => SetCore(
        plan,
        SetDisplayConfigFlags.UseSuppliedDisplayConfig |
        SetDisplayConfigFlags.Apply |
        SetDisplayConfigFlags.SaveToDatabase |
        SetDisplayConfigFlags.VirtualModeAware |
        SetDisplayConfigFlags.VirtualRefreshRateAware,
        "activation.persistence.failed",
        "Windows could not save the verified display topology.");

    internal bool ApplyExtendedFallback()
    {
        _ensureWriterOwnership?.Invoke();
        var result = _api.SetDisplayConfig(
            [],
            [],
            SetDisplayConfigFlags.TopologyExtend |
            SetDisplayConfigFlags.Apply |
            SetDisplayConfigFlags.PathPersistIfRequired);
        _ensureWriterOwnership?.Invoke();
        return result == DisplayConfigNativeFacade.ErrorSuccess;
    }

    internal void SetDpiScale(NativeAdapterId adapterId, uint sourceId, int relativeScale)
    {
        _ensureWriterOwnership?.Invoke();
        var packet = new NativeDpiScaleSet
        {
            Header = CreateHeader(UndocumentedDpiScaleContract.SetRequestType, Marshal.SizeOf<NativeDpiScaleSet>(), adapterId, sourceId),
            RelativeScale = relativeScale,
        };
        var result = _api.SetDpiScale(ref packet);
        _ensureWriterOwnership?.Invoke();
        ThrowIfFailed(
            result,
            "activation.scaling.failed",
            "Windows could not apply the saved display scaling.",
            "DisplayConfigSetDeviceInfo (DPI scale)");
    }

    internal void SetHdrState(NativeAdapterId adapterId, uint targetId, bool enabled)
    {
        _ensureWriterOwnership?.Invoke();
        var hdrPacket = new NativeHdrStateSet
        {
            Header = CreateHeader(16, Marshal.SizeOf<NativeHdrStateSet>(), adapterId, targetId),
            Value = enabled ? 1u : 0u,
        };
        var hdrResult = _api.SetHdrState(ref hdrPacket);
        _ensureWriterOwnership?.Invoke();
        if (hdrResult == DisplayConfigNativeFacade.ErrorSuccess)
        {
            return;
        }

        if (!DisplayConfigNativeFacade.IsUnsupportedRequest(hdrResult))
        {
            ThrowIfFailed(
                hdrResult,
                "activation.hdr.failed",
                "Windows could not apply the saved HDR state.",
                "DisplayConfigSetDeviceInfo (HDR)");
        }

        var packet = new NativeAdvancedColorStateSet
        {
            Header = CreateHeader(10, Marshal.SizeOf<NativeAdvancedColorStateSet>(), adapterId, targetId),
            Value = enabled ? 1u : 0u,
        };
        _ensureWriterOwnership?.Invoke();
        var colorResult = _api.SetAdvancedColorState(ref packet);
        _ensureWriterOwnership?.Invoke();
        ThrowIfFailed(
            colorResult,
            "activation.hdr.failed",
            "Windows could not apply the saved HDR state.",
            "DisplayConfigSetDeviceInfo (Advanced Color)");
    }

    private void SetCore(NativeDisplaySnapshot plan, uint flags, string errorCode, string message)
    {
        _ensureWriterOwnership?.Invoke();
        var nativeErrorCode = _api.SetDisplayConfig(plan.Paths, plan.Modes, flags);
        _ensureWriterOwnership?.Invoke();
        if (nativeErrorCode != DisplayConfigNativeFacade.ErrorSuccess)
        {
            throw new DisplayActivationException(
                errorCode,
                message,
                "SetDisplayConfig",
                nativeErrorCode,
                DescribePlan(plan, flags));
        }
    }

    private static string DescribePlan(NativeDisplaySnapshot plan, uint flags)
    {
        var sourceOrdinals = new Dictionary<(NativeAdapterId AdapterId, uint SourceId), int>();
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"flags=0x{flags:X8}; paths={plan.Paths.Length}; modes={plan.Modes.Length}.");
        for (var index = 0; index < plan.Paths.Length; index++)
        {
            var path = plan.Paths[index];
            var sourceKey = (path.SourceInfo.AdapterId, path.SourceInfo.Id);
            if (!sourceOrdinals.TryGetValue(sourceKey, out var sourceOrdinal))
            {
                sourceOrdinal = sourceOrdinals.Count;
                sourceOrdinals.Add(sourceKey, sourceOrdinal);
            }

            builder.Append(CultureInfo.InvariantCulture,
                $" Path[{index}]: source={sourceOrdinal}, sourceMode=0x{path.SourceInfo.ModeInfoIndex:X8}, " +
                $"targetMode=0x{path.TargetInfo.ModeInfoIndex:X8}, pathFlags=0x{path.Flags:X8}, " +
                $"rotation={path.TargetInfo.Rotation}, scaling={path.TargetInfo.Scaling}, " +
                $"refresh={path.TargetInfo.RefreshRate.Numerator}/{path.TargetInfo.RefreshRate.Denominator}, " +
                $"scanline={path.TargetInfo.ScanLineOrdering}, available={path.TargetInfo.TargetAvailable}.");
        }

        for (var index = 0; index < plan.Modes.Length; index++)
        {
            var mode = plan.Modes[index];
            if (mode.InfoType == 1)
            {
                builder.Append(CultureInfo.InvariantCulture,
                    $" Mode[{index}]: source {mode.Mode.SourceMode.Width}x{mode.Mode.SourceMode.Height} " +
                    $"at {mode.Mode.SourceMode.Position.X},{mode.Mode.SourceMode.Position.Y}, pixelFormat={mode.Mode.SourceMode.PixelFormat}.");
            }
            else if (mode.InfoType == 2)
            {
                var signal = mode.Mode.TargetMode.TargetVideoSignalInfo;
                builder.Append(CultureInfo.InvariantCulture,
                    $" Mode[{index}]: target active={signal.ActiveSize.Width}x{signal.ActiveSize.Height}, " +
                    $"total={signal.TotalSize.Width}x{signal.TotalSize.Height}, " +
                    $"vSync={signal.VerticalSyncFrequency.Numerator}/{signal.VerticalSyncFrequency.Denominator}, " +
                    $"scanline={signal.ScanLineOrdering}.");
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $" Mode[{index}]: type={mode.InfoType}.");
            }
        }

        return builder.ToString();
    }

    private static NativeDeviceInfoHeader CreateHeader(int type, int size, NativeAdapterId adapterId, uint id) => new()
    {
        Type = type,
        Size = checked((uint)size),
        AdapterId = adapterId,
        Id = id,
    };

    private static void ThrowIfFailed(
        int nativeErrorCode,
        string errorCode,
        string message,
        string nativeOperation)
    {
        if (nativeErrorCode != DisplayConfigNativeFacade.ErrorSuccess)
        {
            throw new DisplayActivationException(errorCode, message, nativeOperation, nativeErrorCode);
        }
    }
}

public sealed class DisplayActivationException(
    string errorCode,
    string message,
    string nativeOperation,
    int nativeErrorCode,
    string? operationDetails = null)
    : ActivationFailureException(
        errorCode,
        message,
        technicalDetails: string.Create(
            CultureInfo.InvariantCulture,
            $"{nativeOperation} failed with native error {nativeErrorCode} (0x{unchecked((uint)nativeErrorCode):X8}).{(string.IsNullOrWhiteSpace(operationDetails) ? string.Empty : $" {operationDetails}")}"))
{
    public int NativeErrorCode { get; } = nativeErrorCode;
}
