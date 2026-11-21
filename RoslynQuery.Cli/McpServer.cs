using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using RoslynQuery.Protocol;

namespace RoslynQuery.Cli;

/// <summary>
/// <c>roslynquery mcp</c>: the same engine behind <c>run</c>, as MCP tools over stdio, for clients that cannot shell out.
/// The client starts it with the project as its working directory, which is what picks the right Visual Studio.
/// stdout belongs to the protocol, so everything else goes to stderr.
/// </summary>
internal static class McpServer
{
    public static async Task<int> RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<Tools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    [McpServerToolType]
    private sealed class Tools
    {
        private const string Common = " Lines and columns are 1-based. Read the 'docs' tool once for the predicate signatures and examples.";

        [McpServerTool(Name = "search", ReadOnly = true, Destructive = false)]
        [Description("Runs a C# predicate over every syntax node, token or operation of a solution and returns the matches as JSON (file, line, column, end, kind, preview)." + Common)]
        public static async Task<string> Search(
            [Description("C# bool expression, or statements that return, over n/t/op, model and doc (see docs).")] string query,
            [Description("Solution/project path; default the only one in the working directory, or the one Visual Studio has open there.")] string solution = null,
            [Description("node, token or operation.")] string target = "node",
            [Description("solution, project, document, type or member. Default: document when 'file' is given, project when 'project' is, else solution.")] string scope = null,
            [Description("Project name or path, for scope=project.")] string project = null,
            [Description("File for scope=document/type/member, or a file inside the project.")] string file = null,
            [Description("1-based caret line for scope=type/member.")] int line = 1,
            [Description("1-based caret column for scope=type/member.")] int column = 1,
            [Description("Maximum matches; stats.truncated says more exist.")] int max = 200,
            bool includeGenerated = false,
            CancellationToken cancellationToken = default)
            => await Run(new RunRequest { Query = query, Solution = solution, Project = project, File = file, Line = line, Column = column, Max = max, IncludeGenerated = includeGenerated }, target, scope, cancellationToken).ConfigureAwait(false);

        [McpServerTool(Name = "replace", Destructive = true)]
        [Description("Searches like 'search', then computes a replacement per match. Without apply=true this is a dry run: it returns one unified diff per file and a planId. Review the diff, then call again with apply=true and expectPlan=<planId>; nothing is written if the plan changed. With Visual Studio attached the edit is one undoable step in the editor." + Common)]
        public static async Task<string> Replace(
            [Description("Predicate, as for 'search'.")] string query,
            [Description("C# expression/statements returning the new source text (string), a SyntaxNode, a SyntaxToken, or null to skip that match. Same parameters as the predicate (target must not be operation).")] string replacement,
            string solution = null,
            string target = "node",
            string scope = null,
            string project = null,
            string file = null,
            int line = 1,
            int column = 1,
            int max = 200,
            bool includeGenerated = false,
            [Description("Write the change. Pass together with expectPlan.")] bool apply = false,
            [Description("The planId of the dry run that was reviewed.")] string expectPlan = null,
            CancellationToken cancellationToken = default)
            => apply && string.IsNullOrEmpty(expectPlan)
                ? JsonSerializer.Serialize(new RunResponse { Error = "apply=true needs expectPlan: run without apply first, review the diff, then pass its planId.", ExitCode = 1 }, CliJsonOut.Compact)
                : await Run(new RunRequest { Query = query, Replace = replacement, Apply = apply, ExpectPlan = expectPlan, Solution = solution, Project = project, File = file, Line = line, Column = column, Max = max, IncludeGenerated = includeGenerated }, target, scope, cancellationToken).ConfigureAwait(false);

        [McpServerTool(Name = "docs", ReadOnly = true)]
        [Description("How to write predicates and replacements: parameter signatures per target, the imports already in scope, examples.")]
        public static string Docs() => Cli.Docs.Build();

        [McpServerTool(Name = "bridges", ReadOnly = true)]
        [Description("Lists the Visual Studio instances whose agent bridge is enabled (pid, open solution, ancestor = started from inside that instance). 'search' and 'replace' use the one matching their solution/working directory; pass 'solution' to choose between several.")]
        public static string Bridges() => Attach.Describe();

        private static async Task<string> Run(RunRequest request, string target, string scope, CancellationToken cancellationToken)
        {
            try
            {
                request.Target = Program.ParseTarget(target ?? "node");
                request.Scope = scope is null
                    ? request.File != null ? Query.ScopeKind.Document : request.Project != null ? Query.ScopeKind.Project : Query.ScopeKind.Solution
                    : Program.ParseScope(scope);
            }
            catch (Program.UsageException ex)
            {
                return JsonSerializer.Serialize(new RunResponse { Error = ex.Message, ExitCode = 1 }, CliJsonOut.Compact);
            }

            var response = await Program.ExecuteAsync(request, Program.AttachMode.Auto, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(response, CliJsonOut.Compact);
        }
    }
}
