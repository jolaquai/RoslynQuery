using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Serializes every test class that compiles predicates. <c>PredicateCompiler</c>'s cache is a
/// process-wide static, so classes running in parallel see each other's entries - which breaks any
/// assertion made against <c>CachedExpressionCount</c> across two reads.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PredicateCompilerCacheCollection
{
    public const string Name = "PredicateCompilerCache";
}
