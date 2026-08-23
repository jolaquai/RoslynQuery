using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// Collapses a bound element when the value is null or an empty string - used for the Replace list's
/// warning line, which most rows don't have. Public for the same reason as <see cref="TargetMonikerConverter"/>:
/// compiled XAML cannot instantiate an internal type without the generated internal type helper.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
