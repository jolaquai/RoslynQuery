using System;
using System.ComponentModel;
using System.Globalization;

namespace RoslynQuery.Options;

/// <summary>
/// Spells <see cref="MetadataNavigationMode"/> out in the options grid. The grid persists whatever this
/// writes, so <see cref="ConvertFrom"/> also accepts the bare enum name that older settings hold.
/// </summary>
internal sealed class MetadataNavigationModeConverter : EnumConverter
{
    private const string IlspyText = "Open in ILSpy";
    private const string VisualStudioText = "Decompile in Visual Studio";

    public MetadataNavigationModeConverter() : base(typeof(MetadataNavigationMode))
    {
    }

    public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is MetadataNavigationMode mode)
            return mode == MetadataNavigationMode.Ilspy ? IlspyText : VisualStudioText;

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string text)
        {
            if (string.Equals(text, IlspyText, StringComparison.OrdinalIgnoreCase)) return MetadataNavigationMode.Ilspy;
            if (string.Equals(text, VisualStudioText, StringComparison.OrdinalIgnoreCase)) return MetadataNavigationMode.VisualStudio;
            if (string.Equals(text, nameof(MetadataNavigationMode.Ilspy), StringComparison.OrdinalIgnoreCase)) return MetadataNavigationMode.Ilspy;
            if (string.Equals(text, nameof(MetadataNavigationMode.VisualStudio), StringComparison.OrdinalIgnoreCase)) return MetadataNavigationMode.VisualStudio;
        }

        return base.ConvertFrom(context, culture, value);
    }
}
