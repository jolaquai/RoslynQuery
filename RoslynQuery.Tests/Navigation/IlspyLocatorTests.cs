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

    [Fact]
    public void TheSearch_RunsOnceAndIsRemembered()
    {
        IlspyLocator.Reset();
        var before = IlspyLocator.Searches;

        var first = IlspyLocator.Find(null);
        var second = IlspyLocator.Find(string.Empty);
        var third = IlspyLocator.Find("   ");

        Assert.Equal(before + 1, IlspyLocator.Searches);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Resetting_ArmsTheSearchAgain()
    {
        IlspyLocator.Reset();
        IlspyLocator.Find(null);
        var after = IlspyLocator.Searches;

        IlspyLocator.Reset();
        IlspyLocator.Find(null);

        Assert.Equal(after + 1, IlspyLocator.Searches);
    }

    [Fact]
    public void AConfiguredPath_NeverStartsASearch()
    {
        IlspyLocator.Reset();
        var before = IlspyLocator.Searches;

        IlspyLocator.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ILSpy.exe"));

        Assert.Equal(before, IlspyLocator.Searches);
    }

    [Fact]
    public void TheMessageForAWrongConfiguredPath_QuotesItAndNamesBothWaysOut()
    {
        var message = IlspyLocator.NotFoundMessage(@"  C:\wrong\ILSpy.exe  ");

        Assert.Contains(@"C:\wrong\ILSpy.exe", message);
        Assert.DoesNotContain("  C:", message);
        Assert.Contains("Tools > Options > RoslynQuery > Reference Graph", message);
        Assert.Contains("Decompile in Visual Studio", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheMessageForNoInstall_SaysNoneWasFound(string configured)
    {
        var message = IlspyLocator.NotFoundMessage(configured);

        Assert.StartsWith("No ILSpy installation was found.", message);
        Assert.Contains("Tools > Options > RoslynQuery > Reference Graph", message);
        Assert.Contains("Decompile in Visual Studio", message);
    }
}
