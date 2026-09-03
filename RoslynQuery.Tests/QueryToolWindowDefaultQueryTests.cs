using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

// The tool window seeds the Find box with this text on first load, unedited - if it doesn't
// compile, that's a compile error greeting every user who opens the window.
[Collection(PredicateCompilerCacheCollection.Name)]
public class QueryToolWindowDefaultQueryTests
{
    [Fact]
    public void DefaultQueryBoxContent_Compiles() =>
        PredicateCompiler.Compile(TargetKind.SyntaxNode, QueryToolWindowControl.DefaultQueryBoxContent);
}
