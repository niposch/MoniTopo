using System.ComponentModel;
using System.Windows;

namespace MoniTopo.App;

public partial class FirstRunWindow : Window
{
    private bool _finished;

    public FirstRunWindow(string? portableWarning)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(portableWarning))
        {
            PortableWarningText.Text = portableWarning;
            PortableWarningText.Visibility = Visibility.Visible;
        }
    }

    public bool StartAtLogin => StartAtLoginCheckBox.IsChecked == true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        _finished = true;
        DialogResult = true;
    }
}
