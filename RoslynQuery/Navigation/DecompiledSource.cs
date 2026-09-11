using System;

namespace RoslynQuery.Navigation;

/// <summary>A decompiled type positioned on one of its members, or the reason there is nothing to show.</summary>
internal sealed class DecompiledSource
{
    private DecompiledSource()
    {
    }

    public string Text { get; private set; }

    /// <summary>Zero-based.</summary>
    public int Line { get; private set; }

    /// <summary>Zero-based.</summary>
    public int Column { get; private set; }

    public string AssemblyName { get; private set; }
    public Version AssemblyVersion { get; private set; }

    /// <summary>The reflection name of the top-level type that was decompiled.</summary>
    public string TypeFullName { get; private set; }

    public string Failure { get; private set; }

    public bool Succeeded => Failure is null;

    public static DecompiledSource Found(string text, int line, int column, string assemblyName, Version assemblyVersion, string typeFullName) =>
        new DecompiledSource
        {
            Text = text,
            Line = line,
            Column = column,
            AssemblyName = assemblyName,
            AssemblyVersion = assemblyVersion,
            TypeFullName = typeFullName
        };

    public static DecompiledSource Failed(string reason) => new DecompiledSource { Failure = reason };
}
