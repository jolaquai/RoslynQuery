using System;
using System.IO;

namespace RoslynQuery.Tests;

/// <summary>Files of this checkout, found by walking up from the test binary to the solution file.</summary>
internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RoslynQuery.slnx"))) return directory.FullName;

        throw new InvalidOperationException("RoslynQuery.slnx was not found above " + AppContext.BaseDirectory);
    }
}
