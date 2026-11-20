using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class HitLocationTests
{
    [Fact]
    public void CodeSearch_IsPathColonLine() =>
        Assert.Equal(@"C:\src\A.cs:12", HitLocation.CodeSearch(@"C:\src\A.cs", 12));

    [Fact]
    public void MSBuild_SingleLineRange_UsesColumnRange() =>
        Assert.Equal(@"C:\src\A.cs(12,5-18)", HitLocation.MSBuild(@"C:\src\A.cs", 12, 5, 12, 18));

    [Fact]
    public void MSBuild_MultiLineRange_UsesFullRange() =>
        Assert.Equal(@"C:\src\A.cs(12,5,14,2)", HitLocation.MSBuild(@"C:\src\A.cs", 12, 5, 14, 2));

    [Fact]
    public void MSBuild_EmptySpan_CollapsesToStart() =>
        Assert.Equal(@"C:\src\A.cs(12,5)", HitLocation.MSBuild(@"C:\src\A.cs", 12, 5, 12, 5));
}
