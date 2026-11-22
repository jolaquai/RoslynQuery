using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
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

    /// <summary>The folder MCP client configs point at; its path survives extension updates.</summary>
    public static string StableDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RoslynQuery", "mcp");

    public static string StableExe => Path.Combine(StableDirectory, "roslynquery.exe");

    /// <summary>Copies the bundled CLI to <see cref="StableDirectory"/>: staged beside it and swapped in, so the folder is never half old and half new; running copies are killed first, agents restart them. Falls back to the bundled path.</summary>
    public static string Install()
    {
        var source = Locate();
        if (source is null) return null;

        try
        {
            var sourceDirectory = Path.GetDirectoryName(source);
            var target = StableDirectory;
            var targetExe = StableExe;
            var stampFile = Path.Combine(target, ".stamp");
            var stamp = Stamp(sourceDirectory);

            bool UpToDate() => File.Exists(targetExe) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp;

            if (UpToDate()) return targetExe;

            // Several Visual Studios, in several sessions of one user, start at once after an update; only one may swap.
            using var mutex = new Mutex(false, @"Global\RoslynQueryCliInstall-" + WindowsIdentity.GetCurrent().User.Value);
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned) return UpToDate() ? targetExe : source;

            try
            {
                if (UpToDate()) return targetExe;

                var staging = target + ".new";
                var old = target + ".old-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                DeleteTree(staging);
                foreach (var leftover in Directory.GetDirectories(Path.GetDirectoryName(target), Path.GetFileName(target) + ".old-*")) DeleteTree(leftover);

                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(staging, file.Substring(sourceDirectory.Length).TrimStart('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, overwrite: true);
                }

                File.WriteAllText(Path.Combine(staging, ".stamp"), stamp);

                Kill(targetExe);
                if (Directory.Exists(target)) Directory.Move(target, old);

                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
                    throw;
                }

                DeleteTree(old);
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

    /// <summary>Identifies the bundled CLI by what is in it, not just its exe.</summary>
    private static string Stamp(string directory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            lines.Add(file.Substring(directory.Length) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
        }

        lines.Sort(StringComparer.Ordinal);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", string.Empty);
    }

    private static void DeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            // A fallback to the bundled path is no stable path to publish.
            var path = Install();
            if (path is null || !string.Equals(path, StableExe, StringComparison.OrdinalIgnoreCase)) return;

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
