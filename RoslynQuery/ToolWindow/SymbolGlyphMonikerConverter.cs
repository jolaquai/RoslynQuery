using System;
using System.Globalization;
using System.Windows.Data;

using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

using RoslynQuery.ReferenceGraph;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// Reference graph glyph per <see cref="SymbolGlyph"/>. Public because compiled XAML cannot
/// instantiate an internal type without the generated internal type helper.
/// </summary>
public sealed class SymbolGlyphMonikerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SymbolGlyph glyph ? For(glyph) : KnownMonikers.CodeInformation;

    internal static ImageMoniker For(SymbolGlyph glyph)
    {
        switch (glyph)
        {
            case SymbolGlyph.Method: return KnownMonikers.Method;
            // The catalog has no constructor glyph; NewClass is what "make one of these" looks like.
            case SymbolGlyph.Constructor: return KnownMonikers.NewClass;
            case SymbolGlyph.Property: return KnownMonikers.Property;
            case SymbolGlyph.Field: return KnownMonikers.Field;
            case SymbolGlyph.Event: return KnownMonikers.Event;
            case SymbolGlyph.Constant: return KnownMonikers.Constant;
            case SymbolGlyph.EnumMember: return KnownMonikers.EnumerationItemPublic;
            case SymbolGlyph.Class: return KnownMonikers.Class;
            case SymbolGlyph.Structure: return KnownMonikers.Structure;
            case SymbolGlyph.Interface: return KnownMonikers.Interface;
            case SymbolGlyph.Enumeration: return KnownMonikers.Enumeration;
            case SymbolGlyph.Delegate: return KnownMonikers.Delegate;
            case SymbolGlyph.IncomingBranch: return KnownMonikers.CallTo;
            case SymbolGlyph.OutgoingBranch: return KnownMonikers.CallFrom;
            case SymbolGlyph.Locations: return KnownMonikers.BulletList;
            case SymbolGlyph.Location: return KnownMonikers.GoToSourceCode;
            default: return KnownMonikers.CodeInformation;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
