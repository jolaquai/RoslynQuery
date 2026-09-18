using System;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RoslynQuery.Tests;

// The row logic is covered through HistoryList; these pin the WPF wiring around it, which no test can instantiate.
public class HistoryWiringTests
{
    private const string ControlPath = @"RoslynQuery\ToolWindow\QueryToolWindowControl.xaml.cs";
    private const string MarkupPath = @"RoslynQuery\ToolWindow\QueryToolWindowControl.xaml";

    private static string Method(string name) =>
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(ControlPath), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name)
            .ToString();

    [Theory]
    [InlineData("OnFavoriteClick", "item.Owner.ToggleFavorite(item)")]
    [InlineData("OnRemoveRowClick", "item.Owner.Drop(item)")]
    [InlineData("OnRenameEditorKeyDown", "item.Owner.CommitRename(item)")]
    [InlineData("OnRenameEditorLostFocus", "item.Owner.CommitRename(item)")]
    public void RowHandlers_ActOnTheRowsOwnList(string handler, string call) => Assert.Contains(call, Method(handler));

    [Fact]
    public void RunningAQuery_UnhidesItsRowBeforeLeavingTheUiThread()
    {
        var run = Method("RunCoreAsync");
        var before = run.Substring(0, run.IndexOf("await TaskScheduler.Default", StringComparison.Ordinal));

        Assert.Contains("_queryHistory.Unhide(PredicateCompiler.KeyFor(target, expression))", before);
    }

    [Fact]
    public void GeneratingAPreview_UnhidesItsReplacementBeforeDispatching()
    {
        var preview = Method("GeneratePreview");
        var before = preview.Substring(0, preview.IndexOf("RunAsync", StringComparison.Ordinal));

        Assert.Contains("_replaceHistory.Unhide(ReplaceCompiler.KeyFor(target, replacementExpression))", before);
    }

    [Fact]
    public void DoubleClickingAReplacement_RestoresWithoutRunning()
    {
        var handler = Method("OnCachedReplacementDoubleClick");

        Assert.Contains("_replacementInput.Text = item.Pretty", handler);
        Assert.DoesNotContain("Run(", handler);
        Assert.DoesNotContain("GeneratePreview", handler);
    }

    [Fact]
    public void Refresh_FillsBothListsFromTheirOwnCompilers()
    {
        var refresh = Method("RefreshCachedPredicates");

        Assert.Contains("_queryHistory.Refresh(PredicateCompiler.Snapshot())", refresh);
        Assert.Contains("_replaceHistory.Refresh(ReplaceCompiler.Snapshot())", refresh);
    }

    [Fact]
    public void BothLists_ShareTheRowTemplate()
    {
        var markup = RepositoryFiles.Read(MarkupPath);

        Assert.Equal(2, Regex.Matches(markup, Regex.Escape("ItemTemplate=\"{StaticResource HistoryRowTemplate}\"")).Count);
        Assert.Contains("MouseDoubleClick=\"OnCachedReplacementDoubleClick\"", markup);
        Assert.Contains("SelectionChanged=\"OnMainTabsSelectionChanged\"", markup);
    }

    /// <summary>SelectionChanged bubbles up from the result lists inside the tabs, which must not toggle the section.</summary>
    [Fact]
    public void TheReplacementSection_FollowsOnlyTheTabControlsOwnSelection()
    {
        var handler = Method("OnMainTabsSelectionChanged");

        Assert.Contains("e.OriginalSource, MainTabs", handler);
    }
}
