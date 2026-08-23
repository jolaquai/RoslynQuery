using System;
using System.Globalization;
using System.Windows.Data;

using Microsoft.VisualStudio.Imaging;

using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// Result glyph per target kind. Public because compiled XAML cannot instantiate an internal type
/// without the generated internal type helper.
/// </summary>
public sealed class TargetMonikerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TargetKind kind) return KnownMonikers.CodeInformation;

        switch (kind)
        {
            case TargetKind.SyntaxToken: return KnownMonikers.Parameter;
            case TargetKind.Operation: return KnownMonikers.Operator;
            default: return KnownMonikers.CodeInformation;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
