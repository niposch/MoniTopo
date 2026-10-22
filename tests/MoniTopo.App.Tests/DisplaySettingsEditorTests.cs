using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoniTopo.App.Dialogs;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Persistence;
using MoniTopo.Windows.Display;

namespace MoniTopo.App.Tests;

public sealed class DisplaySettingsEditorTests
{
    [Fact]
    public void ResolutionFiltersRefreshChoicesAndUnavailableDisplayPreservesSavedValues()
    {
        var fixture = new Fixture();
        var vm = new DisplaySettingsEditorViewModel(fixture.Profile, fixture.Caps.Values);
        var item = Assert.Single(vm.Displays);
        Assert.False(vm.HasChanges);
        item.SelectedResolution = item.Resolutions.Single(option => option.Value == new DisplaySize(1280, 720));
        Assert.Single(item.RefreshRates);
        Assert.Equal(50, item.SelectedRefreshRate.Value.RefreshRate.Hertz);
        Assert.True(vm.HasChanges);
        var unavailable = new DisplaySettingsEditorViewModel(fixture.Profile, []);
        item = Assert.Single(unavailable.Displays);
        Assert.False(item.CanEditMode); Assert.False(item.CanEditHdr); Assert.False(item.CanEditScale);
        Assert.False(unavailable.HasChanges);
        Assert.Equal(ProfileSettingsEditor.ModeOf(fixture.Profile.Displays[0]), item.GetEdit().Mode);
    }

