using System;
using System.Linq;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RoslynQuery.Tests;

// Dropping a sidebar row must never evict the compiled delegate behind it: on net472 the emitted assembly
// cannot be unloaded, so evicting reclaims nothing and re-running the same text leaks a second assembly.
// These read the source because the behaviour lives in a WPF click handler no test can instantiate.
public class SidebarRowActionTests
{
    private const string ControlPath = @"RoslynQuery\ToolWindow\QueryToolWindowControl.xaml.cs";
    private const string CompilerPath = @"RoslynQuery\Query\PredicateCompiler.cs";

    private static MethodDeclarationSyntax Method(string path, string name) =>
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(path), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);

    [Fact]
    public void DroppingARow_NeverTouchesTheCompilerCache()
    {
        var handler = Method(ControlPath, "OnRemoveRowClick").ToString();

        Assert.DoesNotContain("PredicateCompiler", handler);
    }

    [Fact]
    public void DroppingARow_HidesItAndUnstarsIt()
    {
        var handler = Method(ControlPath, "OnRemoveRowClick").ToString();

        Assert.Contains("_hiddenRows.Add", handler);
        Assert.Contains("_cachedPredicates.Remove(item)", handler);
        Assert.Contains("FavoritesStore.Remove", handler);
    }

    [Fact]
    public void AHiddenRow_StaysOutOfTheSidebarOnRefresh()
    {
        var refresh = Method(ControlPath, "RefreshCachedPredicates").ToString();

        Assert.Contains("_hiddenRows", refresh);
    }

    /// <summary>The cache exposes no removal at all, so no caller can evict one even by mistake.</summary>
    [Fact]
    public void TheCompilerCache_HasNoRemovalPath()
    {
        var source = RepositoryFiles.Read(CompilerPath);

        Assert.DoesNotContain("TryRemove", source);
        Assert.DoesNotContain("Cache.Clear", source);
    }

    [Fact]
    public void RenamingARow_PersistsOnlyWhileStarred()
    {
        var commit = Method(ControlPath, "CommitRename").ToString();

        Assert.Contains("item.CommitEdit()", commit);
        Assert.Contains("item.IsFavorite", commit);
        Assert.Contains("FavoritesStore.Rename", commit);
    }

    /// <summary>A dropped row is hidden, not forgotten, so running its query again has to bring it back.</summary>
    [Fact]
    public void RunningAQuery_UnhidesItsRow()
    {
        var run = Method(ControlPath, "RunCoreAsync").ToString();
        var before = run.Substring(0, run.IndexOf("await TaskScheduler.Default", StringComparison.Ordinal));

        Assert.Contains("_hiddenRows.Remove(PredicateCompiler.KeyFor(target, expression))", before);
    }
}
