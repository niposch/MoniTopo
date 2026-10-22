using System.Windows;
using System.Windows.Controls;
using MoniTopo.App.Dialogs;
using MoniTopo.App.Interaction;
using MoniTopo.Core.Models;
using Button = System.Windows.Controls.Button;

namespace MoniTopo.App.Tests;

[Collection(WpfUiTestCollectionDefinition.Name)]
public sealed class HotkeyCaptureDialogTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnmodifiedKeyRequiresExplicitConfirmationAndCancelRetainsPreviousBinding(bool accept)
    {
        StaDispatcherTest.Run(() =>
        {
            var previous = new HotkeyBinding(HotkeyModifiers.Control, 0x31);
            var next = new HotkeyBinding(HotkeyModifiers.None, 0x77);
            var dialog = new HotkeyCaptureDialog(previous);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                dialog.CaptureBinding(next);
                Assert.True(dialog.IsVisible);
                Assert.Equal(previous, dialog.SelectedBinding);
                var panel = (StackPanel)dialog.Content;
                Assert.Contains(panel.Children.OfType<TextBlock>(), text => text.Text.Contains("system-wide", StringComparison.Ordinal));
                var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>();
                var action = buttons.Single(button => Equals(button.Content, accept ? "Use F8 globally" : "Cancel"));
                if (accept) action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.DialogResult = false;
            });
            Assert.Equal(accept, dialog.ShowDialog());
            Assert.Equal(accept ? next : previous, dialog.SelectedBinding);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ModifiedCombinationKeepsImmediateAcceptance()
    {
        StaDispatcherTest.Run(() =>
        {
            var next = new HotkeyBinding(HotkeyModifiers.Alt, 0x77);
            var dialog = new HotkeyCaptureDialog(null);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.CaptureBinding(next));
            Assert.True(dialog.ShowDialog());
            Assert.Equal(next, dialog.SelectedBinding);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(0x77, "F8")]
    [InlineData(0x87, "F24")]
    [InlineData(0x20, "Space")]
    [InlineData(0x41, "A")]
    public void SingleKeyNamesAreReadable(int key, string name) =>
        Assert.Equal(name, HotkeyDisplayText.Format(new HotkeyBinding(HotkeyModifiers.None, key)));
}
