using System;
using System.IO;
using System.Linq;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

public class IlspyLauncherTests
{
    private const string DocumentationId = "M:System.String.Format(System.String,System.Object)";

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TheResponseFile_HoldsOneArgumentPerLine()
    {
        var lines = IlspyLauncher
            .ResponseFileText(@"C:\Program Files\dotnet\shared\App\System.Private.CoreLib.dll", DocumentationId)
            .Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            [@"C:\Program Files\dotnet\shared\App\System.Private.CoreLib.dll", "--navigateto:" + DocumentationId],
            lines);
    }

    /// <summary>ILSpy splits a response file on line breaks alone, so a quoted path would reach it with the quotes attached.</summary>
    [Fact]
    public void TheResponseFile_QuotesNothing()
    {
        var text = IlspyLauncher.ResponseFileText(@"C:\Program Files\ILSpy\A b.dll", DocumentationId);

        Assert.DoesNotContain("\"", text);
    }

    [Fact]
    public void TheCommandLine_PassesTheInstanceIdAndTheResponseFile()
    {
        var arguments = IlspyLauncher.Arguments(@"C:\Tools\My ILSpy\ILSpy.exe", @"C:\Temp\a b\args.rsp");

        Assert.Equal("--instanceid \"C:\\Tools\\My ILSpy\\ILSpy.exe\" @\"C:\\Temp\\a b\\args.rsp\"", arguments);
    }

    [Fact]
    public void AWrittenResponseFile_LandsUnderTheRootWithTheExpectedText()
    {
        var root = Scratch();

        try
        {
            var path = IlspyLauncher.WriteResponseFile(root, @"C:\a.dll", DocumentationId);

            Assert.Equal(root, Path.GetDirectoryName(path));
            Assert.Equal(".rsp", Path.GetExtension(path));
            Assert.Equal(IlspyLauncher.ResponseFileText(@"C:\a.dll", DocumentationId), File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WritingAResponseFile_SweepsStaleOnesAndKeepsFreshOnes()
    {
        var root = Scratch();

        try
        {
            var stale = Path.Combine(root, "stale.rsp");
            var fresh = Path.Combine(root, "fresh.rsp");
            File.WriteAllText(stale, "old");
            File.WriteAllText(fresh, "new");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

            IlspyLauncher.WriteResponseFile(root, @"C:\a.dll", DocumentationId);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARowWithNoSymbolId_IsRefusedWithoutStartingAnything()
    {
        var root = Scratch();

        try
        {
            var failure = IlspyLauncher.Launch(@"C:\nope\ILSpy.exe", @"C:\a.dll", null, root);

            Assert.Contains("no symbol id", failure);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMissingIlspy_IsReportedRatherThanThrown()
    {
        var root = Scratch();

        try
        {
            var failure = IlspyLauncher.Launch(
                Path.Combine(root, "ILSpy.exe"), @"C:\a.dll", DocumentationId, root);

            Assert.NotNull(failure);
            Assert.Contains("Could not start ILSpy", failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultResponseFileRoot_SitsUnderTheTempDirectory() =>
        Assert.StartsWith(Path.GetTempPath(), IlspyLauncher.DefaultResponseFileRoot, StringComparison.OrdinalIgnoreCase);
}
