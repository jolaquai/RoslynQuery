using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;

using RoslynQuery.Query;

namespace RoslynQuery.Cli;

internal static class Program
{
    private const string Usage = """
        roslynquery run    [options]   search (and optionally replace) a solution, print JSON
        roslynquery schema             JSON Schemas of the request and response
        roslynquery docs               how to write predicates (markdown, for agents)

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
        var (request, pretty) = Parse(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var response = await Runner.RunAsync(request, cts.Token).ConfigureAwait(false);

        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, response, pretty ? CliJsonOut.Pretty : CliJsonOut.Compact);
        stdout.WriteByte((byte)'\n');
        return response.ExitCode;
    }

    private static (RunRequest Request, bool Pretty) Parse(string[] args)
    {
        string requestPath = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--request") requestPath = args[i + 1];

        var request = requestPath is null
            ? new RunRequest()
            : JsonSerializer.Deserialize(requestPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(requestPath), CliJson.Default.RunRequest)
                ?? throw new UsageException("Empty request.");

        var pretty = false;
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
                case "--request": i++; break;
                default: throw new UsageException($"Unknown option '{args[i]}'.");
            }
        }

        if (!scopeGiven)
        {
            if (request.File != null) request.Scope = ScopeKind.Document;
            else if (request.Project != null) request.Scope = ScopeKind.Project;
        }

        return (request, pretty);
    }

    private static TargetKind ParseTarget(string value) => value.ToLowerInvariant() switch
    {
        "node" or "syntaxnode" => TargetKind.SyntaxNode,
        "token" or "syntaxtoken" => TargetKind.SyntaxToken,
        "operation" or "op" or "ioperation" => TargetKind.Operation,
        _ => throw new UsageException($"Unknown target '{value}' (node, token, operation).")
    };

    private static ScopeKind ParseScope(string value) => value.ToLowerInvariant() switch
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

    private sealed class UsageException(string message) : Exception(message);
}

/// <summary>
/// The generated context with the default encoder swapped out: the default escapes <c>'</c>, <c>+</c>, <c>&lt;</c> and
/// every non-ASCII character as \uXXXX, which is pure token overhead for the code snippets this prints.
/// </summary>
internal static class CliJsonOut
{
    public static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<RunResponse> Compact = Make(false);
    public static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<RunResponse> Pretty = Make(true);

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<RunResponse> Make(bool indented) =>
        new CliJson(new JsonSerializerOptions(CliJson.Default.Options)
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).RunResponse;
}
