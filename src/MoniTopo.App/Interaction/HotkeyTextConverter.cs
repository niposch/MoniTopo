using System.Globalization;
using System.Windows.Data;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Interaction;

public sealed class HotkeyTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        HotkeyDisplayText.Format(value as HotkeyBinding);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
