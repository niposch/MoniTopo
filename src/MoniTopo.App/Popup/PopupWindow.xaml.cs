using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Shell;
using Forms = System.Windows.Forms;

namespace MoniTopo.App.Popup;

public partial class PopupWindow : Window
{
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

    private void OnContentRendered(object? sender, EventArgs e) => PlaceNearNotificationArea();

    private Forms.Screen? _placementScreen;
    private PixelRect _anchor;

    private void PreparePlacement()
    {
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        _placementScreen = screen;
        var bounds = ToPixelRect(screen.Bounds);
        var workArea = ToPixelRect(screen.WorkingArea);
        _anchor = screen.Bounds.Contains(cursor) && !screen.WorkingArea.Contains(cursor)
            ? new PixelRect(cursor.X, cursor.Y, cursor.X + 1, cursor.Y + 1)
            : TrayPopupPlacement.DefaultNotificationAnchor(bounds, workArea);
        var scale = DesktopWindowPlacement.GetDpi(new PixelPoint(cursor.X, cursor.Y)) / 96d;
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
