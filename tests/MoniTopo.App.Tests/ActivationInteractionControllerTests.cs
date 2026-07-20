using MoniTopo.App.Interaction;
using MoniTopo.App.Tray;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Tests;

public sealed class ActivationInteractionControllerTests
{
    [Fact]
    public async Task PopupActivationReportsInlineWithoutNotification()
    {
        var notifications = new FakeNotifications();
        var hotkeyStates = new List<bool>();
        var controller = new ActivationInteractionController(
            new FakeActivator(Success()),
            notifications,
            hotkeyStates.Add);
        ActivationResult? inlineResult = null;
        controller.PopupResultAvailable += (_, result) => inlineResult = result;

        var result = await controller.ActivateAsync(Profile(), ActivationOrigin.Popup);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Same(result, inlineResult);
        Assert.Empty(notifications.Information);
        Assert.Empty(notifications.Errors);
        Assert.Equal([false, true], hotkeyStates);
    }

    [Fact]
    public async Task DirectHotkeyUsesShortSuccessNotification()
    {
        var notifications = new FakeNotifications();
        var controller = new ActivationInteractionController(
            new FakeActivator(Success()),
            notifications,
            _ => { });

        await controller.ActivateAsync(Profile(), ActivationOrigin.DirectHotkey);

        Assert.Equal(["Movie activated"], notifications.Information);
        Assert.Empty(notifications.Errors);
    }

    [Fact]
    public async Task DirectHotkeyFailureUsesConcreteErrorNotification()
    {
        var notifications = new FakeNotifications();
        var failure = new ActivationResult(
            ActivationOutcome.Failed,
            "Movie requires the television, but it is not connected.",
            "activation.identity.missing",
            false,
            false,
            false,
            false);
        var controller = new ActivationInteractionController(new FakeActivator(failure), notifications, _ => { });

        await controller.ActivateAsync(Profile(), ActivationOrigin.DirectHotkey);

        Assert.Equal([failure.Message], notifications.Errors);
        Assert.Empty(notifications.Information);
    }

    [Fact]
    public async Task HotkeyRouterTogglesPopupOrActivatesMappedProfile()
    {
        var profile = Profile();
        var activator = new FakeActivator(Success());
        var controller = new ActivationInteractionController(activator, new FakeNotifications(), _ => { });
        var surfaces = new FakeSurfaces();
        var router = new HotkeyCommandRouter(() => [profile], controller, surfaces);

        await router.HandleAsync(new HotkeyCommand(HotkeyCommandKind.TogglePopup));
        await router.HandleAsync(new HotkeyCommand(HotkeyCommandKind.ActivateProfile, profile.Id));

        Assert.Equal(1, surfaces.ToggleCount);
        Assert.Equal(1, activator.CallCount);
    }

    private static ActivationResult Success() => new(
        ActivationOutcome.Success,
        "Movie activated",
        null,
        true,
        false,
        false,
        false);

    private static DisplayProfile Profile()
    {
        var identity = new MonitorIdentityFingerprint(
            "synthetic://tv",
            "SYNTHETIC\\TV",
            null,
            "TV-1",
            "TST",
            1,
            "Synthetic TV",
            1000,
            600,
            DisplayOutputTechnology.Hdmi,
            1,
            new DisplaySize(3840, 2160),
            "synthetic");
        var display = new DesiredDisplayPath(
            "tv",
            identity,
            "source-tv",
            null,
            new DisplayPoint(0, 0),
            new DisplaySize(3840, 2160),
            new RefreshRate(60, 1),
            DisplayOrientation.Landscape,
            DisplayPathScaling.Identity,
            100,
            true,
            true,
            "Synthetic TV");
        return new DisplayProfile(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "Movie",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            [display],
            display.DisplayId,
            1,
            []);
    }

    private sealed class FakeActivator(ActivationResult result) : IProfileActivator
    {
        internal int CallCount { get; private set; }

        public Task<ActivationResult> ActivateAsync(
            DisplayProfile profile,
            IProgress<ActivationProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report(new ActivationProgress(ActivationPhase.ApplyingTopology, "Applying"));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeNotifications : IUserNotificationService
    {
        internal List<string> Information { get; } = [];

        internal List<string> Errors { get; } = [];

        public void ShowInformation(string message) => Information.Add(message);

        public void ShowError(string message) => Errors.Add(message);
    }

    private sealed class FakeSurfaces : IApplicationSurfaces
    {
        internal int ToggleCount { get; private set; }

        public void TogglePopup() => ToggleCount++;

        public void OpenSettings()
        {
        }
    }
}
