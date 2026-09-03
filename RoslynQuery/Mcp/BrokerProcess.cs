using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RoslynQuery.Mcp;

/// <summary>Launches and owns the lifetime of the RoslynQuery.Mcp.Broker child process.</summary>
internal sealed class BrokerProcess : IDisposable
{
    private Process _process;

    /// <returns>False if the broker executable couldn't be located - nothing else here has run.</returns>
    public bool Start(string pipeName, int port)
    {
        // release.yml's "Embed broker into VSIX" step is what actually puts this here, as a
        // self-contained single-file exe per RID under Broker/<rid>/ next to this assembly - VS
        // extracts a .vsix's whole content tree on install, this assembly included.
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var dir = Path.GetDirectoryName(typeof(BrokerProcess).Assembly.Location);
        var brokerPath = Path.Combine(dir ?? string.Empty, "Broker", rid, "RoslynQuery.Mcp.Broker.exe");
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
            // No entireProcessTree overload on net472 (that's .NET Core 3.0+ only) - fine as long as
            // the broker doesn't itself spawn children, which it doesn't today.
            if (_process is { HasExited: false }) _process.Kill();
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
