using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RoslynQuery.Bridge;

/// <summary>Where the bundled <c>roslynquery</c> CLI lives inside the installed extension.</summary>
internal static class BridgeCli
{
    /// <summary>The CLI built for the architecture Visual Studio itself runs as (x64 VS on ARM is emulated, so not the OS architecture); null when this build does not bundle it.</summary>
    public static string Locate()
    {
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var path = Path.Combine(Path.GetDirectoryName(typeof(BridgeCli).Assembly.Location), "Cli", rid, "roslynquery.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Writes the path to <c>%LOCALAPPDATA%/RoslynQuery/cli-path.txt</c>, the one stable place an agent can look it up.</summary>
    public static void PublishPath()
    {
        try
        {
            var path = Locate();
            if (path is null) return;

            var directory = Path.GetDirectoryName(Protocol.BridgePaths.Directory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cli-path.txt"), path, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Convenience only.
        }
    }
}
