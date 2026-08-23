using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

internal delegate ValueTask<object> NodeMatch(SyntaxNode n, SemanticModel model, Document doc);
internal delegate ValueTask<object> TokenMatch(SyntaxToken t, SemanticModel model, Document doc);
internal delegate ValueTask<object> OperationMatch(IOperation op, SemanticModel model, Document doc);

internal sealed class PredicateCompilationException(string message, ImmutableArray<Diagnostic> diagnostics) : Exception(message)
{
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Emits the user's expression as a static method and binds a delegate to it.</summary>
internal static class PredicateCompiler
{
    // net472 has no collectible load context, so every unique expression leaks its emitted assembly for
    // the process lifetime; the cap below only bounds this dictionary, not the underlying leak.
    private const int MaxCachedExpressions = 512;
    private static readonly ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate> Cache = new ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate>();
    private static readonly ConcurrentQueue<(TargetKind, PredicateMode, string)> CacheOrder = new ConcurrentQueue<(TargetKind, PredicateMode, string)>();
    private static long _totalEmittedBytes;

    /// <summary>Sum of raw PE image bytes handed to <see cref="Assembly.Load(byte[])"/> so far, including
    /// evicted entries: on net472 that memory is never actually reclaimed, so this only grows.</summary>
    public static long TotalEmittedBytes => Interlocked.Read(ref _totalEmittedBytes);

    public static int CachedExpressionCount => Cache.Count;

    /// <summary>Cached predicates, most-recently-compiled first. Skips keys evicted since being enqueued.</summary>
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
        TargetKind.SyntaxNode => typeof(NodeMatch),
        TargetKind.SyntaxToken => typeof(TokenMatch),
        TargetKind.Operation => typeof(OperationMatch),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static Delegate Compile(TargetKind kind, string text) => Compile(kind, ExpressionSupport.DetectMode(text), text);

    public static Delegate Compile(TargetKind kind, PredicateMode mode, string text)
    {
        // Rejected outright: ParseTokens defines no preprocessor symbols, so an #if/#else would
        // silently collapse to one branch with the other gone before anything downstream sees it.
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

        // Built from the text as typed, not the cache key, so diagnostics map to what the user wrote.
        var source = PredicateTemplate.Build(kind, mode, text, out var offset);
        var compilation = CSharpCompilation.Create(
            "RoslynQuery_Predicate_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(SourceText.From(source), PredicateTemplate.ParseOptions)],
            ExpressionSupport.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

        using (var stream = new ArrayPoolMemoryStream())
        {
            var result = compilation.Emit(stream);
            if (!result.Success)
                throw new PredicateCompilationException(ExpressionSupport.Describe(result.Diagnostics, source, offset), result.Diagnostics);

            var bytes = stream.ToArray();
            Interlocked.Add(ref _totalEmittedBytes, bytes.Length);
            var type = Assembly.Load(bytes).GetType(PredicateTemplate.ClassName, throwOnError: true);
            var method = type.GetMethod(PredicateTemplate.MethodName, BindingFlags.Public | BindingFlags.Static);
            var @delegate = Cache.GetOrAdd(key, method.CreateDelegate(DelegateType(kind)));
            CacheOrder.Enqueue(key);

            while (Cache.Count > MaxCachedExpressions && CacheOrder.TryDequeue(out var oldest))
                Cache.TryRemove(oldest, out _);

            return @delegate;
        }
    }
}
