using System.Collections.Generic;
using System.Linq;

using RoslynQuery.Query;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class CachedPredicateItemTests
{
    [Fact]
    public void Display_ShortText_IsUnchanged()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        Assert.Equal("n != null", item.Display);
    }

    // A long but genuinely valid expression, so these exercise the truncation boundary rather than
    // the formatter's fallback path.
    private static string LongExpression() => string.Join(" || ", Enumerable.Repeat("n != null", 200));

    [Fact]
    public void Display_OverLimit_IsTruncatedWithEllipsis()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        Assert.EndsWith("...", item.Display);
        Assert.Equal(2003, item.Display.Length);
    }

    [Fact]
    public void Display_WellPastTheOldLimitButUnderTheNewOne_IsNotTruncated()
    {
        // 300 was low enough to clip text a widened sidebar had room for.
        var text = string.Join(" || ", Enumerable.Repeat("n != null", 40));
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Equal(item.Pretty, item.Display);
        Assert.True(item.Display.Length > 300);
    }

    [Fact]
    public void Pretty_IsNeverTruncated()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        // Restoring happens from Pretty; a truncated restore would silently run a fragment.
        Assert.True(item.Pretty.Length > 2000);
        Assert.DoesNotContain("...", item.Pretty);
    }

    [Theory]
    // The body normalizer emits a separator between every pair of tokens, which is what these
    // entries look like coming out of the cache.
    [InlineData("n . Parent != null", "n.Parent != null")]
    [InlineData("n.IsKind ( SyntaxKind.IfStatement )", "n.IsKind(SyntaxKind.IfStatement)")]
    [InlineData("n!=null", "n != null")]
    public void Pretty_Expression_IsReformatted(string stored, string expected)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, stored);

        Assert.Equal(expected, item.Pretty);
    }

    [Fact]
    public void Pretty_Body_IsReformattedOnePerLineWithoutWrappingBraces()
    {
        var item = new CachedPredicateItem(
            TargetKind.SyntaxNode,
            PredicateMode.Body,
            "await Task . Yield ( ) ; return true ;");

        var lines = item.Pretty.Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["await Task.Yield();", "return true;"], lines);
        Assert.DoesNotContain("{", item.Pretty);
        Assert.DoesNotContain("}", item.Pretty);
    }

    [Fact]
    public void Pretty_UnparseableText_FallsBackToTheStoredText()
    {
        const string junk = "this is ((( not c#";
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, junk);

        Assert.Equal(junk, item.Pretty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Pretty_Empty_IsEmpty(string text)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Equal(string.Empty, item.Pretty);
    }

    // TargetKind/PredicateMode are internal, so [Theory] data is carried as int (InlineData cannot
    // reference an internal enum as a typed argument), same workaround PredicateTemplateBodyModeTests
    // uses.
    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, (int)PredicateMode.Expression, "SyntaxNode")]
    [InlineData((int)TargetKind.SyntaxToken, (int)PredicateMode.Body, "SyntaxToken (body)")]
    [InlineData((int)TargetKind.Operation, (int)PredicateMode.Expression, "Operation")]
    public void Subtitle_NamesTheKindAndFlagsBodyModeOnly(int kindValue, int modeValue, string expected)
    {
        var item = new CachedPredicateItem((TargetKind)kindValue, (PredicateMode)modeValue, "n != null");

        Assert.Equal(expected, item.Subtitle);
    }

    [Fact]
    public void IsFavorite_DefaultsToFalseAndRaisesPropertyChangedOnChangeOnly()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");
        var raised = 0;
        item.PropertyChanged += (s, e) =>
        {
            Assert.Equal(nameof(CachedPredicateItem.IsFavorite), e.PropertyName);
            raised++;
        };

        Assert.False(item.IsFavorite);

        item.IsFavorite = true;
        item.IsFavorite = true;

        Assert.True(item.IsFavorite);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Name_ReplacesTheDisplayTextAndMovesThePredicateToTheTooltip()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        Assert.Null(item.Tooltip);

        item.Name = "Non-null nodes";

        Assert.Equal("Non-null nodes", item.Display);
        Assert.Equal("n != null", item.Tooltip);
        Assert.Equal("n!=null", item.Text);
        Assert.Equal("n != null", item.Pretty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_ClearedBackToNothing_ShowsThePredicateAgain(string cleared)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null") { Name = "Named" };

        item.Name = cleared;

        Assert.Null(item.Name);
        Assert.Equal("n != null", item.Display);
        Assert.Null(item.Tooltip);
    }

    [Fact]
    public void Name_IsTrimmed()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null") { Name = "  padded  " };

        Assert.Equal("padded", item.Name);
    }

    [Fact]
    public void Name_RaisesPropertyChangedForEverythingItAffects_OnChangeOnly()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");
        var raised = new List<string>();
        item.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

        item.Name = "Named";
        item.Name = "Named";

        Assert.Equal(
            [nameof(CachedPredicateItem.Name), nameof(CachedPredicateItem.Display), nameof(CachedPredicateItem.Tooltip)],
            raised);
    }

    [Fact]
    public void Name_LongerThanTheDisplayLimit_IsShownWhole()
    {
        var name = new string('x', 5000);
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null") { Name = name };

        Assert.Equal(name, item.Display);
    }

    [Fact]
    public void Tooltip_OfANamedLongPredicate_IsTruncated()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression()) { Name = "Named" };

        Assert.EndsWith("...", item.Tooltip);
        Assert.Equal(2003, item.Tooltip.Length);
    }

    [Fact]
    public void Constructor_CanStartNamed()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", name: "  Named  ");

        Assert.Equal("Named", item.Name);
        Assert.Equal("Named", item.Display);
    }

    [Fact]
    public void IsEditing_DefaultsToFalseAndRaisesPropertyChangedOnChangeOnly()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");
        var raised = 0;
        item.PropertyChanged += (s, e) =>
        {
            Assert.Equal(nameof(CachedPredicateItem.IsEditing), e.PropertyName);
            raised++;
        };

        Assert.False(item.IsEditing);

        item.IsEditing = true;
        item.IsEditing = true;

        Assert.True(item.IsEditing);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void EditText_IsIndependentOfTheName()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        item.EditText = "abandoned";

        Assert.Null(item.Name);
        Assert.Equal("n != null", item.Display);
        Assert.Equal("abandoned", item.EditText);
    }

    [Fact]
    public void EditText_RaisesPropertyChangedOnChangeOnly()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");
        var raised = 0;
        item.PropertyChanged += (s, e) =>
        {
            Assert.Equal(nameof(CachedPredicateItem.EditText), e.PropertyName);
            raised++;
        };

        item.EditText = "x";
        item.EditText = "x";

        Assert.Equal(1, raised);
    }

    [Fact]
    public void BeginEdit_OnAnUnnamedRow_SeedsTheWholePredicate()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        item.BeginEdit();

        Assert.True(item.IsEditing);
        Assert.Equal("n != null", item.EditText);
    }

    [Fact]
    public void BeginEdit_OnANamedRow_SeedsTheName()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null", name: "Named");

        item.BeginEdit();

        Assert.Equal("Named", item.EditText);
    }

    /// <summary>Display is truncated, so seeding from it would turn a clipped predicate into the row's name.</summary>
    [Fact]
    public void BeginEdit_OnALongPredicate_SeedsTheUntruncatedText()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        item.BeginEdit();

        Assert.Equal(item.Pretty, item.EditText);
        Assert.DoesNotContain("...", item.EditText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CommitEdit_AnEmptiedBox_BringsTheDefaultBack(string emptied)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null", name: "Named");

        item.BeginEdit();
        item.EditText = emptied;
        item.CommitEdit();

        Assert.Null(item.Name);
        Assert.Equal("n != null", item.Display);
        Assert.Null(item.Tooltip);
        Assert.False(item.IsEditing);
    }

    [Fact]
    public void CommitEdit_ThePredicateTypedBackUnchanged_IsNotAName()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null", name: "Named");

        item.BeginEdit();
        item.EditText = "  n != null  ";
        item.CommitEdit();

        Assert.Null(item.Name);
        Assert.Equal("n != null", item.Display);
    }

    /// <summary>Seeding untruncated is only half of it: committing that seed unchanged must also leave no name.</summary>
    [Fact]
    public void CommitEdit_ALongPredicateLeftUnchanged_IsNotAName()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        item.BeginEdit();
        item.CommitEdit();

        Assert.Null(item.Name);
        Assert.Null(item.Tooltip);
    }

    [Fact]
    public void CommitEdit_ARealName_IsKeptTrimmed()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        item.BeginEdit();
        item.EditText = "  Non-null nodes  ";
        item.CommitEdit();

        Assert.Equal("Non-null nodes", item.Name);
        Assert.Equal("Non-null nodes", item.Display);
        Assert.Equal("n != null", item.Tooltip);
    }

    [Fact]
    public void AbandoningAnEdit_LeavesTheNameAlone()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null", name: "Named");

        item.BeginEdit();
        item.EditText = "abandoned";
        item.IsEditing = false;

        Assert.Equal("Named", item.Name);
    }

    [Fact]
    public void Constructor_CanStartFavorited()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", isFavorite: true);

        Assert.True(item.IsFavorite);
    }

    [Fact]
    public void Constructor_ExposesKindModeAndTextUnchanged()
    {
        var item = new CachedPredicateItem(TargetKind.Operation, PredicateMode.Body, "return op != null;");

        Assert.Equal(TargetKind.Operation, item.Kind);
        Assert.Equal(PredicateMode.Body, item.Mode);
        Assert.Equal("return op != null;", item.Text);
    }
}