    [Fact]
    public async Task SavingRechecksCapabilitiesAndPreservesConcurrentRenameAndHotkeyWithoutCapturingOrActivating()
    {
        var fixture = new Fixture();
        await fixture.Service.GetSettingsCapabilitiesAsync(fixture.Profile);
        var edit = fixture.Edits()[0] with { ScalePercent = 150, HdrEnabled = true };
        var hotkey = new HotkeyBinding(HotkeyModifiers.None, 0x77);
        await fixture.Service.RenameAsync(fixture.Profile.Id, "Renamed");
        await fixture.Service.SetHotkeyAsync(fixture.Profile.Id, hotkey);
        var updated = await fixture.Service.SaveDisplaySettingsAsync(fixture.Profile, [edit]);
        Assert.Equal(2, fixture.Caps.Queries);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(hotkey, updated.DirectHotkey);
        Assert.Equal(150, updated.Displays[0].WindowsUiScalePercent);
        Assert.True(updated.Displays[0].HdrEnabled);
        Assert.Equal(fixture.Profile.Displays[0].TargetSignal, updated.Displays[0].TargetSignal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleOptionsOrConcurrentDisplayEditsCannotBeSaved(bool staleOptions)
    {
        var fixture = new Fixture();
        var edit = fixture.Edits()[0] with { ScalePercent = 150 };
        if (staleOptions) fixture.Caps.Values = [];
        else await fixture.Session.UpdateAsync(current => current with
        { Profiles = [fixture.Profile with { Displays = [fixture.Profile.Displays[0] with { HdrEnabled = true }] }] });
        var before = fixture.Session.Current;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveDisplaySettingsAsync(fixture.Profile, [edit]));
        Assert.Same(before, fixture.Session.Current);
    }

    [Fact]
    public async Task FailedSavePreservesOriginalProfileAndSchemaThreeRequiresRecapture()
    {
        var fixture = new Fixture();
        fixture.Store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.SaveDisplaySettingsAsync(fixture.Profile,
            [fixture.Edits()[0] with { HdrEnabled = true }]));
        Assert.Same(fixture.Profile, fixture.Session.Current.Profiles[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetSettingsCapabilitiesAsync(fixture.Profile with { CaptureSchemaVersion = 3 }));
    }

    [Fact]
    public void ActualEditorUsesNonEditableDropdownsAndSavesOnlyAfterExplicitConfirmation()
    {
        StaDispatcherTest.Run(() =>
        {
            var fixture = new Fixture();
            var vm = new DisplaySettingsEditorViewModel(fixture.Profile, fixture.Caps.Values);
            var dialog = new DisplaySettingsEditorDialog(vm);
            Exception? failure = null;
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    dialog.UpdateLayout();
                    var combos = Descendants<ComboBox>(dialog).ToArray();
                    Assert.Equal(4, combos.Length);
                    Assert.All(combos, combo => Assert.False(combo.IsEditable));
                    var save = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Save profile"));
                    Assert.False(save.IsEnabled);
                    var resolution = combos.Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "Resolution");
                    resolution.SelectedItem = vm.Displays[0].Resolutions.Single(option => option.Value == new DisplaySize(1280, 720));
                    dialog.UpdateLayout();
                    Assert.Equal(50, vm.Displays[0].SelectedRefreshRate.Value.RefreshRate.Hertz);
                    Assert.NotNull(combos.Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "Refresh rate").SelectedItem);
                    vm.Displays[0].SelectedHdr = vm.Displays[0].HdrStates.Single(option => option.Value);
                    dialog.UpdateLayout();
                    Assert.True(save.IsEnabled);
                    Assert.False(fixture.Session.Current.Profiles[0].Displays[0].HdrEnabled);
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { failure = exception; dialog.Close(); }
            });
            var result = dialog.ShowDialog();
            if (failure is not null) throw failure;
            Assert.True(result);
            Assert.True(vm.HasChanges);
            Assert.Equal(0, fixture.Store.Saves);
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class Fixture
    {
        internal MemoryStore Store { get; } = new();
        internal Capabilities Caps { get; }
        internal DisplayProfile Profile { get; }
        internal ConfigurationSession Session { get; }
        internal ProfileManagementService Service { get; }
        internal Fixture()
        {
            var profile = LayoutSchematicTests.SyntheticProfile();
            var display = profile.Displays[0] with
            {
                TargetSignal = new DisplayTargetSignal(148500000, new RefreshRate(67500, 1), new RefreshRate(60, 1),
                new DisplaySize(3440, 1440), new DisplaySize(3600, 1480), 0, 1),
                BoostRefreshRate = false
            };
            Profile = profile with { Displays = [display], CaptureSchemaVersion = 4 };
            Session = new ConfigurationSession(Store, ApplicationConfiguration.CreateDefault() with { Profiles = [Profile], ProfileOrder = [Profile.Id] });
            Caps = new Capabilities
            {
                Values = [new DisplaySettingsCapability(display.DisplayId,
                [ProfileSettingsEditor.ModeOf(display), new AvailableDisplayMode(new DisplaySize(1280, 720), new RefreshRate(50, 1), 1)], [100, 150], [false, true], null)]
            };
            Service = new ProfileManagementService(Session, new NoCapture(), new ActiveProfileMatcher(new MonitorIdentityResolver()), capabilities: Caps);
        }
        internal DisplaySettingsEdit[] Edits() => [new(Profile.Displays[0].DisplayId, ProfileSettingsEditor.ModeOf(Profile.Displays[0]), 100, false)];
    }
    private sealed class Capabilities : IDisplaySettingsCapabilities
    {
        internal int Queries;
        internal IReadOnlyList<DisplaySettingsCapability> Values = [];
        public Task<IReadOnlyList<DisplaySettingsCapability>> QueryAsync(DisplayProfile profile, CancellationToken cancellationToken = default)
        { Queries++; return Task.FromResult(Values); }
    }
    private sealed class NoCapture : IDisplayCaptureService
    {
        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Editing must not recapture or apply the desktop.");
    }
    private sealed class MemoryStore : IConfigurationStore
    {
        internal bool Fail;
        internal int Saves;
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        { if (Fail) throw new IOException("Synthetic save failure"); Saves++; return Task.CompletedTask; }
    }
}
