using System.Drawing;
using System.Windows.Forms;

namespace MoniTopo.App.Tray;

public interface IUserNotificationService
{
    void ShowInformation(string message);

    void ShowError(string message);
}

public sealed class TrayIconService : IUserNotificationService, IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public TrayIconService()
    {
        _icon = ApplicationIcon.CreateTrayIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open MoniTopo", null, (_, _) => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit MoniTopo", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "MoniTopo — Custom",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += OnMouseClick;
    }

    public event EventHandler? TogglePopupRequested;

    public event EventHandler? OpenMainWindowRequested;

    public event EventHandler? ExitRequested;

    public void SetCurrentState(string? profileName)
    {
        var state = string.IsNullOrWhiteSpace(profileName) ? "Custom" : profileName.Trim();
        _notifyIcon.Text = $"MoniTopo — {state}"[..Math.Min(63, $"MoniTopo — {state}".Length)];
    }

    public void ShowInformation(string message) => ShowBalloon(message, ToolTipIcon.Info);

    public void ShowError(string message) => ShowBalloon(message, ToolTipIcon.Error);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = "MoniTopo";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void OnMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            TogglePopupRequested?.Invoke(this, EventArgs.Empty);
        }
    }

}
