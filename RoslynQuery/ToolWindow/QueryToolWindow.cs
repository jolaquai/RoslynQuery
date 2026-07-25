using System.Runtime.InteropServices;

using Microsoft.VisualStudio.Shell;

namespace RoslynQuery.ToolWindow;

[Guid("e8b92829-b807-4bf0-99be-edfb69f60b0d")]
public sealed class QueryToolWindow : ToolWindowPane
{
    public QueryToolWindow() : base(null)
    {
        Caption = "Roslyn Query";
        Content = new QueryToolWindowControl();
    }
}
