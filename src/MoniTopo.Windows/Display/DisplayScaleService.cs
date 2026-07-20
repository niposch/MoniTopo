namespace MoniTopo.Windows.Display;

public readonly record struct DisplaySourceAddress(uint AdapterLowPart, int AdapterHighPart, uint SourceId);

public sealed record DisplayScaleCapability(
    bool IsSupported,
    int? CurrentPercent,
    int? RecommendedPercent,
    IReadOnlyList<int> SupportedPercentages,
    string? ErrorMessage);

public interface IDisplayScaleService
{
    DisplayScaleCapability Query(DisplaySourceAddress source);
}

public sealed class DisplayScaleService : IDisplayScaleService
{
    private readonly IDisplayConfigNativeFacade _native;

    public DisplayScaleService()
        : this(new DisplayConfigNativeFacade())
    {
    }

    internal DisplayScaleService(IDisplayConfigNativeFacade native)
    {
        _native = native;
    }

    public DisplayScaleCapability Query(DisplaySourceAddress source)
    {
        var (errorCode, packet) = _native.TryGetDpiScale(
            new NativeAdapterId(source.AdapterLowPart, source.AdapterHighPart),
            source.SourceId);
        if (errorCode != DisplayConfigNativeFacade.ErrorSuccess)
        {
            return new DisplayScaleCapability(
                IsSupported: false,
                CurrentPercent: null,
                RecommendedPercent: null,
                SupportedPercentages: Array.Empty<int>(),
                ErrorMessage: "Windows display scaling is not supported on this Windows build or display source.");
        }

        var values = UndocumentedDpiScaleContract.ScalePercentages;
        var minimumIndex = 0;
        var recommendedIndex = -packet.MinimumRelativeScale;
        var currentIndex = packet.CurrentRelativeScale - packet.MinimumRelativeScale;
        var maximumIndex = packet.MaximumRelativeScale - packet.MinimumRelativeScale;
        if (recommendedIndex < minimumIndex ||
            currentIndex < minimumIndex ||
            maximumIndex < currentIndex ||
            maximumIndex >= values.Length)
        {
            return new DisplayScaleCapability(
                IsSupported: false,
                CurrentPercent: null,
                RecommendedPercent: null,
                SupportedPercentages: Array.Empty<int>(),
                ErrorMessage: "Windows returned an unrecognized display scaling range.");
        }

        return new DisplayScaleCapability(
            IsSupported: true,
            CurrentPercent: values[currentIndex],
            RecommendedPercent: values[recommendedIndex],
            SupportedPercentages: values[..(maximumIndex + 1)],
            ErrorMessage: null);
    }

    internal static bool TryResolveRelativeScale(
        NativeDpiScaleGet packet,
        int desiredPercent,
        out int relativeScale)
    {
        var desiredIndex = Array.IndexOf(UndocumentedDpiScaleContract.ScalePercentages, desiredPercent);
        var recommendedIndex = -packet.MinimumRelativeScale;
        var currentIndex = packet.CurrentRelativeScale - packet.MinimumRelativeScale;
        var maximumIndex = packet.MaximumRelativeScale - packet.MinimumRelativeScale;
        if (recommendedIndex < 0 ||
            currentIndex < 0 ||
            maximumIndex < currentIndex ||
            maximumIndex >= UndocumentedDpiScaleContract.ScalePercentages.Length ||
            desiredIndex < 0 ||
            desiredIndex > maximumIndex)
        {
            relativeScale = 0;
            return false;
        }

        relativeScale = checked(desiredIndex + packet.MinimumRelativeScale);
        return true;
    }
}
