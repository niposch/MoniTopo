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
        Assert.Equal(DisplayProfileCaptureSchema.Current, result.Profile.CaptureSchemaVersion);
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
        await fixture.Service.RememberBindingAsync(first.Id, first.Displays[0].DisplayId, fixture.Capture.Snapshot.ConnectedDisplays[0]);
        await fixture.Service.DeleteAsync(second.Id);

        Assert.Equal([first.Id], fixture.Session.Current.ProfileOrder);
        Assert.Equal("runtime-1", Assert.Single(fixture.Session.Current.Profiles[0].LastSuccessfulIdentityBindings).RuntimeIdentityKey);
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
            BoostRefreshRate = false,
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

    [Fact]
    public async Task QueuedRenameMergesIntoLatestSettingsAndSameProfileChanges()
    {
        var profile = new DisplayProfile(Guid.NewGuid(), "Desktop", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, [Display("display-1")], "display-1", DisplayProfileCaptureSchema.Current, []);
        var store = new BlockingStore();
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault() with
        { Profiles = [profile], ProfileOrder = [profile.Id] });
        var service = new ProfileManagementService(session, new FakeCapture { Snapshot = Snapshot(Display("display-1")) },
            new ActiveProfileMatcher(new MonitorIdentityResolver()));
        var hotkey = new HotkeyBinding(HotkeyModifiers.Control, 0x31);
        var settingsSave = session.UpdateAsync(current => current with
        {
            ApplicationSettings = current.ApplicationSettings with { UpdateChecksEnabled = false },
            Profiles = [profile with { DirectHotkey = hotkey }],
        });
        await store.SaveStarted.Task;
        var rename = service.RenameAsync(profile.Id, "Renamed");
        Assert.False(rename.IsCompleted);
        store.AllowSave.SetResult();
        await Task.WhenAll(settingsSave, rename);
        Assert.False(session.Current.ApplicationSettings.UpdateChecksEnabled);
        Assert.Equal("Renamed", session.Current.Profiles[0].Name);
        Assert.Equal(hotkey, session.Current.Profiles[0].DirectHotkey);
    }

    [Fact]
    public async Task ConcurrentSameNameCreatesOnlyOneProfile()
    {
        var fixture = new Fixture();
        await fixture.Service.SaveCurrentAsync("Desktop");
        fixture.Capture.Snapshot = Snapshot(Display("display-2", "SERIAL-2"));
        var captured = new TaskCompletionSource<CapturedDisplaySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Capture.Handler = _ => captured.Task;
        var first = fixture.Service.SaveCurrentAsync("Movie");
        var second = fixture.Service.SaveCurrentAsync(" movie ");
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        captured.SetResult(fixture.Capture.Snapshot);
        var outcomes = await Task.WhenAll(first.ContinueWith(task => task.Exception?.InnerException, TaskScheduler.Default),
            second.ContinueWith(task => task.Exception?.InnerException, TaskScheduler.Default));
        Assert.Single(outcomes, exception => exception is ConfigurationValidationException);
        Assert.Equal(2, fixture.Session.Current.Profiles.Count);
    }

    [Fact]
    public async Task CaptureUpdatePreservesConcurrentRenameAndHotkeyChange()
    {
        var fixture = new Fixture();
        var saved = (await fixture.Service.SaveCurrentAsync("Desktop")).Profile;
        var captured = new TaskCompletionSource<CapturedDisplaySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Capture.Handler = _ => captured.Task;
        var pending = fixture.Service.UpdateFromCurrentAsync(saved.Id);
        await fixture.Service.RenameAsync(saved.Id, "Renamed");
        var binding = new HotkeyBinding(HotkeyModifiers.Alt, 0x31);
        await fixture.Service.SetHotkeyAsync(saved.Id, binding);
        captured.SetResult(Snapshot(Display("display-1") with { WindowsUiScalePercent = 175 }));
        var updated = await pending;
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(binding, updated.DirectHotkey);
        Assert.Equal(175, updated.Displays[0].WindowsUiScalePercent);
    }

    [Fact]
    public async Task ReusedRuntimeIdCannotChangeSelectedBindingIdentity()
    {
        var fixture = new Fixture();
        var saved = (await fixture.Service.SaveCurrentAsync("Desktop")).Profile;
        var selected = fixture.Capture.Snapshot.ConnectedDisplays[0];
        fixture.Capture.Snapshot = Snapshot(Display("replacement", "OTHER-SERIAL"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RememberBindingAsync(saved.Id, saved.Displays[0].DisplayId, selected));
        Assert.Empty(fixture.Session.Current.Profiles[0].LastSuccessfulIdentityBindings);
    }

    private sealed class BlockingStore : IConfigurationStore
    {
        internal TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            await AllowSave.Task.WaitAsync(cancellationToken);
        }
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
        internal Func<CancellationToken, Task<CapturedDisplaySnapshot>>? Handler { get; set; }

        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Handler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 20, 19, 0, 0, TimeSpan.Zero);
    }
}
