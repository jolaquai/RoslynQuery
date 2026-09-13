using RoslynQuery.Storage;

using Xunit;

namespace RoslynQuery.Tests;

public class VersionedFileTests
{
    private const string Name = "test-format";

    [Fact]
    public void Header_StampsTheNameAndVersion() => Assert.Equal("test-format\t7", VersionedFile.Header(Name, 7));

    [Fact]
    public void Read_ReturnsTheStampedVersionAndTheRowsBelowIt()
    {
        var (version, rows) = VersionedFile.Read(Name, ["test-format\t3", "a", "b"]);

        Assert.Equal(3, version);
        Assert.Equal(["a", "b"], rows);
    }

    [Fact]
    public void Read_AStampWithNoRows_IsAnEmptyFileAtThatVersion()
    {
        var (version, rows) = VersionedFile.Read(Name, ["test-format\t3"]);

        Assert.Equal(3, version);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("other-format\t1")]
    [InlineData("test-format")]
    [InlineData("test-format\t")]
    [InlineData("test-format\tx")]
    [InlineData("test-format\t0")]
    [InlineData("test-format\t-1")]
    [InlineData("test-format\t1\t2")]
    [InlineData("test-format\t 1")]
    [InlineData("")]
    public void Read_AStampItCannotUse_IsVersionZero(string header)
    {
        var (version, rows) = VersionedFile.Read(Name, [header, "a"]);

        Assert.Equal(0, version);
        Assert.Empty(rows);
    }

    [Fact]
    public void Read_NoLinesAtAll_IsVersionZero()
    {
        Assert.Equal(0, VersionedFile.Read(Name, []).Version);
        Assert.Equal(0, VersionedFile.Read(Name, null).Version);
    }

    [Fact]
    public void Write_StampsThenWritesEachRowOnItsOwnCrlfLine() =>
        Assert.Equal("test-format\t2\r\na\r\nb\r\n", VersionedFile.Write(Name, 2, ["a", "b"]));

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var text = VersionedFile.Write(Name, 5, ["x", "y"]);

        var (version, rows) = VersionedFile.Read(Name, text.Split(["\r\n"], System.StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(5, version);
        Assert.Equal(["x", "y"], rows);
    }
}
