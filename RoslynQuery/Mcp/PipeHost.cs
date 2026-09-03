using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;

using StreamJsonRpc;

namespace RoslynQuery.Mcp;

/// <summary>
/// Accepts one RoslynQuery.Mcp.Broker connection at a time over a named pipe and exposes a single
/// RoslynQueryRpcServer on it via StreamJsonRpc, for the package's whole lifetime. The server is
/// shared across accept-loop iterations so a dropped-and-reconnected broker keeps any live replace
/// previews its PreviewCache is holding.
/// </summary>
internal sealed class PipeHost : IDisposable
{
    private readonly string _pipeName;
    private readonly RoslynQueryRpcServer _server;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task _acceptLoop;

    public PipeHost(string pipeName, Workspace workspace, JoinableTaskFactory mainThread = null)
    {
        _pipeName = pipeName;
        _server = new RoslynQueryRpcServer(workspace, mainThread);
    }

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // maxNumberOfServerInstances: 1 - only ever one of these live at once, since the next
            // iteration doesn't start until this connection's JsonRpc completes below.
            using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var rpc = JsonRpc.Attach(pipe, _server);
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A dropped or malformed connection attempt shouldn't take the whole host down -
                // loop back and accept the next one.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
