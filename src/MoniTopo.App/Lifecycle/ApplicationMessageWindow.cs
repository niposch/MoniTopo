using System.Windows.Interop;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Lifecycle;

public sealed class ApplicationMessageWindow : IDisposable
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDeviceChange = 0x0219;
    private static readonly nint MessageOnlyWindow = (nint)(-3);
    private readonly HwndSource _source;
    private GlobalHotkeyService? _hotkeys;

    public ApplicationMessageWindow()
    {
        var parameters = new HwndSourceParameters("MoniTopo.MessageWindow")
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);
    }

    public nint Handle => _source.Handle;

    public event EventHandler? DisplayStateChanged;

    public void AttachHotkeys(GlobalHotkeyService hotkeys) => _hotkeys = hotkeys;

    public void Dispose()
    {
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_hotkeys?.ProcessWindowMessage(message, wParam) == true)
        {
            handled = true;
            return nint.Zero;
        }

        if (message is WmDisplayChange or WmSettingChange or WmDeviceChange)
        {
            DisplayStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }
}
