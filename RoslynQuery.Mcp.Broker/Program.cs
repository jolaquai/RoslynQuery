using System;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using RoslynQuery.Mcp.Contracts;

using StreamJsonRpc;

namespace RoslynQuery.Mcp.Broker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var pipeName = ArgValue(args, "--pipe") ?? throw new ArgumentException("--pipe <name> is required.");
        var port = int.Parse(ArgValue(args, "--port") ?? "5050");

        // One connection for the broker's whole lifetime: RoslynQueryPackage's PipeHost accepts it
        // once and serves every tool call over it, rather than reconnecting per request.
        var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipeClient.ConnectAsync((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
        var rpc = JsonRpc.Attach<IRoslynQueryRpc>(pipeClient);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IRoslynQueryRpc>(rpc);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost", "http://127.0.0.1")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        var app = builder.Build();

        // Loopback bind below stops off-box traffic; this stops on-box DNS rebinding - the same
        // Host-header guard VSC-MCPServer documents for its own local listener.
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 421; // Misdirected Request
                return;
            }
            await next();
        });

        app.UseCors();
        app.MapMcp();

        // "localhost" is Kestrel's own special-cased host: it binds both IPv4 and IPv6 loopback,
        // never 0.0.0.0 - the plain "127.0.0.1" spelling would miss the IPv6 side.
        await app.RunAsync($"http://localhost:{port}");
    }

    private static string ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
