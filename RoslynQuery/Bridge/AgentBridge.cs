using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

using RoslynQuery.Protocol;
using RoslynQuery.Replace;

using IServiceProvider = System.IServiceProvider;

namespace RoslynQuery.Bridge;

/// <summary>
/// Lets the <c>roslynquery</c> CLI run its requests against this Visual Studio's live workspace instead of loading the
/// solution from disk. One request per connection over a current-user-only named pipe: line 1 the token from the host
/// file, line 2 the <see cref="RunRequest"/> JSON; the reply is one <see cref="RunResponse"/> JSON line.
/// </summary>
internal sealed class AgentBridge : IDisposable
{
    private static readonly Guid PaneGuid = new Guid("5c1f3a0e-6a52-4d19-9f5d-2b7a9e44c0d1");
    private const int MaxLogLines = 500;
    private const int MaxTokenChars = 128;
    private const int MaxRequestChars = 1 << 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);
    private static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;

    private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
        Formatting = Formatting.None
    };

    private readonly IServiceProvider _services;
    private readonly IComponentModel _componentModel;
    private readonly VisualStudioWorkspace _workspace;
    private readonly string _version;

    private readonly object _gate = new object();
    private readonly LinkedList<string> _log = new LinkedList<string>();
    private SemaphoreSlim _one = new SemaphoreSlim(1, 1);

    private CancellationTokenSource _cts;
    private bool _directoryRestricted;
    private string _pipe;
    private string _token;
    private string _hostFile;
    private string _startedUtc;
    private IVsOutputWindowPane _pane;

    public AgentBridge(IServiceProvider services, IComponentModel componentModel, string version)
    {
        _services = services;
        _componentModel = componentModel;
        _workspace = componentModel.GetService<VisualStudioWorkspace>();
        _version = version;
    }

    public bool IsRunning => _cts != null;

    public event EventHandler StateChanged;

    public IReadOnlyList<string> LogSnapshot()
    {
        lock (_gate) return new List<string>(_log);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null) return;

            _pipe = "RoslynQuery-" + Pid + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _token = NewToken();
            _startedUtc = DateTime.UtcNow.ToString("o");
            _cts = new CancellationTokenSource();
            _one = new SemaphoreSlim(1, 1);
            _directoryRestricted = false;
            _hostFile = Path.Combine(BridgePaths.Directory, Pid + ".json");

            _workspace.WorkspaceChanged += OnWorkspaceChanged;
        }

        WriteHostFile();
        Log("Bridge started on " + _pipe);

        var token = _cts.Token;
        var gate = _one;
        _ = Task.Run(() => AcceptLoopAsync(_pipe, gate, token));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        string file;
        lock (_gate)
        {
            if (_cts is null) return;

            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            file = _hostFile;
        }

        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Log("Bridge stopped.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void Dispose() => Stop();

    private static string NewToken()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
    {
        switch (e.Kind)
        {
            case WorkspaceChangeKind.SolutionAdded:
            case WorkspaceChangeKind.SolutionRemoved:
            case WorkspaceChangeKind.SolutionCleared:
            case WorkspaceChangeKind.SolutionReloaded:
                WriteHostFile();
                break;
        }
    }

    private void WriteHostFile()
    {
        // Under the gate and only while running: a workspace event racing Stop must not bring the file back.
        lock (_gate)
        {
            if (_cts is null) return;
            WriteHostFileLocked();
        }
    }

    private void WriteHostFileLocked()
    {
        try
        {
            if (!_directoryRestricted)
            {
                _directoryRestricted = RestrictDirectory(Directory.CreateDirectory(BridgePaths.Directory));
                if (!_directoryRestricted) return;
            }

            var info = new BridgeHostInfo
            {
                Pid = Pid,
                Pipe = _pipe,
                Token = _token,
                Solution = _workspace.CurrentSolution.FilePath,
                StartedUtc = _startedUtc,
                Version = _version
            };

            var temp = _hostFile + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(info, Json), new UTF8Encoding(false));
            if (File.Exists(_hostFile)) File.Replace(temp, _hostFile, null);
            else File.Move(temp, _hostFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Could not write the host file: " + ex.Message);
        }
    }

    // The token lives here: only this user and the system may read it.
    private bool RestrictDirectory(DirectoryInfo directory)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log("Could not restrict " + directory.FullName + " to this user, so no host file (and no token) was written: " + ex.Message);
            return false;
        }
    }

    private async Task AcceptLoopAsync(string pipe, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var connection = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connection, gate, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log("Accept failed: " + ex.GetType().Name + ": " + ex.Message);
                await Task.Delay(500, cancellationToken).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        using (pipe)
        {
            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            RunRequest request;
            try
            {
                // A client that connects and goes quiet must not pin a connection forever.
                var read = ReadRequestAsync(reader);
                _ = read.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false) != read)
                {
                    Log("Dropped a connection that sent nothing.");
                    return;
                }

                request = await read.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log("Rejected a request: " + ex.Message);
                if (ex is InvalidDataException or JsonException) await TryReplyAsync(writer, new RunResponse { Ok = false, Error = "Rejected: " + ex.Message, ExitCode = 1, Source = "vs" }).ConfigureAwait(false);
                return;
            }

            // Ends the run when the bridge stops, the time limit passes, or the client hangs up (its pipe end closes).
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            run.CancelAfter(RequestTimeout);
            _ = reader.ReadAsync(new char[1], 0, 1).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                    try { run.Cancel(); } catch (ObjectDisposedException) { }
                }
                else if (task.Status == TaskStatus.RanToCompletion && task.Result == 0)
                {
                    try { run.Cancel(); } catch (ObjectDisposedException) { }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);

            RunResponse response;
            var acquired = false;
            try
            {
                // A query that never returns holds the gate; others get an answer instead of waiting out its time limit.
                acquired = await gate.WaitAsync(BusyWait, run.Token).ConfigureAwait(false);
                if (!acquired)
                {
                    Log("Refused a request: another one has been running for more than " + (int)BusyWait.TotalSeconds + " s.");
                    response = new RunResponse { Ok = false, Error = "The bridge is busy with another request (a long or stuck query). Try again, or restart the bridge (Extensions > Roslyn Query > Restart Agent Bridge). Nothing was run.", ExitCode = 1, Source = "vs" };
                }
                else
                {
                    var started = DateTime.UtcNow;
                    response = await RequestEngine.RunAsync(request, ct => OpenAsync(request, ct), ApplyAsync, run.Token).ConfigureAwait(false);
                    response.Source ??= "vs";
                    Log(Describe(request, response, DateTime.UtcNow - started));
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return;

                Log("Cancelled: the time limit passed or the client went away.");
                response = new RunResponse { Ok = false, Error = "Cancelled: the request exceeded the time limit (" + (int)RequestTimeout.TotalMinutes + " minutes) or the client disconnected. Nothing was written.", ExitCode = 1, Source = "vs" };
            }
            catch (Exception ex)
            {
                response = new RunResponse { Ok = false, Error = ex.GetType().Name + ": " + ex.Message, ExitCode = 1, Source = "vs" };
                Log("Request failed: " + ex);
            }
            finally
            {
                if (acquired) gate.Release();
            }

            try
            {
                await writer.WriteLineAsync(JsonConvert.SerializeObject(response, Json)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client went away; nothing to tell it.
            }
        }
    }

    private static async Task TryReplyAsync(StreamWriter writer, RunResponse response)
    {
        try { await writer.WriteLineAsync(JsonConvert.SerializeObject(response, Json)).ConfigureAwait(false); }
        catch (IOException) { }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, int maxChars)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) return line.Length == 0 ? null : line.ToString();
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= maxChars) throw new InvalidDataException("A request line is longer than " + maxChars + " characters.");
            line.Append(one[0]);
        }
    }

    private async Task<RunRequest> ReadRequestAsync(StreamReader reader)
    {
        var token = await ReadLineAsync(reader, MaxTokenChars).ConfigureAwait(false);
        if (!ConstantTimeEquals(token, _token)) throw new UnauthorizedAccessException("Wrong token.");

        var body = await ReadLineAsync(reader, MaxRequestChars).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<RunRequest>(body ?? string.Empty, Json) ?? throw new JsonException("Empty request.");
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;

        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private Task<Session> OpenAsync(RunRequest request, CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;
        if (string.IsNullOrEmpty(solution.FilePath)) throw new InvalidOperationException("No solution is open in Visual Studio.");

        // The client picked this instance by its solution; it may have switched since, or another request may have queued behind a switch.
        if (!string.IsNullOrEmpty(request.Solution) && IsSolutionFile(request.Solution)
            && !string.Equals(Path.GetFullPath(request.Solution), Path.GetFullPath(solution.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This Visual Studio has '" + solution.FilePath + "' open now, not '" + request.Solution + "'. Nothing was run.");

        return Task.FromResult(new Session
        {
            Workspace = _workspace,
            Solution = solution,
            Root = Path.GetDirectoryName(solution.FilePath),
            Source = "vs"
        });
    }

    private static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One undoable edit through the live workspace, exactly like Replace in the tool window. Commits the reviewed plan itself; a document edited since it was planned makes it refuse.</summary>
    private async Task ApplyAsync(ApplyContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var undo = GlobalUndoScope.Open(_services, _componentModel, "Roslyn Query: agent replace");
        var committed = false;
        try
        {
            Action<DocumentId> enroll = undo is null ? null : id => undo.AddDocument(_workspace, id);
            var outcome = await ChangeApplier.CommitAsync(_workspace, context.Plan, enroll, CancellationToken.None).ConfigureAwait(true);

            if (outcome.Applied == 0)
                throw new ApplyRefusedException("Nothing was applied: " + (outcome.Warnings.Count > 0 ? outcome.Warnings[0] : "the workspace rejected the change set.") + " Nothing was written.");

            undo?.Commit();
            committed = true;
        }
        finally
        {
            if (!committed) undo?.Dispose();
        }
    }

    private static string Describe(RunRequest request, RunResponse response, TimeSpan elapsed)
    {
        var query = request.Query.Replace('\r', ' ').Replace('\n', ' ');
        if (query.Length > 80) query = query.Substring(0, 77) + "...";

        var what = request.Replace is null ? "search" : request.Apply ? "replace+apply" : "replace (dry run)";
        var result = response.Ok
            ? (response.Stats?.Matched ?? 0) + " match(es)" + (response.Applied ? ", applied" : string.Empty)
            : "failed: " + response.Error;
        return $"{what} [{request.Target}/{request.Scope}] {query} -> {result} in {elapsed.TotalMilliseconds:N0} ms";
    }

    public void Log(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        lock (_gate)
        {
            _log.AddLast(line);
            while (_log.Count > MaxLogLines) _log.RemoveFirst();
        }

        _pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }

    /// <summary>Creates the "Roslyn Query" Output pane on first use and replays what was logged before it existed. Main thread only.</summary>
    public void ShowLog()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_pane is null && _services.GetService(typeof(SVsOutputWindow)) is IVsOutputWindow window)
        {
            var guid = PaneGuid;
            window.CreatePane(ref guid, "Roslyn Query", 1, 1);
            window.GetPane(ref guid, out _pane);

            foreach (var line in LogSnapshot()) _pane?.OutputStringThreadSafe(line + Environment.NewLine);
        }

        _pane?.Activate();
    }
}
