using System.Collections.Generic;

namespace RoslynQuery.Storage;

/// <summary>
/// A format version seen without knowing its model type. This exists so a version can hold its predecessor
/// in a plain field: typing the predecessor would make the type arguments grow with the version number.
/// </summary>
internal interface IFormatVersion
{
    int Version { get; }

    object ReadUntyped(int stampedVersion, IReadOnlyList<string> rows);
}

/// <summary>
/// The oldest version of a persisted format: it parses its own rows and has nothing to upgrade from.
/// Rows exclude the stamped header line.
/// </summary>
internal abstract class FormatVersion<TModel> : IFormatVersion
{
    public abstract int Version { get; }

    /// <summary>Reads rows written by exactly this version. Structurally broken rows are skipped, never thrown on.</summary>
    protected abstract TModel Parse(IReadOnlyList<string> rows);

    /// <summary>This version's model, whatever version the file was stamped with. Anything unreachable reads as empty.</summary>
    public virtual TModel Read(int stampedVersion, IReadOnlyList<string> rows) =>
        stampedVersion == Version ? Parse(rows) : Parse([]);

    object IFormatVersion.ReadUntyped(int stampedVersion, IReadOnlyList<string> rows) => Read(stampedVersion, rows);
}

/// <summary>
/// A later version of a persisted format. It knows two things and no more: how to parse its own rows, and how
/// to carry the version directly below it forward. Older files are reached by recursion, so adding a version
/// means writing one parser and one upgrade step rather than one per version that came before.
/// </summary>
internal abstract class FormatVersion<TModel, TPreviousModel> : FormatVersion<TModel>
{
    /// <summary>The version directly below this one.</summary>
    protected abstract IFormatVersion Previous { get; }

    /// <summary>Carries the version below this one forward. Only ever handed <see cref="Previous"/>'s model.</summary>
    protected abstract TModel Upgrade(TPreviousModel previous);

    public override TModel Read(int stampedVersion, IReadOnlyList<string> rows)
    {
        if (stampedVersion == Version) return Parse(rows);

        // Above this version means a newer extension wrote it; at or below zero means there was no stamp.
        if (stampedVersion <= 0 || stampedVersion > Version) return Parse([]);

        return Upgrade((TPreviousModel)Previous.ReadUntyped(stampedVersion, rows));
    }
}
