using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Agent.Mcp;

public enum SessionKind
{
    /// <summary>The Claude Code process intrf4ce started itself.</summary>
    Hosted,

    /// <summary>An agent the user started and pointed at intrf4ce.</summary>
    Attached,

    /// <summary>An attached Claude Code session loaded as a channel: user events are pushed to it.</summary>
    Channel,
}

/// <summary>Runs one tool call for a session.</summary>
public interface IToolDispatcher
{
    Task<ToolResult> CallAsync(string name, JsonObject arguments, McpSession session, CancellationToken cancellationToken);
}

/// <summary>
/// One MCP connection: JSON-RPC 2.0, one message per line, over whatever stream the transport gave.
/// </summary>
/// <remarks>
/// Hand-rolled rather than the official SDK because the surface is small (initialize, ping,
/// tools, and outbound notifications) and the tool schemas are reviewed documents rather
/// than generated from C# signatures. Requests run concurrently: <c>wait_for_user</c> may
/// block for minutes and must not hold up a <c>ping</c> or another tool call. Writes are
/// serialised, because two responses interleaved on one line are two corrupt messages.
/// </remarks>
public sealed class McpSession : IAsyncDisposable
{
    /// <summary>Newest first; the client's version is echoed when it is one of these.</summary>
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly string ServerVersion =
        typeof(McpSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    private readonly Stream _stream;
    private readonly ToolCatalog _catalog;
    private readonly IToolDispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _firstLine;
    private static int _nextId;

    public McpSession(
        Stream stream,
        SessionKind kind,
        ToolCatalog catalog,
        IToolDispatcher dispatcher,
        Action<string> log,
        string? firstLine = null)
    {
        _stream = stream;
        Kind = kind;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _log = log;
        _firstLine = firstLine;
        Id = Interlocked.Increment(ref _nextId);
        _catalog.Changed += OnCatalogChanged;
    }

    public int Id { get; }

    public SessionKind Kind { get; }

    /// <summary>What the client called itself in <c>initialize</c>, e.g. "claude-code 2.1.208".</summary>
    public string Client { get; private set; } = "unknown";

    public bool IsInitialized { get; private set; }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    /// <summary>The tool this session last called, for the status line.</summary>
    public event EventHandler<string>? ToolCalled;

    public event EventHandler? Closed;

    public async Task RunAsync()
    {
        using var reader = new StreamReader(_stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        try
        {
            if (_firstLine is not null)
            {
                await HandleLineAsync(_firstLine).ConfigureAwait(false);
            }

            while (!_lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    await HandleLineAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The other end went away; that is how every session ends.
        }
        finally
        {
            _catalog.Changed -= OnCatalogChanged;
            foreach (var running in _running.Values)
            {
                await running.CancelAsync().ConfigureAwait(false);
            }

            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A plugin came or went: the client asks for the list again when told it changed.</summary>
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _ = NotifyAsync("notifications/tools/list_changed", []).ContinueWith(
            t => _log($"session {Id}: could not say the tools changed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>Sends a notification, such as a Claude Code channel event.</summary>
    public Task NotifyAsync(string method, JsonObject parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeGate.Dispose();
    }

    private async Task HandleLineAsync(string line)
    {
        LastActivity = DateTimeOffset.Now;

        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(null, -32700, "Parse error: " + ex.Message).ConfigureAwait(false);
            return;
        }

        if (message is null)
        {
            await WriteErrorAsync(null, -32600, "Invalid request: expected a JSON object").ConfigureAwait(false);
            return;
        }

        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();

        if (method is null)
        {
            // A response to something we asked; this server asks nothing yet.
            return;
        }

        if (id is null)
        {
            HandleNotification(method, message["params"] as JsonObject);
            return;
        }

        var parameters = message["params"] as JsonObject ?? [];

        switch (method)
        {
            case "initialize":
                await RespondAsync(id, Initialize(parameters)).ConfigureAwait(false);
                break;

            case "ping":
                await RespondAsync(id, []).ConfigureAwait(false);
                break;

            case "tools/list":
                await RespondAsync(id, new JsonObject { ["tools"] = _catalog.Tools.DeepClone() }).ConfigureAwait(false);
                break;

            case "tools/call":
                // Off the read loop, so a long call never stops the next message being read.
                _ = Task.Run(() => CallToolAsync(id, parameters));
                break;

            case "prompts/list":
                await RespondAsync(id, new JsonObject { ["prompts"] = new JsonArray() }).ConfigureAwait(false);
                break;

            case "resources/list":
                await RespondAsync(id, new JsonObject { ["resources"] = new JsonArray() }).ConfigureAwait(false);
                break;

            default:
                await WriteErrorAsync(id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                break;
        }
    }

    private void HandleNotification(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "notifications/initialized":
                IsInitialized = true;
                break;

            case "notifications/cancelled" when (parameters?["requestId"] is { } requestId):
                if (_running.TryGetValue(requestId.ToJsonString(), out var running))
                {
                    running.Cancel();
                }

                break;
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        var version = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];

        if (parameters["clientInfo"] is JsonObject client)
        {
            Client = $"{client["name"]?.GetValue<string>() ?? "?"} {client["version"]?.GetValue<string>() ?? ""}".Trim();
        }

        var capabilities = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } };
        if (Kind == SessionKind.Channel)
        {
            capabilities["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject() };
        }

        _log($"session {Id} ({Kind}) initialised by {Client}, protocol {version}");

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject { ["name"] = "intrf4ce", ["version"] = ServerVersion },
            ["instructions"] = Kind == SessionKind.Channel
                ? _catalog.Instructions + "\n\nThe user's chat messages and stage events arrive as <channel source=\"intrf4ce\" type=\"...\"> tags. Answer on the stage, and with the reply tool for conversation."
                : _catalog.Instructions,
        };
    }

    private async Task CallToolAsync(JsonNode id, JsonObject parameters)
    {
        var key = id.ToJsonString();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _running[key] = cancellation;

        var name = parameters["name"]?.GetValue<string>() ?? "";
        var arguments = parameters["arguments"] as JsonObject ?? [];

        try
        {
            if (!_catalog.Names.Contains(name))
            {
                await WriteErrorAsync(id, -32602, $"Unknown tool: {name}").ConfigureAwait(false);
                return;
            }

            ToolCalled?.Invoke(this, name);

            ToolResult result;
            try
            {
                result = await _dispatcher.CallAsync(name, arguments, this, cancellation.Token).ConfigureAwait(false);
            }
            catch (ToolInputException ex)
            {
                result = ToolResult.Error(ex.Message);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or JsonException or IOException)
            {
                result = ToolResult.Error(ex.Message);
            }

            await RespondAsync(id, result.ToJson()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The session closed while the tool ran.
        }
        catch (Exception ex)
        {
            _log($"tool {name} failed: {ex}");
            await RespondAsync(id, ToolResult.Error($"{name} failed inside intrf4ce: {ex.Message}").ToJson()).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private Task RespondAsync(JsonNode id, JsonObject result) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

    private Task WriteErrorAsync(JsonNode? id, int code, string message) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private async Task WriteAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
