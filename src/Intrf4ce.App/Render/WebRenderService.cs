using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Intrf4ce.Core.Rendering;

namespace Intrf4ce.App.Render;

/// <summary>
/// A web view nobody sees, used as a compiler from Mermaid, TeX and Vega-Lite to SVG.
/// </summary>
/// <remarks>
/// Mermaid has no .NET implementation and needs a real layout engine to measure text, so a
/// browser is unavoidable. Keeping it hidden and asking only for SVG means everything the
/// user sees is still drawn by Avalonia: diagrams zoom, dim and take callouts like any other
/// element, which a visible web stage could not offer.
/// <para>
/// The libraries are injected as script after a blank page loads rather than loaded from
/// files, so nothing is unpacked to disk and file:// rules, which differ on every platform,
/// never come into it. Requests are serialised: Mermaid keeps global state while rendering
/// and is not safe to run twice at once.
/// </para>
/// </remarks>
public sealed class WebRenderService : IRenderService, IDisposable
{
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(20);

    private static readonly string[] Scripts =
    [
        "mathjax-config.js",
        "mathjax-tex-svg-full.js",
        "mermaid.min.js",
        "vega.min.js",
        "vega-lite.min.js",
        "renderer.js",
    ];

    private readonly NativeWebView _view;
    private readonly RendererTheme _theme;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, RenderResult> _cache = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;
    private bool _started;

