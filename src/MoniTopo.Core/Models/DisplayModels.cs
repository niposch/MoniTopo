namespace MoniTopo.Core.Models;

public readonly record struct DisplayPoint(int X, int Y)
{
    public static DisplayPoint operator -(DisplayPoint left, DisplayPoint right) =>
        new(left.X - right.X, left.Y - right.Y);
}

public readonly record struct DisplaySize(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct DisplayRectangle(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top;
}

public sealed record DisplayDesktopImage(
    DisplaySize PathSourceSize,
    DisplayRectangle Region,
    DisplayRectangle Clip)
{
    public bool IsValid => PathSourceSize.IsValid && Region.IsValid && Clip.IsValid &&
        Region.Left >= 0 && Region.Top >= 0 &&
        Region.Right <= PathSourceSize.Width && Region.Bottom <= PathSourceSize.Height;
}

public readonly record struct RefreshRate(long Numerator, long Denominator)
{
    public double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    public bool IsValid => Numerator > 0 && Denominator > 0;

    public RefreshRate Normalize()
    {
        if (!IsValid)
        {
            return this;
        }

        var divisor = GreatestCommonDivisor(Numerator, Denominator);
        return new RefreshRate(Numerator / divisor, Denominator / divisor);
    }

    public bool IsEquivalentTo(RefreshRate other, double toleranceHertz = 0.1) =>
        IsValid && other.IsValid && Math.Abs(Hertz - other.Hertz) <= toleranceHertz;

    private static long GreatestCommonDivisor(long left, long right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}

public enum DisplayOrientation
{
    Landscape = 0,
    Portrait = 1,
    LandscapeFlipped = 2,
    PortraitFlipped = 3,
}

public enum DisplayPathScaling
{
    Identity = 0,
    Centered = 1,
    Stretched = 2,
    AspectRatioCenteredMax = 3,
    Custom = 4,
    Preferred = 5,
}

public enum DisplayOutputTechnology
{
    Unknown = 0,
    Hd15 = 1,
    Dvi = 2,
    Hdmi = 3,
    DisplayPort = 4,
    Internal = 5,
    Wireless = 6,
    Other = 7,
}

public sealed record DisplayTargetSignal(
    ulong PixelRate,
    RefreshRate HorizontalSyncFrequency,
    RefreshRate VerticalSyncFrequency,
    DisplaySize ActiveSize,
    DisplaySize TotalSize,
    uint VideoStandard,
    uint ScanLineOrdering)
{
    private const double RefreshToleranceHertz = 0.1;
    private const double ClockRoundingTolerance = 0.0001;
    private const double MaximumRelativeClockTolerance = 0.002;

    public bool IsValid => PixelRate > 0 && HorizontalSyncFrequency.IsValid &&
        VerticalSyncFrequency.IsValid && ActiveSize.IsValid && TotalSize.IsValid &&
        TotalSize.Width >= ActiveSize.Width && TotalSize.Height >= ActiveSize.Height &&
        ScanLineOrdering <= 3;

    public bool IsEquivalentTo(DisplayTargetSignal other)
    {
        if (!IsValid || !other.IsValid ||
            ActiveSize != other.ActiveSize || TotalSize != other.TotalSize ||
            ScanLineOrdering != other.ScanLineOrdering ||
            // The low 16 bits are a driver-provided timing-standard label and the
            // high 10 bits are reserved. Only the Miracast divider affects semantics.
            (VideoStandard & 0x003F0000u) != (other.VideoStandard & 0x003F0000u) ||
            !VerticalSyncFrequency.IsEquivalentTo(other.VerticalSyncFrequency, RefreshToleranceHertz))
        {
            return false;
        }

        // Windows can alternate a common rate's fractional and rounded forms.
        // Admit consistent clock differences implied by that rate representation,
        // plus 0.01% rounding, but never more than 0.2% for a physical clock.
        var relativeRefreshDifference = Math.Abs(VerticalSyncFrequency.Hertz - other.VerticalSyncFrequency.Hertz) /
            Math.Max(VerticalSyncFrequency.Hertz, other.VerticalSyncFrequency.Hertz);
        var clockTolerance = Math.Min(MaximumRelativeClockTolerance, relativeRefreshDifference + ClockRoundingTolerance);
        return CloseClock(PixelRate, other.PixelRate, clockTolerance) &&
            CloseClock(HorizontalSyncFrequency.Hertz, other.HorizontalSyncFrequency.Hertz, clockTolerance);
    }

    // Permit rounding of a reported clock, not a different physical timing mode.
    private static bool CloseClock(double left, double right, double relativeTolerance) =>
        Math.Abs(left - right) <= Math.Max(1e-6, Math.Max(left, right) * relativeTolerance);
}

public sealed record MonitorIdentityFingerprint(
    string? MonitorDevicePath,
    string? DeviceInstanceId,
    string? DeviceContainerId,
    string? EdidSerial,
    string? EdidManufacturerId,
    int? EdidProductCode,
    string? FriendlyModelName,
    int? PhysicalWidthMillimeters,
    int? PhysicalHeightMillimeters,
    DisplayOutputTechnology OutputTechnology,
    uint? ConnectorInstance,
    DisplaySize? PreferredMode,
    string? SupportedModeSignature)
{
    public bool HasAnySignal =>
        !string.IsNullOrWhiteSpace(MonitorDevicePath) ||
        !string.IsNullOrWhiteSpace(DeviceInstanceId) ||
        !string.IsNullOrWhiteSpace(DeviceContainerId) ||
        !string.IsNullOrWhiteSpace(EdidSerial) ||
        !string.IsNullOrWhiteSpace(EdidManufacturerId) ||
        EdidProductCode is not null ||
        !string.IsNullOrWhiteSpace(FriendlyModelName) ||
        PreferredMode is not null;
}

public sealed record DesiredDisplayPath(
    string DisplayId,
    MonitorIdentityFingerprint Identity,
    string SourceGroupId,
    string? CloneGroupId,
    DisplayPoint Position,
    DisplaySize SourceResolution,
    RefreshRate RefreshRate,
    DisplayOrientation Orientation,
    DisplayPathScaling PathScaling,
    int WindowsUiScalePercent,
    bool HdrEnabled,
    bool IsPrimary,
    string FriendlyLabel)
{
    // Explicit mode selection has no invented physical timings: Windows resolves
    // the target timing while the requested source size/rate remain mandatory.
    public DisplayModeRequest? ModeRequest { get; init; }

    public DisplayTargetSignal? TargetSignal { get; init; }

    public DisplayDesktopImage? DesktopImage { get; init; }

    // Null means a legacy capture did not record dynamic refresh-rate policy.
    public bool? BoostRefreshRate { get; init; }
}

public sealed record DisplayModeRequest(uint ScanLineOrdering);

public sealed record AvailableDisplayMode(DisplaySize Resolution, RefreshRate RefreshRate, uint ScanLineOrdering);

public sealed record DisplaySettingsCapability(
    string DisplayId,
    IReadOnlyList<AvailableDisplayMode> Modes,
    IReadOnlyList<int> ScalePercentages,
    IReadOnlyList<bool> HdrStates,
    string? Message);

public interface IDisplaySettingsCapabilities
{
    Task<IReadOnlyList<DisplaySettingsCapability>> QueryAsync(DisplayProfile profile, CancellationToken cancellationToken = default);
}

public sealed record ConnectedDisplayState(
    string RuntimeId,
    MonitorIdentityFingerprint Identity,
    bool IsActive,
    string FriendlyLabel,
    DesiredDisplayPath? ActivePath);

public sealed record CapturedDisplaySnapshot(
    IReadOnlyList<DesiredDisplayPath> ActivePaths,
    IReadOnlyList<ConnectedDisplayState> ConnectedDisplays,
    string PrimaryDisplayId,
    DateTimeOffset CapturedUtc);
