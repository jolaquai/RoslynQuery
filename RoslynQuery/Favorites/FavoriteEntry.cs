using System;

using RoslynQuery.Query;

namespace RoslynQuery.Favorites;

/// <summary>One starred expression. <see cref="Name"/> is null when the row shows the expression itself.</summary>
internal readonly struct FavoriteEntry : IEquatable<FavoriteEntry>
{
    public FavoriteEntry(TargetKind kind, PredicateMode mode, string text, string name)
    {
        Kind = kind;
        Mode = mode;
        Text = text;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public TargetKind Kind { get; }
    public PredicateMode Mode { get; }
    public string Text { get; }
    public string Name { get; }

    public bool Equals(FavoriteEntry other) =>
        Kind == other.Kind
        && Mode == other.Mode
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is FavoriteEntry other && Equals(other);

    public override int GetHashCode() =>
        (((int)Kind * 397 ^ (int)Mode) * 397 ^ (Text?.GetHashCode() ?? 0)) * 397 ^ (Name?.GetHashCode() ?? 0);

    public override string ToString() =>
        Name is null ? Kind + " " + Mode + " " + Text : Kind + " " + Mode + " " + Text + " as " + Name;
}
