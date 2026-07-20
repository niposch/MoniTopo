using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace MoniTopo.App.Tests;

public sealed class MainWindowBindingTests
{
    [Fact]
    public void ReadOnlySettingsUseOneWayBindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                var settings = Descendants<CheckBox>((DependencyObject)window.Content)
                    .Where(checkBox => checkBox.Content is "Run MoniTopo when I sign in" or "Check for updates automatically")
                    .ToArray();

                Assert.Equal(2, settings.Length);
                foreach (var checkBox in settings)
                {
                    var binding = Assert.IsType<Binding>(
                        BindingOperations.GetBindingBase(checkBox, ToggleButton.IsCheckedProperty));
                    Assert.Equal(BindingMode.OneWay, binding.Mode);
                }

                window.AllowClose();
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF binding test did not complete.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
