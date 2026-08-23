using System;
using System.Globalization;
using System.Linq;

using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

using RoslynQuery.ReferenceGraph;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

// Convert is a pure function over an enum, so none of this needs a WPF or VS host.
public class SymbolGlyphMonikerConverterTests
{
    private static object Convert(object value) =>
        new SymbolGlyphMonikerConverter().Convert(value, typeof(ImageMoniker), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_MapsEachGlyphToItsMoniker()
    {
        Assert.Equal(KnownMonikers.Method, Convert(SymbolGlyph.Method));
        Assert.Equal(KnownMonikers.NewClass, Convert(SymbolGlyph.Constructor));
        Assert.Equal(KnownMonikers.Property, Convert(SymbolGlyph.Property));
        Assert.Equal(KnownMonikers.Field, Convert(SymbolGlyph.Field));
        Assert.Equal(KnownMonikers.Event, Convert(SymbolGlyph.Event));
        Assert.Equal(KnownMonikers.Constant, Convert(SymbolGlyph.Constant));
        Assert.Equal(KnownMonikers.EnumerationItemPublic, Convert(SymbolGlyph.EnumMember));
        Assert.Equal(KnownMonikers.Class, Convert(SymbolGlyph.Class));
        Assert.Equal(KnownMonikers.Structure, Convert(SymbolGlyph.Structure));
        Assert.Equal(KnownMonikers.Interface, Convert(SymbolGlyph.Interface));
        Assert.Equal(KnownMonikers.Enumeration, Convert(SymbolGlyph.Enumeration));
        Assert.Equal(KnownMonikers.Delegate, Convert(SymbolGlyph.Delegate));
        Assert.Equal(KnownMonikers.CallTo, Convert(SymbolGlyph.IncomingBranch));
        Assert.Equal(KnownMonikers.CallFrom, Convert(SymbolGlyph.OutgoingBranch));
    }

    [Fact]
    public void Convert_UnknownAndNonGlyphInput_FallsBackToCodeInformation()
    {
        Assert.Equal(KnownMonikers.CodeInformation, Convert(SymbolGlyph.Unknown));
        Assert.Equal(KnownMonikers.CodeInformation, Convert(null));
        Assert.Equal(KnownMonikers.CodeInformation, Convert("Method"));
    }

    [Fact]
    public void Convert_EveryDeclaredGlyph_IsHandled()
    {
        // Anything added to the enum without a case here would silently show the fallback icon.
        var mapped = Enum.GetValues(typeof(SymbolGlyph))
            .Cast<SymbolGlyph>()
            .Where(g => g != SymbolGlyph.Unknown)
            .Select(g => (Glyph: g, Moniker: Convert(g)))
            .ToList();

        Assert.DoesNotContain(mapped, m => Equals(m.Moniker, KnownMonikers.CodeInformation));
        Assert.Equal(mapped.Count, mapped.Select(m => m.Moniker).Distinct().Count());
    }

    [Fact]
    public void ConvertBack_IsNotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            new SymbolGlyphMonikerConverter().ConvertBack(KnownMonikers.Method, typeof(SymbolGlyph), null, CultureInfo.InvariantCulture));
}
