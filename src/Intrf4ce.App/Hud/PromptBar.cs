using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Core.Commands;

namespace Intrf4ce.App.Hud;

public enum PromptMode
{
    /// <summary><c>:</c> — a command, with completion.</summary>
    Command,

    /// <summary><c>`</c> — something to say to the agent.</summary>
    Talk,
}

/// <summary>
/// The line at the foot of the stage: <c>:</c> for a command, <c>`</c> to say something to the
/// agent, and the place a command's answer or complaint is shown.
/// </summary>
/// <remarks>
/// One bar for both because they are the same gesture — a key, a line, Enter — and a person
/// should not have to learn two places to type. The command line completes names and
/// arguments with Tab and remembers what was run; talking takes several lines with
/// Shift+Enter and carries the thing a question is about, as the conversation panel's box
/// does. Escape puts it away without a trace, which is what makes it safe to summon.
/// </remarks>
public sealed class PromptBar : Panel
{
    private const int CompletionRows = 8;

    private readonly Skin _skin;
    private readonly Border _box;
    private readonly TextBlock _prefix;
    private readonly TextBox _input;
    private readonly TextBlock _hint;
    private readonly Border _context;
    private readonly TextBlock _contextText;
    private readonly Border _completionsFrame;
    private readonly StackPanel _completions = new() { Spacing = 1 };
    private readonly Border _message;
    private readonly TextBlock _messageText;
    private readonly StackPanel _column;
    private readonly Dictionary<PromptMode, List<string>> _history = new() { [PromptMode.Command] = [], [PromptMode.Talk] = [] };
    private IReadOnlyList<Completion> _candidates = [];
    private int _selected = -1;
    private string _origin = "";
    private int _historyAt = -1;
    private bool _settingText;
    private int _messages;

