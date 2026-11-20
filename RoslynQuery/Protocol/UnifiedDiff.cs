using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Protocol;

/// <summary>Line-based unified diff (Myers) between two versions of one file.</summary>
internal static class UnifiedDiff
{
    private const int Context = 3;

    // Past this the file was effectively rewritten; one removed block and one added block is as readable and bounded.
    private const long WorkBudget = 40_000_000;

    private enum Kind : byte { Same, Remove, Add }

    public static string Create(string path, SourceText oldText, SourceText newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var ops = new List<(Kind Kind, int A, int B)>(a.Length + 16);
        for (var i = 0; i < prefix; i++) ops.Add((Kind.Same, i, i));
        Myers(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix, ops);
        for (var i = 0; i < suffix; i++) ops.Add((Kind.Same, a.Length - suffix + i, b.Length - suffix + i));

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n').Append("+++ b/").Append(path).Append('\n');

        var index = 0;
        while (index < ops.Count)
        {
            while (index < ops.Count && ops[index].Kind == Kind.Same) index++;
            if (index >= ops.Count) break;

            var start = Math.Max(0, index - Context);

            // Extend until Context + Context unchanged ops separate this change from the next.
            var end = index;
            var lastChange = index;
            while (end < ops.Count && end - lastChange <= 2 * Context)
            {
                if (ops[end].Kind != Kind.Same) lastChange = end;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + Context + 1);

            int aCount = 0, bCount = 0;
            for (var i = start; i < end; i++)
            {
                if (ops[i].Kind != Kind.Add) aCount++;
                if (ops[i].Kind != Kind.Remove) bCount++;
            }

            var aStart = FirstLine(ops, start, end, a: true);
            var bStart = FirstLine(ops, start, end, a: false);
            sb.Append("@@ -").Append(aCount == 0 ? aStart : aStart + 1).Append(',').Append(aCount)
              .Append(" +").Append(bCount == 0 ? bStart : bStart + 1).Append(',').Append(bCount).Append(" @@\n");

            for (var i = start; i < end; i++)
            {
                var (kind, ai, bi) = ops[i];
                switch (kind)
                {
                    case Kind.Same: sb.Append(' ').Append(a[ai]).Append('\n'); break;
                    case Kind.Remove: sb.Append('-').Append(a[ai]).Append('\n'); break;
                    default: sb.Append('+').Append(b[bi]).Append('\n'); break;
                }
            }

            index = end;
        }

        return sb.ToString();
    }

    /// <summary>0-based line this side starts at inside the hunk. A side with no lines in it sits where the next op of the other side begins.</summary>
    private static int FirstLine(List<(Kind Kind, int A, int B)> ops, int start, int end, bool a)
    {
        for (var i = start; i < end; i++)
        {
            if (a && ops[i].Kind != Kind.Add) return ops[i].A;
            if (!a && ops[i].Kind != Kind.Remove) return ops[i].B;
        }

        return a ? ops[start].A : ops[start].B;
    }

    private static string[] Lines(SourceText text)
    {
        // SourceText sees an empty last line after a trailing newline; diff tools do not.
        var count = text.Lines.Count;
        if (count > 1 && text.Lines[count - 1].Span.IsEmpty) count--;

        var lines = new string[count];
        for (var i = 0; i < lines.Length; i++) lines[i] = text.Lines[i].ToString();
        return lines;
    }

    private static void Myers(string[] a, string[] b, int aLo, int aHi, int bLo, int bHi, List<(Kind, int, int)> ops)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        if (n == 0 && m == 0) return;

        var max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = false;
        long work = 0;

        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());
            work += v.Length;
            if (work > WorkBudget)
            {
                Fallback(aLo, aHi, bLo, bHi, ops);
                return;
            }

            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aLo + x] == b[bLo + y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }

        var script = new List<(Kind, int, int)>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]) ? k + 1 : k - 1;
            var px = vd[offset + prevK];
            var py = px - prevK;

            while (cx > px && cy > py)
            {
                cx--; cy--;
                script.Add((Kind.Same, aLo + cx, bLo + cy));
            }

            if (d > 0)
            {
                if (cx == px) script.Add((Kind.Add, aLo + cx, bLo + py));
                else script.Add((Kind.Remove, aLo + px, bLo + cy));
            }

            cx = px; cy = py;
        }

        script.Reverse();
        ops.AddRange(script);
    }

    private static void Fallback(int aLo, int aHi, int bLo, int bHi, List<(Kind, int, int)> ops)
    {
        for (var i = aLo; i < aHi; i++) ops.Add((Kind.Remove, i, bLo));
        for (var i = bLo; i < bHi; i++) ops.Add((Kind.Add, aHi, i));
    }
}
