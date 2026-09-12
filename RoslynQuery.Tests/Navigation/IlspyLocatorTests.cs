using System;
using System.IO;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

public class IlspyLocatorTests
{
    [Fact]
    public void AConfiguredPathThatExists_IsUsedAsIs()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"), "ILSpy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Empty);

        try
        {
            Assert.Equal(path, IlspyLocator.Find(path));
            Assert.Equal(path, IlspyLocator.Find("  " + path + "  "));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path), recursive: true);
        }
    }

    /// <summary>A configured path is a statement of intent, so a wrong one is reported rather than quietly replaced by a found install.</summary>
    [Fact]
    public void AConfiguredPathThatIsMissing_FindsNothing() =>
        Assert.Null(IlspyLocator.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ILSpy.exe")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredPath_FallsBackToSearching(string configured)
    {
        var found = IlspyLocator.Find(configured);

        Assert.SkipUnless(found != null, "No ILSpy is installed on this machine.");
        Assert.True(File.Exists(found));
        Assert.Equal("ILSpy.exe", Path.GetFileName(found));
    }
}
