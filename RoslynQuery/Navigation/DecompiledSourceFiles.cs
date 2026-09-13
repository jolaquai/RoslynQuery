using System;
using System.IO;
using System.Text;

namespace RoslynQuery.Navigation;

/// <summary>Decompiled source on disk, one read-only file per type, so the editor opens it like any other file.</summary>
internal static class DecompiledSourceFiles
{
    public static string DefaultRoot { get; } = Path.Combine(Path.GetTempPath(), "RoslynQuery", "Decompiled");

    public static string Write(string root, string assemblyName, Version assemblyVersion, string typeFullName, string text)
    {
        var directory = Path.Combine(root, Sanitize(assemblyName) + "-" + (assemblyVersion?.ToString() ?? "0.0.0.0"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, Sanitize(typeFullName) + ".cs");

        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);

        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name) builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        return builder.ToString();
    }
}
