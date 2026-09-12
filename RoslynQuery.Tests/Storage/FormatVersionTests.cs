using System.Collections.Generic;
using System.Linq;

using RoslynQuery.Storage;

using Xunit;

namespace RoslynQuery.Tests;

// A synthetic three-version chain. Favorites itself is still version 1, so this is what proves the upgrade
// scaffolding works before a real version 2 needs it - and what pins the rule it exists for: a version knows
// how to parse itself and how to carry the one directly below it forward, and nothing else.
public class FormatVersionTests
{
    /// <summary>Version 1's model: the raw value.</summary>
    private sealed class V1 : FormatVersion<List<string>>
    {
        public int ParseCalls { get; private set; }

        public override int Version => 1;

        protected override List<string> Parse(IReadOnlyList<string> rows)
        {
            ParseCalls++;
            return rows.ToList();
        }
    }

    /// <summary>Version 2 renames the model: every value gains a "v2:" prefix when carried up from version 1.</summary>
    private sealed class V2(V1 previous) : FormatVersion<List<string>, List<string>>
    {
        public int UpgradeCalls { get; private set; }

        public override int Version => 2;

        protected override IFormatVersion Previous => previous;

        protected override List<string> Parse(IReadOnlyList<string> rows) => rows.Select(r => "v2:" + r).ToList();

        protected override List<string> Upgrade(List<string> previousModel)
        {
            UpgradeCalls++;
            return previousModel.Select(r => "v2:" + r).ToList();
        }
    }

    private sealed class V3(V2 previous) : FormatVersion<List<string>, List<string>>
    {
        public int UpgradeCalls { get; private set; }

        public override int Version => 3;

        protected override IFormatVersion Previous => previous;

        protected override List<string> Parse(IReadOnlyList<string> rows) => rows.Select(r => "v3:v2:" + r).ToList();

        protected override List<string> Upgrade(List<string> previousModel)
        {
            UpgradeCalls++;
            return previousModel.Select(r => "v3:" + r).ToList();
        }
    }

    private static (V1 One, V2 Two, V3 Three) Chain()
    {
        var one = new V1();
        var two = new V2(one);

        return (one, two, new V3(two));
    }

    [Fact]
    public void ItsOwnVersion_IsParsedDirectly()
    {
        var (one, two, three) = Chain();

        Assert.Equal(["v3:v2:a"], three.Read(3, ["a"]));
        Assert.Equal(0, three.UpgradeCalls);
        Assert.Equal(0, two.UpgradeCalls);
        Assert.Equal(0, one.ParseCalls);
    }

    [Fact]
    public void TheVersionDirectlyBelow_IsParsedThereAndUpgradedOnce()
    {
        var (one, two, three) = Chain();

        Assert.Equal(["v3:v2:a"], three.Read(2, ["a"]));
        Assert.Equal(1, three.UpgradeCalls);
        Assert.Equal(0, two.UpgradeCalls);
        Assert.Equal(0, one.ParseCalls);
    }

    /// <summary>The point of the design: version 3 never learns version 1's layout, it only ever asks version 2.</summary>
    [Fact]
    public void AnOlderVersion_IsWalkedUpOneStepAtATime()
    {
        var (one, two, three) = Chain();

        Assert.Equal(["v3:v2:a"], three.Read(1, ["a"]));
        Assert.Equal(1, one.ParseCalls);
        Assert.Equal(1, two.UpgradeCalls);
        Assert.Equal(1, three.UpgradeCalls);
    }

    [Fact]
    public void AVersionAboveTheCurrentOne_ReadsAsEmpty()
    {
        var (_, _, three) = Chain();

        Assert.Empty(three.Read(4, ["a"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AMissingStamp_ReadsAsEmpty(int stamped)
    {
        var (_, _, three) = Chain();

        Assert.Empty(three.Read(stamped, ["a"]));
    }

    [Fact]
    public void TheOldestVersion_ReadsAnythingButItselfAsEmpty()
    {
        var (one, _, _) = Chain();

        Assert.Empty(one.Read(2, ["a"]));
        Assert.Empty(one.Read(0, ["a"]));
        Assert.Equal(["a"], one.Read(1, ["a"]));
    }

    [Fact]
    public void AVersionIsReachableWithoutKnowingItsModel()
    {
        var (_, two, _) = Chain();

        Assert.Equal(2, ((IFormatVersion)two).Version);
        Assert.Equal(["v2:a"], Assert.IsType<List<string>>(((IFormatVersion)two).ReadUntyped(2, ["a"])));
    }
}
