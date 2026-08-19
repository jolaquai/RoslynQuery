using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>One entry in the cached-predicates sidebar: a normalized predicate still in <see cref="PredicateCompiler"/>'s cache.</summary>
internal sealed class CachedPredicateItem
{
    private const int MaxPreviewLength = 300;

    public CachedPredicateItem(TargetKind kind, PredicateMode mode, string text)
    {
        Kind = kind;
        Mode = mode;
        Text = text;
    }

    public TargetKind Kind { get; }
    public PredicateMode Mode { get; }
    public string Text { get; }

    public string Preview => Text.Length > MaxPreviewLength ? Text.Substring(0, MaxPreviewLength) + "..." : Text;

    public string Subtitle => Kind + " . " + Mode;
}
