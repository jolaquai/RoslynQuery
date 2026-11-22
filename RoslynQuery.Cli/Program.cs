using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;

using RoslynQuery.Protocol;
using RoslynQuery.Query;

namespace RoslynQuery.Cli;

internal static class Program
{
    private const string Usage = """
        roslynquery run    [options]   search (and optionally replace) a solution, print JSON
        roslynquery schema             JSON Schemas of the request and response
        roslynquery docs               how to write predicates (markdown, for agents)
        roslynquery mcp                MCP server on stdio (search, replace, docs, bridges); same engine, attaches to Visual Studio when it can

        run options:
          -s, --solution <path>      .sln/.slnx/.csproj (default: the only one in the cwd)
          -q, --query <text|->       predicate; '-' reads stdin
              --query-file <path>
          -t, --target node|token|operation        (default node)
              --scope solution|project|document|type|member
                                     default: document with --file, project with --project, else solution
          -p, --project <name|path>  Project scope
          -f, --file <path>          Document/type/member scope, or the project containing it
              --line <n> --column <n>   1-based caret for type/member scope
          -r, --replace <text|->     replacement transform; without --apply only a dry run
              --replace-file <path>
              --apply                write the replacement to disk
              --expect <planId>      with --apply: refuse unless the plan is the one a dry run reported
              --max <n>              result cap (default 200)
              --include-generated
              --request <path|->     a RunRequest JSON; flags override it
              --attach               use the running Visual Studio's bridge or fail (default: use it when there is one for the solution)
              --no-attach            always load the solution headless, from disk
              --pretty               indent the output

        exit codes: 0 ok, 1 failed, 2 usage, 3 --apply refused (plan changed)
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            switch (args[0])
            {
                case "run": return await RunAsync(args.AsSpan(1).ToArray()).ConfigureAwait(false);
                case "schema": return Write(Schema(), pretty: true);
                case "docs": return Write(Docs.Build(), pretty: false);
                case "mcp": return await McpServer.RunAsync().ConfigureAwait(false);
                default: return Usage2($"Unknown command '{args[0]}'.");
            }
        }
        catch (UsageException ex)
        {
            return Usage2(ex.Message);
        }
        catch (JsonException ex)
        {
            return Usage2("Invalid request JSON: " + ex.Message + " (enums are names like SyntaxToken or ContainingType; see `roslynquery schema`).");
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var (request, pretty, attach) = Parse(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var response = await ExecuteAsync(request, attach, cts.Token).ConfigureAwait(false);

        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, response, pretty ? CliJsonOut.Pretty : CliJsonOut.Compact);
        stdout.WriteByte((byte)'\n');
        return response.ExitCode;
    }

    internal enum AttachMode { Auto, Force, Never }

    internal static async Task<RunResponse> ExecuteAsync(RunRequest request, AttachMode mode, CancellationToken cancellationToken)
    {
        if (mode != AttachMode.Never)
        {
            var host = Attach.Find(request, out var candidates, out var match);
            if (host is null && mode == AttachMode.Force)
                return new RunResponse { Error = "No Visual Studio bridge is running for this solution. Enable it under Extensions > Roslyn Query > Agent Bridge.", ExitCode = 1 };

            if (host != null)
            {
                try
                {
                    var answer = await Attach.RunAsync(host, request, cancellationToken).ConfigureAwait(false);
                    answer.BridgePid = host.Pid;
                    answer.BridgeMatch = BridgeSelection.Name(match);
                    var wanted = string.IsNullOrEmpty(request.Solution) ? null : Path.GetFullPath(request.Solution);
                    if (BridgeSelection.Explanation(candidates, host, match, wanted) is { } explanation)
                        (answer.Warnings ??= []).Add(explanation);

                    return answer;
                }
                catch (BridgeReplyLostException ex) when (request.Apply)
                {
                    return new RunResponse
                    {
                        Error = ex.Message + " The outcome of the apply is unknown and it was not retried: check the open files in Visual Studio (Edit > Undo), then run a dry run again before another apply.",
                        ExitCode = 1,
                        BridgePid = host.Pid
                    };
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or BridgeReplyLostException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    if (mode == AttachMode.Force)
                        return new RunResponse { Error = "Could not reach the Visual Studio bridge: " + ex.Message, ExitCode = 1 };

                    Console.Error.WriteLine("Visual Studio bridge unreachable (" + ex.Message + "); loading the solution headless instead.");
                }
            }
        }

        var response = await Runner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        if (mode == AttachMode.Auto)
        {
            var open = Attach.LiveSolutions();
            if (open.Count > 0)
            {
                (response.Warnings ??= []).Add("Ran headless on the files on disk (unsaved editor changes are not seen) although Visual Studio has an agent bridge open: "
                    + string.Join("; ", open) + ". If one of those is the solution you mean, pass its path as 'solution' (--solution) to use the live workspace.");
            }
        }

        return response;
    }

    private static (RunRequest Request, bool Pretty, AttachMode Attach) Parse(string[] args)
    {
        string requestPath = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--request") requestPath = args[i + 1];

        var request = requestPath is null
            ? new RunRequest()
            : JsonSerializer.Deserialize(requestPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(requestPath), CliJson.Default.RunRequest)
                ?? throw new UsageException("Empty request.");

        var pretty = false;
        var attach = AttachMode.Auto;
        var scopeGiven = requestPath != null;
        var stdinUsed = requestPath == "-";

        string Next(ref int i)
        {
            if (i + 1 >= args.Length) throw new UsageException($"{args[i]} needs a value.");
            return args[++i];
        }

        string Text(string value)
        {
            if (value != "-") return value;
            if (stdinUsed) throw new UsageException("Only one of --request/--query/--replace can read stdin.");
            stdinUsed = true;
            return Console.In.ReadToEnd();
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s" or "--solution": request.Solution = Next(ref i); break;
                case "-q" or "--query": request.Query = Text(Next(ref i)); break;
                case "--query-file": request.Query = File.ReadAllText(Next(ref i)); break;
                case "-r" or "--replace": request.Replace = Text(Next(ref i)); break;
                case "--replace-file": request.Replace = File.ReadAllText(Next(ref i)); break;
                case "-t" or "--target": request.Target = ParseTarget(Next(ref i)); break;
                case "--scope": request.Scope = ParseScope(Next(ref i)); scopeGiven = true; break;
                case "-p" or "--project": request.Project = Next(ref i); break;
                case "-f" or "--file": request.File = Next(ref i); break;
                case "--line": request.Line = ParseInt(Next(ref i), "--line"); break;
                case "--column": request.Column = ParseInt(Next(ref i), "--column"); break;
                case "--max": request.Max = ParseInt(Next(ref i), "--max"); break;
                case "--expect": request.ExpectPlan = Next(ref i); break;
                case "--apply": request.Apply = true; break;
                case "--include-generated": request.IncludeGenerated = true; break;
                case "--pretty": pretty = true; break;
                case "--attach": attach = AttachMode.Force; break;
                case "--no-attach": attach = AttachMode.Never; break;
                case "--request": i++; break;
                default: throw new UsageException($"Unknown option '{args[i]}'.");
            }
        }

        if (!scopeGiven)
        {
            if (request.File != null) request.Scope = ScopeKind.Document;
            else if (request.Project != null) request.Scope = ScopeKind.Project;
        }

        return (request, pretty, attach);
    }

    internal static TargetKind ParseTarget(string value) => value.ToLowerInvariant() switch
    {
        "node" or "syntaxnode" => TargetKind.SyntaxNode,
        "token" or "syntaxtoken" => TargetKind.SyntaxToken,
        "operation" or "op" or "ioperation" => TargetKind.Operation,
        _ => throw new UsageException($"Unknown target '{value}' (node, token, operation).")
    };

    internal static ScopeKind ParseScope(string value) => value.ToLowerInvariant() switch
    {
        "solution" => ScopeKind.Solution,
        "project" => ScopeKind.Project,
        "document" or "file" => ScopeKind.Document,
        "type" or "containingtype" => ScopeKind.ContainingType,
        "member" or "containingmember" => ScopeKind.ContainingMember,
        _ => throw new UsageException($"Unknown scope '{value}' (solution, project, document, type, member).")
    };

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, out var n) ? n : throw new UsageException($"{name} needs a number, got '{value}'.");

    private static string Schema()
    {
        var node = new JsonObject
        {
            ["request"] = JsonSchemaExporter.GetJsonSchemaAsNode(CliJson.Default.RunRequest),
            ["response"] = JsonSchemaExporter.GetJsonSchemaAsNode(CliJson.Default.RunResponse)
        };
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static int Write(string text, bool pretty)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(new UTF8Encoding(false).GetBytes(text));
        stdout.WriteByte((byte)'\n');
        return 0;
    }

    private static int Usage2(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }

    internal sealed class UsageException(string message) : Exception(message);
}
