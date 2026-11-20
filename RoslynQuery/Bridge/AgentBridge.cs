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
    private readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);

    private CancellationTokenSource _cts;
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
            _hostFile = Path.Combine(BridgePaths.Directory, Pid + ".json");

            _workspace.WorkspaceChanged += OnWorkspaceChanged;
        }

        WriteHostFile();
        Log("Bridge started on " + _pipe);

        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(_pipe, token));
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
        try
        {
            Directory.CreateDirectory(BridgePaths.Directory);

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
            if (File.Exists(_hostFile)) File.Delete(_hostFile);
            File.Move(temp, _hostFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Could not write the host file: " + ex.Message);
        }
    }

    private async Task AcceptLoopAsync(string pipe, CancellationToken cancellationToken)
    {
        var security = new PipeSecurity();
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
                _ = Task.Run(() => HandleAsync(connection, cancellationToken));
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

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
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
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false) != read)
                {
                    Log("Dropped a connection that sent nothing.");
                    return;
                }

                request = await read.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log("Rejected a request: " + ex.Message);
                return;
            }

            RunResponse response;
            await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var started = DateTime.UtcNow;
                response = await RequestEngine.RunAsync(request, OpenAsync, ApplyAsync, cancellationToken).ConfigureAwait(false);
                response.Source ??= "vs";
                Log(Describe(request, response, DateTime.UtcNow - started));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                response = new RunResponse { Ok = false, Error = ex.GetType().Name + ": " + ex.Message, ExitCode = 1, Source = "vs" };
                Log("Request failed: " + ex);
            }
            finally
            {
                _one.Release();
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

    private async Task<RunRequest> ReadRequestAsync(StreamReader reader)
    {
        var token = await reader.ReadLineAsync().ConfigureAwait(false);
        if (!ConstantTimeEquals(token, _token)) throw new UnauthorizedAccessException("Wrong token.");

        var body = await reader.ReadLineAsync().ConfigureAwait(false);
        return JsonConvert.DeserializeObject<RunRequest>(body ?? string.Empty, Json) ?? throw new JsonException("Empty request.");
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;

        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private Task<Session> OpenAsync(CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;
        if (string.IsNullOrEmpty(solution.FilePath)) throw new InvalidOperationException("No solution is open in Visual Studio.");

        return Task.FromResult(new Session
        {
            Workspace = _workspace,
            Solution = solution,
            Root = Path.GetDirectoryName(solution.FilePath),
            Source = "vs"
        });
    }

    /// <summary>One undoable edit through the live workspace, exactly like Replace in the tool window.</summary>
    private async Task ApplyAsync(ApplyContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var undo = GlobalUndoScope.Open(_services, _componentModel, "Roslyn Query: agent replace");
        var committed = false;
        try
        {
            Action<DocumentId> enroll = undo is null ? null : id => undo.AddDocument(_workspace, id);
            var outcome = await ChangeApplier.ApplyAsync(_workspace, context.Session.Solution, context.Items, enroll, CancellationToken.None).ConfigureAwait(true);

            if (outcome.Applied == 0)
                throw new ApplyRefusedException("Nothing was applied: " + (outcome.Warnings.Count > 0 ? outcome.Warnings[0] : "the workspace rejected the change set."));

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
