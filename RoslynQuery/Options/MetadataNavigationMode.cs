namespace RoslynQuery.Options;

/// <summary>What opens when a row that came from a referenced assembly is activated.</summary>
public enum MetadataNavigationMode
{
    /// <summary>Hand the assembly and the symbol's documentation id to ILSpy.</summary>
    Ilspy,

    /// <summary>Decompile the containing type into a temporary file and open it in the editor.</summary>
    VisualStudio
}
