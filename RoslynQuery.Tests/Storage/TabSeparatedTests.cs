using RoslynQuery.Storage;

using Xunit;

namespace RoslynQuery.Tests;

public class TabSeparatedTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("crlf\r\nhere")]
    [InlineData("cr\rhere")]
    [InlineData("lf\nhere")]
    [InlineData("back\\slash")]
    [InlineData("trailing\\")]
    [InlineData("\\t not a tab")]
    [InlineData("\\\\")]
    [InlineData("everything \\ \t \r\n at once")]
    public void AValue_SurvivesEscapingAndUnescaping(string value) =>
        Assert.Equal(value, TabSeparated.Unescape(TabSeparated.Escape(value)));

    /// <summary>The invariant the whole format rests on: neither separator survives escaping.</summary>
    [Theory]
    [InlineData("tab\there")]
    [InlineData("crlf\r\nhere")]
    public void AnEscapedValue_ContainsNeitherATabNorALineBreak(string value)
    {
        var escaped = TabSeparated.Escape(value);

        Assert.DoesNotContain("\t", escaped);
        Assert.DoesNotContain("\r", escaped);
        Assert.DoesNotContain("\n", escaped);
    }

    [Fact]
    public void ARow_JoinsEscapedFieldsWithTabs() =>
        Assert.Equal("a\tb\\tc\t", TabSeparated.Row("a", "b\tc", null));

    [Fact]
    public void ARow_SplitsBackIntoItsFields() =>
        Assert.Equal(["a", "b\tc", ""], TabSeparated.FieldsOfLength(TabSeparated.Row("a", "b\tc", null), 3));

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ARowOfTheWrongLength_IsRefused(int length) =>
        Assert.Null(TabSeparated.FieldsOfLength("a\tb\tc", length));

    [Fact]
    public void AValueContainingOnlyASeparator_StillRoundTrips() =>
        Assert.Equal(["\t", "\r\n"], TabSeparated.FieldsOfLength(TabSeparated.Row("\t", "\r\n"), 2));
}
