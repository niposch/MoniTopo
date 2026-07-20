using System.Windows;
using System.Windows.Controls;
using MoniTopo.Core.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace MoniTopo.App.Dialogs;

public sealed class BindingResolutionDialog : Window
{
    private readonly ComboBox _saved;
    private readonly ComboBox _connected;

    public BindingResolutionDialog(DisplayProfile profile, CapturedDisplaySnapshot snapshot)
    {
        Title = "Resolve display binding";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _saved = new ComboBox { ItemsSource = profile.Displays, DisplayMemberPath = "FriendlyLabel", SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
        _connected = new ComboBox { ItemsSource = snapshot.ConnectedDisplays, DisplayMemberPath = "FriendlyLabel", SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 12) };
        var save = new Button { Content = "Remember binding", IsDefault = true, MinWidth = 110 };
        save.Click += (_, _) =>
        {
            if (_saved.SelectedItem is DesiredDisplayPath saved && _connected.SelectedItem is ConnectedDisplayState connected)
            {
                DisplayId = saved.DisplayId;
                RuntimeId = connected.RuntimeId;
                DialogResult = true;
            }
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Saved display" });
        content.Children.Add(_saved);
        content.Children.Add(new TextBlock { Text = "Currently connected display" });
        content.Children.Add(_connected);
        content.Children.Add(new TextBlock { Text = "Use this only when you can identify the physical display. Disconnected displays are not listed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(buttons);
        Content = content;
    }

    public string? DisplayId { get; private set; }

    public string? RuntimeId { get; private set; }
}
