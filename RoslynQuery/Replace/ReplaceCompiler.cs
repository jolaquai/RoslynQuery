using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

namespace RoslynQuery.Replace;

// Same ValueTask rationale as NodeMatch/TokenMatch in PredicateCompiler.cs: previews are generated
// for every hit in a run, and a Task allocation per hit is not free.
internal delegate ValueTask<object> NodeReplace(SyntaxNode n, SemanticModel model, Document doc);
internal delegate ValueTask<object> TokenReplace(SyntaxToken t, SemanticModel model, Document doc);

/// <summary>
/// Compiles a user's replacement transform the same way <see cref="PredicateCompiler"/> compiles a
/// predicate - emitted as a static method, delegate bound by reflection, cached by normalized text.
/// A separate cache from the predicate one: identical text means different things as a bool-typed
/// match and an object-typed replacement, and the delegate types don't match either.
/// </summary>
internal static class ReplaceCompiler
{
    // Same net472 caveat as PredicateCompiler: no collectible load context, so every unique
    // transform leaks its emitted assembly for the process lifetime. This cap only bounds the
    // dictionary, not the underlying leak.
    private const int MaxCachedExpressions = 512;
    private static readonly ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate> Cache = new ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate>();
    private static readonly ConcurrentQueue<(TargetKind, PredicateMode, string)> CacheOrder = new ConcurrentQueue<(TargetKind, PredicateMode, string)>();
    private static long _totalEmittedBytes;

    public static long TotalEmittedBytes => Interlocked.Read(ref _totalEmittedBytes);

    public static int CachedExpressionCount => Cache.Count;

    public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot()
    {
        var order = CacheOrder.ToArray();
        var result = new List<(TargetKind, PredicateMode, string)>(order.Length);
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var key = order[i];
            if (Cache.ContainsKey(key))
                result.Add(key);
        }
        return result;
    }

    public static Type DelegateType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => typeof(NodeReplace),
        TargetKind.SyntaxToken => typeof(TokenReplace),
        _ => throw new NotSupportedException("Replace does not support IOperation matches - switch Target to SyntaxNode or SyntaxToken.")
    };

    public static Delegate Compile(TargetKind kind, string text) => Compile(kind, ExpressionSupport.DetectMode(text), text);

    public static Delegate Compile(TargetKind kind, PredicateMode mode, string text)
    {
        var directive = ExpressionSupport.FindDirective(text);
        if (directive != null)
        {
            throw new PredicateCompilationException(
                $"#directives are not supported: found '{directive}'.",
                []);
        }

        var normalized = mode == PredicateMode.Body ? ExpressionSupport.NormalizeBody(text) : ExpressionSupport.Normalize(text);
        var key = (kind, mode, normalized);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        // Built from the text as typed, not from the cache key - see PredicateCompiler.Compile for why.
        var source = ReplaceTemplate.Build(kind, mode, text, out var offset);
        var compilation = CSharpCompilation.Create(
            "RoslynQuery_Replace_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(SourceText.From(source), PredicateTemplate.ParseOptions)],
            ExpressionSupport.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

        using (var stream = new MemoryStream())
        {
            var result = compilation.Emit(stream);
            if (!result.Success)
                throw new PredicateCompilationException(ExpressionSupport.Describe(result.Diagnostics, source, offset), result.Diagnostics);

            var bytes = stream.ToArray();
            Interlocked.Add(ref _totalEmittedBytes, bytes.Length);
            var type = Assembly.Load(bytes).GetType(ReplaceTemplate.ClassName, throwOnError: true);
            var method = type.GetMethod(ReplaceTemplate.MethodName, BindingFlags.Public | BindingFlags.Static);
            var @delegate = Cache.GetOrAdd(key, method.CreateDelegate(DelegateType(kind)));
            CacheOrder.Enqueue(key);

            while (Cache.Count > MaxCachedExpressions && CacheOrder.TryDequeue(out var oldest))
                Cache.TryRemove(oldest, out _);

            return @delegate;
        }
    }
}
