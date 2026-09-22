using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

internal delegate string TemplateBuilder(TargetKind kind, PredicateMode mode, string text, out int offset);

/// <summary>Emits a user's expression as a static method, binds a delegate to it and caches it for the process lifetime.</summary>
internal sealed class ExpressionCache(string assemblyPrefix, TemplateBuilder build, string className, string methodName, Func<TargetKind, Type> delegateType)
{
    // net472 has no collectible load context, so every unique expression leaks its emitted assembly for
    // the process lifetime. Never evict: removing an entry reclaims nothing, and re-running it then
    // emits and leaks a second assembly for text that already has one.
    private readonly ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate> _cache = new ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate>();
    private readonly ConcurrentQueue<(TargetKind, PredicateMode, string)> _cacheOrder = new ConcurrentQueue<(TargetKind, PredicateMode, string)>();
    private long _totalEmittedBytes;

    /// <summary>Sum of raw PE image bytes handed to <see cref="Assembly.Load(byte[])"/> so far. On net472 that
    /// memory is never reclaimed, so this only grows.</summary>
    public long TotalEmittedBytes => Interlocked.Read(ref _totalEmittedBytes);

    public int CachedExpressionCount => _cache.Count;

    /// <summary>Cached expressions, most-recently-compiled first. Skips keys evicted since being enqueued.</summary>
    public IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot()
    {
        var order = _cacheOrder.ToArray();
        var result = new List<(TargetKind, PredicateMode, string)>(order.Length);
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var key = order[i];
            if (_cache.ContainsKey(key))
                result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// The cache key this text compiles under, without compiling it. The one definition of the key, so a
    /// caller matching a <see cref="Snapshot"/> entry cannot drift out of step with how Compile stores it.
    /// </summary>
    public (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, string text) =>
        KeyFor(kind, ExpressionSupport.DetectMode(text), text);

    public (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, PredicateMode mode, string text) =>
        (kind, mode, mode == PredicateMode.Body ? ExpressionSupport.NormalizeBody(text) : ExpressionSupport.Normalize(text));

    public Delegate Compile(TargetKind kind, string text) => Compile(kind, ExpressionSupport.DetectMode(text), text);

    public Delegate Compile(TargetKind kind, PredicateMode mode, string text)
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

        var key = KeyFor(kind, mode, text);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // Built from the text as typed, not the cache key, so diagnostics map to what the user wrote.
        var source = build(kind, mode, text, out var offset);
        var compilation = CSharpCompilation.Create(
            assemblyPrefix + Guid.NewGuid().ToString("N"),
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
            var type = Assembly.Load(bytes).GetType(className, throwOnError: true);
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            var @delegate = _cache.GetOrAdd(key, method.CreateDelegate(delegateType(kind)));
            _cacheOrder.Enqueue(key);

            return @delegate;
        }
    }
}
