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
                    .Where(checkBox => checkBox.Content is
                        "Run MoniTopo when I sign in" or
                        "Show the main window when MoniTopo starts" or
                        "Check for updates automatically once per day")
                    .ToArray();

                Assert.Equal(3, settings.Length);
                foreach (var checkBox in settings)
                {
                    var binding = Assert.IsType<Binding>(
                        BindingOperations.GetBindingBase(checkBox, ToggleButton.IsCheckedProperty));
                    Assert.Equal(BindingMode.OneWay, binding.Mode);
                }

                var tabs = Descendants<TabItem>((DependencyObject)window.Content)
                    .Select(tab => tab.Header?.ToString())
                    .ToArray();
                Assert.Contains("Profiles", tabs);
                Assert.Contains("Settings", tabs);

                var versionLabel = Descendants<TextBlock>((DependencyObject)window.Content)
                    .Select(textBlock => BindingOperations.GetBindingBase(textBlock, TextBlock.TextProperty))
                    .OfType<Binding>()
                    .Single(binding => binding.Path.Path == "CurrentVersionText");
                Assert.Equal(BindingMode.OneWay, versionLabel.Mode);

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
