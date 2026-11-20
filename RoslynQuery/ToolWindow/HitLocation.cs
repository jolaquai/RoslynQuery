using System.Globalization;

namespace RoslynQuery.ToolWindow;

/// <summary>Text forms of a match's location, for the result context menu. Inputs are 1-based, as an editor shows them.</summary>
internal static class HitLocation
{
    /// <summary>Visual Studio's Code Search form: <c>C:\src\File.cs:12</c>.</summary>
    public static string CodeSearch(string path, int line) => string.Concat(path, ":", line.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The location part of an MSBuild diagnostic, which the VS Error List and Output window make clickable:
    /// <c>path(line)</c>, <c>path(line,col)</c>, <c>path(line,col-endCol)</c> or, across lines, <c>path(line,col,endLine,endCol)</c>.
    /// End columns are exclusive, like Roslyn's own spans and csc's /errorendlocation. An empty span collapses to its start.
    /// </summary>
    public static string MSBuild(string path, int line, int column, int endLine, int endColumn)
    {
        string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

        string location;
        if (endLine != line) location = string.Concat(Number(line), ",", Number(column), ",", Number(endLine), ",", Number(endColumn));
        else if (endColumn > column) location = string.Concat(Number(line), ",", Number(column), "-", Number(endColumn));
        else location = string.Concat(Number(line), ",", Number(column));

        return string.Concat(path, "(", location, ")");
    }
}
