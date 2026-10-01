using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Intrf4ce.App.Hud;
using Intrf4ce.App.Stage.Elements;
using Intrf4ce.Core.Commands;
using Intrf4ce.Core.Input;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.App;

/// <summary>
/// Everything the app can be told to do, as <c>:</c> commands, and the keys they sit on.
/// </summary>
/// <remarks>
/// Keyboard first, as in Fjord: every button on the window is one of these, every key runs one,
/// and the prompt reaches all of them — so a key can be rebound to anything, and anything can
/// be done without the mouse, which remains there for everything too. The table below is only
/// the shipped defaults; <c>keys:</c> in settings.yaml, or <c>:bind</c>, layers the user's own
/// on top.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>The keys intrf4ce ships with: vim's where vim has one, mnemonic otherwise.</summary>
    private static readonly (string Keys, string Command)[] DefaultKeys =
    [
        ("h", "prev"), ("<Left>", "prev"), ("<PgUp>", "prev"),
        ("l", "next"), ("<Right>", "next"), ("<PgDn>", "next"),
        ("j", "step-next"), ("<Down>", "step-next"),
        ("k", "step-prev"), ("<Up>", "step-prev"),
        ("gg", "first"), ("<Home>", "first"),
        ("G", "last"), ("<End>", "last"),
        ("o", "overview"),
        ("0", "reframe"),
        ("+", "zoom-in"), ("=", "zoom-in"),
        ("-", "zoom-out"),
        ("f", "fullscreen"), ("<F11>", "fullscreen"),
        ("i", "inspect"),
        ("z", "hud"),
        ("<Space>", "play"),
        ("c", "continuous"),
        ("x", "stop"),
        ("m", "mute"),
        ("s", "subtitles"),
        ("gv", "revoice"),
        ("q", "sessions"),
        ("p", "conversation"),
        ("`", "talk"), ("<Enter>", "talk"),
        (":", "prompt"),
        ("n", "new"),
        (",", "settings"),
        ("?", "help"),
        ("<Esc>", "escape"),
    ];

    private void LoadKeys(AppSettings settings)
    {
        foreach (var (keys, command) in DefaultKeys)
        {
            _keyMap.AddDefault(keys, command);
        }

        ApplyUserKeys(settings);
    }

    /// <summary>The user's own bindings from settings.yaml, over the defaults; a bad one is reported, not fatal.</summary>
    private void ApplyUserKeys(AppSettings settings)
    {
        _keyMap.ClearUser();
        foreach (var (keys, command) in settings.Keys)
        {
            try
            {
                _keyMap.Bind(keys, command);
            }
            catch (FormatException ex)
            {
                _chat.AddMessage(fromUser: false, $"settings.yaml keys: {ex.Message}", isError: true);
            }
        }
    }

    private void RegisterChoices()
    {
        _commands.Choices["settings"] = () => SettingsSchema.All.Select(s => new Completion(s.Key, s.Description));
        _commands.Choices["sessions"] = () => _decks.Store.List().Select(d => new Completion(d.Id, d.Title));
        _commands.Choices["groups"] = () => _sessions.Groups.Select(g => new Completion(g, "group"));
        _commands.Choices["commands"] = () => _commands.All.Select(c => new Completion(c.Name, c.Description));
        _commands.Choices["slides"] = () =>
            _controller.Deck?.Slides.Select((s, i) => new Completion((i + 1).ToString(CultureInfo.InvariantCulture), s.Title ?? s.Id))
            ?? [];
        _commands.Choices["onoff"] = () => [new("on", ""), new("off", "")];
        _commands.Choices["scope"] = () => [new("all", "every slide, not just this one")];
    }

    // ------------------------------------------------------------------ moving around

    [Command("next", Description = "The next slide", Group = "Moving around")]
    internal void CommandNext(int count = 1) => _controller.Show(_controller.CurrentIndex + Math.Max(1, count));

    [Command("prev", Aliases = ["previous"], Description = "The previous slide", Group = "Moving around")]
    internal void CommandPrevious(int count = 1) => _controller.Show(_controller.CurrentIndex - Math.Max(1, count));

    [Command("first", Description = "The first slide", Group = "Moving around")]
    internal void CommandFirst() => _controller.First();

    [Command("last", Description = "The last slide", Group = "Moving around")]
    internal void CommandLast() => _controller.Last();

    [Command("go", Description = "A slide by number or id; :12 does the same", Group = "Moving around")]
    internal string? CommandGo([Choices("slides")] string slide)
    {
        if (_controller.Deck is not { } deck)
        {
            throw new CommandException("nothing is on the stage");
        }

        var index = int.TryParse(slide, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n - 1 : deck.IndexOf(slide);
        if (index < 0 || index >= deck.Count)
        {
            throw new CommandException($"there is no slide '{slide}'; there are {deck.Count}");
        }

        _controller.Show(index);
        return null;
    }

    [Command("step-next", Description = "The next part of this slide — a callout, a node, a row — or the next slide", Group = "Moving around")]
    internal void CommandStepNext(int count = 1)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            if (!_controller.Step(+1))
            {
                _controller.Next();
            }
        }
    }

    [Command("step-prev", Description = "The previous part of this slide, or the previous slide", Group = "Moving around")]
    internal void CommandStepPrevious(int count = 1)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            if (!_controller.Step(-1))
            {
                _controller.Previous();
            }
        }
    }

    [Command("overview", Description = "The whole deck at once, or back from it", Group = "Moving around")]
    internal void CommandOverview() => _controller.ToggleOverview();

    [Command("reframe", Description = "Back to the whole slide", Group = "Moving around")]
    internal void CommandReframe() => _controller.Reframe();

    [Command("zoom-in", Description = "Closer", Group = "Moving around")]
    internal void CommandZoomIn(int count = 1)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            _stage.ZoomAt(new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2), 0.8);
        }
    }

    [Command("zoom-out", Description = "Further away", Group = "Moving around")]
    internal void CommandZoomOut(int count = 1)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            _stage.ZoomAt(new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2), 1.25);
        }
    }

    [Command("fullscreen", Description = "Fill the screen, or stop filling it", Group = "Moving around")]
    internal void CommandFullscreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    [Command("inspect", Description = "The picture on this slide, filling the stage; drag a box to ask about part of it", Group = "Moving around")]
    internal void CommandInspect()
    {
        var picture = _controller.CurrentView?.Elements.OfType<ImageView>().FirstOrDefault()
            ?? throw new CommandException("there is no picture on this slide");
        _inspector.Show(picture);
    }

    // ------------------------------------------------------------------ the performance

    [Command("play", Description = "Play or pause this slide", Group = "The performance")]
    internal void CommandPlay()
    {
        _narrator.Play(_controller.CurrentSlide);
        UpdateHud();
    }

    [Command("pause", Description = "Pause the voice where it is", Group = "The performance")]
    internal void CommandPause() => _narrator.Perform("pause", _controller.CurrentSlide);

    [Command("stop", Description = "Stop: no voice, and no slide moves on by itself until play", Group = "The performance")]
    internal void CommandStop()
    {
        _narrator.Hold();
        UpdateHud();
    }

    [Command("continuous", Aliases = ["keep-going"], Description = "Run on through the deck, or stop at the end of each slide", Group = "The performance")]
    internal string CommandContinuous([Choices("onoff")] string? state = null)
    {
        _narrator.SetContinuous(Toggle(state, _narrator.Continuous));
        UpdateHud();
        return _narrator.Continuous ? "keeps going through the deck" : "stops at the end of each slide";
    }

    [Command("mute", Description = "Silence the voice — nothing is sent to it — or let it speak", Group = "The performance")]
    internal string CommandMute([Choices("onoff")] string? state = null)
    {
        var muted = Toggle(state, _narrator.Muted);
        if (muted != _narrator.Muted)
        {
            ToggleMute();
        }

        return muted ? "muted: subtitles only, nothing spoken or paid for" : "the voice is back";
    }

    [Command("subtitles", Description = "Show or hide the subtitles", Group = "The performance")]
    internal void CommandSubtitles([Choices("onoff")] string? state = null) =>
        _narrator.SetSubtitles(Toggle(state, _narrator.SubtitlesOn));

    [Command("revoice", Description = "Speak this slide again in the current voice; ':revoice all' for every slide", Group = "The performance")]
    internal string CommandRevoice([Choices("scope")] string? scope = null)
    {
        if (_controller.Deck is not { } deck)
        {
            throw new CommandException("nothing is on the stage");
        }

        var slides = scope == "all" ? deck.Slides.ToList() : _controller.CurrentSlide is { } one ? [one] : [];
        var discarded = _narrator.Revoice(slides, _controller.CurrentSlide);
        return discarded == 0
            ? "nothing recorded to redo; it will be spoken in the current voice"
            : $"{discarded} recording{(discarded == 1 ? "" : "s")} set aside; spoken again in the current voice as each slide comes";
    }

    // ------------------------------------------------------------------ the window

    [Command("sessions", Description = "Open or fold the sessions column", Group = "The window")]
    internal void CommandSessions()
    {
        _sessions.Collapse(!_sessions.Collapsed);
        if (!_sessions.Collapsed)
        {
            ListSessions();
        }
    }

    [Command("conversation", Description = "Open or fold the conversation column", Group = "The window")]
    internal void CommandConversation()
    {
        _chat.Collapse(!_chat.Collapsed);
        _stage.Focus();
    }

    [Command("talk", Description = "Say something to the agent: in the conversation, or at the foot of the stage when that is folded", Group = "The window")]
    internal void CommandTalk() => Talk();

    [Command("prompt", Description = "The command line", Group = "The window")]
    internal void CommandPrompt() => _prompt.Open(PromptMode.Command);

    [Command("hud", Aliases = ["zen", "controls"], Description = "Show or hide the title, progress and buttons over the stage", Group = "The window")]
    internal string CommandHud([Choices("onoff")] string? state = null)
    {
        var show = Toggle(state, _settings.Stage.ShowControls);
        _settings.Stage.ShowControls = show;
        ApplySetting("stage.show_controls", show ? "true" : "false");
        UpdateHud();
        return show ? "controls shown" : "just the slides — z brings the controls back";
    }

    [Command("config", Aliases = ["edit-config"], Description = "Open settings.yaml in your editor", Group = "Settings and keys")]
    internal string CommandConfig()
    {
        SettingsFile.WriteDefaultIfMissing(_paths.SettingsFile);
        return Shell.Edit(_paths.SettingsFile) is { } error
            ? throw new CommandException(error)
            : $"opened {_paths.SettingsFile}; saving it applies the changes";
    }

    [Command("settings", Description = "The settings panel", Group = "The window")]
    internal void CommandSettings() => ShowSettings(true);

    [Command("help", Description = "Every command and the keys it is on; ':help name' for one", Group = "The window")]
    internal string? CommandHelp([Choices("commands")] string? command = null)
    {
        if (command is null)
        {
            _help.Show(_commands.All, _keyMap);
            return null;
        }

        var info = _commands.Find(command) ?? throw new CommandException($"there is no command '{command}'");
        var keys = _keyMap.KeysFor(info.Name);
        return $"{info.Usage} — {info.Description}" + (keys.Count > 0 ? $"  [{string.Join(" ", keys)}]" : "");
    }

    [Command("escape", Description = "Back out of whatever is open", Group = "The window")]
    internal void CommandEscape() => Escape();

    [Command("quit", Aliases = ["q", "exit"], Description = "Close intrf4ce", Group = "The window")]
    internal void CommandQuit() => Close();

    // ------------------------------------------------------------------ sessions

    [Command("new", Description = "An empty stage and a conversation with nothing behind it", Group = "Sessions")]
    internal void CommandNew() => NewSession();

    [Command("open", Description = "Open a session by id", Group = "Sessions")]
    internal void CommandOpen([Choices("sessions")] string session) => OpenDeck(session);

    [Command("group", Description = "File this session under a group", Group = "Sessions")]
    internal string CommandGroup([Choices("groups")][Rest] string? name = null)
    {
        var deck = _decks.Current ?? throw new CommandException("no session is open");
        if (name is null)
        {
            return deck.Group is { } group ? $"filed under {group}" : "not in a group";
        }

        _decks.SetGroup(deck.Id, name);
        return $"filed under {name.Trim()}";
    }

    [Command("ungroup", Description = "Take this session out of its group", Group = "Sessions")]
    internal void CommandUngroup()
    {
        var deck = _decks.Current ?? throw new CommandException("no session is open");
        _decks.SetGroup(deck.Id, null);
    }

    [Command("rename", Description = "Retitle this session", Group = "Sessions")]
    internal void CommandRename([Rest] string title)
    {
        if (_decks.Current is null)
        {
            throw new CommandException("no session is open");
        }

        _decks.Rename(title.Trim());
    }

    // ------------------------------------------------------------------ the agent

    [Command("ask", Description = "Say something to the agent", Group = "The agent")]
    internal void CommandAsk([Rest] string text) => Say(text);

    [Command("attach", Description = "Give the agent a picture, folder or file with the next thing you say", Group = "The agent")]
    internal string CommandAttach([Rest] string path)
    {
        var full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new CommandException($"there is nothing at {full}");
        }

        Attach([Path.GetFullPath(full)]);
        return $"{Path.GetFileName(full.TrimEnd('\\', '/'))} goes with the next thing you say";
    }

    [Command("stop-agent", Description = "Stop the agent's turn, keeping the conversation", Group = "The agent")]
    internal void CommandStopAgent() => StopAgent();

    // ------------------------------------------------------------------ settings and keys

    [Command("set", Description = "Change a setting: ':set key value', ':set key!' flips it, ':set key' says what it is", Group = "Settings and keys")]
    internal string? CommandSet([Choices("settings")] string? key = null, [Rest] string? value = null)
    {
        if (key is null)
        {
            ShowSettings(true);
            return null;
        }

        // key=value, key!, nokey, as in vim.
        if (value is null && key.Contains('=', StringComparison.Ordinal))
        {
            (key, value) = (key[..key.IndexOf('=', StringComparison.Ordinal)], key[(key.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        }

        var flip = key.EndsWith('!');
        key = key.TrimEnd('!');
        var entry = SettingsSchema.Find(key) ?? SettingsSchema.All.FirstOrDefault(s => s.Key.EndsWith("." + key, StringComparison.OrdinalIgnoreCase));
        if (entry is null && key.StartsWith("no", StringComparison.OrdinalIgnoreCase)
            && (SettingsSchema.Find(key[2..]) ?? SettingsSchema.All.FirstOrDefault(s => s.Key.EndsWith("." + key[2..], StringComparison.OrdinalIgnoreCase))) is { Kind: SettingKind.Toggle } negated)
        {
            (entry, value) = (negated, "false");
        }

        if (entry is null)
        {
            throw new CommandException($"there is no setting '{key}'; :set and tab lists them");
        }
        var current = SettingsSchema.Read(_settings, entry.Key);

        if (flip)
        {
            if (entry.Kind != SettingKind.Toggle)
            {
                throw new CommandException($"{entry.Key} is not on or off, so it cannot be flipped");
            }

            value = current == "true" ? "false" : "true";
        }

        if (value is null)
        {
            return $"{entry.Key} = {current}";
        }

        if (SettingsFile.Write(_paths.SettingsFile, entry.Key, value) is { } error)
        {
            throw new CommandException(error);
        }

        return $"{entry.Key} = {value.Trim()}" + (entry.NeedsRestart ? " (from the next start)" : "");
    }

    [Command("bind", Description = "Put a command on keys, kept in settings.yaml: ':bind gt next'", Group = "Settings and keys")]
    internal string CommandBind(string keys, [Choices("commands")][Rest] string command)
    {
        var canonical = Canonical(keys);
        var name = CommandRegistry.Tokenize(command).FirstOrDefault().Text ?? "";
        if (_commands.Find(name) is null)
        {
            throw new CommandException($"there is no command '{name}' to bind");
        }

        if (SettingsFile.WriteBinding(_paths.SettingsFile, canonical, command.Trim()) is { } error)
        {
            throw new CommandException(error);
        }

        _keyMap.Bind(canonical, command.Trim());
        return $"{canonical} runs :{command.Trim()}";
    }

    [Command("unbind", Description = "Take a command off keys, kept in settings.yaml", Group = "Settings and keys")]
    internal string CommandUnbind(string keys)
    {
        var canonical = Canonical(keys);
        if (SettingsFile.WriteBinding(_paths.SettingsFile, canonical, "") is { } error)
        {
            throw new CommandException(error);
        }

        _keyMap.Bind(canonical, "");
        return $"{canonical} does nothing now";
    }

    [Command("keys", Description = "Every key and what it runs", Group = "Settings and keys")]
    internal void CommandKeys() => _help.Show(_commands.All, _keyMap);

    private static string Canonical(string keys)
    {
        try
        {
            return KeyMap.Canonical(keys);
        }
        catch (FormatException ex)
        {
            throw new CommandException(ex.Message);
        }
    }

    /// <summary>"on", "off", or nothing to flip what it is now.</summary>
    private static bool Toggle(string? state, bool now) => state?.ToLowerInvariant() switch
    {
        null => !now,
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => throw new CommandException($"'{state}' is not on or off"),
    };
}
