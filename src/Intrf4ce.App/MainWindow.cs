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
using Intrf4ce.App.Input;
using Intrf4ce.App.Media;
using Intrf4ce.App.Render;
using Intrf4ce.App.Stage;
using Intrf4ce.App.Stage.Elements;
using Intrf4ce.Core;
using Intrf4ce.Core.Commands;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Input;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Settings;
using Intrf4ce.Core.Stage;
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
public sealed partial class MainWindow : Window
{
    private readonly Skin _theme = Skin.Default;
    private readonly DeckService _decks;
    private readonly HttpClient _http;
    private readonly HttpClient _api;
    private readonly WebRenderService _renderer;
    private readonly AssetFetcher _fetcher;
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
    private readonly ConversationPanel _chat;
    private readonly TransportBar _transport;
    private readonly SlideNav _nav;
    private readonly SessionsPanel _sessions;
    private readonly SettingsPanel _preferences;
    private readonly ImageInspector _inspector;
    private readonly TitleBar _titleBar;
    private readonly PromptBar _prompt;
    private readonly HelpPanel _help;
    private readonly KeyMap _keyMap = new();
    private readonly KeyReader _keyReader;
    private readonly CommandRegistry _commands = new();
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly SettingsWatcher? _watcher;
    private readonly WelcomePanel _welcome;
    private readonly List<(bool FromUser, string Text)> _saidBeforeThereWasADeck = [];
    private AskSubject? _asking;
    private string? _resumed;
    private string? _said;
    private int _toolCalls;

    public MainWindow(AppPaths paths, AppSettings settings, string? settingsError)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        Title = "intrf4ce";
        Width = 1480;
        Height = 920;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // No system title bar: ours is the app's colour and carries the half-typed keys. The
        // frame stays, so the window still resizes from its edges and snaps.
        WindowDecorations = WindowDecorations.BorderOnly;
        _keyReader = new KeyReader(_keyMap);
        Background = new SolidColorBrush(_theme.Ink);

        // ---- services
        var library = settings.Library.Folder is { Length: > 0 } folder ? folder : paths.Library;
        var store = new DeckStore(library);
        store.AdoptFrom(AppPaths.LegacyLibrary, Log.Write);
        _decks = new DeckService(store);
        _http = AssetFetcher.CreateClient();
        var fetcher = new AssetFetcher(_http) { CopyMedia = settings.Library.CopyMedia };
        var images = new ImageLoader(fetcher, _decks);
        _renderer = new WebRenderService(_theme.Renderer);

        // Its own client: the image client asks for image/* and gives up after 30 s, and a long
        // narration can take longer than that to synthesise.
        _api = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("intrf4ce/0.1");
        _voices = VoiceService.Create(settings, paths, _api, _audio);

        // Narration is kept with the presentation that speaks it, so reopening one — or copying
        // its folder to another machine — plays without paying for the words again.
        _voices.Clips.Session = () =>
            _decks.Current is { } open ? Path.Combine(_decks.Store.FolderOf(open.Id), "audio") : null;

        _stage = new StageView(_theme) { AnimationSpeed = settings.Stage.AnimationSpeed };
        _root = new Grid();
        var context = new ElementContext(_theme, _renderer, images, new TileSource(_http, paths.Cache), PublishFromStage);
        _controller = new StageController(_stage, context, _decks, () => _stage)
        {
            FollowAgent = settings.Stage.FollowAgent,
        };

        _fetcher = fetcher;
        var media = new MediaTools(fetcher, Path.Combine(paths.Cache, "inspect"));
        var tools = new PresenterTools(_decks, _controller, _events, media, new SettingsAccess(settings, paths.SettingsFile));
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
        _chat = new ConversationPanel(_theme);
        _transport = new TransportBar(_theme);
        _nav = new SlideNav(_theme);
        _sessions = new SessionsPanel(_theme);
        _preferences = new SettingsPanel(_theme, paths.SettingsFile);
        _inspector = new ImageInspector(_theme);
        _titleBar = new TitleBar(_theme, this);
        _prompt = new PromptBar(_theme);
        _help = new HelpPanel(_theme);
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

