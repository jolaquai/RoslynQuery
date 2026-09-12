using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RoslynQuery.Navigation;

/// <summary>
/// Starts ILSpy on one assembly, positioned at one symbol. The arguments go through a response file, which
/// ILSpy parses one argument per line, so neither a path with spaces nor a documentation id needs quoting.
/// </summary>
internal static class IlspyLauncher
{
    private static readonly TimeSpan KeepResponseFiles = TimeSpan.FromHours(1);

    /// <summary>ILSpy reuses a running instance whose identity matches, so every launch from here lands in one window.</summary>
    public static string Arguments(string ilspyPath, string responseFilePath) =>
        "--instanceid \"" + ilspyPath + "\" @\"" + responseFilePath + "\"";

    public static string ResponseFileText(string assemblyPath, string documentationId) =>
        assemblyPath + "\r\n--navigateto:" + documentationId + "\r\n";

    public static string DefaultResponseFileRoot =>
        Path.Combine(Path.GetTempPath(), "RoslynQuery", "ILSpy");

    /// <summary>Null when ILSpy started, otherwise the reason it did not.</summary>
    public static string Launch(string ilspyPath, string assemblyPath, string documentationId) =>
        Launch(ilspyPath, assemblyPath, documentationId, DefaultResponseFileRoot);

    public static string Launch(string ilspyPath, string assemblyPath, string documentationId, string responseFileRoot)
    {
        if (string.IsNullOrEmpty(documentationId)) return "That row carries no symbol id, so ILSpy has nothing to navigate to.";

        string responseFile;

        try
        {
            responseFile = WriteResponseFile(responseFileRoot, assemblyPath, documentationId);
        }
        catch (Exception ex)
        {
            return "Could not write the ILSpy response file: " + ex.Message;
        }

        try
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = ilspyPath,
                    Arguments = Arguments(ilspyPath, responseFile),
                    UseShellExecute = false
                };

                process.Start();
            }
        }
        catch (Exception ex)
        {
            return "Could not start ILSpy at '" + ilspyPath + "': " + ex.Message;
        }

        return null;
    }

    /// <summary>ILSpy reads the file after this returns, so sweeping by age is the only safe time to delete one.</summary>
    public static string WriteResponseFile(string root, string assemblyPath, string documentationId)
    {
        Directory.CreateDirectory(root);
        Sweep(root);

        var path = Path.Combine(root, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".rsp");
        File.WriteAllText(path, ResponseFileText(assemblyPath, documentationId));

        return path;
    }

    private static void Sweep(string root)
    {
        try
        {
            var stale = DateTime.UtcNow - KeepResponseFiles;

            foreach (var file in Directory.GetFiles(root, "*.rsp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < stale) File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
