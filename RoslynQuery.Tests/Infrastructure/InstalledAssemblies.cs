using System;
using System.IO;
using System.Linq;

namespace RoslynQuery.Tests;

/// <summary>Reference and implementation assemblies installed on this machine, for tests that resolve or decompile real files.</summary>
internal static class InstalledAssemblies
{
    public static string NetFrameworkImplementation => typeof(object).Assembly.Location;

    public static string NetFrameworkReference(string relativePath) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2",
        relativePath);

    /// <summary>A reference assembly from any installed Microsoft.NETCore.App reference pack, or null.</summary>
    public static string NetReferencePack(string file)
    {
        var packs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\packs\Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs)) return null;

        return Directory.EnumerateDirectories(packs)
            .Where(v => Directory.Exists(Path.Combine(v, "ref")))
            .SelectMany(v => Directory.EnumerateDirectories(Path.Combine(v, "ref")))
            .Select(tfm => Path.Combine(tfm, file))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
