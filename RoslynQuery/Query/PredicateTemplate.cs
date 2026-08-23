using System;
using System.CodeDom.Compiler;
using System.IO;

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

    public static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview, documentationMode: Microsoft.CodeAnalysis.DocumentationMode.None);

    /// <summary>Shared with <see cref="RoslynQuery.Replace.ReplaceTemplate"/>: both wrappers need the same imports.</summary>
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Linq;
        using System.Text;
        using System.Text.RegularExpressions;
        using System.Threading.Tasks;

        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.CSharp;
        using Microsoft.CodeAnalysis.CSharp.Syntax;
        using Microsoft.CodeAnalysis.Operations;
        using Microsoft.CodeAnalysis.Text;
        """;

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

    public static string Signature(TargetKind kind) => $"async ValueTask<object> ({ParameterType(kind)} {ParameterName(kind)}, SemanticModel model, Document doc)";

    /// <summary>Returns the full source and the offset at which <paramref name="expression"/> starts.</summary>
    public static string Build(TargetKind kind, string expression, out int expressionOffset) =>
        Build(kind, PredicateMode.Expression, expression, out expressionOffset);

    /// <summary>Returns the full source and the offset at which <paramref name="text"/> starts.</summary>
    public static string Build(TargetKind kind, PredicateMode mode, string text, out int textOffset)
    {
        using var sw = new StringWriter();
        using (var itw = new IndentedTextWriter(sw))
        {
            itw.WriteLine(Usings);

            // Awaiting is opt-in, so most predicates never do; without this every one of them would
            // report CS1998 as a compile error the user did not cause.
            itw.WriteLine("#pragma warning disable CS1998");

            itw.Write("public static class ");
            itw.WriteLine(ClassName);
            using (itw.Scope())
            {
                itw.Write("public static async ValueTask<object> ");
                itw.Write(MethodName);
                itw.Write("(");
                itw.Write(ParameterType(kind));
                itw.Write(" ");
                itw.Write(ParameterName(kind));
                itw.Write(", SemanticModel model, Document doc)");
                using (itw.Scope())
                {
                    // Write() before capturing the offset: IndentedTextWriter emits the pending indent
                    // on the next write, so the offset would otherwise land on the indent.
                    itw.Write(mode == PredicateMode.Body ? string.Empty : "return (object)(");
                    itw.Flush();
                    sw.Flush();
                    textOffset = sw.GetStringBuilder().Length;

                    if (mode == PredicateMode.Body)
                    {
                        itw.WriteLine(string.IsNullOrWhiteSpace(text) ? "return true;" : text);
                    }
                    else
                    {
                        itw.WriteLine(string.IsNullOrWhiteSpace(text) ? "true" : text);
                        itw.WriteLine(");");
                    }
                }
            }
        }

        return sw.ToString();
    }
}
internal static class ItwExtensions
{
    extension(IndentedTextWriter itw)
    {
        internal IndentScope Indent() => new IndentScope(itw);
        internal BraceScope Scope() => new BraceScope(itw);
    }
}
internal readonly ref struct IndentScope : IDisposable
{
    private readonly IndentedTextWriter _itw;
    public IndentScope(IndentedTextWriter itw)
    {
        _itw = itw;
        itw.Indent++;
    }
    public readonly void Dispose() => _itw.Indent--;
}
internal readonly ref struct BraceScope : IDisposable
{
    private readonly IndentedTextWriter _itw;
    public BraceScope(IndentedTextWriter itw)
    {
        _itw = itw;
        itw.WriteLine('{');
        itw.Indent++;
    }
    public readonly void Dispose()
    {
        _itw.Indent--;
        _itw.WriteLine('}');
    }
}
