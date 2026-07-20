using System.Drawing;
using System.Windows.Forms;
using MoniTopo.Windows.Shell;

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
        _icon = TrayIconDrawing.Create();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings", null, (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
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

    public event EventHandler? OpenSettingsRequested;

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

    private static class TrayIconDrawing
    {
        internal static Icon Create()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Color.White, 2.5f))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawRoundedRectangle(pen, new Rectangle(2, 5, 18, 13), new Size(2, 2));
                graphics.DrawLine(pen, 8, 21, 14, 21);
                graphics.DrawLine(pen, 11, 18, 11, 21);
                graphics.DrawRoundedRectangle(pen, new Rectangle(14, 14, 16, 12), new Size(2, 2));
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var borrowed = Icon.FromHandle(handle);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                NativeIconHandle.Destroy(handle);
            }
        }
    }
}
