using System;
using System.Collections.Generic;

namespace RoslynQuery.ToolWindow;

internal enum WordDiffKind { Same, Removed, Added }

/// <summary>A word/whitespace-run diff between two short preview strings, used to colour <see cref="DiffHighlight"/>'s runs.</summary>
internal static class WordDiff
{
    // Bounds the O(tokens(before) * tokens(after)) DP table against a pathological After (e.g. an
    // unclamped NormalizeWhitespace dump of a large node): past this, callers get a plain remove/add
    // split instead of a real alignment rather than the UI thread stalling on the table.
    private const int MaxCells = 40_000;

    public static List<(WordDiffKind Kind, string Text)> Compute(string before, string after)
    {
        var a = Tokenize(before);
        var b = Tokenize(after);

        if ((long)a.Count * b.Count > MaxCells) return Unaligned(a, b);

        // lcs[i, j] = length of the longest common token subsequence of a[i..] and b[j..].
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<(WordDiffKind, string)>(a.Count + b.Count);
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                result.Add((WordDiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add((WordDiffKind.Removed, a[x]));
                x++;
            }
            else
            {
                result.Add((WordDiffKind.Added, b[y]));
                y++;
            }
        }
        while (x < a.Count) result.Add((WordDiffKind.Removed, a[x++]));
        while (y < b.Count) result.Add((WordDiffKind.Added, b[y++]));

        return result;
    }

    private static List<(WordDiffKind, string)> Unaligned(List<string> a, List<string> b)
    {
        var result = new List<(WordDiffKind, string)>(a.Count + b.Count);
        foreach (var token in a) result.Add((WordDiffKind.Removed, token));
        foreach (var token in b) result.Add((WordDiffKind.Added, token));
        return result;
    }

    /// <summary>Alternating whitespace-run/non-whitespace-run tokens, each kept verbatim so unchanged runs reproduce exactly.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var isSpace = char.IsWhiteSpace(text[i]);
            var start = i;
            do i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]) == isSpace);
            tokens.Add(text.Substring(start, i - start));
        }
        return tokens;
    }
}
