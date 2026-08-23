using System;
using System.Globalization;
using System.Windows.Data;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// True when the value is null or an empty string - used to disable a replacement row's checkbox
/// when it carries a warning, so a match that can't actually apply can't be re-checked only to fail
/// silently again at Apply time. Public for the same reason as <see cref="TargetMonikerConverter"/>:
/// compiled XAML cannot instantiate an internal type without the generated internal type helper.
/// </summary>
public sealed class NullToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
