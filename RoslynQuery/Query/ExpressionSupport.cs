using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

/// <summary>
/// Normalization, reference-set, and diagnostic-formatting logic shared by every "compile the
/// user's C# into a delegate" pipeline (<see cref="PredicateCompiler"/> today, the Replace
/// compiler alongside it). Stateless: callers own their own compiled-delegate cache, since a
/// predicate and a replacement with identical text are not interchangeable.
/// </summary>
internal static class ExpressionSupport
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences =
        new Lazy<ImmutableArray<MetadataReference>>(BuildReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImmutableArray<MetadataReference> References => LazyReferences.Value;

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Keyed on simple name, not path: two files with the same identity in one reference set is
        // CS1703, and the closure below can easily surface a second copy of one of these.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        void Add(Assembly assembly)
        {
            if (assembly is null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                return;
            if (seen.Add(assembly.GetName().Name))
                builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        Add(typeof(object).Assembly);
        Add(typeof(Uri).Assembly);
        Add(typeof(Enumerable).Assembly);
        Add(typeof(ImmutableArray).Assembly);
        Add(typeof(Regex).Assembly);

        var roslyn = new[] { typeof(SyntaxNode).Assembly, typeof(CSharpSyntaxNode).Assembly, typeof(Document).Assembly };
        foreach (var assembly in roslyn)
            Add(assembly);

        // Loaded by full display name, not Assembly.Load("netstandard") - .NET Framework won't GAC-probe a partial name, and without the facade even IsKind() fails CS0012.
        foreach (var name in roslyn.SelectMany(assembly => assembly.GetReferencedAssemblies()))
        {
            try
            { Add(Assembly.Load(name)); }
            catch (Exception) { }
        }

        return builder.ToImmutable();
    }

    /// <summary>Cache-key normalization: formatting-only differences collapse to the same key instead of each leaking their own compiled assembly.</summary>
    public static string Normalize(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return string.Empty;
        // Defence in depth: Compile rejects directives before ever getting here, but this stays
        // correct if it is ever called on its own.
        if (FindDirective(expression) != null)
            return expression;

        try
        {
            var sb = new StringBuilder(expression.Length);
            string previous = null;

            foreach (var token in SyntaxFactory.ParseTokens(expression, options: PredicateTemplate.ParseOptions))
            {
                if (token.IsKind(SyntaxKind.EndOfFileToken))
                    continue;
                var text = token.Text;
                if (text.Length == 0)
                    continue;

                if (previous != null && NeedsSpaceBetween(previous, text))
                    sb.Append(' ');
                sb.Append(text);
                previous = text;
            }

            return sb.ToString();
        }
        catch (Exception)
        {
            return expression;
        }
    }

    // Conservative: only ever inserts a space where omitting it could re-tokenize the boundary (e.g. "- -" -> "--").
    private static bool NeedsSpaceBetween(string previous, string next)
    {
        var a = previous[previous.Length - 1];
        var b = next[0];

        bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool IsOpChar(char c) => "+-*/%=!<>&|^?:~".IndexOf(c) >= 0;

        return (IsIdentChar(a) && IsIdentChar(b)) || (IsOpChar(a) && IsOpChar(b));
    }

    /// <summary>Cache-key normalization for a full method body; reformatting or a comment-only edit is one cache entry.</summary>
    public static string NormalizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        // Defence in depth, as in Normalize: Compile rejects directives before getting here.
        if (FindDirective(body) != null)
            return body.Trim();

        try
        {
            var sb = new StringBuilder(body.Length);
            var first = true;

            foreach (var token in SyntaxFactory.ParseTokens(body, options: PredicateTemplate.ParseOptions))
            {
                if (token.IsKind(SyntaxKind.EndOfFileToken))
                    continue;
                if (token.Text.Length == 0)
                    continue;

                if (!first)
                    sb.Append(' ');
                sb.Append(token.Text);
                first = false;
            }

            return sb.ToString();
        }
        catch (Exception)
        {
            return body.Trim();
        }
    }

    /// <summary>The text of the first real preprocessor directive in <paramref name="text"/>, or null if it has none.</summary>
    public static string FindDirective(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('#') < 0)
            return null;

        try
        {
            foreach (var token in SyntaxFactory.ParseTokens(text, options: PredicateTemplate.ParseOptions))
            {
                foreach (var trivia in token.LeadingTrivia)
                {
                    if (trivia.IsDirective)
                        return FirstLine(trivia.ToString());
                }

                foreach (var trivia in token.TrailingTrivia)
                {
                    if (trivia.IsDirective)
                        return FirstLine(trivia.ToString());
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Carries a '#' but will not even tokenize: refuse rather than normalize a directive
            // that went unseen.
            return "#";
        }
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed.Substring(0, newline);
    }

    /// <summary>Whether <paramref name="text"/> is a single complete expression or a statement body.</summary>
    public static PredicateMode DetectMode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PredicateMode.Expression;

        var expression = SyntaxFactory.ParseExpression(text, options: PredicateTemplate.ParseOptions);
        var complete = !expression.ContainsDiagnostics && text.Substring(expression.FullSpan.End).Trim().Length == 0;

        return complete ? PredicateMode.Expression : PredicateMode.Body;
    }

    /// <summary>The mode completions should be scaffolded in, for text that is still being typed.</summary>
    public static PredicateMode CompletionMode(string text)
    {
        if (DetectMode(text) == PredicateMode.Expression)
            return PredicateMode.Expression;
        return ParsesAsStatements(text) ? PredicateMode.Body : PredicateMode.Expression;
    }

    private static bool ParsesAsStatements(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            // Wrapped in a block so a bare statement list is a legal parse unit on its own.
            var block = SyntaxFactory.ParseStatement(string.Concat("{", text, "}"), options: PredicateTemplate.ParseOptions);
            return !block.ContainsDiagnostics;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Describe(ImmutableArray<Diagnostic> diagnostics, string source, int offset)
    {
        var text = SourceText.From(source);
        var origin = text.Lines.GetLinePosition(offset);
        var sb = new StringBuilder();

        foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3))
        {
            if (sb.Length > 0)
                sb.Append("  |  ");

            var start = diagnostic.Location.SourceSpan.Start;
            if (start >= offset && start <= text.Length)
            {
                // Only the first emitted line carries the template's prefix, so every later line
                // maps straight across. A one-line predicate keeps reporting a bare column.
                var position = text.Lines.GetLinePosition(start);
                var line = position.Line - origin.Line;

                if (line > 0)
                    sb.Append("line ").Append(line + 1).Append(", ");
                sb.Append("col ").Append((line == 0 ? position.Character - origin.Character : position.Character) + 1).Append(": ");
            }

            sb.Append(diagnostic.Id).Append(": ").Append(diagnostic.GetMessage());
        }

        return sb.Length > 0 ? sb.ToString() : "The expression did not compile.";
    }
}
