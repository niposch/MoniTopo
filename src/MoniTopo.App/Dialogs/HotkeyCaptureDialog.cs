using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoniTopo.Core.Models;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace MoniTopo.App.Dialogs;

public sealed class HotkeyCaptureDialog : Window
{
    private readonly TextBlock _captured;

    public HotkeyCaptureDialog(HotkeyBinding? current, string title = "Profile hotkey", bool allowRemove = true)
    {
        SelectedBinding = current;
        Title = title;
        Width = 390;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _captured = new TextBlock
        {
            Text = current is null ? "Press a modifier and key" : "Press a replacement hotkey",
            FontSize = 16,
            Margin = new Thickness(0, 10, 0, 14),
        };
        var remove = new Button { Content = "Remove hotkey", MinWidth = 100 };
        remove.Click += (_, _) => { SelectedBinding = null; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (allowRemove)
        {
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Press the desired hotkey. Ctrl, Alt, Shift, or Win is required.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_captured);
        content.Children.Add(buttons);
        Content = content;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public HotkeyBinding? SelectedBinding { get; private set; }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (modifiers == HotkeyModifiers.None || virtualKey is < 1 or > 0xFE)
        {
            _captured.Text = "Include at least one modifier key.";
            e.Handled = true;
            return;
        }

        SelectedBinding = new HotkeyBinding(modifiers, virtualKey);
        DialogResult = true;
        e.Handled = true;
    }
}
