using System.ComponentModel;

using RoslynQuery.Query;

namespace RoslynQuery.Replace;

/// <summary>
/// A generated replacement for one <see cref="QueryHit"/>. Mutable (<see cref="Included"/> is
/// toggled by the user and by conflict detection), unlike <see cref="QueryHit"/> itself.
/// </summary>
internal sealed class ReplacementItem : INotifyPropertyChanged
{
    private bool _included = true;

    public QueryHit Hit { get; set; }

    /// <summary>The hit's own preview text, reused verbatim so before/after line up.</summary>
    public string Before { get; set; }

    /// <summary>
    /// The replacement text: the string result verbatim, or <c>NormalizeWhitespace().ToFullString()</c>
    /// for a SyntaxNode/SyntaxToken result. Null when the hit was skipped (see <see cref="Warning"/>).
    /// </summary>
    public string After { get; set; }

    // Notifies, unlike the other properties: Select All/None sets this from code after the row's
    // checkbox is already bound and on screen, and WPF only refreshes on a write it hears about.
    public bool Included
    {
        get => _included;
        set
        {
            if (_included == value) return;
            _included = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included)));
        }
    }

    /// <summary>Null-result skip, an overlap with another included item, a stale span, or an exception message.</summary>
    public string Warning { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;
}
