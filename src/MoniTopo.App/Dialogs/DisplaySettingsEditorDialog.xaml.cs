using System.Windows;
using MoniTopo.App.Profiles;
using MoniTopo.App.Tray;

namespace MoniTopo.App.Dialogs;

public partial class DisplaySettingsEditorDialog : Window
{
    public DisplaySettingsEditorDialog(DisplaySettingsEditorViewModel viewModel)
    {
        InitializeComponent();
        Icon = ApplicationIcon.WindowIcon;
        DataContext = viewModel;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
