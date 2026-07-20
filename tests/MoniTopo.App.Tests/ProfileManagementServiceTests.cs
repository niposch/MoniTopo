using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Validation;
using MoniTopo.Windows.Display;

namespace MoniTopo.App.Tests;

public sealed class ProfileManagementServiceTests
{
    [Fact]
    public async Task SaveCurrentAddsNormalizedProfileAndOrderAtomically()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.SaveCurrentAsync("  Desktop  ");

        Assert.Null(result.ExistingMatch);
        Assert.Equal("Desktop", result.Profile.Name);
        Assert.Equal(2, result.Profile.CaptureSchemaVersion);
        Assert.Equal([result.Profile.Id], fixture.Session.Current.ProfileOrder);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task DuplicateCurrentStateIsReturnedWithoutCreatingSecondProfile()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.SaveCurrentAsync("Desktop");

        var duplicate = await fixture.Service.SaveCurrentAsync("Desk copy");

        Assert.Equal(first.Profile.Id, duplicate.ExistingMatch?.Id);
        Assert.Single(fixture.Session.Current.Profiles);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task UpdatePreservesIdentityNameOrderAndHotkey()
    {
        var fixture = new Fixture();
        var saved = (await fixture.Service.SaveCurrentAsync("Desktop")).Profile;
        var hotkey = new HotkeyBinding(HotkeyModifiers.Control, 0x31);
        await fixture.Service.SetHotkeyAsync(saved.Id, hotkey);
        fixture.Capture.Snapshot = Snapshot(Display("display-1") with { WindowsUiScalePercent = 175 });

        var updated = await fixture.Service.UpdateFromCurrentAsync(saved.Id);

        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal("Desktop", updated.Name);
        Assert.Equal(hotkey, updated.DirectHotkey);
        Assert.Equal(175, updated.Displays[0].WindowsUiScalePercent);
        Assert.Equal([saved.Id], fixture.Session.Current.ProfileOrder);
    }

    [Fact]
    public async Task RenameAndHotkeyConflictsAreRejectedWithoutSaving()
    {
        var fixture = new Fixture();
        var first = (await fixture.Service.SaveCurrentAsync("Desktop")).Profile;
        fixture.Capture.Snapshot = Snapshot(Display("display-2", "SERIAL-2"));
        var second = (await fixture.Service.SaveCurrentAsync("Movie")).Profile;
        await fixture.Service.SetHotkeyAsync(first.Id, new HotkeyBinding(HotkeyModifiers.Control, 0x31));
        var saveCount = fixture.Store.SaveCount;

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => fixture.Service.RenameAsync(second.Id, " desktop "));
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => fixture.Service.SetHotkeyAsync(
            second.Id,
            new HotkeyBinding(HotkeyModifiers.Control, 0x31)));

        Assert.Equal(saveCount, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task MoveDeleteAndRememberBindingMaintainConfigurationInvariants()
    {
        var fixture = new Fixture();
        var first = (await fixture.Service.SaveCurrentAsync("Desktop")).Profile;
        fixture.Capture.Snapshot = Snapshot(Display("display-2", "SERIAL-2"));
        var second = (await fixture.Service.SaveCurrentAsync("Movie")).Profile;
        await fixture.Service.MoveAsync(second.Id, -1);
        await fixture.Service.RememberBindingAsync(first.Id, first.Displays[0].DisplayId, "runtime-remembered");
        await fixture.Service.DeleteAsync(second.Id);

        Assert.Equal([first.Id], fixture.Session.Current.ProfileOrder);
        Assert.Equal("runtime-remembered", Assert.Single(fixture.Session.Current.Profiles[0].LastSuccessfulIdentityBindings).RuntimeIdentityKey);
    }

    private static DesiredDisplayPath Display(string id, string? serial = null)
    {
        var identity = new MonitorIdentityFingerprint(
            $"synthetic://{id}",
            $"SYNTHETIC\\{id}",
            null,
            serial ?? "SERIAL-1",
            "TST",
            1,
            "Synthetic panel",
            600,
            340,
            DisplayOutputTechnology.DisplayPort,
            1,
            new DisplaySize(2560, 1440),
            "synthetic");
        return new DesiredDisplayPath(
            id,
            identity,
            $"source-{id}",
            null,
            new DisplayPoint(0, 0),
            new DisplaySize(2560, 1440),
            new RefreshRate(60, 1),
            DisplayOrientation.Landscape,
            DisplayPathScaling.Identity,
            150,
            false,
            true,
            "Synthetic panel")
        {
            TargetSignal = new DisplayTargetSignal(
                241_500_000,
                new RefreshRate(88_787, 1),
                new RefreshRate(60, 1),
                new DisplaySize(2560, 1440),
                new DisplaySize(2720, 1481),
                0,
                1),
        };
    }

    private static CapturedDisplaySnapshot Snapshot(DesiredDisplayPath display) => new(
        [display],
        [new ConnectedDisplayState("runtime-1", display.Identity, true, display.FriendlyLabel, display)],
        display.DisplayId,
        DateTimeOffset.UtcNow);

    private sealed class Fixture
    {
        internal Fixture()
        {
            Store = new FakeStore();
            Session = new ConfigurationSession(Store, ApplicationConfiguration.CreateDefault());
            Capture = new FakeCapture { Snapshot = Snapshot(Display("display-1")) };
            var resolver = new MonitorIdentityResolver();
            Service = new ProfileManagementService(
                Session,
                Capture,
                new ActiveProfileMatcher(resolver),
                new FrozenTimeProvider());
        }

        internal FakeStore Store { get; }

        internal ConfigurationSession Session { get; }

        internal FakeCapture Capture { get; }

        internal ProfileManagementService Service { get; }
    }

    private sealed class FakeStore : IConfigurationStore
    {
        internal int SaveCount { get; private set; }

        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationConfiguration.CreateDefault());

        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        {
            ConfigurationValidator.EnsureValid(configuration);
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCapture : IDisplayCaptureService
    {
        internal required CapturedDisplaySnapshot Snapshot { get; set; }

        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 20, 19, 0, 0, TimeSpan.Zero);
    }
}
