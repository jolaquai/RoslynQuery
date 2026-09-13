using System;
using System.IO;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

public sealed class DecompiledSourceFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Write_CreatesAReadOnlyCSharpFileHoldingTheText()
    {
        var path = DecompiledSourceFiles.Write(_root, "mscorlib", new Version(4, 0, 0, 0), "System.IO.MemoryStream", "class MemoryStream { }");

        Assert.Equal(".cs", Path.GetExtension(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("class MemoryStream { }", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ReplacesAnEarlierReadOnlyCopy()
    {
        DecompiledSourceFiles.Write(_root, "mscorlib", new Version(4, 0, 0, 0), "System.String", "first");

        var path = DecompiledSourceFiles.Write(_root, "mscorlib", new Version(4, 0, 0, 0), "System.String", "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Write_UsesCrlfLineEndingsWhateverTheDecompilerProduced()
    {
        var path = DecompiledSourceFiles.Write(_root, "a", null, "T", "one\ntwo\r\nthree\n");

        Assert.Equal("one\r\ntwo\r\nthree\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ReplacesCharactersAFileNameCannotHold()
    {
        var path = DecompiledSourceFiles.Write(_root, "a", null, "Outer<T>|Inner", "x");

        Assert.Equal("Outer_T__Inner.cs", Path.GetFileName(path));
    }

    [Fact]
    public void Write_KeepsGenericArityInTheFileName()
    {
        var path = DecompiledSourceFiles.Write(_root, "mscorlib", null, "System.Collections.Generic.List`1", "x");

        Assert.Equal("System.Collections.Generic.List`1.cs", Path.GetFileName(path));
    }

    [Fact]
    public void Write_KeepsDifferentAssemblyVersionsApart()
    {
        var older = DecompiledSourceFiles.Write(_root, "System.Runtime", new Version(8, 0, 0, 0), "System.String", "eight");
        var newer = DecompiledSourceFiles.Write(_root, "System.Runtime", new Version(10, 0, 0, 0), "System.String", "ten");

        Assert.NotEqual(older, newer);
        Assert.Equal("eight", File.ReadAllText(older));
        Assert.Equal("ten", File.ReadAllText(newer));
    }
}
