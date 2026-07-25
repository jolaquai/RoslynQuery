using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

internal delegate bool NodeMatch(SyntaxNode n, SemanticModel model, Document doc);
internal delegate bool TokenMatch(SyntaxToken t, SemanticModel model, Document doc);
internal delegate bool OperationMatch(IOperation op, SemanticModel model, Document doc);

internal sealed class PredicateCompilationException : Exception
{
    public PredicateCompilationException(string message, ImmutableArray<Diagnostic> diagnostics) : base(message) => Diagnostics = diagnostics;

    public ImmutableArray<Diagnostic> Diagnostics { get; }
}

/// <summary>
/// Emits the user's expression as a static method and binds a delegate to it. A script host
/// (<c>CSharpScript</c>) would cost a Task and a globals binding per invocation, which is not
/// affordable at hundreds of thousands of calls per run.
/// </summary>
internal static class PredicateCompiler
{
    private static readonly ConcurrentDictionary<(TargetKind, string), Delegate> Cache = new ConcurrentDictionary<(TargetKind, string), Delegate>();

    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences =
        new Lazy<ImmutableArray<MetadataReference>>(BuildReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImmutableArray<MetadataReference> References => LazyReferences.Value;

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        void Add(Assembly assembly)
        {
            if (assembly is null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return;
            if (seen.Add(assembly.Location)) builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        Add(typeof(object).Assembly);
        Add(typeof(Uri).Assembly);
        Add(typeof(Enumerable).Assembly);
        Add(typeof(ImmutableArray).Assembly);
        Add(typeof(Regex).Assembly);
        Add(typeof(SyntaxNode).Assembly);
        Add(typeof(CSharpSyntaxNode).Assembly);
        Add(typeof(Document).Assembly);

        // Roslyn is compiled against netstandard2.0; without the facades the user's expression
        // cannot see types forwarded through them.
        foreach (var facade in new[] { "netstandard", "System.Runtime" })
        {
            try { Add(Assembly.Load(facade)); }
            catch (Exception) { }
        }

        return builder.ToImmutable();
    }

    public static Type DelegateType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => typeof(NodeMatch),
        TargetKind.SyntaxToken => typeof(TokenMatch),
        TargetKind.Operation => typeof(OperationMatch),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static Delegate Compile(TargetKind kind, string expression)
    {
        var key = (kind, expression ?? string.Empty);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var source = PredicateTemplate.Build(kind, expression, out var offset);
        var compilation = CSharpCompilation.Create(
            "RoslynQuery_Predicate_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(source), PredicateTemplate.ParseOptions) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

        using (var stream = new MemoryStream())
        {
            var result = compilation.Emit(stream);
            if (!result.Success) throw new PredicateCompilationException(Describe(result.Diagnostics, source, offset), result.Diagnostics);

            // net472 has no collectible load context, so every distinct expression leaks one small
            // assembly for the session. The cache holds that to one per unique expression.
            var type = Assembly.Load(stream.ToArray()).GetType(PredicateTemplate.ClassName, throwOnError: true);
            var method = type.GetMethod(PredicateTemplate.MethodName, BindingFlags.Public | BindingFlags.Static);
            return Cache.GetOrAdd(key, method.CreateDelegate(DelegateType(kind)));
        }
    }

    private static string Describe(ImmutableArray<Diagnostic> diagnostics, string source, int offset)
    {
        var text = SourceText.From(source);
        var sb = new StringBuilder();

        foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3))
        {
            if (sb.Length > 0) sb.Append("  |  ");

            var start = diagnostic.Location.SourceSpan.Start;
            if (start >= offset && start <= text.Length) sb.Append("col ").Append(start - offset + 1).Append(": ");
            sb.Append(diagnostic.Id).Append(": ").Append(diagnostic.GetMessage());
        }

        return sb.Length > 0 ? sb.ToString() : "The predicate did not compile.";
    }
}
