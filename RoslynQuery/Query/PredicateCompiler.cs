using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

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
    private static readonly ExpressionCache Cache = new ExpressionCache(
        "RoslynQuery_Predicate_", PredicateTemplate.Build, PredicateTemplate.ClassName, PredicateTemplate.MethodName, DelegateType);

    /// <summary>Sum of raw PE image bytes handed to <see cref="System.Reflection.Assembly.Load(byte[])"/> so far, including
    /// evicted entries: on net472 that memory is never actually reclaimed, so this only grows.</summary>
    public static long TotalEmittedBytes => Cache.TotalEmittedBytes;

    public static int CachedExpressionCount => Cache.CachedExpressionCount;

    /// <summary>Cached predicates, most-recently-compiled first. Skips keys evicted since being enqueued.</summary>
    public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot() => Cache.Snapshot();

    public static Type DelegateType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => typeof(NodeMatch),
        TargetKind.SyntaxToken => typeof(TokenMatch),
        TargetKind.Operation => typeof(OperationMatch),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static Delegate Compile(TargetKind kind, string text) => Cache.Compile(kind, text);

    /// <summary>
    /// The cache key this text compiles under, without compiling it. The one definition of the key, so a
    /// caller matching a <see cref="Snapshot"/> entry cannot drift out of step with how Compile stores it.
    /// </summary>
    public static (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, string text) => Cache.KeyFor(kind, text);

    public static (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, PredicateMode mode, string text) =>
        Cache.KeyFor(kind, mode, text);

    public static Delegate Compile(TargetKind kind, PredicateMode mode, string text) => Cache.Compile(kind, mode, text);
}
