using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Agent.Hosted;
using Intrf4ce.Agent.Mcp;
using Intrf4ce.Agent.Tools;
using Intrf4ce.App.Hud;
using Intrf4ce.App.Media;
using Intrf4ce.App.Render;
using Intrf4ce.App.Stage;
using Intrf4ce.App.Stage.Elements;
using Intrf4ce.Core;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Settings;
using Intrf4ce.Media.Audio;
using Intrf4ce.Media.Images;
using Intrf4ce.Media.Tts;

namespace Intrf4ce.App;

/// <summary>
/// The window, and the composition root.
/// </summary>
/// <remarks>
/// Every object is built here by hand, as in Fjord: the graph is shallow enough to read in one
/// place, and a dependency nobody supplied fails to compile rather than at startup.
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly Skin _theme = Skin.Default;
    private readonly DeckService _decks;
    private readonly HttpClient _http;
    private readonly HttpClient _api;
    private readonly WebRenderService _renderer;
    private readonly AudioSystem _audio = new();
    private readonly VoiceService _voices;
    private readonly Narrator _narrator;
    private readonly SubtitleBar _subtitles;
    private readonly StageView _stage;
    private readonly StageController _controller;
    private readonly EventHub _events = new();
    private readonly McpServer _server;
    private readonly ClaudeCodeHost? _agent;
    private readonly Grid _root;
    private readonly HeaderBar _header;
    private readonly ChatBar _chat;
    private readonly WelcomePanel _welcome;

    public MainWindow(AppPaths paths, AppSettings settings, string? settingsError)
    {
        Title = "intrf4ce";
        Width = 1480;
        Height = 920;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(_theme.Ink);

        // ---- services
        var library = settings.Library.Folder is { Length: > 0 } folder ? folder : paths.Library;
        _decks = new DeckService(new DeckStore(library));
        _http = AssetFetcher.CreateClient();
        var fetcher = new AssetFetcher(_http);
        var images = new ImageLoader(fetcher, _decks);
        _renderer = new WebRenderService(_theme.Renderer);

        // Its own client: the image client asks for image/* and gives up after 30 s, and a long
        // narration can take longer than that to synthesise.
        _api = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("intrf4ce/0.1");
        _voices = VoiceService.Create(settings, paths, _api, _audio);

        _stage = new StageView(_theme) { AnimationSpeed = settings.Stage.AnimationSpeed };
        _root = new Grid();
        _controller = new StageController(_stage, new ElementContext(_theme, _renderer, images), _decks, () => _stage)
        {
            FollowAgent = settings.Stage.FollowAgent,
        };

        var media = new MediaTools(fetcher, Path.Combine(paths.Cache, "inspect"));
        var tools = new PresenterTools(_decks, _controller, _events, media);
        var catalog = ToolCatalog.Load();
        _server = new McpServer(PipeName.ForCurrentUser(), catalog, tools, Log.Write);

        if (string.Equals(settings.Agent.Mode, "hosted", StringComparison.OrdinalIgnoreCase))
        {
            var prompt = catalog.Instructions + "\n\n" + catalog.HostedPrompt;
            _agent = new ClaudeCodeHost(new HostedAgentOptions(settings.Agent, Environment.ProcessPath!, paths.AgentHome, prompt), Log.Write);
            _events.AddSink(_agent);
        }

        _events.AddSink(new ChannelSink(_server, Log.Write));

        // ---- view
        _header = new HeaderBar(_theme);
        _chat = new ChatBar(_theme);
        _welcome = new WelcomePanel(_theme);
        _subtitles = new SubtitleBar(_theme);
        _narrator = new Narrator(_voices, _audio, _controller, _subtitles, _chat.Orb, settings.Narration);

        _stage.Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x14, 0x1B, 0x25), 0),
                new GradientStop(_theme.Ink, 1),
            },
        };

        var hidden = new Panel { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false };
        hidden.Children.Add(_renderer.View);

        _root.Children.Add(hidden);
        _root.Children.Add(_stage);
        _root.Children.Add(_welcome);
        _root.Children.Add(_header);
        _root.Children.Add(_subtitles);
        _root.Children.Add(_chat);
        Content = _root;

        // ---- wiring
        _controller.Changed += (_, _) => UpdateHud();
        _controller.AgentReplied += (_, text) => _chat.AddMessage(fromUser: false, text);
        _controller.Playback = () => _narrator.State;
        _controller.Voice = _voices.Describe;
        _controller.Agent = DescribeAgent;
        _controller.CurrentChanged += (_, slide) => _narrator.SlideShown(slide);
        _controller.CurrentUpdated += (_, slide) => _narrator.SlideUpdated(slide);
        _controller.SlidesChanged += (_, _) => _narrator.SlidesChanged();
        _controller.SayRequested += (_, text) => _narrator.Say(text);

        _narrator.StateChanged += (_, _) => UpdateHud();
        _voices.Notice += (_, notice) => Dispatcher.UIThread.Post(() =>
        {
            Log.Write("voice: " + notice);
            _chat.AddMessage(fromUser: false, notice, isError: true);
        });

        _chat.Submitted += (_, text) => OnChat(text);
        _chat.Dismissed += (_, _) => _stage.Focus();

        _server.ActivateRequested += (_, _) => Dispatcher.UIThread.Post(BringForward);
        _server.SessionsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHud);
        _server.ToolCalled += (_, call) => Dispatcher.UIThread.Post(() => OnToolCalled(call.Session, call.Tool));

        if (_agent is not null)
        {
            _agent.Activity += (_, activity) => Dispatcher.UIThread.Post(() => OnAgentActivity(activity));
            _agent.StatusChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHud);
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _stage.Clicked += (_, world) =>
        {
            _stage.Focus();
            if (_controller.IsOverview)
            {
                _controller.ShowSlideAt(world);
            }
        };
        _stage.DoubleTapped += (_, _) => _controller.Reframe();
        _stage.PointerPressed += (_, _) => _stage.Focus();
        _stage.Focusable = true;

        Opened += (_, _) => Start(settingsError);
        Closing += (_, _) => Shutdown();

        UpdateHud();
    }

    private void Start(string? settingsError)
    {
        try
        {
            _server.Start();
            Log.Write($"listening on pipe {PipeName.ForCurrentUser()}");
        }
        catch (IOException ex)
        {
            Log.Error("could not open the MCP pipe", ex);
            _chat.AddMessage(fromUser: false, "Could not open the connection for agents: " + ex.Message, isError: true);
        }

        _agent?.Start(null);

        if (settingsError is not null)
        {
            _chat.AddMessage(fromUser: false, $"settings.yaml: {settingsError}", isError: true);
        }

        _stage.Focus();
    }

    private void Shutdown()
    {
        _narrator.Stop();
        _audio.Dispose();
        _api.Dispose();
        _decks.Dispose();
        _ = _agent?.DisposeAsync().AsTask();
        _ = _server.DisposeAsync().AsTask();
        _renderer.Dispose();
        _http.Dispose();
    }

    private void OnChat(string text)
    {
        if (_agent is not null && _agent.Status == AgentStatus.Failed && _agent.LastError is not null && !_agent.IsRunning)
        {
            _agent.Start(_agent.SessionId);
        }

        var meta = new Dictionary<string, string>();
        if (_controller.Deck is { } deck && _controller.CurrentIndex >= 0)
        {
            meta["deck"] = deck.Id;
            meta["slide"] = deck.Slides[_controller.CurrentIndex].Id;
        }

        // Plain chat goes to the agent as the user typed it; the context rides along only
        // where there is a channel for it, so the hosted agent's messages read naturally.
        _events.Publish("chat", text, _agent?.IsRunning == true ? null : meta);
        _chat.SetStatus(_agent is not null ? "Thinking…" : null);
        _chat.Orb.Mood = OrbMood.Working;
    }

    private void OnAgentActivity(AgentActivity activity)
    {
        switch (activity.Kind)
        {
            case AgentActivityKind.Said:
                _chat.AddMessage(fromUser: false, activity.Text);
                break;

            case AgentActivityKind.Tool when activity.Text.Length > 0:
                _chat.SetStatus(activity.Text + "…");
                break;

            case AgentActivityKind.TurnDone:
                _chat.SetStatus(null);
                break;

            case AgentActivityKind.Error:
                _chat.AddMessage(fromUser: false, activity.Text, isError: true);
                _chat.SetStatus(null);
                break;
        }

        UpdateHud();
    }

    private void OnToolCalled(McpSession session, string tool)
    {
        // The hosted agent reports its own tool use, in words, through its output stream.
        if (session.Kind != SessionKind.Hosted)
        {
            _chat.SetStatus($"{session.Client}: {tool.Replace('_', ' ')}");
        }
    }

    private string DescribeAgent()
    {
        var sessions = _server.Sessions;
        var attached = sessions.Count(s => s.Kind != SessionKind.Hosted);
        var hosted = _agent is null ? "no hosted agent" : $"hosted {_agent.Status.ToString().ToLowerInvariant()}";
        return $"{hosted}; {attached} attached session{(attached == 1 ? "" : "s")}";
    }

    private void UpdateHud()
    {
        var deck = _controller.Deck;
        _header.Update(deck?.Title, _controller.CurrentIndex, deck?.Count ?? 0, _controller.IsOverview);

        var empty = deck is null || deck.Count == 0;
        _welcome.IsVisible = empty;

        var attached = _server.Sessions.Where(s => s.Kind != SessionKind.Hosted).ToList();
        if (deck is not null && deck.Count == 0)
        {
            _welcome.Set($"“{deck.Title}” is being prepared…", null);
        }
        else if (_agent is not null)
        {
            _welcome.Set(
                "Ask for anything to be explained — a concept, a machine, a folder of photos, a book — and watch the presentation build itself.",
                attached.Count > 0 ? $"also connected: {string.Join(", ", attached.Select(s => s.Client))}" : null);
        }
        else
        {
            _welcome.Set(
                attached.Count > 0
                    ? $"Connected: {string.Join(", ", attached.Select(s => s.Client))}. Ask it to present something."
                    : "Waiting for an agent to connect. Register intrf4ce with your agent:",
                attached.Count > 0 ? null : $"claude mcp add intrf4ce -- \"{Environment.ProcessPath}\" --mcp");
        }

        var busy = _agent?.Status is AgentStatus.Working or AgentStatus.Starting;
        _chat.Orb.Mood = _agent?.Status == AgentStatus.Failed ? OrbMood.Error
            : _narrator.IsSpeaking ? OrbMood.Speaking
            : busy ? OrbMood.Working
            : _agent is not null || attached.Count > 0 ? OrbMood.Idle
            : OrbMood.Absent;

        if (_agent is null)
        {
            _chat.SetPlaceholder(attached.Count > 0 ? "Message your agent…" : "Connect an agent to chat (see above)");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_chat.IsTyping)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right or Key.PageDown:
                _controller.Next();
                break;

            case Key.Left or Key.PageUp:
                _controller.Previous();
                break;

            // Up and down walk the current slide's parts (a diagram's steps, an image's
            // callouts); on a slide without parts they move between slides.
            case Key.Down:
                if (!_controller.Step(+1))
                {
                    _controller.Next();
                }

                break;

            case Key.Up:
                if (!_controller.Step(-1))
                {
                    _controller.Previous();
                }

                break;

            case Key.OemPlus or Key.Add:
                _stage.ZoomAt(new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2), 0.8);
                break;

            case Key.OemMinus or Key.Subtract:
                _stage.ZoomAt(new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2), 1.25);
                break;

            case Key.D0 or Key.NumPad0:
                _controller.Reframe();
                break;

            case Key.Home:
                _controller.First();
                break;

            case Key.End:
                _controller.Last();
                break;

            case Key.O:
                _controller.ToggleOverview();
                break;

            case Key.Escape when _controller.IsOverview:
                _controller.ToggleOverview();
                break;

            case Key.Escape when _controller.State.Focus is not null:
                _controller.Focus(null, null);
                break;

            case Key.Escape when WindowState == WindowState.FullScreen:
                WindowState = WindowState.Normal;
                break;

            case Key.Enter or Key.OemQuestion or Key.Divide:
                _chat.FocusInput();
                break;

            case Key.F11 or Key.F:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;

            case Key.Space:
                _narrator.Toggle(_controller.CurrentSlide);
                UpdateHud();
                break;

            case Key.M:
                _narrator.Mute();
                break;

            case Key.S:
                _narrator.ToggleSubtitles();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }
}
