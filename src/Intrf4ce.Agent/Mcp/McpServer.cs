using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Agent.Mcp;

/// <summary>The pipe every agent reaches intrf4ce through, one per user — and one per separate home.</summary>
public static class PipeName
{
    /// <remarks>
    /// A window started with its own <c>INTRF4CE_HOME</c> is a separate stage with separate
    /// sessions, so it gets its own pipe: otherwise a test run beside the everyday window would
    /// hand its agent to that window, or start a second listener on the same name for agents to
    /// be shared between at random. The shim an agent starts inherits the variable, and finds
    /// the same pipe.
    /// </remarks>
    public static string ForCurrentUser()
    {
        var user = new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var name = "intrf4ce." + user;

        if (Environment.GetEnvironmentVariable(Core.AppPaths.HomeVariable) is { Length: > 0 } home)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home).ToUpperInvariant()));
            name += "." + Convert.ToHexString(hash)[..10].ToLowerInvariant();
        }

        return name;
    }
}

/// <summary>
/// The first line a connecting process sends, before any MCP: who it is and what it wants.
/// </summary>
/// <remarks>
/// The stdio shim writes it so the server knows whether a session is the hosted agent, a
/// plain attached agent, or a Claude Code channel, which decides where user events go. A
/// second launch of the GUI writes <c>activate</c> instead. A client that connects without
/// the shim sends MCP straight away and is treated as attached.
/// </remarks>
public sealed record Hello(string Kind)
{
    public const string Key = "intrf4ce-hello";

    public string ToLine() => new JsonObject { [Key] = 1, ["kind"] = Kind }.ToJsonString();

    public static Hello? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject obj && obj.ContainsKey(Key)
                ? new Hello(obj["kind"]?.GetValue<string>() ?? "attached")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Accepts connections on the per-user pipe and runs an MCP session for each.
/// </summary>
/// <remarks>
/// A named pipe rather than a TCP port: nothing to allocate or collide with, and
/// <see cref="PipeOptions.CurrentUserOnly"/> means another account on the machine cannot
/// drive the stage. On Linux and macOS .NET backs the same API with a Unix domain socket.
/// </remarks>
public sealed class McpServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ToolCatalog _catalog;
    private readonly IToolDispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, McpSession> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _accepting;

    public McpServer(string pipeName, ToolCatalog catalog, IToolDispatcher dispatcher, Action<string> log)
    {
        _pipeName = pipeName;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>A second launch of the app asked this window to come forward.</summary>
    public event EventHandler? ActivateRequested;

    public event EventHandler? SessionsChanged;

    /// <summary>A session called a tool; for the status line.</summary>
    public event EventHandler<(McpSession Session, string Tool)>? ToolCalled;

    public IReadOnlyList<McpSession> Sessions => [.. _sessions.Values.OrderBy(s => s.Id)];

    /// <summary>Whether another intrf4ce already owns the pipe. If so, asks it to come forward.</summary>
    public static bool TryActivateExisting(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(250);
            var bytes = Encoding.UTF8.GetBytes(new Hello("activate").ToLine() + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Start() => _accepting = Task.Run(AcceptLoopAsync);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_accepting is not null)
        {
            try
            {
                await _accepting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _log("pipe accept failed: " + ex.Message);
                await Task.Delay(250).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        string? first;
        try
        {
            first = await ReadLineAsync(pipe, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (first is null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var hello = Hello.TryParse(first);
        if (hello?.Kind == "activate")
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            ActivateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var kind = hello?.Kind switch
        {
            "hosted" => SessionKind.Hosted,
            "channel" => SessionKind.Channel,
            _ => SessionKind.Attached,
        };

        var session = new McpSession(pipe, kind, _catalog, _dispatcher, _log, firstLine: hello is null ? first : null);
        session.ToolCalled += (_, tool) => ToolCalled?.Invoke(this, (session, tool));
        _sessions[session.Id] = session;
        SessionsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            _log($"session {session.Id} ({session.Kind}, {session.Client}) closed");
        }
    }

    /// <summary>Reads one line byte by byte, so nothing after it is consumed before the session's reader takes over.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            }

            bytes.Add(buffer[0]);
            if (bytes.Count > 1 << 20)
            {
                throw new IOException("first line too long");
            }
        }
    }
}
