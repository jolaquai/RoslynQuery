using System.IO;

namespace RoslynQuery;

/// <summary>
/// The VSIX's pooled stream leans on net472 internals (and polyfills for types .NET has natively). Here the emitted
/// predicate image is one short-lived buffer per compile, so a plain MemoryStream does the same job.
/// </summary>
internal sealed class ArrayPoolMemoryStream : MemoryStream;
