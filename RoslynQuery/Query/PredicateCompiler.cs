using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

internal delegate bool NodeMatch(SyntaxNode n, SemanticModel model, Document doc);
internal delegate bool TokenMatch(SyntaxToken t, SemanticModel model, Document doc);
internal delegate bool OperationMatch(IOperation op, SemanticModel model, Document doc);

internal sealed class PredicateCompilationException : Exception
{
    public PredicateCompilationException(string message, ImmutableArray<Diagnostic> diagnostics) : base(message) => Diagnostics = diagnostics;

    public ImmutableArray<Diagnostic> Diagnostics { get; }
}

/// <summary>
/// Emits the user's expression as a static method and binds a delegate to it. A script host
/// (<c>CSharpScript</c>) would cost a Task and a globals binding per invocation, which is not
/// affordable at hundreds of thousands of calls per run.
/// </summary>
internal static class PredicateCompiler
{
    // net472 has no collectible load context (AssemblyLoadContext.RunAndCollect and
    // AssemblyBuilderAccess.RunAndCollect are both .NET Core+) and the only Framework-era unload
    // primitive, AppDomain.Unload, cannot host this: SyntaxNode/SemanticModel/Document are not
    // MarshalByRefObject or serializable, so a predicate delegate cannot cross an AppDomain boundary
    // without per-member proxying, which would cost far more than the several KB per expression this
    // is trying to save. Every unique expression therefore leaks its emitted assembly for the process
    // lifetime; the cap below only bounds *this* dictionary against pathological input (e.g.
    // programmatically generated expressions with embedded GUIDs), not the underlying leak.
    private const int MaxCachedExpressions = 512;
    private static readonly ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate> Cache = new ConcurrentDictionary<(TargetKind, PredicateMode, string), Delegate>();
    private static readonly ConcurrentQueue<(TargetKind, PredicateMode, string)> CacheOrder = new ConcurrentQueue<(TargetKind, PredicateMode, string)>();
    private static long _totalEmittedBytes;

    /// <summary>Sum of raw PE image bytes handed to <see cref="Assembly.Load(byte[])"/> so far, including
    /// evicted entries: on net472 that memory is never actually reclaimed, so this only grows.</summary>
    public static long TotalEmittedBytes => Interlocked.Read(ref _totalEmittedBytes);

    public static int CachedExpressionCount => Cache.Count;

