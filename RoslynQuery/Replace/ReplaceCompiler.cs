using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.Query;

namespace RoslynQuery.Replace;

internal delegate ValueTask<object> NodeReplace(SyntaxNode n, SemanticModel model, Document doc);
internal delegate ValueTask<object> TokenReplace(SyntaxToken t, SemanticModel model, Document doc);

/// <summary>Compiles a user's replacement transform, cached separately from <see cref="PredicateCompiler"/>'s cache since identical text compiles to a different delegate type here.</summary>
internal static class ReplaceCompiler
{
    private static readonly ExpressionCache Cache = new ExpressionCache(
        "RoslynQuery_Replace_", ReplaceTemplate.Build, ReplaceTemplate.ClassName, ReplaceTemplate.MethodName, DelegateType);

    public static long TotalEmittedBytes => Cache.TotalEmittedBytes;

    public static int CachedExpressionCount => Cache.CachedExpressionCount;

    public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot() => Cache.Snapshot();

    public static Type DelegateType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => typeof(NodeReplace),
        TargetKind.SyntaxToken => typeof(TokenReplace),
        _ => throw new NotSupportedException("Replace does not support IOperation matches - switch Target to SyntaxNode or SyntaxToken.")
    };

    /// <summary>The cache key this text compiles under, without compiling it. See <see cref="PredicateCompiler.KeyFor(TargetKind, string)"/>.</summary>
    public static (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, string text) => Cache.KeyFor(kind, text);

    public static (TargetKind Kind, PredicateMode Mode, string Text) KeyFor(TargetKind kind, PredicateMode mode, string text) =>
        Cache.KeyFor(kind, mode, text);

    public static Delegate Compile(TargetKind kind, string text) => Cache.Compile(kind, text);

    public static Delegate Compile(TargetKind kind, PredicateMode mode, string text) => Cache.Compile(kind, mode, text);
}