        // The stage keeps a column to itself; the conversation sits beside it rather than over
        // it, so nothing the agent says covers the thing it is saying it about.
        var presentation = new Panel();
        presentation.Children.Add(hidden);
        presentation.Children.Add(_stage);
        presentation.Children.Add(_welcome);
        presentation.Children.Add(_header);
        presentation.Children.Add(_subtitles);
        presentation.Children.Add(_transport);
        presentation.Children.Add(_nav);
        presentation.Children.Add(_prompt);
        presentation.Children.Add(_inspector);
        presentation.Children.Add(_help);
        presentation.Children.Add(_preferences);

        // Sessions on the left, the stage in the middle, the conversation on the right.
        _root.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Grid.SetColumn(_sessions, 0);
        Grid.SetColumn(presentation, 1);
        Grid.SetColumn(_chat, 2);
        _root.Children.Add(_sessions);
        _root.Children.Add(presentation);
        _root.Children.Add(_chat);
        var window = new DockPanel();
        DockPanel.SetDock(_titleBar, Dock.Top);
        window.Children.Add(_titleBar);
        window.Children.Add(_root);
        Content = window;

        // A share of the window, within reason: wide enough to read, never half the stage.
        SizeChanged += (_, e) =>
        {
            _chat.SetPreferredWidth(Math.Clamp(e.NewSize.Width * 0.27, 300, 460));
            _sessions.SetPreferredWidth(Math.Clamp(e.NewSize.Width * 0.19, 240, 340));
        };

        // ---- wiring
        _controller.Changed += (_, _) => UpdateHud();
        _controller.AgentReplied += (_, text) =>
        {
            _chat.AddMessage(fromUser: false, text);
            Remember(fromUser: false, text);
        };
        _controller.Playback = () => _narrator.State;
        _controller.Voice = _voices.Describe;
        _controller.Agent = DescribeAgent;
        _controller.CurrentChanged += (_, shown) => _narrator.SlideShown(shown.Slide, shown.Play);
        _controller.CurrentUpdated += (_, slide) => _narrator.SlideUpdated(slide);
        _controller.SlidesChanged += (_, _) => _narrator.SlidesChanged();
        _controller.SayRequested += (_, text) => _narrator.Say(text);
        _controller.Perform = ApplyPlayback;

        _narrator.StateChanged += (_, _) => UpdateHud();
        _narrator.SlideSpoken += (_, id) => _decks.MarkSpoken(id);
        _voices.Notice += (_, notice) => Dispatcher.UIThread.Post(() =>
        {
            Log.Write("voice: " + notice);
            _chat.AddMessage(fromUser: false, notice, isError: true);
        });

        _chat.Submitted += (_, text) => OnChat(text);
        _chat.TalkRequested += (_, _) => Talk();

        _prompt.Complete = _commands.Complete;
        _prompt.Submitted += (_, entry) =>
        {
            _stage.Focus();
            if (entry.Mode == PromptMode.Command)
            {
                Execute(entry.Text);
            }
            else
            {
                Say(entry.Text);
            }
        };

        _prompt.Dismissed += (_, _) => _stage.Focus();

        _commands.Register(this);
        RegisterChoices();
        LoadKeys(settings);
        _chat.Dismissed += (_, _) => _stage.Focus();
        _chat.ContextCleared += (_, _) => _asking = null;
        _chat.StopRequested += (_, _) => StopAgent();

        _nav.Previous += (_, _) => _controller.Previous();
        _nav.Next += (_, _) => _controller.Next();

        _transport.PlayPause += (_, _) =>
        {
            _narrator.Play(_controller.CurrentSlide);
            UpdateHud();
        };

        // Unticking it while a slide is speaking lets that slide finish and stops there, which
        // is what someone reaching for it means: not "be quiet", but "do not run on".
        _transport.Continuous += (_, _) =>
        {
            _narrator.SetContinuous(!_narrator.Continuous);
            UpdateHud();
        };

