using System;
using System.Collections.Generic;
using System.Linq;

namespace RoslynQuery.Tests;

/// <summary>
/// The predicates printed in README.md, read out of the file itself rather than copied into test data. A
/// mirrored list only proves that the copy compiles, which is exactly the example nobody reads.
/// </summary>
internal static class ReadmeExamples
{
    /// <summary>Everything from "## Using it" down to "### Keys"; past that the README stops printing predicates.</summary>
    private const string StartHeading = "## Using it";
    private const string EndHeading = "### Keys";

    private const string Fence = "```";
    private const string CSharpFence = "```csharp";

    internal sealed class Example
    {
        public Example(string section, string target, string text)
        {
            Section = section;
            Target = target;
            Text = text;
        }

        /// <summary>The nearest "####" heading above the example, or empty for the ones before the first.</summary>
        public string Section { get; }

        /// <summary>"SyntaxNode", "SyntaxToken" or "Operation", read off <see cref="Section"/>.</summary>
        public string Target { get; }

        public string Text { get; }

        /// <summary>Examples under the statement-bodies heading are the ones the README claims are bodies.</summary>
        public bool IsDocumentedAsBody => Section.IndexOf("statement bodies", StringComparison.OrdinalIgnoreCase) >= 0;

        public override string ToString() => (Section.Length == 0 ? "(intro)" : Section) + ": " + Text;
    }

    public static IReadOnlyList<Example> All()
    {
        var lines = RepositoryFiles.Read("README.md").Replace("\r\n", "\n").Split('\n');
        var examples = new List<Example>();
        var section = string.Empty;
        var started = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (!started)
            {
                started = line.Trim() == StartHeading;
                continue;
            }

            if (line.Trim() == EndHeading) break;
            if (line.StartsWith("#### ", StringComparison.Ordinal)) section = line.Substring(5).Trim();

            if (line.Trim() != CSharpFence) continue;

            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim() != Fence; i++) body.Add(lines[i]);

            examples.Add(new Example(section, TargetOf(section), string.Join("\r\n", body)));
        }

        if (!started) throw new InvalidOperationException("README.md no longer has a '" + StartHeading + "' heading.");

        return examples;
    }

    public static IEnumerable<object[]> For(string target) =>
        All().Where(e => e.Target == target).Select(e => new object[] { e.Text });

    private static string TargetOf(string section)
    {
        if (section.IndexOf("SyntaxToken", StringComparison.OrdinalIgnoreCase) >= 0) return "SyntaxToken";
        if (section.IndexOf("IOperation", StringComparison.OrdinalIgnoreCase) >= 0) return "Operation";

        return "SyntaxNode";
    }
}
