using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MoniTopo.App.Popup;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Tests;

[Collection(WpfUiTestCollectionDefinition.Name)]
public sealed class WindowStartupSmokeTests
{
    [Fact]
    public void MainWindowCanBeShownWithItsCompleteViewModel()
    {
        RunInSta(() =>
        {
            using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault());
            using var updates = new UpdateCoordinator(new FakeUpdateClient(), session);
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(session, updates),
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            window.Show();
            window.UpdateLayout();
            window.ShowSettingsPage();
            var navigation = LogicalDescendants<TabControl>(window).Single();
            Assert.Equal("Settings", ((TabItem)navigation.SelectedItem).Header);
            window.ShowProfilesPage();
            window.Width = 740;
            window.UpdateLayout();
            Assert.Equal(1, Grid.GetRow((ScrollViewer)window.FindName("ProfileDetails")));
            window.Width = 980;
            window.UpdateLayout();
            Assert.Equal(2, Grid.GetColumn((ScrollViewer)window.FindName("ProfileDetails")));
            window.AllowClose();
            window.Close();
        });
    }

    [Fact]
    public void PopupCanBeShownWithARealizedProfileRow()
    {
        RunInSta(() =>
        {
            var window = new PopupWindow { ShowActivated = false, ShowInTaskbar = false };
            window.ViewModel.Profiles.Add(new PopupProfileItem(
                Guid.NewGuid(),
                "Desktop",
                2,
                "Ctrl+Alt+1",
                IsActive: true));
            window.ViewModel.SelectedIndex = 0;
            window.ViewModel.ApplyUpdateState(new UpdateState(
                UpdateStatus.Available,
                new AvailableUpdate("2026.721.0", "2026.07.21", null),
                0,
                "Available"));
            var mainRequested = false;
            var updateRequested = false;
            window.MainWindowRequested += (_, _) => mainRequested = true;
            window.UpdateActionRequested += (_, _) => updateRequested = true;

            window.Show();
            window.UpdateLayout();
            var buttons = LogicalDescendants<Button>(window).ToArray();
            buttons.Single(button => AutomationProperties.GetName(button) == "Open MoniTopo")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(button => AutomationProperties.GetName(button) == "Update MoniTopo")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(mainRequested);
            Assert.True(updateRequested);
            window.Close();
        });
    }

    [Fact]
    public void FirstRunWindowCanCompleteItsDialogLifecycle()
    {
        RunInSta(() =>
        {
            var window = new FirstRunWindow("Portable warning");
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
            {
                var finish = LogicalDescendants<Button>(window).Single(button => Equals(button.Content, "Finish"));
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            Assert.True(window.ShowDialog());
            Assert.True(window.StartAtLogin);
        });
    }

    [Fact]
    public void PopupRoutesEnterToFooterButtonsAndProfileActivationOnlyInsideList()
    {
        RunInSta(() =>
        {
            var window = new PopupWindow { ShowInTaskbar = false, ShowActivated = false };
            window.ViewModel.Profiles.Add(new PopupProfileItem(Guid.NewGuid(), "Desktop", 2, "", true));
            window.ViewModel.Profiles.Add(new PopupProfileItem(Guid.NewGuid(), "Movie", 1, "", false));
            window.ViewModel.SelectedIndex = 0;
            window.ViewModel.ApplyUpdateState(new UpdateState(UpdateStatus.Available,
                new AvailableUpdate("2026.721.0", "2026.07.21", null), 0, "Available"));
            var activations = 0;
            var opens = 0;
            var updates = 0;
            window.ActivateProfileRequested += (_, _) => activations++;
            window.MainWindowRequested += (_, _) => opens++;
            window.UpdateActionRequested += (_, _) => updates++;
            window.Show();
            window.UpdateLayout();
            var buttons = LogicalDescendants<Button>(window).ToArray();
            var open = buttons.Single(button => AutomationProperties.GetName(button) == "Open MoniTopo");
            var update = buttons.Single(button => AutomationProperties.GetName(button) == "Update MoniTopo");
            Keyboard.Focus(update);
            RaiseKey(update, window, Key.Right, Keyboard.PreviewKeyDownEvent);
            Assert.Equal(0, window.ViewModel.SelectedIndex);
            Assert.False(RaiseKey(update, window, Key.Enter, Keyboard.PreviewKeyDownEvent).Handled);
            RaiseKey(update, window, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal(1, updates);
            Assert.Equal(0, activations);
            Keyboard.Focus(open);
            Assert.False(RaiseKey(open, window, Key.Enter, Keyboard.PreviewKeyDownEvent).Handled);
            RaiseKey(open, window, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal(1, opens);
            Assert.Equal(0, activations);
            window.Show();
            var list = LogicalDescendants<ListBox>(window).Single();
            Keyboard.Focus(list);
            Assert.True(RaiseKey(list, window, Key.Enter, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, activations);
            window.Close();
        });
    }

    private static KeyEventArgs RaiseKey(UIElement target, Window window, Key key, RoutedEvent routedEvent)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
        {
            RoutedEvent = routedEvent,
        };
        target.RaiseEvent(args);
        return args;
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The WPF startup smoke test did not complete.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in LogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FakeUpdateClient : IUpdateClient
    {
        public bool IsInstalled => false;

        public string? CurrentPackageVersion => "2026.720.0";

        public AvailableUpdate? PendingRestart => null;

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AvailableUpdate?>(null);

        public Task DownloadAsync(
            AvailableUpdate update,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ApplyAndRestart(AvailableUpdate update)
        {
        }
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationConfiguration.CreateDefault());

        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