        _transport.Mute += (_, _) => ToggleMute();
        _transport.Captions += (_, _) => _narrator.ToggleSubtitles();

        _preferences.Changed += (_, change) => ApplySetting(change.Key, change.Value);
        _preferences.Close += (_, _) => ShowSettings(false);

        _sessions.Picked += (_, id) => OpenDeck(id);
        _sessions.NewRequested += (_, _) => NewSession();
        _sessions.SettingsRequested += (_, _) => ShowSettings(!_preferences.IsVisible);
        _sessions.Regrouped += (_, move) => Regroup(move.Id, move.Group);
        _sessions.GroupRenamed += (_, rename) => Guard(() => _decks.RenameGroup(rename.From, rename.To));
        _decks.Changed += (_, e) => Dispatcher.UIThread.Post(
            () =>
            {
                ListSessions();
                if (e.Kind == DeckChangeKind.Opened)
                {
                    // Something is on the stage now; that is what the window is for.
                    _sessions.Collapse(true);
                    AdoptWhatWasSaidFirst(e.Deck);
                }
            },
            DispatcherPriority.Background);

        _server.ActivateRequested += (_, _) => Dispatcher.UIThread.Post(BringForward);
        _server.SessionsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHud);
        _server.ToolCalled += (_, call) => Dispatcher.UIThread.Post(() => OnToolCalled(call.Session, call.Tool));

        if (_agent is not null)
        {
            _agent.Activity += (_, activity) => Dispatcher.UIThread.Post(() => OnAgentActivity(activity));
            _agent.StatusChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHud);
        }

        App.UiThreadError += ex => Dispatcher.UIThread.Post(
            () => _chat.AddMessage(fromUser: false, "Something went wrong inside intrf4ce: " + ex.Message, isError: true));

        try
        {
            _watcher = new SettingsWatcher(paths.SettingsFile);
            _watcher.Reloaded += (_, load) => Dispatcher.UIThread.Post(() => OnSettingsReloaded(load));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Log.Error("could not watch settings.yaml", ex);
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _stage.Clicked += (_, world) =>
        {
            _stage.Focus();
            _controller.Click(world);
        };
        _stage.ContextRequested += OnStageContextRequested;

        // A double-click on a picture gives it the whole window; anywhere else it is the old
        // "put the slide back the way it was".
        _stage.DoubleClicked += (_, world) =>
        {
            if (_controller.ImageAt(world) is { } picture)
            {
                _inspector.Show(picture);
            }
            else
            {
                _controller.Reframe();
            }
        };

        _inspector.ContextRequested += OnInspectorContextRequested;
        _inspector.Asked += (_, question) => AskAboutRegion(question);
        _inspector.Closed += (_, _) => _stage.Focus();
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

        // Nothing on the stage yet: the presentations already made are the first thing to see.
        ListSessions();
        _sessions.Collapse(_decks.Current is not null);

        if (settingsError is not null)
        {
            _chat.AddMessage(fromUser: false, $"settings.yaml: {settingsError}", isError: true);
        }

        _stage.Focus();
    }

    /// <summary>What is on disk, newest first.</summary>
    private void ListSessions() => _sessions.Show(_decks.Store.List(), _decks.Current?.Id);

    /// <summary>
    /// Opens a session the user picked: the deck comes back, the agent goes back to the
    /// conversation that built it, and it is told what it is now looking at so it can carry on
    /// or rework the whole thing.
    /// </summary>
    private void OpenDeck(string id)
    {
        try
        {
            var deck = _decks.Open(id);
            _sessions.Collapse(true);
            _stage.Focus();

            // The questions that produced these slides, so the user is not reading a stranger's
            // presentation, and can see where they had got to.
            _chat.Clear();
            foreach (var line in deck.Chat)
            {
                _chat.AddMessage(line.Who == "user", line.Text, isError: false, line.At);
            }

            _chat.AddNote($"reopened · {DateTime.Now:HH:mm}");
            if (_agent is not null && deck.AgentSession is { Length: > 0 } session)
            {
                _ = _agent.SwitchToAsync(session);
            }

            // Kept for the next thing the user says rather than sent now: an agent woken by
            // the mere opening of a session would answer a question nobody asked, and a turn
            // costs the user whether or not it was wanted.
            var slides = string.Join(", ", deck.Slides.Take(12).Select(s => s.Id));
            _resumed = $"<intrf4ce-resumed deck=\"{deck.Id}\" slides=\"{deck.Count}\">The user reopened \"{deck.Title}\""
                + $" ({slides}). This is the presentation built in this conversation; call get_state to see it as"
                + " it stands, including which slides they have already heard. Answer what they ask next by adding"
                + " slides at the end, and play only those."
                + "</intrf4ce-resumed>\n";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error($"opening deck '{id}'", ex);
            _chat.AddMessage(fromUser: false, $"Could not open that deck: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Puts the presentation away and starts again: an empty stage, an empty conversation, and
    /// an agent with nothing behind it.
    /// </summary>
    /// <remarks>
    /// No deck is made here. A session is a subject, and the subject arrives with the first
    /// thing the user asks for; a row called "Untitled" that nobody ever filled in is litter.
    /// </remarks>
    private void NewSession()
    {
        _decks.Flush();
        _narrator.Stop();
        _decks.Close();
        _asking = null;
        _resumed = null;
        _saidBeforeThereWasADeck.Clear();
        _chat.Clear();
        _chat.SetStatus(null);
        _ = _agent?.ResetAsync(null);
        ListSessions();
        _chat.SetPlaceholder("What should I explain?");
        _chat.FocusInput();
        UpdateHud();
        Log.Write("new session");
    }

    /// <summary>Files a session under a group, or takes it out of one.</summary>
    private void Regroup(string id, string? group) => Guard(() => _decks.SetGroup(id, group));

    /// <summary>Runs something a click asked for, reporting a failure rather than letting it escape.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
            ListSessions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException
            or System.Text.Json.JsonException or ArgumentException)
        {
            _prompt.ShowMessage(ex.Message, error: true);
        }
    }

    private void Guard(Func<int> action) => Guard(() => { _ = action(); });

    private void ShowSettings(bool show)
    {
        if (show)
        {
            _preferences.Show(_settings);
        }
        else
        {
            _preferences.IsVisible = false;
            _stage.Focus();
        }
    }

    /// <summary>
    /// A setting the user changed in the panel, written to settings.yaml as one line.
    /// </summary>
    /// <remarks>
    /// Through the file rather than into the live objects, so there is one path in: the
    /// watcher applies it the same way it applies an edit made in a text editor, and what the
    /// app is doing always matches what the file says.
    /// </remarks>
    private void ApplySetting(string key, string value)
    {
        if (SettingsFile.Write(_paths.SettingsFile, key, value) is { } error)
        {
            _preferences.Complain(error);
            Log.Write($"settings: {error}");
            return;
        }

        Log.Write($"settings: {key} = {value}");
    }

    /// <summary>
    /// The agent working the transport: the same controls the user has, for an agent that has
    /// just built three slides and wants those three heard.
    /// </summary>
    private string ApplyPlayback(PlaybackRequest request)
    {
        if (request.Continuous is { } continuous)
        {
            _narrator.SetContinuous(continuous);
        }

        if (request.Muted is { } muted)
        {
            _narrator.SetMuted(muted);
        }

        if (request.Subtitles is { } subtitles)
        {
            _narrator.SetSubtitles(subtitles);
        }

        // Set before the slide is shown: showing it starts its narration, and the end of that
        // narration is where "through" has to have taken effect.
        if (request.Through is { } through)
        {
            _narrator.StopAfter = through;
            _narrator.SetContinuous(request.Continuous ?? true);
        }

        if (request.From is { } from && _controller.Deck?.IndexOf(from) is { } index and >= 0)
        {
            _narrator.Perform("play", null);
            _controller.Show(index, play: request.Action != "pause");
        }
        else if (request.Action is { } action)
        {
            _narrator.Perform(action, _controller.CurrentSlide);
        }

        UpdateHud();

        var where = _controller.Deck is { } deck && _controller.CurrentIndex >= 0
            ? $" on slide {_controller.CurrentIndex + 1} of {deck.Count}"
            : "";

        return _narrator.State + where
            + (_narrator.Continuous ? ", running on through the deck" : ", stopping at the end of this slide")
            + (_narrator.Muted ? ", voice silenced" : "")
            + (_narrator.SubtitlesOn ? "" : ", subtitles off")
            + (_narrator.StopAfter is { } last ? $", until {last}" : "");
    }

    /// <summary>
    /// Silences the voice at once, and remembers it in settings.yaml: muted is how a
    /// presentation is built without spending characters, and that should survive a restart.
    /// </summary>
    private void ToggleMute()
    {
        var muted = !_narrator.Muted;
        _narrator.SetMuted(muted);
        ApplySetting("narration.muted", muted ? "true" : "false");
    }

    /// <summary>Stops the turn in flight without losing the conversation.</summary>
    private void StopAgent()
    {
        if (_agent is null)
        {
            return;
        }

        _chat.SetStatus("Stopping…");
        _chat.AddNote($"stopped · {DateTime.Now:HH:mm}");
        _ = _agent.InterruptAsync().ContinueWith(
            _ => Dispatcher.UIThread.Post(UpdateHud), TaskScheduler.Default);
    }

    /// <summary>settings.yaml was saved: what can change now does, and the rest is reported.</summary>
    private void OnSettingsReloaded(SettingsLoad load)
    {
        if (load.Error is { } error)
        {
            _chat.AddMessage(fromUser: false, $"settings.yaml: {error}", isError: true);
            return;
        }

        var later = SettingsFile.ApplyLive(_settings, load.Settings);
        _fetcher.CopyMedia = _settings.Library.CopyMedia;
        _stage.AnimationSpeed = _settings.Stage.AnimationSpeed;
        _controller.FollowAgent = _settings.Stage.FollowAgent;
        _narrator.SettingsChanged();
        ApplyUserKeys(_settings);

        foreach (var warning in load.Warnings ?? [])
        {
            _chat.AddMessage(fromUser: false, "settings.yaml: " + warning, isError: true);
        }

        if (_preferences.IsVisible)
        {
            _preferences.Show(_settings);
        }

        _chat.SetStatus(later.Count == 0
            ? "Settings reloaded"
            : $"Settings reloaded; {string.Join(", ", later)} needs a restart");
        Log.Write($"settings reloaded ({later.Count} needing a restart)");
    }

    private void Shutdown()
    {
        _watcher?.Dispose();
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

        if (_asking is { } subject)
        {
            _asking = null;
            _chat.ClearContext();

            // Kept with the presentation as it reads in the panel: a question about a callout
            // is as much part of the conversation as anything typed on its own.
            Remember(fromUser: true, $"About {subject.Label}: {text}");
            PublishFromStage("ask", text, Meta(subject));
            return;
        }

        Remember(fromUser: true, text);

        // Plain chat reads as the user typed it, with where they are on one line after it, so
        // "what about this one?" has something to mean.
        _events.Publish("chat", Resumption() + text + ContextTag(Situation()), null);
        ShowSent();
    }

    /// <summary>Something the user did on the stage (a quiz answer, a question about a part), for the agent.</summary>
    private void PublishFromStage(string type, string text, IReadOnlyDictionary<string, string> meta)
    {
        var all = Situation();
        foreach (var (key, value) in meta)
        {
            all[key] = value;
        }

        _events.Publish(type, Resumption() + text, all);
        ShowSent();
    }

    /// <summary>
    /// Where the user is and what they are looking at: the context a question is asked in.
    /// </summary>
    /// <remarks>
    /// Without it "how deep is that?" reaches the agent as four words with nothing attached,
    /// and it answers about whatever it happens to remember building last rather than what is
    /// on the screen. <c>unplayed</c> is the part that keeps an answer from being read over
    /// the top of slides the user has already sat through.
    /// </remarks>
    private Dictionary<string, string> Situation()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_controller.Deck is not { } deck)
        {
            return meta;
        }

        meta["deck"] = deck.Id;
        if (_controller.CurrentIndex >= 0 && _controller.CurrentIndex < deck.Count)
        {
            meta["slide"] = deck.Slides[_controller.CurrentIndex].Id;
            meta["at"] = $"{_controller.CurrentIndex + 1} of {deck.Count}";
        }

        if (_controller.State.Focus is { Length: > 0 } focus)
        {
            meta["focus"] = focus;
        }

        if (_decks.Unspoken() is { Count: > 0 } unplayed)
        {
            meta["unplayed"] = string.Join(",", unplayed.Take(12));
        }

        meta["playback"] = _narrator.State;
        return meta;
    }

    private static string ContextTag(IReadOnlyDictionary<string, string> meta) =>
        meta.Count == 0
            ? ""
            : "\n<intrf4ce-context " + string.Join(' ', meta.Select(m => $"{m.Key}=\"{m.Value}\"")) + "/>";

    /// <summary>Keeps a line of the conversation with the presentation it produced.</summary>
    private void Remember(bool fromUser, string text)
    {
        if (_decks.Current is null)
        {
            // Said before the agent had decided what the subject was; kept for the deck it makes.
            _saidBeforeThereWasADeck.Add((fromUser, text));
            return;
        }

        _decks.AddChat(fromUser, text);
    }

    /// <summary>The first question belongs to the presentation that answers it.</summary>
    private void AdoptWhatWasSaidFirst(Deck deck)
    {
        if (_saidBeforeThereWasADeck.Count == 0)
        {
            return;
        }

        if (deck.Chat.IsEmpty)
        {
            foreach (var (fromUser, text) in _saidBeforeThereWasADeck)
            {
                _decks.AddChat(fromUser, text);
            }
        }

        _saidBeforeThereWasADeck.Clear();
    }

    /// <summary>The note about a reopened session, once, in front of whatever the user says next.</summary>
    private string Resumption()
    {
        var note = _resumed;
        _resumed = null;
        return note ?? "";
    }

    private void ShowSent()
    {
        var listening = _agent is not null || _server.Sessions.Any(s => s.Kind != SessionKind.Hosted);
        _chat.SetStatus(_agent is not null ? "Thinking…" : listening ? "Sent to your agent" : "No agent is connected to hear this");
        _chat.Orb.Mood = listening ? OrbMood.Working : OrbMood.Absent;
    }

    private static Dictionary<string, string> Meta(AskSubject subject)
    {
        var meta = new Dictionary<string, string> { ["slide"] = subject.SlideId, ["label"] = subject.Label };
        if (subject.ElementId is { } element)
        {
            meta["element"] = element;
        }

        if (subject.Target is { } target)
        {
            meta["target"] = target;
        }

        // The same coordinates the agent writes callouts in, so it can zoom there, read that
        // part of the picture with inspect_image, or label it.
        if (subject.Region is [var x, var y, var w, var h])
        {
            meta["region"] = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{x:0.###},{y:0.###},{w:0.###},{h:0.###}");
        }

        return meta;
    }

    /// <summary>
    /// Right-click on the stage: a question about whatever is under the pointer.
    /// </summary>
    /// <remarks>
    /// Choosing it frames the thing on the stage as well as aiming the question at it, so the
    /// user can see what they are about to ask about before they have typed a word.
    /// </remarks>
    private void OnStageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var screen = e.TryGetPosition(_stage, out var point) ? point : new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2);
        if (_controller.SubjectAt(_stage.ScreenToWorld(screen)) is not { } subject)
        {
            return;
        }

        e.Handled = true;
        var label = subject.Label.Length > 48 ? subject.Label[..47] + "…" : subject.Label;

        var ask = new MenuItem { Header = $"Ask about “{label}”" };
        ask.Click += (_, _) =>
        {
            _asking = subject;
            Talk(label);
            if (subject.Target is { } target)
            {
                _controller.Focus(target, null);
            }
        };

        var menu = new ContextMenu();
        menu.Items.Add(ask);
        menu.Open(_stage);
    }

    /// <summary>Right-click inside the enlarged picture: ask about the box drawn on it.</summary>
    private void OnInspectorContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_inspector.Question() is not { } question)
        {
            return;
        }

        e.Handled = true;
        var label = question.Label.Length > 52 ? question.Label[..51] + "…" : question.Label;
        var ask = new MenuItem { Header = $"Ask about {(question.Region is null ? "this picture" : $"“{label}”")}" };
        ask.Click += (_, _) => _inspector.Ask();

        var menu = new ContextMenu();
        menu.Items.Add(ask);
        menu.Open(_inspector);
    }

    /// <summary>
    /// A question about part of a picture: the stage zooms there and the box is what the
    /// question is about.
    /// </summary>
    private void AskAboutRegion(ImageQuestion question)
    {
        _asking = new AskSubject(question.SlideId, question.ElementId, question.ElementId, question.Label, question.Region);
        Talk(question.Label);
        _controller.Focus(question.ElementId, question.Region);
    }

    private void OnAgentActivity(AgentActivity activity)
    {
        switch (activity.Kind)
        {
            case AgentActivityKind.Said:
                _said = activity.Text;
                _chat.AddMessage(fromUser: false, activity.Text);
                Remember(fromUser: false, activity.Text);
                break;

            case AgentActivityKind.Tool when activity.Text.Length > 0:
                _chat.SetStatus(activity.Text + "…");
                break;

            case AgentActivityKind.TurnDone:
                _chat.SetStatus(null);

                // Remember whose conversation built this deck, so reopening it resumes there.
                if (_agent?.SessionId is { Length: > 0 } session && _decks.Current?.AgentSession != session)
                {
                    _decks.TrySetAgentSession(session);
                }

                break;

            // An agent that fails often says why and then reports the same thing as an error;
            // twice in red helps nobody.
            case AgentActivityKind.Error when activity.Text != _said:
                _said = activity.Text;
                _chat.AddMessage(fromUser: false, activity.Text, isError: true);
                _chat.SetStatus(null);
                break;

            case AgentActivityKind.Error:
                _chat.SetStatus(null);
                break;
        }

        UpdateHud();
    }

    private void OnToolCalled(McpSession session, string tool)
    {
        // The hosted agent reports its own tool use, in words, through its output stream.
        if (session.Kind == SessionKind.Hosted)
        {
            return;
        }

        _chat.SetStatus($"{session.Client}: {tool.Replace('_', ' ')}");

        // An attached agent never says it has finished, so the line clears itself when the
        // calls stop rather than standing there naming the last one for ever.
        var calls = ++_toolCalls;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (calls == _toolCalls)
                {
                    _chat.SetStatus(null);
                }
            },
            TimeSpan.FromSeconds(4));
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
        _titleBar.SetTitle(deck?.Title);

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

        _transport.Update(
            _narrator.IsSpeaking, _narrator.Held, _narrator.Continuous, _narrator.Muted, _narrator.SubtitlesOn, _narrator.State);
        _transport.IsVisible = deck is { Count: > 0 };
        _nav.Update(_controller.CurrentIndex, deck?.Count ?? 0);
        _nav.IsVisible = deck is { Count: > 1 };
        _chat.SetBusy(_agent?.IsWorking == true);

        var busy = _agent?.Status is AgentStatus.Working or AgentStatus.Starting;
        _chat.Orb.Mood = _agent?.Status == AgentStatus.Failed ? OrbMood.Error
            : _narrator.IsSpeaking ? OrbMood.Speaking
            : busy ? OrbMood.Working
            : _agent is not null || attached.Count > 0 ? OrbMood.Idle
            : OrbMood.Absent;

        if (_agent is null)
        {
            _chat.SetPlaceholder(attached.Count > 0 ? "Message your agent…" : "Connect an agent to chat");
        }
    }

    /// <summary>
    /// A key: typed into whatever text box has the focus, taken by whatever is open over the
    /// stage, or read against the key map and run as a command.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Typing is typing: in any box, a key is a character, not a command.
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        // What is open over the stage takes its own keys first.
        if (_inspector.IsVisible || _help.IsVisible || _preferences.IsVisible)
        {
            if (e.Key is Key.Escape or Key.Q || (_preferences.IsVisible && e.Key == Key.OemComma) || (_help.IsVisible && e.Key == Key.Oem2))
            {
                Escape();
                e.Handled = true;
            }

            return;
        }

        // The sessions list, while it is open, is walked with j and k or the arrows.
        if (_sessions.Handle(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (AvaloniaChords.From(e) is not { } chord)
        {
            return;
        }

        var (taken, command, count) = _keyReader.Feed(chord);
        ShowPendingKeys();

        if (command is not null)
        {
            Execute(command, count);
        }

        if (taken)
        {
            e.Handled = true;
        }
    }

    /// <summary>What has been typed towards a command, and the ways it could finish, in the title bar.</summary>
    private void ShowPendingKeys()
    {
        var pending = _keyReader.Pending;
        if (pending.Length == 0)
        {
            _titleBar.SetPendingKeys(null);
            return;
        }

        var ways = _keyMap.Continuations(Chord.Parse(pending.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') is { Length: > 0 } keys ? keys : " "))
            .Take(5)
            .Select(c => $"{c.Keys} {c.Command}");
        _titleBar.SetPendingKeys(pending + "   " + string.Join("  ·  ", ways));
    }

    /// <summary>Runs a command line, showing what it had to say or why it could not.</summary>
    private void Execute(string line, int? count = null)
    {
        try
        {
            if (_commands.Run(line, count) is { Length: > 0 } message)
            {
                _prompt.ShowMessage(message);
            }
        }
        catch (CommandException ex)
        {
            _prompt.ShowMessage(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException
            or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            _prompt.ShowMessage(ex.Message, error: true);
        }

        UpdateHud();
    }

    /// <summary>Back out of the innermost thing: whatever is open, then overview, focus, the agent, fullscreen.</summary>
    private void Escape()
    {
        _keyReader.Reset();
        ShowPendingKeys();

        if (_inspector.IsVisible)
        {
            _inspector.Close();
        }
        else if (_help.IsVisible)
        {
            _help.IsVisible = false;
        }
        else if (_preferences.IsVisible)
        {
            ShowSettings(false);
        }
        else if (_prompt.IsOpen)
        {
            _prompt.Close();
        }
        else if (_controller.IsOverview)
        {
            _controller.ToggleOverview();
        }
        else if (_controller.State.Focus is not null)
        {
            _controller.Focus(null, null);
        }
        else if (_agent?.IsWorking == true)
        {
            StopAgent();
        }
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
        }

        _stage.Focus();
    }

    /// <summary>
    /// Somewhere to say something to the agent: the conversation's own box when it is open,
    /// the bar at the foot of the stage when it is folded away.
    /// </summary>
    private void Talk(string? about = null)
    {
        if (!_chat.Collapsed)
        {
            if (about is not null)
            {
                _chat.SetContext(about);
            }

            _chat.FocusInput();
            return;
        }

        _prompt.SetContext(about);
        _prompt.Open(PromptMode.Talk);
        _prompt.SetContext(about);
    }

    /// <summary>Something said from the bar or with <c>:ask</c>: into the transcript, then to the agent.</summary>
    private void Say(string text)
    {
        _chat.AddMessage(fromUser: true, _asking is { } subject ? $"About {subject.Label}: {text}" : text);
        OnChat(text);
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
