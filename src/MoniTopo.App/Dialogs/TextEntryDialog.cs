using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace MoniTopo.App.Dialogs;

public sealed class TextEntryDialog : Window
{
    private readonly TextBox _textBox;

    public TextEntryDialog(string title, string prompt, string initialValue = "")
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _textBox = new TextBox { Text = initialValue, Margin = new Thickness(0, 6, 0, 12), MaxLength = 64 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_textBox);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => { _textBox.Focus(); _textBox.SelectAll(); };
    }

    public string Value => _textBox.Text;
}
