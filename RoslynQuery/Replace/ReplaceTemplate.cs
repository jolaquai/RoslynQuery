using System;
using System.CodeDom.Compiler;
using System.IO;

using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;

namespace RoslynQuery.Replace;

/// <summary>Builds the compilable wrapper around a user's replacement transform. Mirrors <see cref="PredicateTemplate"/>.</summary>
internal static class ReplaceTemplate
{
    public const string ClassName = "RoslynQueryReplace";
    public const string MethodName = "Replace";

    /// <summary>Replace only makes sense against live syntax; an <see cref="IOperation"/> has nothing to be replaced with.</summary>
    private static void RequireSupportedTarget(TargetKind kind)
    {
        if (kind == TargetKind.Operation)
            throw new NotSupportedException("Replace does not support IOperation matches - switch Target to SyntaxNode or SyntaxToken.");
    }

    public static string Signature(TargetKind kind)
    {
        RequireSupportedTarget(kind);
        return $"async ValueTask<object> ({PredicateTemplate.ParameterType(kind)} {PredicateTemplate.ParameterName(kind)}, SemanticModel model, Document doc)";
    }

    /// <summary>Returns the full source and the offset at which <paramref name="text"/> starts.</summary>
    public static string Build(TargetKind kind, PredicateMode mode, string text, out int textOffset)
    {
        RequireSupportedTarget(kind);

        using var sw = new StringWriter();
        using (var itw = new IndentedTextWriter(sw))
        {
            itw.WriteLine(PredicateTemplate.Usings);

            // Awaiting is opt-in, so most transforms never do; without this every one of them
            // would report CS1998 as a compile error the user did not cause.
            itw.WriteLine("#pragma warning disable CS1998");

            itw.Write("public static class ");
            itw.WriteLine(ClassName);
            using (itw.Scope())
            {
                itw.Write("public static async ValueTask<object> ");
                itw.Write(MethodName);
                itw.Write("(");
                itw.Write(PredicateTemplate.ParameterType(kind));
                itw.Write(" ");
                itw.Write(PredicateTemplate.ParameterName(kind));
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
                        itw.WriteLine(string.IsNullOrWhiteSpace(text) ? "return null;" : text);
                    }
                    else
                    {
                        itw.WriteLine(string.IsNullOrWhiteSpace(text) ? "null" : text);
                        itw.WriteLine(");");
                    }
                }
            }
        }

        return sw.ToString();
    }
}
