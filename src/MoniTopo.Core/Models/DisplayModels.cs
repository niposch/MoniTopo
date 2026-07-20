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
    uint ScanLineOrdering);

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
    public DisplayTargetSignal? TargetSignal { get; init; }
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
