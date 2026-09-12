using System.Collections.Generic;
using System.Text;

namespace RoslynQuery.Storage;

/// <summary>
/// Tab-separated fields with the separators escaped out of the values, which is what lets a reader split
/// a file on line breaks and tabs without a parser: neither character survives <see cref="Escape"/>.
/// </summary>
internal static class TabSeparated
{
    public static string[] Fields(string line) => line.Split('\t');

    public static string Row(params string[] fields)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append('\t');
            sb.Append(Escape(fields[i] ?? string.Empty));
        }

        return sb.ToString();
    }

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    public static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            switch (text[++i])
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(text[i]); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Unescapes every field of a row whose length matches, or null when it does not.</summary>
    public static string[] FieldsOfLength(string line, int length)
    {
        var fields = Fields(line);
        if (fields.Length != length) return null;

        for (var i = 0; i < fields.Length; i++) fields[i] = Unescape(fields[i]);

        return fields;
    }
}
