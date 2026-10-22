using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoniTopo.App.Popup;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.App.Tray;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Tests;

[Collection(WpfUiTestCollectionDefinition.Name)]
public sealed class ProfilePresentationTests
{
    [Fact]
    public void ClosingPopupBeforeQueuedContentRenderCannotRecreateItsHandle()
    {
        StaDispatcherTest.Run(async () =>
        {
            var window = new PopupWindow { ShowActivated = false };
            window.Show();
            window.Close();
            window.RefreshPlacement();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        });
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedPopupTileUsesLegibleThemeTextAndRealSchematicWithoutDisplayCount(bool dark)
    {
        StaDispatcherTest.Run(() =>
        {
            var window = new PopupWindow { ShowInTaskbar = false, ShowActivated = false };
            var primary = dark ? Colors.White : Colors.Black;
            window.Resources["TextFillColorPrimaryBrush"] = new SolidColorBrush(primary);
            window.Resources["TextFillColorSecondaryBrush"] = new SolidColorBrush(dark ? Colors.LightGray : Colors.DarkSlateGray);
            window.Resources["SolidBackgroundFillColorBaseBrush"] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
            window.ViewModel.Load([LayoutSchematicTests.SyntheticProfile()], null);
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = (ListBox)window.FindName("ProfileList");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.True(item.IsSelected);
                var text = Descendants<TextBlock>(item).ToArray();
                Assert.Equal(primary, ((SolidColorBrush)text.Single(block => block.Text == "Desktop").Foreground).Color);
                Assert.Contains(text, block => block.Text == "F8");
                Assert.DoesNotContain(text, block => block.Text.Contains("displays", StringComparison.Ordinal));
                Assert.Contains(Descendants<ItemsControl>(item), control => control.Items.Count == 2);
                Assert.Contains(Descendants<TextBlock>(window), block => block.Text == "MoniTopo");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MainListShowsHotkeyActionsPrecedePreviewAndSharedIconLoads()
    {
        StaDispatcherTest.Run(() =>
        {
            var profile = LayoutSchematicTests.SyntheticProfile();
            using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault() with
            { Profiles = [profile], ProfileOrder = [profile.Id] });
            using var updates = new UpdateCoordinator(new NoUpdates(), session);
            using var vm = new MainWindowViewModel(session, updates);
            var window = new MainWindow { DataContext = vm, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(window), block => block.Text == "F8");
                var actions = (WrapPanel)window.FindName("ProfileActions");
                var preview = (Border)window.FindName("PreviewHost");
                var parent = (StackPanel)actions.Parent;
                Assert.True(parent.Children.IndexOf(actions) < parent.Children.IndexOf(preview));
                Assert.NotNull(window.Icon);
                window.ShowSettingsPage();
                var theme = (ComboBox)window.FindName("ThemeSelector");
                Assert.Equal(3, theme.Items.Count);
                Assert.Equal(ThemePreference.System, theme.SelectedValue);
                using var trayIcon = ApplicationIcon.CreateTrayIcon();
                Assert.Equal(32, trayIcon.Width);
                Assert.Equal(32, trayIcon.Height);
            }
            finally { window.AllowClose(); window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class MemoryStore : IConfigurationStore
    {
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoUpdates : IUpdateClient
    {
        public bool IsInstalled => false;
        public string? CurrentPackageVersion => null;
        public AvailableUpdate? PendingRestart => null;
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void ApplyAndRestart(AvailableUpdate update) => throw new InvalidOperationException();
    }
}
