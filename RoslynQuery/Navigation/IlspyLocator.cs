using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RoslynQuery.Navigation;

/// <summary>
/// Finds ILSpy: the configured executable when one is set, otherwise one autodetected search, whose result
/// is kept until <see cref="Reset"/>. A standalone install wins over the copy inside the ILSpy extension for
/// Visual Studio, which Visual Studio replaces on its own schedule.
/// </summary>
internal static class IlspyLocator
{
    private const string Executable = "ILSpy.exe";

    private const string Remedy =
        "Set 'ILSpy path' under Tools > Options > RoslynQuery > Reference Graph, or change "
        + "'Open metadata symbols in' to 'Decompile in Visual Studio'.";

    private static readonly object Gate = new object();

    private static bool _searched;
    private static string _found;

    /// <summary>Counts autodetect passes, so a test can prove the search runs once.</summary>
    internal static int Searches { get; private set; }

    /// <summary>Arms the autodetect again, for when the configured path changes or is cleared.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _searched = false;
            _found = null;
        }
    }

    /// <summary>The ILSpy to launch, or null when neither the configured path nor any install exists.</summary>
    public static string Find(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim();
            return Exists(trimmed) ? trimmed : null;
        }

        lock (Gate)
        {
            if (_searched) return _found;

            _searched = true;
            Searches++;
            _found = Search();

            return _found;
        }
    }

    /// <summary>What to tell the user when <see cref="Find"/> came back empty.</summary>
    public static string NotFoundMessage(string configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? "No ILSpy installation was found.\r\n\r\n" + Remedy
            : "There is no ILSpy at the configured path:\r\n\r\n" + configured.Trim() + "\r\n\r\n" + Remedy;

    private static string Search()
    {
        foreach (var candidate in Candidates())
            if (Exists(candidate))
                return candidate;

        return null;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var localAppData = Folder(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Folder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Folder(Environment.SpecialFolder.ProgramFilesX86);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        var userProfile = Folder(Environment.SpecialFolder.UserProfile);

        if (localAppData != null) yield return Path.Combine(localAppData, "Programs", "ILSpy", Executable);
        if (programFiles != null) yield return Path.Combine(programFiles, "ILSpy", Executable);
        if (programFilesX86 != null) yield return Path.Combine(programFilesX86, "ILSpy", Executable);
        if (userProfile != null) yield return Path.Combine(userProfile, "scoop", "apps", "ilspy", "current", Executable);
        if (programData != null) yield return Path.Combine(programData, "chocolatey", "lib", "ilspy", "tools", Executable);

        if (localAppData != null)
            foreach (var directory in Directories(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages")))
                yield return Path.Combine(directory, Executable);

        foreach (var path in OnSearchPath()) yield return path;

        if (localAppData != null)
            foreach (var path in InVisualStudioExtensions(Path.Combine(localAppData, "Microsoft", "VisualStudio")))
                yield return path;
    }

    private static string Folder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static IEnumerable<string> OnSearchPath()
    {
        var search = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(search)) yield break;

        foreach (var directory in search.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), Executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }

    /// <summary>
    /// The extension lays ILSpy out as <c>&lt;extension&gt;\{x64|arm64}\ILSpy\ILSpy.exe</c>, and the
    /// experimental hive adds one directory level above that, so both depths are searched.
    /// </summary>
    private static IEnumerable<string> InVisualStudioExtensions(string root)
    {
        foreach (var hive in Directories(root))
        foreach (var extensions in Directories(Path.Combine(hive, "Extensions")))
        {
            foreach (var path in Architectures(extensions)) yield return path;

            foreach (var nested in Directories(extensions))
            foreach (var path in Architectures(nested))
                yield return path;
        }
    }

    private static IEnumerable<string> Architectures(string directory)
    {
        var native = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        yield return Path.Combine(directory, native, "ILSpy", Executable);
        yield return Path.Combine(directory, native == "x64" ? "arm64" : "x64", "ILSpy", Executable);
    }

    private static IEnumerable<string> Directories(string path)
    {
        string[] directories;

        try
        {
            directories = Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception)
        {
            directories = [];
        }

        return directories;
    }
}
