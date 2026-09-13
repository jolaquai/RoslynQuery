using System.Linq;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RoslynQuery.Tests;

// DialogPage cannot load outside a Visual Studio host, so the page is checked as source, like the other option pages.
public class EnvironmentOptionsTests
{
    private const string PagePath = @"RoslynQuery\Options\EnvironmentOptions.cs";
    private const string ReferenceGraphPagePath = @"RoslynQuery\Options\ReferenceGraphOptions.cs";
    private const string PackagePath = @"RoslynQuery\RoslynQueryPackage.cs";
    private const string Category = "Environment variables (read-only)";

    private static ClassDeclarationSyntax Class(string path, string name) =>
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(path), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == name);

    private static AttributeSyntax Attribute(PropertyDeclarationSyntax property, string name) =>
        property.AttributeLists.SelectMany(l => l.Attributes).Single(a => a.Name.ToString() == name);

    private static string Literal(AttributeSyntax attribute) =>
        ((LiteralExpressionSyntax)attribute.ArgumentList.Arguments[0].Expression).Token.ValueText;

    [Fact]
    public void ThePage_IsADialogPage() =>
        Assert.Contains(Class(PagePath, "EnvironmentOptions").BaseList.Types, t => t.Type.ToString() == "DialogPage");

    [Fact]
    public void ThePackage_RegistersThePageUnderRoslynQuery()
    {
        var registration = CSharpSyntaxTree.ParseText(RepositoryFiles.Read(PackagePath), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString() == "ProvideOptionPage")
            .Select(a => a.ArgumentList.Arguments.Select(x => x.ToString()).ToArray())
            .SingleOrDefault(args => args[0] == "typeof(EnvironmentOptions)");

        Assert.NotNull(registration);
        Assert.Equal("\"RoslynQuery\"", registration[1]);
        Assert.Equal("\"Environment\"", registration[2]);
    }

    /// <summary>
    /// Get-only and hidden from serialization: DialogPage saves only serialization-visible properties and assigns only
    /// stored values on load, so a setter or a missing attribute would start persisting a value that is never an input.
    /// </summary>
    [Theory]
    [InlineData("RollForward", "DOTNET_ROLL_FORWARD")]
    [InlineData("RollForwardToPreviews", "DOTNET_ROLL_FORWARD_TO_PRERELEASE")]
    [InlineData("NuGetPackageCache", "NUGET_PACKAGES")]
    [InlineData("DotNetInstallRoot", "DOTNET_ROOT")]
    public void EachRow_IsReadOnlyAndNeverPersisted(string name, string variable)
    {
        var property = Class(PagePath, "EnvironmentOptions").Members.OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.Text == name);

        Assert.Equal("string", property.Type.ToString());
        Assert.NotNull(property.ExpressionBody);
        Assert.Null(property.AccessorList);
        Assert.Equal("DesignerSerializationVisibility.Hidden", Attribute(property, "DesignerSerializationVisibility").ArgumentList.Arguments[0].ToString());
        Assert.Equal(Category, Literal(Attribute(property, "Category")));
        Assert.Contains(variable, Literal(Attribute(property, "Description")));
    }

    [Fact]
    public void ThePage_HasNothingToSet() =>
        Assert.All(
            Class(PagePath, "EnvironmentOptions").Members.OfType<PropertyDeclarationSyntax>(),
            p => Assert.Null(p.AccessorList));

    /// <summary>These describe Visual Studio's environment, not a window, so they must not drift back onto a window's page.</summary>
    [Fact]
    public void TheReferenceGraphPage_CarriesNoEnvironmentRows()
    {
        var source = RepositoryFiles.Read(ReferenceGraphPagePath);

        Assert.DoesNotContain("GetEnvironmentVariable", source);
        Assert.DoesNotContain("(read-only)", source);
    }
}
