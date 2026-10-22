using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MoniTopo.Core.Models;
using MoniTopo.Core.Updates;
using MoniTopo.Windows.Shell;
using Forms = System.Windows.Forms;

namespace MoniTopo.App.Popup;

public partial class PopupWindow : Window
{
    private bool _closed;
    public PopupWindow(string versionText = "Development build")
    {
        InitializeComponent();
        ViewModel = new PopupViewModel(versionText);
        DataContext = ViewModel;
        ContentRendered += OnContentRendered;
    }

    public PopupViewModel ViewModel { get; }

    public event EventHandler<Guid>? ActivateProfileRequested;

    public event EventHandler? MainWindowRequested;

    public event EventHandler? UpdateActionRequested;

    public event EventHandler<AvailableUpdate>? ReleaseNotesRequested;

    public void ShowProfiles(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId)
    {
        PreparePlacement();
        ViewModel.Load(profiles, activeProfileId);
        Show();
        PlaceNearNotificationArea();
        Activate();
        ProfileList.Focus();
        if (ViewModel.SelectedIndex >= 0)
        {
            ProfileList.ScrollIntoView(ProfileList.SelectedItem);
        }
    }

    public void Toggle(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowProfiles(profiles, activeProfileId);
        }
    }

    public void RefreshPlacement()
    {
        if (_closed || !IsVisible) return;
        PreparePlacement();
        PlaceNearNotificationArea();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (!ProfileList.IsKeyboardFocusWithin)
        {
            return;
        }
        else if (e.Key == Key.Left)
        {
            ViewModel.MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            ViewModel.MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ViewModel.MoveSelection(-ColumnCount);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            ViewModel.MoveSelection(ColumnCount);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !ViewModel.IsBusy)
        {
            RequestSelectedActivation();
            e.Handled = true;
        }
    }

    private int ColumnCount => Math.Max(1, (int)(ProfileList.ActualWidth / 114));

    private void OnProfileClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ProfileList, source) is ListBoxItem item)
        {
            ProfileList.SelectedItem = item.DataContext;
            RequestSelectedActivation();
            e.Handled = true;
        }
    }

    private void OnMainWindowClick(object sender, RoutedEventArgs e)
    {
        Hide();
        MainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) =>
        UpdateActionRequested?.Invoke(this, EventArgs.Empty);

    private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanViewReleaseNotes || ViewModel.AvailableUpdate is not { } update) return;
        // Hide before opening a modal window; deactivation normally dismisses this popup.
        Hide();
        ReleaseNotesRequested?.Invoke(this, update);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            Hide();
        }
    }

    private void RequestSelectedActivation()
    {
        if (!ViewModel.IsBusy && ViewModel.SelectedProfile is { } selected)
        {
            ActivateProfileRequested?.Invoke(this, selected.Id);
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // WPF queues this callback; shutdown can close the window before it runs.
        if (!_closed && IsVisible) PlaceNearNotificationArea();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private Forms.Screen? _placementScreen;
    private PixelRect _anchor;

    private void PreparePlacement()
    {
        var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        _placementScreen = screen;
        var bounds = ToPixelRect(screen.Bounds);
        var workArea = ToPixelRect(screen.WorkingArea);
        _anchor = TrayPopupPlacement.DefaultNotificationAnchor(bounds, workArea);
        var center = new PixelPoint(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2);
        var scale = DesktopWindowPlacement.GetDpi(center) / 96d;
        MaxHeight = Math.Max(100, (workArea.Height - 16) / scale);
        Width = Math.Min(360, (workArea.Width - 16) / scale);
        // Move the HWND first so WPF measures using the destination monitor's DPI.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        DesktopWindowPlacement.MoveWindow(handle, new PixelPoint(workArea.Right - 1, workArea.Bottom - 1));
    }

    private void PlaceNearNotificationArea()
    {
        if (_placementScreen is null)
        {
            PreparePlacement();
        }

        var workArea = ToPixelRect(_placementScreen!.WorkingArea);
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxHeight = Math.Max(100, (workArea.Height - 16) / dpi.DpiScaleY);
        UpdateLayout();
        var popupSize = new PixelSize(
            checked((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            checked((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
        var placement = TrayPopupPlacement.CalculatePixels(_anchor, workArea, popupSize);
        DesktopWindowPlacement.MoveWindow(new WindowInteropHelper(this).Handle, placement);
    }

    private static PixelRect ToPixelRect(System.Drawing.Rectangle rectangle) => new(
        rectangle.Left,
        rectangle.Top,
        rectangle.Right,
        rectangle.Bottom);
}
