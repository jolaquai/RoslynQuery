using System;
using System.ComponentModel;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>One entry in the cached-predicates sidebar: a normalized predicate still in <see cref="PredicateCompiler"/>'s cache, or one starred in <see cref="Favorites.FavoritesStore"/>.</summary>
internal sealed class CachedPredicateItem : INotifyPropertyChanged
{
    // Not a cap on the entry: Pretty stays whole and is what a double-click restores. This only
    // stops a pasted novel from being text-laid-out in full for the 64px the row actually shows.
    private const int MaxDisplayLength = 2000;

    private string _pretty;
    private bool _isFavorite;
    private bool _isEditing;
    private string _name;
    private string _editText;

    public CachedPredicateItem(TargetKind kind, PredicateMode mode, string text, bool isFavorite = false, string name = null)
    {
        Kind = kind;
        Mode = mode;
        Text = text;
        _isFavorite = isFavorite;
        _name = Normalize(name);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public TargetKind Kind { get; }
    public PredicateMode Mode { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;

            _isFavorite = value;
            Raise(nameof(IsFavorite));
        }
    }

    /// <summary>The label shown instead of the predicate, or null to show the predicate itself.</summary>
    public string Name
    {
        get => _name;
        set
        {
            value = Normalize(value);
            if (string.Equals(_name, value, StringComparison.Ordinal)) return;

            _name = value;
            Raise(nameof(Name));
            Raise(nameof(Display));
            Raise(nameof(Tooltip));
        }
    }

    /// <summary>True while the row's label is being edited in place.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;

            _isEditing = value;
            Raise(nameof(IsEditing));
        }
    }

    /// <summary>
    /// What the in-place editor holds. Separate from <see cref="Name"/> so abandoning an edit needs no undo,
    /// and so the recycled editor a virtualizing list hands back cannot show the last abandoned text.
    /// </summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (string.Equals(_editText, value, StringComparison.Ordinal)) return;

            _editText = value;
            Raise(nameof(EditText));
        }
    }

    /// <summary>The cache key text exactly as <see cref="PredicateCompiler"/> stores it.</summary>
    public string Text { get; }

    /// <summary><see cref="Text"/> re-formatted for human eyes, and what gets restored into the input box.</summary>
    public string Pretty => _pretty ??= Format(Text, Mode);

    public string Display => _name ?? Truncate(Pretty);

    /// <summary>A renamed row shows no predicate, so the predicate becomes the row's tooltip.</summary>
    public string Tooltip => _name is null ? null : Truncate(Pretty);

    public string Subtitle => Mode == PredicateMode.Body ? Kind + " (body)" : Kind.ToString();

    /// <summary>
    /// Starts an in-place rename. The editor seeds from the whole predicate rather than <see cref="Display"/>,
    /// which is truncated: committing that unchanged would turn a clipped predicate into the row's name.
    /// </summary>
    public void BeginEdit()
    {
        EditText = _name ?? Pretty;
        IsEditing = true;
    }

    /// <summary>Applies the editor's text. An emptied box, or the predicate typed back unchanged, means no name.</summary>
    public void CommitEdit()
    {
        Name = string.Equals(_editText?.Trim(), Pretty, StringComparison.Ordinal) ? null : _editText;
        IsEditing = false;
    }

    private static string Normalize(string name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string Truncate(string text) =>
        text.Length > MaxDisplayLength ? text.Substring(0, MaxDisplayLength) + "..." : text;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static string Format(string text, PredicateMode mode)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        try
        {
            if (mode == PredicateMode.Body)
            {
                // Braces so a statement list is one parse unit; the block itself is then dropped so
                // the restored text is the statements the user wrote, not a nested scope.
                var block = SyntaxFactory.ParseStatement("{" + text + "}", options: PredicateTemplate.ParseOptions) as BlockSyntax;
                if (block is null || block.ContainsDiagnostics) return text;

                return string.Join(
                    Environment.NewLine,
                    block.Statements.Select(statement => statement.NormalizeWhitespace().ToFullString().Trim()));
            }

            var expression = SyntaxFactory.ParseExpression(text, options: PredicateTemplate.ParseOptions);
            return expression.ContainsDiagnostics ? text : expression.NormalizeWhitespace().ToFullString().Trim();
        }
        catch (Exception)
        {
            // Display formatting must never be the thing that breaks the sidebar.
            return text;
        }
    }
}
