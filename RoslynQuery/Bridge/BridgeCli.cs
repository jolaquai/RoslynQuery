using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

    /// <summary>Copies the bundled CLI to <c>%LOCALAPPDATA%/Programs/RoslynQuery/mcp</c>, whose path survives extension updates; running copies are killed first, agents restart them. Falls back to the bundled path.</summary>
    public static string Install()
    {
        var source = Locate();
        if (source is null) return null;

        try
        {
            var sourceDirectory = Path.GetDirectoryName(source);
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RoslynQuery", "mcp");
            var targetExe = Path.Combine(target, "roslynquery.exe");
            var info = new FileInfo(source);
            var stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            var stampFile = Path.Combine(target, ".stamp");

            bool UpToDate() => File.Exists(targetExe) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp;

            if (UpToDate()) return targetExe;

            // Several Visual Studios start at once after an update; only one may kill and copy.
            using var mutex = new Mutex(false, @"Local\RoslynQueryCliInstall");
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned) return UpToDate() ? targetExe : source;

            try
            {
                if (UpToDate()) return targetExe;

                Directory.CreateDirectory(target);
                File.Delete(stampFile);
                Kill(targetExe);

                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(target, file.Substring(sourceDirectory.Length).TrimStart('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, overwrite: true);
                }

                File.WriteAllText(stampFile, stamp);
                return targetExe;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return source;
        }
    }

    private static void Kill(string exe)
    {
        foreach (var process in Process.GetProcessesByName("roslynquery"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Exited meanwhile or not ours to inspect.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Writes the path to <c>%LOCALAPPDATA%/RoslynQuery/cli-path.txt</c>, the one stable place an agent can look it up.</summary>
    public static void PublishPath()
    {
        try
        {
            var path = Install();
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
