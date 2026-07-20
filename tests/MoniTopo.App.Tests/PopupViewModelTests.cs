using MoniTopo.App.Popup;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Tests;

public sealed class PopupViewModelTests
{
    [Fact]
    public void ActiveProfileIsSelectedWhenPopupOpens()
    {
        var first = Profile("Desktop", 1);
        var second = Profile("Movie", 2);
        var viewModel = new PopupViewModel();

        viewModel.Load([first, second], second.Id);

        Assert.Equal("Movie", viewModel.CurrentState);
        Assert.Equal(1, viewModel.SelectedIndex);
        Assert.True(viewModel.SelectedProfile?.IsActive);
    }

    [Fact]
    public void CustomStateSelectsFirstProfileWithoutActivatingIt()
    {
        var viewModel = new PopupViewModel();

        viewModel.Load([Profile("Desktop", 1), Profile("Movie", 2)], activeProfileId: null);

        Assert.Equal("Custom", viewModel.CurrentState);
        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.DoesNotContain(viewModel.Profiles, profile => profile.IsActive);
    }

    [Fact]
    public void ArrowSelectionIsBoundedAndDisabledDuringActivation()
    {
        var viewModel = new PopupViewModel();
        viewModel.Load([Profile("Desktop", 1), Profile("Movie", 2)], null);

        viewModel.MoveSelection(-1);
        Assert.Equal(0, viewModel.SelectedIndex);
        viewModel.MoveSelection(1);
        Assert.Equal(1, viewModel.SelectedIndex);
        viewModel.BeginActivation("Movie");
        viewModel.MoveSelection(-1);

        Assert.Equal(1, viewModel.SelectedIndex);
        Assert.True(viewModel.IsBusy);
        Assert.Contains("Movie", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionUpdatesInlineStateAndActiveIndicator()
    {
        var movie = Profile("Movie", 2);
        var viewModel = new PopupViewModel();
        viewModel.Load([Profile("Desktop", 1), movie], null);
        viewModel.BeginActivation(movie.Name);

        viewModel.CompleteActivation(true, "Movie activated", movie.Id);

        Assert.False(viewModel.IsBusy);
        Assert.Equal("Movie activated", viewModel.StatusMessage);
        Assert.Equal("Movie", viewModel.CurrentState);
        Assert.True(viewModel.Profiles.Single(profile => profile.Id == movie.Id).IsActive);
    }

    private static DisplayProfile Profile(string name, int index)
    {
        var identity = new MonitorIdentityFingerprint(
            $"synthetic://{index}",
            $"SYNTHETIC\\{index}",
            null,
            $"SERIAL-{index}",
            "TST",
            index,
            "Synthetic",
            600,
            340,
            DisplayOutputTechnology.DisplayPort,
            checked((uint)index),
            new DisplaySize(2560, 1440),
            "synthetic");
        var display = new DesiredDisplayPath(
            $"display-{index}",
            identity,
            $"source-{index}",
            null,
            new DisplayPoint(0, 0),
            new DisplaySize(2560, 1440),
            new RefreshRate(60, 1),
            DisplayOrientation.Landscape,
            DisplayPathScaling.Identity,
            150,
            false,
            true,
            "Synthetic");
        return new DisplayProfile(
            Guid.Parse($"10000000-0000-0000-0000-{index:D12}"),
            name,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x30 + index),
            [display],
            display.DisplayId,
            1,
            []);
    }
}
