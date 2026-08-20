using System.Runtime.InteropServices;

using Microsoft.VisualStudio.Shell;

namespace RoslynQuery.ToolWindow;

[Guid("a10b8756-d683-40a5-914c-2f369a0541f6")]
public sealed class ReferenceGraphToolWindow : ToolWindowPane
{
    public ReferenceGraphToolWindow() : base(null)
    {
        Caption = "Reference Graph";
        Content = new ReferenceGraphToolWindowControl();
    }

    internal ReferenceGraphToolWindowControl Control => (ReferenceGraphToolWindowControl)Content;
}