    public WebRenderService(RendererTheme theme)
    {
        _theme = theme;

        // Laid out at a real size so text measurement has a viewport, but never shown.
        _view = new NativeWebView
        {
            Width = 1200,
            Height = 900,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _view.AdapterCreated += (_, _) => Start();
        _view.NavigationCompleted += OnNavigationCompleted;
        _view.WebMessageReceived += OnWebMessage;
    }

    /// <summary>The control to place in the window. It must be in the visual tree to start.</summary>
    public Control View => _view;

    public bool IsAvailable =>
        _view.AdapterInfo is { Engine: not WebViewEngine.Unknown } info
        && info is not DetailedWebViewAdapterInfo { IsSupported: false };

    /// <summary>Completes with true once the libraries are loaded, false if they cannot be.</summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>The most recent failure to start, for diagnostics.</summary>
    public string? StartError { get; private set; }

    public async Task<RenderResult> RenderAsync(
        RenderKind kind,
        string source,
        RenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= RenderOptions.Default;

        var key = CacheKey(kind, source, options);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var ready = await Ready.WaitAsync(RenderTimeout, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            return RenderResult.Failed(StartError ?? "No web engine is available to render diagrams.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await RenderOnceAsync(kind, source, options, cancellationToken).ConfigureAwait(false);

            // Failures are cached too: the same broken Mermaid will fail the same way, and an
            // agent retrying an unchanged slide should not queue behind a twenty-second render.
            _cache[key] = result;
            return result;
        }
        catch (TimeoutException)
        {
            return RenderResult.Failed($"The renderer did not answer within {RenderTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();

        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }
    }

    private async Task<RenderResult> RenderOnceAsync(
        RenderKind kind, string source, RenderOptions options, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var request = new JsonObject
            {
                ["id"] = id,
                ["kind"] = kind switch
                {
                    RenderKind.Mermaid => "mermaid",
                    RenderKind.Tex => "tex",
                    RenderKind.VegaLite => "vegalite",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                },
                ["source"] = source,
                ["display"] = options.Display,
            };

            if (options.Width is { } width)
            {
                request["width"] = width;
            }

            if (options.Height is { } height)
            {
                request["height"] = height;
            }

            if (options.FontSize is { } fontSize)
            {
                request["fontSize"] = fontSize;
            }

            if (options.Color is { } color)
            {
                request["color"] = color;
            }

            if (options.KeepOriginal)
            {
                request["debug"] = true;
            }

            var script = "i4.render(" + request.ToJsonString() + ")";
            await Dispatcher.UIThread.InvokeAsync(() => _view.InvokeScript(script));

            var reply = await completion.Task.WaitAsync(RenderTimeout, cancellationToken).ConfigureAwait(false);
            return Parse(reply);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static RenderResult Parse(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            return RenderResult.Failed(reply["error"]?.GetValue<string>() ?? "render failed");
        }

        var viewBox = reply["viewBox"] as JsonObject;
        var originX = viewBox?["x"]?.GetValue<double>() ?? 0;
        var originY = viewBox?["y"]?.GetValue<double>() ?? 0;

        var parts = new List<RenderPart>();
        if (reply["parts"] is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                var partId = node["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(partId))
                {
                    continue;
                }

                // The page reports boxes in viewBox space; the native picture starts at 0,0.
                parts.Add(new RenderPart(partId, new RenderBox(
                    node["x"]!.GetValue<double>() - originX,
                    node["y"]!.GetValue<double>() - originY,
                    node["w"]!.GetValue<double>(),
                    node["h"]!.GetValue<double>())));
            }
        }

        return new RenderResult(
            reply["svg"]?.GetValue<string>(),
            reply["width"]?.GetValue<double>() ?? 0,
            reply["height"]?.GetValue<double>() ?? 0,
            reply["depth"]?.GetValue<double>() ?? 0,
            parts,
            Warning: reply["warning"]?.GetValue<string>(),
            Original: reply["original"]?.GetValue<string>());
    }

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _view.NavigateToString("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body></body></html>");
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_ready.Task.IsCompleted)
        {
            return;
        }

        try
        {
            foreach (var name in Scripts)
            {
                // A trailing expression keeps the engine from serialising whatever the
                // library's last statement evaluates to back across the bridge.
                await _view.InvokeScript(ReadScript(name) + "\n;0");
            }

            var configured = await _view.InvokeScript("i4.configure(" + _theme.ToJson() + ")");
            var ready = await _view.InvokeScript("i4.ready()");

            if (!string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
            {
                StartError = $"The renderer page loaded but its libraries did not (configure={configured}, ready={ready}).";
                _ready.TrySetResult(false);
                return;
            }

            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StartError = "The renderer failed to start: " + ex.Message;
            _ready.TrySetResult(false);
        }
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Body))
        {
            return;
        }

        JsonObject? message;
        try
        {
            message = JsonNode.Parse(e.Body) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (message?["id"]?.GetValue<int>() is { } id && _pending.TryGetValue(id, out var completion))
        {
            completion.TrySetResult(message);
        }
    }

    private static string ReadScript(string name)
    {
        var assembly = typeof(WebRenderService).Assembly;
        using var stream = assembly.GetManifestResourceStream("Intrf4ce.Web." + name)
            ?? throw new InvalidOperationException($"embedded script {name} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string CacheKey(RenderKind kind, string source, RenderOptions options)
    {
        var text = string.Join(
            '',
            kind.ToString(),
            source,
            options.Width?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.Height?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.Display ? "d" : "i",
            options.FontSize?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.Color ?? "",
            options.KeepOriginal ? "o" : "",
            _theme.ToJson());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>The colours and font the renderer bakes into every SVG it produces.</summary>
/// <remarks>
/// Baked in rather than applied afterwards because Mermaid lays text out with the font it is
/// given: rendering with one font and drawing with another misplaces every label.
/// </remarks>
public sealed record RendererTheme(
    string FontFamily,
    double FontSize,
    string Text,
    string Muted,
    string Accent,
    string Cool,
    string Surface,
    string Surface2,
    string Line,
    IReadOnlyList<string> Palette,
    bool Dark)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        fontFamily = FontFamily,
        fontSize = FontSize,
        text = Text,
        muted = Muted,
        accent = Accent,
        cool = Cool,
        surface = Surface,
        surface2 = Surface2,
        line = Line,
        palette = Palette,
        dark = Dark,
    });
}
