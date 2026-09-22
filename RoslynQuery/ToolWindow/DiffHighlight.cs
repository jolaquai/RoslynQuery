using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RoslynQuery.ToolWindow;

internal enum DiffSide { Removed, Added }

/// <summary>
/// Word-level diff colouring for a replacement preview row, split across its Before/After
/// <see cref="TextBlock"/>s (one <see cref="DiffSide"/> each). Renders directly like
/// <see cref="SignatureText"/> since <c>Inlines</c> is not bindable.
/// </summary>
internal static class DiffHighlight
{
    private static readonly Brush RemovedBrush = Frozen(Color.FromRgb(0xE0, 0x65, 0x5F));
    private static readonly Brush AddedBrush = Frozen(Color.FromRgb(0x5F, 0xA0, 0x5F));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty BeforeProperty = DependencyProperty.RegisterAttached(
        "Before", typeof(string), typeof(DiffHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty AfterProperty = DependencyProperty.RegisterAttached(
        "After", typeof(string), typeof(DiffHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached(
        "Side", typeof(DiffSide), typeof(DiffHighlight), new PropertyMetadata(DiffSide.Removed, OnChanged));

    public static string GetBefore(DependencyObject element) => (string)element.GetValue(BeforeProperty);
    public static void SetBefore(DependencyObject element, string value) => element.SetValue(BeforeProperty, value);

    public static string GetAfter(DependencyObject element) => (string)element.GetValue(AfterProperty);
    public static void SetAfter(DependencyObject element, string value) => element.SetValue(AfterProperty, value);

    public static DiffSide GetSide(DependencyObject element) => (DiffSide)element.GetValue(SideProperty);
    public static void SetSide(DependencyObject element, DiffSide value) => element.SetValue(SideProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is TextBlock block)) return;

        var before = GetBefore(block);
        var after = GetAfter(block);
        var side = GetSide(block);

        block.Inlines.Clear();
        if (before is null) return;

        // No After to diff against (a skipped/warning row): reproduce the old plain-strikethrough
        // Before line, and leave the After line empty exactly as its old Text binding did.
        if (after is null)
        {
            if (side == DiffSide.Removed)
                block.Inlines.Add(new Run(before) { TextDecorations = TextDecorations.Strikethrough });
            return;
        }

        foreach (var (kind, text) in WordDiff.Compute(before, after))
        {
            if (side == DiffSide.Removed && kind == WordDiffKind.Added) continue;
            if (side == DiffSide.Added && kind == WordDiffKind.Removed) continue;

            var run = new Run(text);
            if (kind == WordDiffKind.Removed)
            {
                run.Foreground = RemovedBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else if (kind == WordDiffKind.Added)
            {
                run.Foreground = AddedBrush;
            }

            block.Inlines.Add(run);
        }
    }
}
