using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Display;

public sealed class DisplaySettingsCapabilities : IDisplaySettingsCapabilities
{
    private readonly IDisplayCaptureService _capture;
    private readonly IDisplayConfigNativeFacade _native;
    private readonly IDisplayModeCatalog _modes;
    private readonly IDisplayScaleService _scale;

    public DisplaySettingsCapabilities(IDisplayCaptureService capture)
        : this(capture, new DisplayConfigNativeFacade(), new DxgiDisplayModeCatalog(), null) { }

    internal DisplaySettingsCapabilities(IDisplayCaptureService capture, IDisplayConfigNativeFacade native,
        IDisplayModeCatalog modes, IDisplayScaleService? scale)
    {
        _capture = capture;
        _native = native;
        _modes = modes;
        _scale = scale ?? new DisplayScaleService(native);
    }

    public async Task<IReadOnlyList<DisplaySettingsCapability>> QueryAsync(DisplayProfile profile, CancellationToken cancellationToken = default)
    {
        var snapshot = await _capture.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var resolution = new MonitorIdentityResolver().Resolve(profile, snapshot.ConnectedDisplays);
        if (resolution.Status != IdentityResolutionStatus.Success)
            return profile.Displays.Select(display => Unavailable(display.DisplayId,
                "Connect the saved displays and resolve any ambiguous display bindings before editing settings.")).ToArray();
        return await Task.Run(() => Query(profile, snapshot, resolution.Bindings, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private List<DisplaySettingsCapability> Query(DisplayProfile profile, CapturedDisplaySnapshot snapshot,
        IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
    {
        var raw = QuerySnapshot();
        var result = new List<DisplaySettingsCapability>();
        foreach (var display in profile.Displays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeId = bindings.Single(binding => binding.DisplayId == display.DisplayId).RuntimeId;
            var connected = snapshot.ConnectedDisplays.Single(candidate => candidate.RuntimeId == runtimeId);
            var paths = raw.Paths.Where(path => path.TargetInfo.TargetAvailable != 0 && (path.Flags & 1) != 0 &&
                $"{path.TargetInfo.AdapterId.StableKey}:T{path.TargetInfo.Id}" == runtimeId).ToArray();
            if (!connected.IsActive || paths.Length != 1)
            {
                result.Add(Unavailable(display.DisplayId, "Activate this display in Windows or switch to a profile that uses it, then reopen the editor."));
                continue;
            }
            var path = paths[0];
            var sourceKey = $"{path.SourceInfo.AdapterId.StableKey}:S{path.SourceInfo.Id}";
            if (connected.ActivePath?.SourceGroupId != sourceKey ||
                !string.Equals(_native.GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id).DevicePath,
                    connected.Identity.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))
                throw new DisplayCaptureException(0, "The display connection changed. Reopen the settings editor.", isTransient: true);
            var messages = new List<string>();
            IReadOnlyList<AvailableDisplayMode> modes = [];
            IReadOnlyList<int> scales = [];
            IReadOnlyList<bool> hdr = [];
            try
            {
                modes = _modes.ReadModes(_native.GetSourceName(path.SourceInfo.AdapterId, path.SourceInfo.Id));
                if (display.Orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped)
                    modes = modes.Select(mode => mode with { Resolution = new DisplaySize(mode.Resolution.Height, mode.Resolution.Width) }).ToArray();
            }
            catch (DisplayCaptureException exception) { messages.Add(exception.Message); }
            try
            {
                var scale = _scale.Query(new DisplaySourceAddress(path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id));
                scales = scale.SupportedPercentages;
                if (!scale.IsSupported) messages.Add(scale.ErrorMessage ?? "Windows cannot report available scale values.");
            }
            catch (DisplayCaptureException exception) { messages.Add(exception.Message); }
            try
            {
                var state = _native.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id);
                hdr = state.IsSupported && !state.IsLimitedByPolicy ? [false, true] : [false];
            }
            catch (DisplayCaptureException exception) { messages.Add(exception.Message); }
            // A source shared by cloned targets cannot be edited independently.
            // Keep its source mode and scale fixed; per-target HDR remains editable.
            if (profile.Displays.Count(other => other.SourceGroupId == display.SourceGroupId) > 1 ||
                raw.Paths.Count(other => (other.Flags & 1) != 0 && other.SourceInfo.AdapterId == path.SourceInfo.AdapterId && other.SourceInfo.Id == path.SourceInfo.Id) > 1)
            {
                modes = [];
                scales = [];
                messages.Add("For cloned displays, change resolution, refresh rate, and scale in Windows and capture the setup again.");
            }
            if (modes.Count == 0 && messages.Count == 0) messages.Add("Windows did not report editable modes for this display.");
            result.Add(new DisplaySettingsCapability(display.DisplayId, modes, scales, hdr,
                messages.Count == 0 ? null : string.Join(" ", messages.Distinct())));
        }
        var after = QuerySnapshot();
        if (!raw.Paths.SequenceEqual(after.Paths))
            throw new DisplayCaptureException(0, "The display configuration changed while reading available options. Reopen the editor.", isTransient: true);
        return result;
    }

    private static DisplaySettingsCapability Unavailable(string id, string message) => new(id, [], [], [], message);

    private NativeDisplaySnapshot QuerySnapshot()
    {
        try { return _native.Query(0x1 | 0x10 | 0x40); }
        catch (DisplayCaptureException exception) when (DisplayConfigNativeFacade.IsUnsupportedRequest(exception.NativeErrorCode))
        { return _native.Query(0x1 | 0x10); }
    }
}
