using System.ComponentModel;

using RoslynQuery.Query;

namespace RoslynQuery.Replace;

/// <summary>
/// A generated replacement for one <see cref="QueryHit"/>. Mutable (<see cref="Included"/> is
/// toggled by the user and by conflict detection), unlike <see cref="QueryHit"/> itself.
/// Public, unlike the rest of Replace's types: WPF's PropertyChanged binding on .NET Framework needs it to be.
/// </summary>
public sealed class ReplacementItem : INotifyPropertyChanged
{
    private bool _included = true;

    // Internal: QueryHit is itself internal, so a public accessor here would be CS0053.
    internal QueryHit Hit { get; set; }

    /// <summary>The hit's own preview text, reused verbatim so before/after line up.</summary>
    public string Before { get; set; }

    /// <summary>Null when the hit was skipped (see <see cref="Warning"/>).</summary>
    public string After { get; set; }

    // Notifies, unlike the other properties: Select All/None sets this from code after the checkbox is already on screen.
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
