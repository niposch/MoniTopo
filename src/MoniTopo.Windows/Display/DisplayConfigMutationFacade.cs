using System.Runtime.InteropServices;
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
}

internal sealed class DisplayConfigMutationFacade
{
    private readonly IDisplayConfigMutationApi _api;

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

    internal bool ApplyExtendedFallback() =>
        _api.SetDisplayConfig(
            [],
            [],
            SetDisplayConfigFlags.TopologyExtend |
            SetDisplayConfigFlags.Apply |
            SetDisplayConfigFlags.PathPersistIfRequired) == DisplayConfigNativeFacade.ErrorSuccess;

    internal void SetDpiScale(NativeAdapterId adapterId, uint sourceId, int relativeScale)
    {
        var packet = new NativeDpiScaleSet
        {
            Header = CreateHeader(UndocumentedDpiScaleContract.SetRequestType, Marshal.SizeOf<NativeDpiScaleSet>(), adapterId, sourceId),
            RelativeScale = relativeScale,
        };
        ThrowIfFailed(
            _api.SetDpiScale(ref packet),
            "activation.scaling.failed",
            "Windows could not apply the saved display scaling.");
    }

    internal void SetAdvancedColor(NativeAdapterId adapterId, uint targetId, bool enabled)
    {
        var packet = new NativeAdvancedColorStateSet
        {
            Header = CreateHeader(10, Marshal.SizeOf<NativeAdvancedColorStateSet>(), adapterId, targetId),
            Value = enabled ? 1u : 0u,
        };
        ThrowIfFailed(
            _api.SetAdvancedColorState(ref packet),
            "activation.hdr.failed",
            "Windows could not apply the saved HDR state.");
    }

    private void SetCore(NativeDisplaySnapshot plan, uint flags, string errorCode, string message) =>
        ThrowIfFailed(_api.SetDisplayConfig(plan.Paths, plan.Modes, flags), errorCode, message);

    private static NativeDeviceInfoHeader CreateHeader(int type, int size, NativeAdapterId adapterId, uint id) => new()
    {
        Type = type,
        Size = checked((uint)size),
        AdapterId = adapterId,
        Id = id,
    };

    private static void ThrowIfFailed(int nativeErrorCode, string errorCode, string message)
    {
        if (nativeErrorCode != DisplayConfigNativeFacade.ErrorSuccess)
        {
            throw new DisplayActivationException(errorCode, message, nativeErrorCode);
        }
    }
}

public sealed class DisplayActivationException(string errorCode, string message, int nativeErrorCode)
    : ActivationFailureException(errorCode, message)
{
    public int NativeErrorCode { get; } = nativeErrorCode;
}