    public PromptBar(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;

        // Above the stage's own row of buttons, never over them.
        Margin = new Thickness(0, 0, 0, Metrics.BottomInset + Metrics.Button + 16);

        _prefix = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Accent),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 10, 4, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
        };

        _contextText = new TextBlock
        {
            FontSize = 12,
            Foreground = skin.Brush(skin.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _context = new Border
        {
            IsVisible = false,
            Margin = new Thickness(4, 8, 2, 8),
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(8),
            Background = skin.Brush(skin.Accent, 0.14),
            BorderBrush = skin.Brush(skin.Accent, 0.5),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _contextText,
        };

        _input = new TextBox
        {
            FontSize = 16,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = skin.Brush(skin.Text),
            CaretBrush = skin.Brush(skin.Accent),
            Padding = new Thickness(6, 10),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160,
            AcceptsReturn = true,
            MinWidth = 200,
        };

        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        })
        {
            _input.Resources[key] = Brushes.Transparent;
        }

        _input.Resources["TextControlForegroundFocused"] = skin.Brush(skin.Text);
        _input.Resources["TextControlPlaceholderForegroundFocused"] = skin.Brush(skin.Faint);
        _input.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.TextChanged += (_, _) =>
        {
            if (!_settingText)
            {
                _origin = _input.Text ?? "";
                _historyAt = -1;
                Refresh();
            }
        };

        _hint = new TextBlock
        {
            FontSize = 12,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 13, 14, 0),
        };

        var row = new DockPanel();
        DockPanel.SetDock(_prefix, Dock.Left);
        DockPanel.SetDock(_context, Dock.Left);
        DockPanel.SetDock(_hint, Dock.Right);
        row.Children.Add(_prefix);
        row.Children.Add(_context);
        row.Children.Add(_hint);
        row.Children.Add(_input);

        _box = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(245, 0x10, 0x16, 0x1E)),
            BorderBrush = skin.Brush(skin.Accent, 0.7),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 12 36 0 #90000000"),
            Child = row,
        };

        _completionsFrame = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            Background = new SolidColorBrush(Color.FromArgb(245, 0x10, 0x16, 0x1E)),
            BorderBrush = skin.Brush(skin.Line),
            BorderThickness = new Thickness(1),
            Child = _completions,
        };

        _messageText = new TextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 700,
        };

        _message = new Border
        {
            IsVisible = false,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            Child = _messageText,
            IsHitTestVisible = false,
        };

        _column = new StackPanel { Children = { _message, _completionsFrame, _box } };
        Children.Add(_column);
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>A line was entered: a command to run, or something to tell the agent.</summary>
    public event EventHandler<(PromptMode Mode, string Text)>? Submitted;

    /// <summary>The bar was put away without anything being entered.</summary>
    public event EventHandler? Dismissed;

    /// <summary>What completion offers for the command line, from the command registry.</summary>
    public Func<string, IReadOnlyList<Completion>>? Complete { get; set; }

    public bool IsOpen => _box.IsVisible;

    public PromptMode Mode { get; private set; }

    public bool HasContext => _context.IsVisible;

    /// <summary>Summons the bar, empty or with <paramref name="text"/> already in it.</summary>
    public void Open(PromptMode mode, string? text = null)
    {
        Mode = mode;
        _prefix.Text = mode == PromptMode.Command ? ":" : "›";
        _hint.Text = mode == PromptMode.Command ? "tab completes · ↑ history · esc" : "↵ send · ⇧↵ new line · esc";
        _input.PlaceholderText = mode == PromptMode.Command ? "a command — :help lists them" : HasContext ? "what about it?" : "say something to the agent";
        _context.IsVisible = mode == PromptMode.Talk && _contextText.Text is { Length: > 0 };
        _box.IsVisible = true;
        _historyAt = -1;
        Set(text ?? "");
        _origin = _input.Text ?? "";
        Fit();
        Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            _input.Focus();
            _input.CaretIndex = _input.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    public void Close()
    {
        _box.IsVisible = false;
        _completionsFrame.IsVisible = false;
        _candidates = [];
        _selected = -1;
        Set("");
    }

    /// <summary>What the next thing said is about, after "Ask about this".</summary>
    public void SetContext(string? label)
    {
        _contextText.Text = label ?? "";
        _context.IsVisible = label is { Length: > 0 } && Mode == PromptMode.Talk && IsOpen;
        if (label is { Length: > 0 } && IsOpen)
        {
            _input.PlaceholderText = "what about it? ↵ asks for more";
        }
    }

    /// <summary>A command's answer, or its complaint, for a few seconds.</summary>
    public void ShowMessage(string text, bool error = false)
    {
        _messageText.Text = text;
        _messageText.Foreground = _skin.Brush(error ? _skin.Danger : _skin.Text, 0.95);
        _message.Background = error ? new SolidColorBrush(Color.FromArgb(235, 0x3A, 0x12, 0x16)) : new SolidColorBrush(Color.FromArgb(235, 0x14, 0x1C, 0x26));
        _message.BorderBrush = _skin.Brush(error ? _skin.Danger : _skin.Line, 0.8);
        _message.BorderThickness = new Thickness(1);
        _message.IsVisible = true;

        var mine = ++_messages;
        DispatcherTimer.RunOnce(() =>
        {
            if (mine == _messages)
            {
                _message.IsVisible = false;
            }
        }, TimeSpan.FromSeconds(error ? 7 : 4));
    }

    private void Fit()
    {
        var available = Parent is Control parent ? parent.Bounds.Width : 900;
        _box.Width = Math.Clamp(available * 0.7, 360, 820);
        _completionsFrame.Width = _box.Width;
    }

    private void Set(string text)
    {
        _settingText = true;
        _input.Text = text;
        _input.CaretIndex = text.Length;
        _settingText = false;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Enter when Mode == PromptMode.Talk && shift:
                // Left to the box: a new line.
                return;

            case Key.Enter:
                Submit();
                break;

            case Key.Escape:
                Close();
                Dismissed?.Invoke(this, EventArgs.Empty);
                break;

            case Key.Tab when Mode == PromptMode.Command:
                Cycle(shift ? -1 : +1);
                break;

            case Key.Down when Mode == PromptMode.Command && _candidates.Count > 0 && _selected >= 0:
                Cycle(+1);
                break;

            case Key.Up when Mode == PromptMode.Command && _candidates.Count > 0 && _selected >= 0:
                Cycle(-1);
                break;

            case Key.Up when (_input.Text ?? "").IndexOf('\n', StringComparison.Ordinal) < 0:
                Walk(+1);
                break;

            case Key.Down when (_input.Text ?? "").IndexOf('\n', StringComparison.Ordinal) < 0:
                Walk(-1);
                break;

            case Key.Back when string.IsNullOrEmpty(_input.Text) && Mode == PromptMode.Talk && HasContext:
                _contextText.Text = "";
                _context.IsVisible = false;
                break;

            // As in vim: deleting past the start of an empty command line leaves it.
            case Key.Back when string.IsNullOrEmpty(_input.Text) && Mode == PromptMode.Command:
                Close();
                Dismissed?.Invoke(this, EventArgs.Empty);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void Submit()
    {
        var text = (_input.Text ?? "").Trim();
        if (Mode == PromptMode.Talk && text.Length == 0 && HasContext)
        {
            text = "Tell me more about this.";
        }

        if (text.Length == 0)
        {
            Close();
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var history = _history[Mode];
        history.Remove(text);
        history.Add(text);

        var mode = Mode;
        Close();
        if (mode == PromptMode.Talk)
        {
            _contextText.Text = "";
            _context.IsVisible = false;
        }

        Submitted?.Invoke(this, (mode, text));
    }

    /// <summary>Up through what was entered before, newest first.</summary>
    private void Walk(int direction)
    {
        var history = _history[Mode];
        if (history.Count == 0)
        {
            return;
        }

        _historyAt = Math.Clamp(_historyAt + direction, -1, history.Count - 1);
        Set(_historyAt < 0 ? _origin : history[history.Count - 1 - _historyAt]);
    }

    /// <summary>The candidates for the line as typed, drawn above it.</summary>
    private void Refresh()
    {
        _candidates = Mode == PromptMode.Command && Complete is { } complete && _origin.Length > 0 ? complete(_origin) : [];
        _selected = -1;
        Draw();
    }

    /// <summary>
    /// Puts the next candidate in the line. Each is applied to the line as it was typed, not to
    /// what the last candidate made of it, or cycling would pile one on another.
    /// </summary>
    private void Cycle(int direction)
    {
        if (_candidates.Count == 0)
        {
            return;
        }

        _selected = (_selected + direction + _candidates.Count) % _candidates.Count;
        var tokens = CommandRegistry.Tokenize(_origin);
        var candidate = _candidates[_selected].Text;
        var quoted = candidate.Contains(' ', StringComparison.Ordinal) ? $"\"{candidate}\"" : candidate;

        string line;
        if (tokens.Count <= 1 && !_origin.EndsWith(' '))
        {
            line = candidate + " ";
        }
        else if (_origin.EndsWith(' '))
        {
            line = _origin + quoted;
        }
        else
        {
            line = _origin[..tokens[^1].Start] + quoted;
        }

        Set(line);
        Draw();
    }

    private void Draw()
    {
        _completions.Children.Clear();
        _completionsFrame.IsVisible = _candidates.Count > 0 && IsOpen;

        var first = _selected >= CompletionRows ? _selected - CompletionRows + 1 : 0;
        for (var i = first; i < Math.Min(_candidates.Count, first + CompletionRows); i++)
        {
            var candidate = _candidates[i];
            var selected = i == _selected;
            var name = new TextBlock
            {
                Text = candidate.Text,
                FontSize = 14,
                MinWidth = 150,
                Foreground = _skin.Brush(selected ? _skin.Ink : _skin.Text),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var about = new TextBlock
            {
                Text = candidate.Description,
                FontSize = 12,
                Foreground = _skin.Brush(selected ? _skin.Ink : _skin.Muted, selected ? 0.85 : 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };

            var line = new DockPanel();
            DockPanel.SetDock(name, Dock.Left);
            line.Children.Add(name);
            line.Children.Add(about);

            var index = i;
            var row = new Border
            {
                Padding = new Thickness(10, 5),
                CornerRadius = new CornerRadius(7),
                Background = selected ? _skin.Brush(_skin.Accent, 0.9) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = line,
            };

            row.PointerPressed += (_, e) =>
            {
                _selected = index - 1;
                Cycle(+1);
                _input.Focus();
                e.Handled = true;
            };

            _completions.Children.Add(row);
        }

        if (_candidates.Count > CompletionRows)
        {
            _completions.Children.Add(new TextBlock
            {
                Text = $"{_candidates.Count - CompletionRows} more — keep typing, or tab through",
                FontSize = 11,
                Foreground = _skin.Brush(_skin.Faint),
                Margin = new Thickness(10, 3, 0, 1),
            });
        }
    }
}
