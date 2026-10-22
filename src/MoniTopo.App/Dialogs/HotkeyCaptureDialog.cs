using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoniTopo.App.Interaction;
using MoniTopo.Core.Models;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace MoniTopo.App.Dialogs;

public sealed class HotkeyCaptureDialog : Window
{
    private readonly TextBlock _captured;
    private readonly TextBlock _warning;
    private readonly Button _confirm;
    private HotkeyBinding? _pending;

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
            Text = current is null ? "Press a key or combination" : $"Current: {HotkeyDisplayText.Format(current)}",
            FontSize = 16,
            Margin = new Thickness(0, 10, 0, 14),
        };
        _warning = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), Visibility = Visibility.Collapsed };
        _confirm = new Button { Content = "Use key globally", MinWidth = 110, Visibility = Visibility.Collapsed, IsDefault = true };
        _confirm.Click += (_, _) =>
        {
            if (_pending is not null) { SelectedBinding = _pending; DialogResult = true; }
        };
        var remove = new Button { Content = "Remove hotkey", MinWidth = 100 };
        remove.Click += (_, _) => { SelectedBinding = null; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_confirm);
        if (allowRemove)
        {
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Press a key or key combination. Escape cancels; Tab moves between buttons.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_captured);
        content.Children.Add(_warning);
        content.Children.Add(buttons);
        Content = content;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public HotkeyBinding? SelectedBinding { get; private set; }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Tab) return;
        if (e.IsRepeat) { e.Handled = true; return; }
        if (_pending is not null && key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) return;
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
        if (virtualKey is < 1 or > 0xFE)
        {
            _captured.Text = "That key cannot be used as a hotkey.";
            e.Handled = true;
            return;
        }

        var binding = new HotkeyBinding(modifiers, virtualKey);
        e.Handled = true;
        CaptureBinding(binding);
    }

    internal void CaptureBinding(HotkeyBinding binding)
    {
        if (binding.Modifiers == HotkeyModifiers.None)
        {
            _pending = binding;
            var name = HotkeyDisplayText.Format(binding);
            _captured.Text = name;
            _warning.Text = $"Use {name} without a modifier? MoniTopo will intercept this key system-wide while it is running, so it may not work for typing or shortcuts in other apps.";
            _warning.Visibility = Visibility.Visible;
            _confirm.Content = $"Use {name} globally";
            _confirm.Visibility = Visibility.Visible;
            return;
        }

        SelectedBinding = binding;
        DialogResult = true;
    }
}
