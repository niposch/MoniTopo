using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoniTopo.App.Popup;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Tests;

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

            window.Show();
            window.UpdateLayout();
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
