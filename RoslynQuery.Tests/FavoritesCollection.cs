using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Serializes every test class that touches the favorites stores. <c>FavoritesStore.DirectoryOverride</c> and
/// the two store instances are process-wide statics, so two classes running in parallel would race each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FavoritesCollection
{
    public const string Name = "Favorites";
}
