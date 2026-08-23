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

// ValueTask rather than Task: a predicate that never awaits completes synchronously, and at the
// hundreds of thousands of invocations a wide run makes, one heap allocation per call would not be
// affordable. Each returned value is awaited exactly once, at its single call site in QueryEngine.
//
// object rather than bool: a predicate may return a bool as before, or the node/token/operation it
// was handed (or one of its own descendants) to act as that hit's result location - a Where+Select
// in one - or null, treated the same as false. QueryEngine.TryClassifyResult enforces that a
// returned SyntaxNode/SyntaxToken/IOperation actually belongs to the tree being searched.
internal delegate ValueTask<object> NodeMatch(SyntaxNode n, SemanticModel model, Document doc);
internal delegate ValueTask<object> TokenMatch(SyntaxToken t, SemanticModel model, Document doc);
internal delegate ValueTask<object> OperationMatch(IOperation op, SemanticModel model, Document doc);

internal sealed class PredicateCompilationException : Exception
{
    public PredicateCompilationException(string message, ImmutableArray<Diagnostic> diagnostics) : base(message) => Diagnostics = diagnostics;

    public ImmutableArray<Diagnostic> Diagnostics { get; }
}

/// <summary>
/// Emits the user's expression as a static method and binds a delegate to it. A script host
/// (<c>CSharpScript</c>) would cost a Task and a globals binding per invocation, which is not
/// affordable at hundreds of thousands of calls per run. Normalization, the reference set, and
/// diagnostic formatting live in <see cref="ExpressionSupport"/>, shared with the Replace compiler;
/// only the delegate shape, template, and cache below are predicate-specific.
/// </summary>
internal static class PredicateCompiler
{
    // net472 has no collectible load context (AssemblyLoadContext.RunAndCollect and
    // AssemblyBuilderAccess.RunAndCollect are both .NET Core+) and the only Framework-era unload
    // primitive, AppDomain.Unload, cannot host this: SyntaxNode/SemanticModel/Document are not
    // MarshalByRefObject or serializable, so a predicate delegate cannot cross an AppDomain boundary
    // without per-member proxying, which would cost far more than the several KB per expression this
    // is trying to save. Every unique expression therefore leaks its emitted assembly for the process
    // lifetime; the cap below only bounds *this* dictionary against pathological input (e.g.
    // programmatically generated expressions with embedded GUIDs), not the underlying leak.
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
        // Rejected outright rather than normalized or passed through. ParseTokens evaluates
        // directives against ParseOptions, which defines no preprocessor symbols, so
        // "#if DEBUG a #else b #endif" collapses to "b" with the other branch gone before anything
        // downstream can see it. Compiling half of what was written, silently, is worse than
        // saying the construct is unsupported.
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

        // Built from the text as typed, not from the cache key: normalization exists to collapse
        // formatting differences onto one entry, and compiling its output instead would report
        // every diagnostic against a line and column the user never wrote.
        var source = PredicateTemplate.Build(kind, mode, text, out var offset);
        var compilation = CSharpCompilation.Create(
            "RoslynQuery_Predicate_" + Guid.NewGuid().ToString("N"),
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
