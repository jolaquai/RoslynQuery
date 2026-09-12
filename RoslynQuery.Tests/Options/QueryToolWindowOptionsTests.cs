using System.Linq;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RoslynQuery.Tests;

// The VS shell assemblies are compile-only here, so a DialogPage cannot be loaded; the page is checked as
// source, the same way ReferenceGraphOptionsTests does it.
public class QueryToolWindowOptionsTests
{
    private const string OptionsPath = @"RoslynQuery\Options\RoslynQueryOptions.cs";
    private const string ControlPath = @"RoslynQuery\ToolWindow\QueryToolWindowControl.xaml.cs";
    private const string MarkupPath = @"RoslynQuery\ToolWindow\QueryToolWindowControl.xaml";

    private static PropertyDeclarationSyntax Property(string name) =>
        CSharpSyntaxTree.ParseText(RepositoryFiles.Read(OptionsPath), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.Text == name);

    [Fact]
    public void TheGetStartedSetting_IsABoolThatDefaultsToOn()
    {
        var property = Property("ShowGetStartedLink");
        var attributes = property.AttributeLists.SelectMany(l => l.Attributes).ToList();

        Assert.Equal("bool", property.Type.ToString());
        Assert.Equal("true", property.Initializer?.Value.ToString());
        Assert.Equal("true", attributes.Single(a => a.Name.ToString() == "DefaultValue").ArgumentList.Arguments[0].ToString());
        Assert.Contains(attributes, a => a.Name.ToString() == "DisplayName");
    }

    [Fact]
    public void TheWindow_HidesTheLinkWhenTheSettingIsOff()
    {
        var control = RepositoryFiles.Read(ControlPath);

        Assert.Contains("options?.ShowGetStartedLink ?? true", control);
        Assert.Contains("GetStartedPanel.Visibility", control);
    }

    [Fact]
    public void TheLink_IsAThemedHyperlinkWiredToTheHandler()
    {
        var markup = RepositoryFiles.Read(MarkupPath);

        Assert.Contains("x:Name=\"GetStartedPanel\"", markup);
        Assert.Contains("Click=\"OnGetStartedClick\"", markup);
        Assert.Contains("ThemedDialogHyperlinkStyleKey", markup);
    }

    [Fact]
    public void TheHandler_OpensTheUrlInTheSystemBrowser()
    {
        var handler = CSharpSyntaxTree.ParseText(RepositoryFiles.Read(ControlPath), cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "OnGetStartedClick")
            .ToString();

        Assert.Contains("VsShellUtilities.OpenSystemBrowser(GetStartedUrl)", handler);
        Assert.Contains("e.Handled = true", handler);
    }
}
