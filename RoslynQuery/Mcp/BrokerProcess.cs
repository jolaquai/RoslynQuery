using System;
using System.Diagnostics;
using System.IO;

namespace RoslynQuery.Mcp;

/// <summary>Launches and owns the lifetime of the RoslynQuery.Mcp.Broker child process.</summary>
internal sealed class BrokerProcess : IDisposable
{
    private Process _process;

    /// <returns>False if the broker executable couldn't be located - nothing else here has run.</returns>
    public bool Start(string pipeName, int port)
    {
        // TODO(pass 2 follow-up, "Broker packaging" in the design doc): nothing yet copies the
        // broker's published output next to the VSIX. Until a build step does that, this candidate
        // path never exists and Start returns false - the bridge simply doesn't come up.
        var dir = Path.GetDirectoryName(typeof(BrokerProcess).Assembly.Location);
        var brokerPath = Path.Combine(dir ?? string.Empty, "Broker", "RoslynQuery.Mcp.Broker.exe");
        if (!File.Exists(brokerPath)) return false;

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = brokerPath,
            Arguments = $"--pipe {pipeName} --port {port}",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        return true;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: VS is already on its way down either way.
        }
        finally
        {
            _process?.Dispose();
        }
    }
}
