using System;

using Microsoft.CodeAnalysis;

using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class SignatureClassificationTests
{
    [Fact]
    public void EveryPartKind_MapsToAClassificationName()
    {
        foreach (SymbolDisplayPartKind kind in Enum.GetValues(typeof(SymbolDisplayPartKind)))
            Assert.False(string.IsNullOrEmpty(SignatureClassification.NameFor(kind)), kind.ToString());
    }

    /// <summary>Literal names, not the constants: these are the strings the editor registers its colours under.</summary>
    [Theory]
    [InlineData(SymbolDisplayPartKind.ClassName, "class name")]
    [InlineData(SymbolDisplayPartKind.StructName, "struct name")]
    [InlineData(SymbolDisplayPartKind.InterfaceName, "interface name")]
    [InlineData(SymbolDisplayPartKind.EnumName, "enum name")]
    [InlineData(SymbolDisplayPartKind.DelegateName, "delegate name")]
    [InlineData(SymbolDisplayPartKind.TypeParameterName, "type parameter name")]
    [InlineData(SymbolDisplayPartKind.NamespaceName, "namespace name")]
    [InlineData(SymbolDisplayPartKind.MethodName, "method name")]
    [InlineData(SymbolDisplayPartKind.ExtensionMethodName, "extension method name")]
    [InlineData(SymbolDisplayPartKind.PropertyName, "property name")]
    [InlineData(SymbolDisplayPartKind.FieldName, "field name")]
    [InlineData(SymbolDisplayPartKind.EventName, "event name")]
    [InlineData(SymbolDisplayPartKind.Keyword, "keyword")]
    [InlineData(SymbolDisplayPartKind.Punctuation, "punctuation")]
    [InlineData(SymbolDisplayPartKind.Operator, "operator")]
    public void NameFor_UsesTheEditorsOwnClassificationNames(SymbolDisplayPartKind kind, string expected) =>
        Assert.Equal(expected, SignatureClassification.NameFor(kind));

    [Theory]
    [InlineData(SymbolDisplayPartKind.MethodName, true)]
    [InlineData(SymbolDisplayPartKind.ExtensionMethodName, true)]
    [InlineData(SymbolDisplayPartKind.PropertyName, true)]
    [InlineData(SymbolDisplayPartKind.FieldName, true)]
    [InlineData(SymbolDisplayPartKind.ConstantName, true)]
    [InlineData(SymbolDisplayPartKind.EnumMemberName, true)]
    [InlineData(SymbolDisplayPartKind.EventName, true)]
    [InlineData(SymbolDisplayPartKind.ClassName, false)]
    [InlineData(SymbolDisplayPartKind.NamespaceName, false)]
    [InlineData(SymbolDisplayPartKind.Keyword, false)]
    [InlineData(SymbolDisplayPartKind.Punctuation, false)]
    public void IsEmphasised_MarksOnlyMemberNames(SymbolDisplayPartKind kind, bool expected) =>
        Assert.Equal(expected, SignatureClassification.IsEmphasised(kind));
}
