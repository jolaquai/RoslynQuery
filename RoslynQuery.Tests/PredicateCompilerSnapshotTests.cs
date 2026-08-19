using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// Cache is a shared static across the whole test run, so every case below compiles an expression
// carrying a GUID-derived token unique to itself and matches on that token rather than on a
// hand-computed normalized string - re-deriving Normalize's own spacing rules here would just
// duplicate (and risk diverging from) the logic under test, same reasoning as
// PredicateCompilerCachingTests above.
public class PredicateCompilerSnapshotTests
{
    private static int UniqueToken() => Guid.NewGuid().GetHashCode() & int.MaxValue;

    [Fact]
    public void Snapshot_ContainsAFreshlyCompiledExpression()
    {
        var token = UniqueToken();
        PredicateCompiler.Compile(TargetKind.SyntaxNode, $"true || {token} == -1");

        Assert.Contains(PredicateCompiler.Snapshot(),
            e => e.Kind == TargetKind.SyntaxNode && e.Mode == PredicateMode.Expression && e.Text.Contains(token.ToString()));
    }

    [Fact]
    public void Snapshot_MostRecentlyCompiledExpressionComesFirst()
    {
        var olderToken = UniqueToken();
        var newerToken = UniqueToken();

        PredicateCompiler.Compile(TargetKind.SyntaxNode, $"true || {olderToken} == -1");
        PredicateCompiler.Compile(TargetKind.SyntaxNode, $"true || {newerToken} == -1");

        var snapshot = PredicateCompiler.Snapshot().ToList();
        var newerIndex = snapshot.FindIndex(e => e.Text.Contains(newerToken.ToString()));
        var olderIndex = snapshot.FindIndex(e => e.Text.Contains(olderToken.ToString()));

        Assert.True(newerIndex >= 0 && olderIndex >= 0);
        Assert.True(newerIndex < olderIndex);
    }

    [Fact]
    public void Snapshot_RecompilingACacheHit_DoesNotDuplicateTheEntry()
    {
        var token = UniqueToken();
        var text = $"true || {token} == -1";

        PredicateCompiler.Compile(TargetKind.SyntaxNode, text);
        PredicateCompiler.Compile(TargetKind.SyntaxNode, text);

        Assert.Single(PredicateCompiler.Snapshot(), e => e.Text.Contains(token.ToString()));
    }

    [Fact]
    public void Snapshot_BodyModeEntry_ReportsBodyMode()
    {
        var token = UniqueToken();
        PredicateCompiler.Compile(TargetKind.SyntaxNode, $"var x = {token}; return x == {token};");

        Assert.Contains(PredicateCompiler.Snapshot(), e => e.Mode == PredicateMode.Body && e.Text.Contains(token.ToString()));
    }

    [Fact]
    public void Snapshot_DistinctTargetKindsForTheSameText_AreBothPresent()
    {
        var token = UniqueToken();
        var text = $"true || {token} == -1";

        PredicateCompiler.Compile(TargetKind.SyntaxNode, text);
        PredicateCompiler.Compile(TargetKind.SyntaxToken, text);

        var snapshot = PredicateCompiler.Snapshot();

        Assert.Contains(snapshot, e => e.Kind == TargetKind.SyntaxNode && e.Text.Contains(token.ToString()));
        Assert.Contains(snapshot, e => e.Kind == TargetKind.SyntaxToken && e.Text.Contains(token.ToString()));
    }

    [Fact]
    public void Snapshot_DistinctModesForTheSameText_AreSeparateEntries()
    {
        // Mode is the other half of the cache key alongside TargetKind, so text shared across
        // modes must not collapse onto one entry any more than text shared across kinds does.
        var token = UniqueToken();
        var body = $"return true || {token} == -1;";

        PredicateCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Body, body);
        // Forcing the same text through Expression mode wraps it as "return <body>;", i.e.
        // "return return ...;" - a guaranteed compile failure that must never reach the cache.
        Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Expression, body));

        var matches = PredicateCompiler.Snapshot().Where(e => e.Text.Contains(token.ToString())).ToList();
        var entry = Assert.Single(matches);
        Assert.Equal(PredicateMode.Body, entry.Mode);
    }

    [Fact]
    public void Snapshot_SkipsKeysEvictedSinceBeingEnqueued()
    {
        // Seeded directly via reflection rather than by actually pushing 512+ real compiles
        // through to trigger eviction: that would take the test run from a few seconds to
        // plausibly a minute-plus and permanently leak 512 more assemblies for the rest of the
        // process, just to reach one filtering branch reflection can hit precisely and cheaply.
        var cacheOrderField = typeof(PredicateCompiler).GetField("CacheOrder", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PredicateCompiler.CacheOrder not found - has it been renamed?");
        var cacheOrder = (ConcurrentQueue<(TargetKind, PredicateMode, string)>)cacheOrderField.GetValue(null);

        var phantomText = "PHANTOM_" + Guid.NewGuid().ToString("N");
        cacheOrder.Enqueue((TargetKind.SyntaxNode, PredicateMode.Expression, phantomText));

        // A real entry enqueued after the phantom proves Snapshot walks past a non-cached key
        // rather than stopping there, not just that it happens to skip a trailing one.
        var token = UniqueToken();
        PredicateCompiler.Compile(TargetKind.SyntaxNode, $"true || {token} == -1");

        Assert.DoesNotContain(PredicateCompiler.Snapshot(), e => e.Text == phantomText);
        Assert.Contains(PredicateCompiler.Snapshot(), e => e.Text.Contains(token.ToString()));
    }
}
