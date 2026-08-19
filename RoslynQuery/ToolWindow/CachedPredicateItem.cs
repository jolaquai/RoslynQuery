using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>One entry in the cached-predicates sidebar: a normalized predicate still in <see cref="PredicateCompiler"/>'s cache.</summary>
internal sealed class CachedPredicateItem
{
    private const int MaxDisplayLength = 300;

    private string _pretty;

    public CachedPredicateItem(TargetKind kind, PredicateMode mode, string text)
    {
        Kind = kind;
        Mode = mode;
        Text = text;
    }

    public TargetKind Kind { get; }
    public PredicateMode Mode { get; }

    /// <summary>The cache key text exactly as <see cref="PredicateCompiler"/> stores it.</summary>
    public string Text { get; }

    /// <summary>
    /// <see cref="Text"/> re-formatted for human eyes, and what gets restored into the input box.
    /// </summary>
    /// <remarks>
    /// The cache key is minified by a normalizer that emits a separator between every pair of
    /// tokens, so a body reads "await Task . Yield ( ) ;". Round-tripping it through the parser
    /// puts real C# formatting back. Safe to restore from, because re-normalizing this only differs
    /// from the key by whitespace and so lands on the same entry. Lazy: the list is virtualized, so
    /// only the rows actually realized pay for the parse.
    /// </remarks>
    public string Pretty => _pretty ??= Format(Text, Mode);

    public string Display => Pretty.Length > MaxDisplayLength ? Pretty.Substring(0, MaxDisplayLength) + "..." : Pretty;

    public string Subtitle => Mode == PredicateMode.Body ? Kind + " (body)" : Kind.ToString();

    private static string Format(string text, PredicateMode mode)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        try
        {
            if (mode == PredicateMode.Body)
            {
                // Braces so a statement list is one parse unit; the block itself is then dropped so
                // the restored text is the statements the user wrote, not a nested scope.
                var block = SyntaxFactory.ParseStatement("{" + text + "}", options: PredicateTemplate.ParseOptions) as BlockSyntax;
                if (block is null || block.ContainsDiagnostics) return text;

                // Each statement is normalized as its own root rather than reading them back out of
                // a normalized block, which would carry the block's one level of indentation onto
                // every line after the braces are dropped.
                return string.Join(
                    Environment.NewLine,
                    block.Statements.Select(statement => statement.NormalizeWhitespace().ToFullString().Trim()));
            }

            var expression = SyntaxFactory.ParseExpression(text, options: PredicateTemplate.ParseOptions);
            return expression.ContainsDiagnostics ? text : expression.NormalizeWhitespace().ToFullString().Trim();
        }
        catch (Exception)
        {
            // Display formatting must never be the thing that breaks the sidebar.
            return text;
        }
    }
}
