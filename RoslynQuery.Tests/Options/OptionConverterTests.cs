using System.ComponentModel;
using System.Globalization;
using System.Linq;

using RoslynQuery.Options;
using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// The options grid persists whatever a converter writes and reads it back through the same converter, so a
// disagreement between the two directions silently resets the setting.
public class OptionConverterTests
{
    private static object To(TypeConverter converter, object value) =>
        converter.ConvertTo(null, CultureInfo.InvariantCulture, value, typeof(string));

    private static object From(TypeConverter converter, string text) =>
        converter.ConvertFrom(null, CultureInfo.InvariantCulture, text);

    [Theory]
    [InlineData(MetadataNavigationMode.Ilspy, "Open in ILSpy")]
    [InlineData(MetadataNavigationMode.VisualStudio, "Decompile in Visual Studio")]
    public void AMetadataNavigationMode_RoundTripsThroughItsLabel(MetadataNavigationMode mode, string expected)
    {
        var converter = new MetadataNavigationModeConverter();

        Assert.Equal(expected, To(converter, mode));
        Assert.Equal(mode, From(converter, expected));
    }

    [Theory]
    [InlineData("Ilspy", MetadataNavigationMode.Ilspy)]
    [InlineData("VisualStudio", MetadataNavigationMode.VisualStudio)]
    public void AMetadataNavigationMode_StillReadsABareEnumName(string persisted, MetadataNavigationMode expected) =>
        Assert.Equal(expected, From(new MetadataNavigationModeConverter(), persisted));

    [Theory]
    [InlineData(ScopeKind.Document, "Current document")]
    [InlineData(ScopeKind.Project, "Current project")]
    [InlineData(ScopeKind.Solution, "My solution")]
    public void AReferenceGraphScope_RoundTripsThroughTheWordingTheComboUses(ScopeKind scope, string expected)
    {
        var converter = new ReferenceGraphScopeConverter();

        Assert.Equal(expected, To(converter, scope));
        Assert.Equal(scope, From(converter, expected));
        Assert.Equal(scope, From(converter, scope.ToString()));
    }

    /// <summary>The window's combo has no entry for the two query-only scopes, so the grid must not offer them.</summary>
    [Fact]
    public void AReferenceGraphScope_OffersOnlyTheThreeScopesTheWindowHas()
    {
        var converter = new ReferenceGraphScopeConverter();

        var offered = converter.GetStandardValues(null).Cast<ScopeKind>().ToArray();

        Assert.Equal([ScopeKind.Document, ScopeKind.Project, ScopeKind.Solution], offered);
        Assert.True(converter.GetStandardValuesSupported(null));
        Assert.True(converter.GetStandardValuesExclusive(null));
    }
}
