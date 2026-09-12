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
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(OptionsPath), cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
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
        var registration = CSharpSyntaxTree.ParseText(RepositoryFiles.Read(PackagePath), cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString() == "ProvideOptionPage")
            .Select(a => a.ArgumentList.Arguments.Select(x => x.ToString()).ToArray())
            .SingleOrDefault(args => args[0] == "typeof(ReferenceGraphOptions)");

        Assert.NotNull(registration);
        Assert.Equal("\"RoslynQuery\"", registration[1]);
        Assert.Equal("\"Reference Graph\"", registration[2]);
    }

    [Fact]
    public void TheMetadataSwitch_DefaultsToIlspyAndIsStored()
    {
        var property = Property("MetadataNavigation");

        Assert.Equal("MetadataNavigationMode", property.Type.ToString());
        Assert.Equal("MetadataNavigationMode.Ilspy", Attribute(property, "DefaultValue").ArgumentList.Arguments[0].ToString());
        Assert.Equal("MetadataNavigationMode.Ilspy", property.Initializer?.Value.ToString());
        Assert.DoesNotContain(property.AttributeLists.SelectMany(l => l.Attributes), a => a.Name.ToString() == "ReadOnly");
        Assert.All(
            property.AccessorList.Accessors,
            a => Assert.True(a.Body is null && a.ExpressionBody is null, "the setting has to persist, so neither accessor may be written out"));
    }

    [Fact]
    public void ThePage_OffersAPathOverrideForIlspy()
    {
        var property = Property("IlspyPath");

        Assert.Equal("string", property.Type.ToString());
        Assert.Contains("ILSpy.exe", Description("IlspyPath"));
    }

    [Fact]
    public void TheMetadataSwitch_IsTheOnlyThingThatPicksBetweenTheTwoWays()
    {
        var control = RepositoryFiles.Read(@"RoslynQuery\ToolWindow\ReferenceGraphToolWindowControl.xaml.cs");

        Assert.Contains("MetadataNavigationMode.VisualStudio", control);
        Assert.Contains("IlspyLocator.Find", control);
    }

    /// <summary>A missing ILSpy has to interrupt, not quietly reroute to the decompiler.</summary>
    [Fact]
    public void AMissingIlspy_ShowsAMessageBoxAndDoesNotDecompileInstead()
    {
        var body = RepositoryFiles.Read(@"RoslynQuery\ToolWindow\ReferenceGraphToolWindowControl.xaml.cs");
        var method = CSharpSyntaxTree.ParseText(body, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "NavigateToMetadata")
            .ToString();

        var guard = method.Substring(method.IndexOf("is null", StringComparison.Ordinal));

        Assert.Contains("ShowMessageBox", guard);
        Assert.Contains("NotFoundMessage", guard);
        Assert.DoesNotContain("NavigateToDecompiled", guard);
    }

    [Fact]
    public void TheDefaultScope_IsAStoredScopeKindStartingAtCurrentProject()
    {
        var property = Property("DefaultScope");

        Assert.Equal("ScopeKind", property.Type.ToString());
        Assert.Equal("ScopeKind.Project", Attribute(property, "DefaultValue").ArgumentList.Arguments[0].ToString());
        Assert.Equal("ScopeKind.Project", property.Initializer?.Value.ToString());
        Assert.Equal("Tool Window Defaults", ((LiteralExpressionSyntax)Attribute(property, "Category").ArgumentList.Arguments[0].Expression).Token.ValueText);
    }

    /// <summary>Mirrors the query window, whose combos all start on their configured default.</summary>
    [Fact]
    public void TheWindow_StartsItsScopeComboOnTheConfiguredDefault()
    {
        var control = RepositoryFiles.Read(@"RoslynQuery\ToolWindow\ReferenceGraphToolWindowControl.xaml.cs");

        Assert.Contains("options?.DefaultScope ?? ScopeKind.Project", control);
        Assert.DoesNotContain("ScopeCombo.SelectedIndex = 1;", control);
    }

    [Fact]
    public void ApplyingThePage_ReArmsTheIlspySearch()
    {
        var apply = Page().Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "OnApply");

        Assert.Contains("IlspyLocator.Reset()", apply.Body.ToString());
    }

    [Fact]
    public void NothingOutsideThePage_ReferencesTheSwitches()
    {
        var source = Path.Combine(RepositoryFiles.Root, "RoslynQuery");

        var readers = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => !f.EndsWith(OptionsPath, StringComparison.OrdinalIgnoreCase))
            .Where(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
                .DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => Switches.Contains(id.Identifier.Text)))
            .ToList();

        Assert.Empty(readers);
    }

    [Fact]
    public void RepositoryFiles_FindsThisCheckout() =>
        Assert.True(File.Exists(Path.Combine(RepositoryFiles.Root, "RoslynQuery.slnx")));
}
