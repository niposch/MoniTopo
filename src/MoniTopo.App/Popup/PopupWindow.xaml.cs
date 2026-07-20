using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Shell;
using Forms = System.Windows.Forms;

namespace MoniTopo.App.Popup;

public partial class PopupWindow : Window
{
    public PopupWindow()
    {
        InitializeComponent();
        ViewModel = new PopupViewModel();
        DataContext = ViewModel;
        ContentRendered += OnContentRendered;
    }

    public PopupViewModel ViewModel { get; }

    public event EventHandler<Guid>? ActivateProfileRequested;

    public event EventHandler? SettingsRequested;

    public void ShowProfiles(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId)
    {
        ViewModel.Load(profiles, activeProfileId);
        Show();
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
        else if (e.Key == Key.Up)
        {
            ViewModel.MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            ViewModel.MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !ViewModel.IsBusy)
        {
            RequestSelectedActivation();
            e.Handled = true;
        }
    }

    private void OnProfileDoubleClick(object sender, MouseButtonEventArgs e) => RequestSelectedActivation();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => OnSettingsClick(sender, e);

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            Hide();
        }
    }

    private void RequestSelectedActivation()
    {
        if (ViewModel.SelectedProfile is { } selected)
        {
            ActivateProfileRequested?.Invoke(this, selected.Id);
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        var screen = Forms.Screen.PrimaryScreen ?? throw new InvalidOperationException("Windows did not report a primary screen.");
        var bounds = ToPixelRect(screen.Bounds);
        var workArea = ToPixelRect(screen.WorkingArea);
        var anchor = TrayPopupPlacement.DefaultNotificationAnchor(bounds, workArea);
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxHeight = Math.Max(240, (workArea.Height - 16) / dpi.DpiScaleY);
        UpdateLayout();
        var popupSize = new PixelSize(
            checked((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            checked((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
        var placement = TrayPopupPlacement.Calculate(anchor, workArea, popupSize, checked((uint)Math.Round(dpi.PixelsPerInchX)));
        Left = placement.LeftDip;
        Top = placement.TopDip;
    }

    private static PixelRect ToPixelRect(System.Drawing.Rectangle rectangle) => new(
        rectangle.Left,
        rectangle.Top,
        rectangle.Right,
        rectangle.Bottom);
}
