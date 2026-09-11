using System;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RoslynQuery.Tests;

// The VS shell assemblies are compile-only in this project, so a DialogPage cannot be loaded here - even
// typeof fails resolving its base type. The page is checked as source instead.
public class ReferenceGraphOptionsTests
{
    private const string OptionsPath = @"RoslynQuery\Options\ReferenceGraphOptions.cs";
    private const string PackagePath = @"RoslynQuery\RoslynQueryPackage.cs";

    private static readonly string[] Switches = ["EnableIlAnalysis", "EnableReverseIlAnalysis"];

    private static ClassDeclarationSyntax Page() =>
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(OptionsPath)).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "ReferenceGraphOptions");

    private static PropertyDeclarationSyntax Property(string name) =>
        Page().Members.OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.Text == name);

    private static AttributeSyntax Attribute(PropertyDeclarationSyntax property, string name) =>
        property.AttributeLists.SelectMany(l => l.Attributes).Single(a => a.Name.ToString() == name);

    private static string Description(string property) =>
        ((LiteralExpressionSyntax)Attribute(Property(property), "Description").ArgumentList.Arguments[0].Expression).Token.ValueText;

    [Fact]
    public void ThePage_IsADialogPage() =>
        Assert.Contains(Page().BaseList.Types, t => t.Type.ToString() == "DialogPage");

    [Theory]
    [InlineData("EnableIlAnalysis")]
    [InlineData("EnableReverseIlAnalysis")]
    public void EachSwitch_IsReadOnlyAndDefaultsToFalse(string name)
    {
        var property = Property(name);

        Assert.Equal("true", Attribute(property, "ReadOnly").ArgumentList.Arguments[0].ToString());
        Assert.Equal("false", Attribute(property, "DefaultValue").ArgumentList.Arguments[0].ToString());
    }

    [Theory]
    [InlineData("EnableIlAnalysis")]
    [InlineData("EnableReverseIlAnalysis")]
    public void EachSwitch_IgnoresWhateverWasPersisted(string name)
    {
        var accessors = Property(name).AccessorList.Accessors;
        var getter = accessors.Single(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setter = accessors.Single(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        Assert.Equal("false", getter.ExpressionBody?.Expression.ToString());
        Assert.NotNull(setter.Body);
        Assert.Empty(setter.Body.Statements);
    }

    [Theory]
    [InlineData("EnableIlAnalysis")]
    [InlineData("EnableReverseIlAnalysis")]
    public void EachSwitch_SaysItIsNotYetAvailable(string name) =>
        Assert.StartsWith("NOT YET AVAILABLE", Description(name));

    [Fact]
    public void TheReverseSwitch_WarnsThatFrameworkCodeRarelyCallsYourCode()
    {
        var description = Description("EnableReverseIlAnalysis");

        Assert.Contains("WARNING", description);
        Assert.Contains("calls into YOUR code", description);
        Assert.Contains("every referenced assembly", description);
    }

    [Fact]
    public void ThePackage_RegistersThePageUnderRoslynQuery()
    {
        var registration = CSharpSyntaxTree.ParseText(RepositoryFiles.Read(PackagePath)).GetRoot()
            .DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString() == "ProvideOptionPage")
            .Select(a => a.ArgumentList.Arguments.Select(x => x.ToString()).ToArray())
            .SingleOrDefault(args => args[0] == "typeof(ReferenceGraphOptions)");

        Assert.NotNull(registration);
        Assert.Equal("\"RoslynQuery\"", registration[1]);
        Assert.Equal("\"Reference Graph\"", registration[2]);
    }

    [Fact]
    public void NothingOutsideThePage_ReferencesTheSwitches()
    {
        var source = Path.Combine(RepositoryFiles.Root, "RoslynQuery");

        var readers = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => !f.EndsWith(OptionsPath, StringComparison.OrdinalIgnoreCase))
            .Where(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot()
                .DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => Switches.Contains(id.Identifier.Text)))
            .ToList();

        Assert.Empty(readers);
    }

    [Fact]
    public void RepositoryFiles_FindsThisCheckout() =>
        Assert.True(File.Exists(Path.Combine(RepositoryFiles.Root, "RoslynQuery.slnx")));
}