    /// <summary>Cached predicates, most-recently-compiled first. Skips keys evicted since being enqueued.</summary>
    public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot()
    {
        var order = CacheOrder.ToArray();
        var result = new List<(TargetKind, PredicateMode, string)>(order.Length);
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var key = order[i];
            if (Cache.ContainsKey(key)) result.Add(key);
        }
        return result;
    }

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
            if (assembly is null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return;
            if (seen.Add(assembly.GetName().Name)) builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        Add(typeof(object).Assembly);
        Add(typeof(Uri).Assembly);
        Add(typeof(Enumerable).Assembly);
        Add(typeof(ImmutableArray).Assembly);
        Add(typeof(Regex).Assembly);

        var roslyn = new[] { typeof(SyntaxNode).Assembly, typeof(CSharpSyntaxNode).Assembly, typeof(Document).Assembly };
        foreach (var assembly in roslyn) Add(assembly);

        // Roslyn is compiled against netstandard2.0, so its public surface reaches System.Object and
        // System.Enum through the facade: without it even `n.IsKind(SyntaxKind.X)` fails CS0012.
        // Assembly.Load("netstandard") cannot get it, because .NET Framework never probes the GAC
        // for a partial name. Roslyn's own reference list carries the full display names, which do
        // bind, and it stays correct if Roslyn's facade set ever changes.
        foreach (var name in roslyn.SelectMany(assembly => assembly.GetReferencedAssemblies()))
        {
            try { Add(Assembly.Load(name)); }
            catch (Exception) { }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Re-lexes the expression and rejoins its tokens with the minimum whitespace that still
    /// tokenizes the same way, dropping comments and all original trivia in the process. Two
    /// expressions that differ only in formatting collapse to the same cache key instead of each
    /// leaking their own compiled assembly. Falls back to the raw text if lexing itself throws.
    /// </summary>
    private static string Normalize(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return string.Empty;
        // Defence in depth: Compile rejects directives before ever getting here, but this stays
        // correct if it is ever called on its own.
        if (FindDirective(expression) != null) return expression;

        try
        {
            var sb = new StringBuilder(expression.Length);
            string previous = null;

            foreach (var token in SyntaxFactory.ParseTokens(expression, options: PredicateTemplate.ParseOptions))
            {
                if (token.IsKind(SyntaxKind.EndOfFileToken)) continue;
                var text = token.Text;
                if (text.Length == 0) continue;

                if (previous != null && NeedsSpaceBetween(previous, text)) sb.Append(' ');
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

    // Conservative: a space is inserted whenever omitting it could re-tokenize the boundary into
    // something else (identifier/keyword/number runs merging, or operator runs like "- -" -> "--",
    // "= =" -> "==" merging). Never omits a space that safety requires; may keep one that isn't
    // strictly necessary (e.g. around punctuation), which only costs a byte, never correctness.
    private static bool NeedsSpaceBetween(string previous, string next)
    {
        var a = previous[previous.Length - 1];
        var b = next[0];

        bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool IsOpChar(char c) => "+-*/%=!<>&|^?:~".IndexOf(c) >= 0;

        return (IsIdentChar(a) && IsIdentChar(b)) || (IsOpChar(a) && IsOpChar(b));
    }

    /// <summary>
    /// Cache-key normalization for a full method body. Where <see cref="Normalize"/> drops a
    /// separator whenever <see cref="NeedsSpaceBetween"/> judges the boundary safe, this always
    /// emits exactly one separator between adjacent tokens. That is a weaker minification but a
    /// stronger guarantee: adding whitespace between two tokens can never merge or split them, so
    /// the result provably re-lexes to the input's token stream without relying on any adjacency
    /// heuristic being correct for whatever a body happens to contain.
    /// </summary>
    /// <remarks>
    /// Every gap collapses to a single space, line breaks included, so reformatting a body cannot
    /// leak a second assembly: "var x = 1;\nreturn x;" and "var x = 1; return x;" are one entry.
    /// Comments are dropped for the same reason. This is a cache key and nothing else - what gets
    /// compiled is the text as typed, so diagnostics still point at the real line and column.
    /// </remarks>
    public static string NormalizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        // Defence in depth, as in Normalize: Compile rejects directives before getting here.
        if (FindDirective(body) != null) return body.Trim();

        try
        {
            var sb = new StringBuilder(body.Length);
            var first = true;

            foreach (var token in SyntaxFactory.ParseTokens(body, options: PredicateTemplate.ParseOptions))
            {
                if (token.IsKind(SyntaxKind.EndOfFileToken)) continue;
                if (token.Text.Length == 0) continue;

                if (!first) sb.Append(' ');
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
    /// <remarks>
    /// Lexer-based rather than textual, because the answer decides whether input is rejected: a
    /// "#if" inside a string literal is part of a token, and one inside a comment is comment
    /// trivia. Neither is a directive and neither is reported here - only trivia the lexer itself
    /// classified as a directive counts. The '#' pre-check keeps the ordinary case to one scan
    /// with no lexing at all.
    /// </remarks>
    private static string FindDirective(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('#') < 0) return null;

        try
        {
            foreach (var token in SyntaxFactory.ParseTokens(text, options: PredicateTemplate.ParseOptions))
            {
                foreach (var trivia in token.LeadingTrivia)
                {
                    if (trivia.IsDirective) return FirstLine(trivia.ToString());
                }

                foreach (var trivia in token.TrailingTrivia)
                {
                    if (trivia.IsDirective) return FirstLine(trivia.ToString());
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

    public static Type DelegateType(TargetKind kind) => kind switch
    {
        TargetKind.SyntaxNode => typeof(NodeMatch),
        TargetKind.SyntaxToken => typeof(TokenMatch),
        TargetKind.Operation => typeof(OperationMatch),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Whether <paramref name="text"/> is a single complete expression or a statement body.
    /// </summary>
    /// <remarks>
    /// Asks the parser rather than scanning for a "return" token, which gets both directions wrong:
    /// a lambda block body puts a return inside what is still one expression
    /// ("xs.Any(c => { return c != null; })"), and a body that throws has no return at all. The
    /// answer is unambiguous by construction, since expression is tested first.
    /// </remarks>
    public static PredicateMode DetectMode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PredicateMode.Expression;

        var expression = SyntaxFactory.ParseExpression(text, options: PredicateTemplate.ParseOptions);
        var complete = !expression.ContainsDiagnostics && text.Substring(expression.FullSpan.End).Trim().Length == 0;

        return complete ? PredicateMode.Expression : PredicateMode.Body;
    }

    /// <summary>
    /// The mode completions should be scaffolded in, for text that is still being typed.
    /// </summary>
    /// <remarks>
    /// <see cref="DetectMode"/> is right for compiling finished text but wrong here: it answers
    /// "not a complete expression" with Body, and text mid-token is never a complete expression, so
    /// every keystroke of an expression predicate would scaffold as a statement body and complete
    /// against the wrong context. Body is therefore only assumed when the text actually parses as
    /// statements; anything still broken is treated as an expression in progress.
    /// </remarks>
    public static PredicateMode CompletionMode(string text)
    {
        if (DetectMode(text) == PredicateMode.Expression) return PredicateMode.Expression;
        return ParsesAsStatements(text) ? PredicateMode.Body : PredicateMode.Expression;
    }

    private static bool ParsesAsStatements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            // Wrapped in a block so a bare statement list is a legal parse unit on its own.
            var block = SyntaxFactory.ParseStatement("{" + text + "}", options: PredicateTemplate.ParseOptions);
            return !block.ContainsDiagnostics;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Delegate Compile(TargetKind kind, string text) => Compile(kind, DetectMode(text), text);

    public static Delegate Compile(TargetKind kind, PredicateMode mode, string text)
    {
        // Rejected outright rather than normalized or passed through. ParseTokens evaluates
        // directives against ParseOptions, which defines no preprocessor symbols, so
        // "#if DEBUG a #else b #endif" collapses to "b" with the other branch gone before anything
        // downstream can see it. Compiling half of what was written, silently, is worse than
        // saying the construct is unsupported.
        var directive = FindDirective(text);
        if (directive != null)
        {
            throw new PredicateCompilationException(
                $"#directives are not supported: found '{directive}'.",
                []);
        }

        var normalized = mode == PredicateMode.Body ? NormalizeBody(text) : Normalize(text);
        var key = (kind, mode, normalized);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        // Built from the text as typed, not from the cache key: normalization exists to collapse
        // formatting differences onto one entry, and compiling its output instead would report
        // every diagnostic against a line and column the user never wrote.
        var source = PredicateTemplate.Build(kind, mode, text, out var offset);
        var compilation = CSharpCompilation.Create(
            "RoslynQuery_Predicate_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(SourceText.From(source), PredicateTemplate.ParseOptions)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

        using (var stream = new MemoryStream())
        {
            var result = compilation.Emit(stream);
            if (!result.Success) throw new PredicateCompilationException(Describe(result.Diagnostics, source, offset), result.Diagnostics);

            var bytes = stream.ToArray();
            Interlocked.Add(ref _totalEmittedBytes, bytes.Length);
            var type = Assembly.Load(bytes).GetType(PredicateTemplate.ClassName, throwOnError: true);
            var method = type.GetMethod(PredicateTemplate.MethodName, BindingFlags.Public | BindingFlags.Static);
            var @delegate = Cache.GetOrAdd(key, method.CreateDelegate(DelegateType(kind)));
            CacheOrder.Enqueue(key);

            while (Cache.Count > MaxCachedExpressions && CacheOrder.TryDequeue(out var oldest)) Cache.TryRemove(oldest, out _);

            return @delegate;
        }
    }

    private static string Describe(ImmutableArray<Diagnostic> diagnostics, string source, int offset)
    {
        var text = SourceText.From(source);
        var origin = text.Lines.GetLinePosition(offset);
        var sb = new StringBuilder();

        foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3))
        {
            if (sb.Length > 0) sb.Append("  |  ");

            var start = diagnostic.Location.SourceSpan.Start;
            if (start >= offset && start <= text.Length)
            {
                // Only the first emitted line carries the template's prefix, so every later line
                // maps straight across. A one-line predicate keeps reporting a bare column.
                var position = text.Lines.GetLinePosition(start);
                var line = position.Line - origin.Line;

                if (line > 0) sb.Append("line ").Append(line + 1).Append(", ");
                sb.Append("col ").Append((line == 0 ? position.Character - origin.Character : position.Character) + 1).Append(": ");
            }

            sb.Append(diagnostic.Id).Append(": ").Append(diagnostic.GetMessage());
        }

        return sb.Length > 0 ? sb.ToString() : "The predicate did not compile.";
    }
}
