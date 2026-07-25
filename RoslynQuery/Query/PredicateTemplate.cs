using System;

using Microsoft.CodeAnalysis.CSharp;

namespace RoslynQuery.Query;

/// <summary>
/// Builds the compilable wrapper around the user's expression. Both the emitter and the completion
/// source go through here so the prefix length used for offset mapping is always identical.
/// </summary>
internal static class PredicateTemplate
{
    public const string ClassName = "RoslynQueryPredicate";
    public const string MethodName = "Match";

    public static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Preview, documentationMode: Microsoft.CodeAnalysis.DocumentationMode.None);

    private const string Usings =
        "using System;\r\n" +
        "using System.Collections.Generic;\r\n" +
        "using System.Collections.Immutable;\r\n" +
        "using System.Linq;\r\n" +
        "using System.Text;\r\n" +
        "using System.Text.RegularExpressions;\r\n" +
        "\r\n" +
        "using Microsoft.CodeAnalysis;\r\n" +
        "using Microsoft.CodeAnalysis.CSharp;\r\n" +
        "using Microsoft.CodeAnalysis.CSharp.Syntax;\r\n" +
        "using Microsoft.CodeAnalysis.Operations;\r\n" +
        "using Microsoft.CodeAnalysis.Text;\r\n" +
        "\r\n";

    private const string Tail = ";\r\n    }\r\n}\r\n";

    public static string ParameterName(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => "n",
        TargetKind.SyntaxToken => "t",
        TargetKind.Operation => "op",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string ParameterType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => "SyntaxNode",
        TargetKind.SyntaxToken => "SyntaxToken",
        TargetKind.Operation => "IOperation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Signature(TargetKind kind) =>
        $"bool ({ParameterType(kind)} {ParameterName(kind)}, SemanticModel model, Document doc)";

    /// <summary>Returns the full source and the offset at which <paramref name="expression"/> starts.</summary>
    public static string Build(TargetKind kind, string expression, out int expressionOffset)
    {
        var head = Usings +
            "public static class " + ClassName + "\r\n{\r\n" +
            "    public static bool " + MethodName + "(" + ParameterType(kind) + " " + ParameterName(kind) + ", SemanticModel model, Document doc)\r\n" +
            "    {\r\n" +
            "        return ";

        expressionOffset = head.Length;
        return head + (string.IsNullOrWhiteSpace(expression) ? "true" : expression) + Tail;
    }
}
