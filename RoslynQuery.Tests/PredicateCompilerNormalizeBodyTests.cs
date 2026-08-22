using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

public class PredicateCompilerNormalizeBodyTests
{
    /// <summary>Kind+text of every token, which is the only thing the compiler ultimately sees.</summary>
    private static List<string> Tokens(string text) =>
        SyntaxFactory.ParseTokens(text, options: PredicateTemplate.ParseOptions)
            .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken))
            .Select(t => t.Kind() + ":" + t.Text)
            .ToList();

    // The whole reason NormalizeBody exists: it must be impossible for it to change what a body
    // lexes to, for any body, without appealing to an adjacency heuristic being right.
    [Theory]
    [InlineData("return true;")]
    [InlineData("var x = 1; return x > 0;")]
    [InlineData("int a=1,b=2; return a<b;")]
    [InlineData("foreach (var c in n.ChildNodes()) { if (c != null) return true; } return false;")]
    [InlineData("for (int i = 0; i < 10; i++) { if (i == 5) return true; } return false;")]
    [InlineData("while (n.Parent != null) { n = n.Parent; } return n is object;")]
    [InlineData("var f = (int x) => x * 2; return f(2) == 4;")]
    [InlineData("return n switch { null => false, _ => true };")]
    [InlineData("try { return n.ToString().Length > 0; } catch { return false; }")]
    [InlineData("return 1 - -1 == 2;")]
    [InlineData("return a==-1;")]
    [InlineData("return 1.ToString().Length > 0;")]
    [InlineData("return x[1..2].Length > 0;")]
    [InlineData(@"return ""a  b"".Length > 0;")]
    [InlineData(@"return @""a  b"".Length > 0;")]
    [InlineData(@"return $""{n} b"".Length > 0;")]
    [InlineData(@"return 'x' == ' ';")]
    [InlineData("return n is not null && n.Parent is object;")]
    [InlineData("// leading\r\nreturn true; /* trailing */")]
    public void NormalizeBody_PreservesTokenStream(string body)
    {
        var normalized = ExpressionSupport.NormalizeBody(body);

        Assert.Equal(Tokens(body), Tokens(normalized));
    }

    [Fact]
    public void NormalizeBody_PreservesRawStringLiteralTokenStream()
    {
        var body = "return \"\"\"a  b\"\"\".Length > 0;";

        Assert.Equal(Tokens(body), Tokens(ExpressionSupport.NormalizeBody(body)));
    }

    [Fact]
    public void NormalizeBody_DoesNotCollapseWhitespaceInsideStringLiterals()
    {
        var normalized = ExpressionSupport.NormalizeBody(@"return ""a    b"" == s;");

        Assert.Contains(@"""a    b""", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NormalizeBody_NullOrWhitespace_NormalizesToEmpty(string body) =>
        Assert.Equal(string.Empty, ExpressionSupport.NormalizeBody(body));

    [Fact]
    public void NormalizeBody_CollapsesGapsToASingleSeparator()
    {
        // Both directions converge on the same canonical form, which is what makes it a cache key.
        Assert.Equal("var x = 1 ;", ExpressionSupport.NormalizeBody("var    x=1;"));
        Assert.Equal("var x = 1 ;", ExpressionSupport.NormalizeBody("var x  =  1 ;"));
    }

    [Fact]
    public void NormalizeBody_DifferentlyFormattedEquivalentBodies_ShareAKey()
    {
        var a = ExpressionSupport.NormalizeBody("if (n != null) { return true; }\r\nreturn false;");
        var b = ExpressionSupport.NormalizeBody("if(n!=null){return true;}\r\nreturn false;");
        var c = ExpressionSupport.NormalizeBody("if ( n  !=  null )  { return true; }\r\n/* x */ return false;");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void NormalizeBody_CollapsesLineBreaks()
    {
        // A line break is just another gap: reformatting a body must not leak a second assembly.
        var normalized = ExpressionSupport.NormalizeBody("var x = 1;\r\nreturn x > 0;");

        Assert.DoesNotContain("\n", normalized);
        Assert.DoesNotContain("\r", normalized);
    }

    [Fact]
    public void NormalizeBody_LineBreakOrSpace_ProducesOneKey()
    {
        var multiLine = ExpressionSupport.NormalizeBody(
            "var node = n as MemberAccessExpressionSyntax;\r\nreturn node is not null;");
        var singleLine = ExpressionSupport.NormalizeBody(
            "var node = n as MemberAccessExpressionSyntax; return node is not null;");

        Assert.Equal(multiLine, singleLine);
    }

    [Fact]
    public void NormalizeBody_DropsComments()
    {
        var normalized = ExpressionSupport.NormalizeBody("return true; // why\r\n/* and */");

        Assert.DoesNotContain("why", normalized);
        Assert.DoesNotContain("and", normalized);
    }

    [Theory]
    [InlineData("var x = 1; return x > 0;")]
    [InlineData("foreach (var c in n.ChildNodes()) { return true; }")]
    [InlineData("if (n != null)\r\n{\r\n    return true;\r\n}\r\nreturn false;")]
    public void NormalizeBody_IsIdempotent(string body)
    {
        var once = ExpressionSupport.NormalizeBody(body);

        Assert.Equal(once, ExpressionSupport.NormalizeBody(once));
    }

    // Defence in depth, matching Normalize: Compile rejects directives before NormalizeBody is
    // reached, but it must stay safe if it is ever called on its own.
    [Fact]
    public void NormalizeBody_ConditionalBody_KeepsBothBranches()
    {
        var normalized = ExpressionSupport.NormalizeBody("#if DEBUG\r\nreturn true;\r\n#else\r\nreturn false;\r\n#endif");

        Assert.Contains("#if DEBUG", normalized);
        Assert.Contains("return true;", normalized);
        Assert.Contains("#else", normalized);
        Assert.Contains("return false;", normalized);
        Assert.Contains("#endif", normalized);
    }

    [Fact]
    public void NormalizeBody_DirectiveBearingInput_IsDeterministic()
    {
        const string body = "#if DEBUG\r\nreturn true;\r\n#endif";

        Assert.Equal(ExpressionSupport.NormalizeBody(body), ExpressionSupport.NormalizeBody(body));
    }

    [Fact]
    public void NormalizeBody_HashInsideStringLiteral_TakesTheNormalPath() =>
        Assert.Equal(@"return s == ""#if"" ;", ExpressionSupport.NormalizeBody(@"return s == ""#if"";"));
}
