using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

public class RuntimeRollForwardTests
{
    private static readonly string[] DocumentedInstalls = ["8.2.0", "8.2.3", "8.4.5", "9.0.0", "9.0.6", "9.7.8"];
    private static readonly string[] DocumentedInstallsWithPatch = ["8.0.1", "8.2.0", "8.2.3", "8.4.5", "9.0.0", "9.0.6", "9.7.8"];

    /// <summary>
    /// The worked example from "Select which .NET version to use": a request for 8.0.0 against these installs,
    /// resolved under each policy. Every row of that table, verbatim.
    /// </summary>
    [Theory]
    [InlineData("Minor", "8.2.3", "8.0.1")]
    [InlineData("Major", "8.2.3", "8.0.1")]
    [InlineData("LatestPatch", null, "8.0.1")]
    [InlineData("LatestMinor", "8.4.5", "8.4.5")]
    [InlineData("LatestMajor", "9.7.8", "9.7.8")]
    [InlineData("Disable", null, null)]
    public void TheDocumentedExample_ResolvesAsMicrosoftDocumentsIt(string policy, string expected, string expectedWithPatch)
    {
        Assert.True(RuntimeRollForward.TryParse(policy, out var parsed));

        Assert.Equal(expected, RuntimeRollForward.Select(DocumentedInstalls, "8.0.0", parsed, rollToPrerelease: false));
        Assert.Equal(expectedWithPatch, RuntimeRollForward.Select(DocumentedInstallsWithPatch, "8.0.0", parsed, rollToPrerelease: false));
    }

    [Theory]
    [InlineData("Minor")]
    [InlineData("Major")]
    [InlineData("LatestPatch")]
    [InlineData("LatestMinor")]
    [InlineData("LatestMajor")]
    [InlineData("Disable")]
    [InlineData("  latestmajor  ")]
    public void APolicyName_ParsesCaseInsensitively(string value) =>
        Assert.True(RuntimeRollForward.TryParse(value, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sideways")]
    [InlineData("1")]
    public void AnythingElse_IsNotAPolicyAndMeansTheDefault(string value)
    {
        Assert.False(RuntimeRollForward.TryParse(value, out var policy));
        Assert.Equal(RollForwardPolicy.Minor, policy);
    }

    [Fact]
    public void TheDefaultPolicy_IsMinor() => Assert.Equal(RollForwardPolicy.Minor, default(RollForwardPolicy));

    [Theory]
    [InlineData("Minor")]
    [InlineData("Major")]
    [InlineData("LatestMinor")]
    [InlineData("LatestMajor")]
    public void NoPolicy_EverRollsBackwards(string policy)
    {
        RuntimeRollForward.TryParse(policy, out var parsed);

        Assert.Null(RuntimeRollForward.Select(["7.0.20", "8.0.31"], "9.0.0", parsed, rollToPrerelease: false));
    }

    [Theory]
    [InlineData("9.0.17", "9.0.0")]
    [InlineData("10.0.12", "10.0.0")]
    [InlineData("8.2.3", "8.2.0")]
    [InlineData("11.0.0-preview.6.26359.118", "11.0.0-preview.6.26359.118")]
    public void AnAppBuiltOnAPack_RequestsItsMajorMinor_OrAPrereleasePackExactly(string pack, string expected) =>
        Assert.Equal(expected, RuntimeRollForward.RequestFor(pack));

    private static readonly string[] ThisMachine = ["8.0.31", "10.0.11", "10.0.12", "11.0.0-preview.6.26359.118"];

    /// <summary>A net9.0 target on a machine with runtimes 8, 10 and an 11 preview, which is exactly how the report came in.</summary>
    [Theory]
    [InlineData("Minor", false, null)]
    [InlineData("LatestPatch", false, null)]
    [InlineData("LatestMinor", false, null)]
    [InlineData("Disable", false, null)]
    [InlineData("Major", false, "10.0.12")]
    [InlineData("LatestMajor", false, "10.0.12")]
    [InlineData("LatestMajor", true, "11.0.0-preview.6.26359.118")]
    public void ANet9TargetWithoutANet9Runtime_ResolvesPerPolicy(string policy, bool rollToPrerelease, string expected)
    {
        RuntimeRollForward.TryParse(policy, out var parsed);

        Assert.Equal(expected, RuntimeRollForward.Select(ThisMachine, RuntimeRollForward.RequestFor("9.0.17"), parsed, rollToPrerelease));
    }

    /// <summary>The host prefers a release, and only considers prereleases when no release satisfies the policy.</summary>
    [Fact]
    public void AReleaseRequest_FallsBackToAPrereleaseOnlyWhenNoReleaseQualifies() =>
        Assert.Equal(
            "11.0.0-preview.6.26359.118",
            RuntimeRollForward.Select(["8.0.31", "11.0.0-preview.6.26359.118"], "9.0.0", RollForwardPolicy.Major, rollToPrerelease: false));

    [Fact]
    public void APrereleaseRequest_CanResolveToARelease() =>
        Assert.Equal("11.0.2", RuntimeRollForward.Select(["11.0.2"], "11.0.0-preview.6.1", RollForwardPolicy.Minor, rollToPrerelease: false));

    /// <summary>Latest-patch roll-forward applies to a release, never to a prerelease.</summary>
    [Fact]
    public void APrereleaseBand_DoesNotRollToItsLatestPreview() =>
        Assert.Equal(
            "11.0.0-preview.6.1",
            RuntimeRollForward.Select(["11.0.0-preview.6.1", "11.0.0-preview.7.1"], "11.0.0-preview.6.1", RollForwardPolicy.Minor, rollToPrerelease: false));

    /// <summary>preview.9 is older than preview.10; compared as text it would sort after it and count as a roll-forward.</summary>
    [Fact]
    public void PrereleaseLabels_CompareNumericallyNotAsText() =>
        Assert.Null(RuntimeRollForward.Select(["11.0.0-preview.9.1"], "11.0.0-preview.10.0", RollForwardPolicy.Minor, rollToPrerelease: false));

    [Fact]
    public void Disable_BindsOnlyTheExactVersion()
    {
        Assert.Equal("9.0.0", RuntimeRollForward.Select(["9.0.0", "9.0.6"], "9.0.0", RollForwardPolicy.Disable, rollToPrerelease: false));
        Assert.Null(RuntimeRollForward.Select(["9.0.6"], "9.0.0", RollForwardPolicy.Disable, rollToPrerelease: false));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("9.0")]
    [InlineData("9.0.0.0")]
    [InlineData("9.0.0-")]
    public void UnreadableVersions_AreNeverCandidates(string junk) =>
        Assert.Equal("9.0.6", RuntimeRollForward.Select([junk, "9.0.6"], "9.0.0", RollForwardPolicy.Minor, rollToPrerelease: false));

    [Fact]
    public void AnUnreadableRequest_ResolvesToNothing() =>
        Assert.Null(RuntimeRollForward.Select(["9.0.6"], "nine", RollForwardPolicy.LatestMajor, rollToPrerelease: false));
}
