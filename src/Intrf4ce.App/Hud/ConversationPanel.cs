using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Intrf4ce.App.Hud;

/// <summary>One line of the conversation.</summary>
public sealed record ChatMessage(bool FromUser, string Text, DateTime At, bool IsError);

/// <summary>
/// The conversation, in a column of its own beside the stage: what the agent is doing, every
/// message since the app started, and the box to write in.
/// </summary>
/// <remarks>
/// It sits beside the slides rather than over them. A presentation is the point, and messages
/// floating across it covered the very thing they were about. Hidden with <c>T</c>, the stage
/// takes the whole window back.
/// </remarks>
public sealed class ConversationPanel : Border
{
    private const int MaxMessages = 300;

    private readonly Skin _theme;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly TextBlock _empty;
    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly Border _context;
    private readonly TextBlock _contextText;
    private readonly TextBlock _hint;
    private readonly IconButton _stop;
    private readonly DockPanel _header;
    private readonly Border _box;
    private readonly DockPanel _full;
    private readonly DockPanel _strip;
    private readonly Panel _stripTop;
    private string _placeholder = "Ask for anything to be explained…";
    private double _preferred = 420;

    public ConversationPanel(Skin theme)
    {
        _theme = theme;
        Orb = new AgentOrb(theme)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // The orb is the panel's handle: it says what the agent is doing, and a click on it
        // folds the conversation away or brings it back.
        Orb.SetValue(ToolTip.TipProperty, "Fold the conversation away, or bring it back (p)");
        Orb.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(Orb).Properties.IsLeftButtonPressed)
            {
                Collapse(!Collapsed);
                e.Handled = true;
            }
        };

        Width = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x18));
        BorderBrush = theme.Brush(theme.Line, 0.8);
        BorderThickness = new Thickness(1, 0, 0, 0);

        // ---- header: the orb, and what the agent is doing
        _status = new TextBlock
        {
            Text = "Conversation",
            Foreground = theme.Brush(theme.Text, 0.9),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _hint = new TextBlock
        {
            Text = "p",
            Foreground = theme.Brush(theme.Faint),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        // Only while the agent is working: a way out of an answer that is going the wrong way,
        // without closing the app or waiting for it to finish.
        _stop = new IconButton(theme, Glyph.StopSquare, "Stop the agent (Esc)", () => StopRequested?.Invoke(this, EventArgs.Empty), size: 30)
        {
            IsVisible = false,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _header = new DockPanel { Margin = new Thickness(18, 16, 18, 12) };
        DockPanel.SetDock(Orb, Dock.Left);
        DockPanel.SetDock(_hint, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        _header.Children.Add(Orb);
        _header.Children.Add(_hint);
        _header.Children.Add(_stop);
        _header.Children.Add(_status);

        // ---- the messages
        _empty = new TextBlock
        {
            Text = "Nothing said yet.",
            Foreground = theme.Brush(theme.Faint),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };

        // The margin belongs to the content, not the scroller: padding there leaves the
        // bubbles measuring themselves against the full width and spilling past the edge.
        // Bottom-aligned, so a short conversation sits above the box rather than floating.
        _messages = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(18, 0, 14, 12),
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { _empty },
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _messages,
        };

        // ---- what a question is about, after "Ask about this" on the stage
        _contextText = new TextBlock
        {
            FontSize = 13,
            Foreground = theme.Brush(theme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _context = new Border
        {
            IsVisible = false,
            Margin = new Thickness(18, 0, 18, 8),
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(10),
            Background = theme.Brush(theme.Accent, 0.14),
            BorderBrush = theme.Brush(theme.Accent, 0.5),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "about", FontSize = 12, Foreground = theme.Brush(theme.Muted), VerticalAlignment = VerticalAlignment.Center },
                    _contextText,
                    new TextBlock { Text = "✕", FontSize = 11, Foreground = theme.Brush(theme.Muted), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

        _context.PointerPressed += (_, e) =>
        {
            ClearContext();
            e.Handled = true;
        };

        // ---- the box
        _input = new TextBox
        {
            PlaceholderText = _placeholder,
            FontSize = 16,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = theme.Brush(theme.Text),
            CaretBrush = theme.Brush(theme.Accent),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 11),
            AcceptsReturn = true,
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 4000,
        };

        // Tunnelled, so Enter is seen before the box makes it a new line: Enter sends,
        // Shift+Enter is the new line.
        _input.AddHandler(KeyDownEvent, OnInputKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Fluent paints a focused box black with an accent border; the frame around it is the
        // visible part here, so the box itself stays invisible in every state.
        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        })
        {
            _input.Resources[key] = Brushes.Transparent;
        }

        _input.Resources["TextControlForegroundFocused"] = theme.Brush(theme.Text);
        _input.Resources["TextControlForegroundPointerOver"] = theme.Brush(theme.Text);
        _input.Resources["TextControlPlaceholderForegroundFocused"] = theme.Brush(theme.Faint);

        var hint = new TextBlock
        {
            Text = "↵",
            Foreground = theme.Brush(theme.Faint),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 12, 12),
        };

        var row = new DockPanel();
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(hint);
        row.Children.Add(_input);

        _box = new Border
        {
            Margin = new Thickness(14, 0, 14, 14),
            Background = theme.Brush(theme.Surface2, 0.55),
            BorderBrush = theme.Brush(theme.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = row,
        };

        _full = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_box, Dock.Bottom);
        DockPanel.SetDock(_context, Dock.Bottom);
        _full.Children.Add(_header);
        _full.Children.Add(_box);
        _full.Children.Add(_context);
        _full.Children.Add(_scroller);

        // Folded: the orb at the top, and at the bottom — level with the stage's own buttons —
        // a way to say something without unfolding anything.
        _stripTop = new Panel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Height = Metrics.Button };
        var talk = new IconButton(theme, Glyph.Chat, "Say something to the agent (`)", () => TalkRequested?.Invoke(this, EventArgs.Empty))
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, Metrics.BottomInset),
        };

        _strip = new DockPanel { IsVisible = false };
        DockPanel.SetDock(_stripTop, Dock.Top);
        DockPanel.SetDock(talk, Dock.Bottom);
        _strip.Children.Add(_stripTop);
        _strip.Children.Add(talk);
        _strip.Children.Add(new Panel());

        Child = new Panel { Children = { _full, _strip } };
    }

    /// <summary>The icon at the foot of the folded strip: open the input at the bottom of the stage.</summary>
    public event EventHandler? TalkRequested;

    public event EventHandler? CollapsedChanged;

    /// <summary>Whether the panel is a strip holding only the orb.</summary>
    public bool Collapsed { get; private set; }

    /// <summary>
    /// Narrows the panel to the orb alone, giving the stage the window back. Not hidden
    /// outright: the orb is how the user sees that the agent is working, and a strip is
    /// something to click on the way back.
    /// </summary>
    public void Collapse(bool collapsed)
    {
        if (Collapsed == collapsed && (_full.IsVisible != collapsed))
        {
            return;
        }

        Collapsed = collapsed;
        Width = collapsed ? Metrics.Strip : _preferred;
        _full.IsVisible = !collapsed;
        _strip.IsVisible = collapsed;

        // One orb, carried between the two faces: centred in the strip, leading the header.
        if (Orb.Parent is Panel parent)
        {
            parent.Children.Remove(Orb);
        }

        if (collapsed)
        {
            Orb.Margin = new Thickness(0);
            Orb.HorizontalAlignment = HorizontalAlignment.Center;
            _stripTop.Children.Add(Orb);
        }
        else
        {
            Orb.Margin = new Thickness(0, 0, 10, 0);
            Orb.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(Orb, Dock.Left);
            _header.Children.Insert(0, Orb);
        }

        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The width it takes when open: a share of the window, decided by the window.</summary>
    public void SetPreferredWidth(double width)
    {
        _preferred = width;
        if (!Collapsed)
        {
            Width = width;
        }
    }

    public AgentOrb Orb { get; }

    /// <summary>The user sent a message.</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>The user pressed Escape in the box, to give the keyboard back to the stage.</summary>
    public event EventHandler? Dismissed;

    /// <summary>The question stopped being about something on the stage.</summary>
    public event EventHandler? ContextCleared;

    /// <summary>Stop whatever the agent is doing now.</summary>
    public event EventHandler? StopRequested;

    public bool IsTyping => _input.IsFocused;

    /// <summary>Whether the stop button is showing, which is also whether there is anything to stop.</summary>
    public bool CanStop => _stop.IsVisible;

    /// <summary>The agent is working (or is not), which decides whether stopping is offered.</summary>
    public void SetBusy(bool busy)
    {
        _stop.IsVisible = busy && !Collapsed;
        _hint.IsVisible = !busy && !Collapsed;
    }

    public bool HasContext => _context.IsVisible;

    public void FocusInput()
    {
        Collapse(false);
        _input.Focus();
    }

    /// <summary>Makes the next message a question about <paramref name="label"/>.</summary>
    public void SetContext(string label)
    {
        _contextText.Text = label;
        _context.IsVisible = true;
        _input.PlaceholderText = $"What about “{Short(label)}”? ↵ asks for more about it";
        FocusInput();
    }

    /// <summary>A label short enough to sit in the box without pushing the rest out.</summary>
    private static string Short(string label) => label.Length <= 28 ? label : label[..27] + "…";

    public void ClearContext()
    {
        if (!_context.IsVisible)
        {
            return;
        }

        _context.IsVisible = false;
        _input.PlaceholderText = _placeholder;
        ContextCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the agent is doing, in the header; null goes back to the title.</summary>
    public void SetStatus(string? text)
    {
        var busy = !string.IsNullOrWhiteSpace(text);
        _status.Text = busy ? text : "Conversation";
        _status.Foreground = _theme.Brush(busy ? _theme.Cool : _theme.Text, busy ? 0.95 : 0.9);
    }

    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        if (!HasContext)
        {
            _input.PlaceholderText = text;
        }
    }

    /// <summary>Empties the transcript, for a session that has nothing behind it.</summary>
    public void Clear()
    {
        _messages.Children.Clear();
        _messages.Children.Add(_empty);
        ClearContext();
    }

    /// <summary>A line about the conversation rather than in it: when it was reopened.</summary>
    public void AddNote(string text)
    {
        _messages.Children.Remove(_empty);
        _messages.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = _theme.Brush(_theme.Faint),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
        });
    }

    public void AddMessage(bool fromUser, string text, bool isError = false, DateTimeOffset? at = null)
    {
        _messages.Children.Remove(_empty);

        var when = (at ?? DateTimeOffset.Now).ToLocalTime();
        var who = new TextBlock
        {
            Text = $"{(fromUser ? "You" : "Agent")} · {(when.Date == DateTimeOffset.Now.Date ? when.ToString("HH:mm") : when.ToString("d MMM HH:mm"))}",
            FontSize = 11,
            Foreground = _theme.Brush(isError ? _theme.Danger : fromUser ? _theme.Muted : _theme.Cool),
        };

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 9),
            Background = fromUser
                ? _theme.Brush(_theme.Surface2, 0.75)
                : new SolidColorBrush(isError ? Color.FromArgb(235, 0x3A, 0x12, 0x16) : Color.FromArgb(200, 0x0E, 0x22, 0x28)),
            BorderBrush = _theme.Brush(isError ? _theme.Danger : fromUser ? _theme.Line : _theme.Cool, fromUser ? 0.8 : 0.35),
            BorderThickness = new Thickness(1),
            Child = new SelectableTextBlock
            {
                Text = text,
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _theme.Brush(_theme.Text, fromUser ? 0.85 : 0.97),
            },
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(260) }],
        };

        _messages.Children.Add(new StackPanel { Spacing = 3, Children = { who, bubble } });
        while (_messages.Children.Count > MaxMessages)
        {
            _messages.Children.RemoveAt(0);
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                bubble.Opacity = 1;
                _scroller.ScrollToEnd();
            },
            DispatcherPriority.Background);
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Shift+Enter is left to the box, which makes it a new line.
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                break;

            case Key.Enter when string.IsNullOrWhiteSpace(_input.Text) && !HasContext:
                e.Handled = true;
                break;

            case Key.Enter:
                // A question about something may be just the pointing.
                var text = string.IsNullOrWhiteSpace(_input.Text) ? "Tell me more about this." : _input.Text.Trim();
                _input.Text = "";
                AddMessage(fromUser: true, HasContext ? $"About {_contextText.Text}: {text}" : text);
                Submitted?.Invoke(this, text);
                e.Handled = true;
                break;

            case Key.Back when HasContext && string.IsNullOrEmpty(_input.Text):
                ClearContext();
                e.Handled = true;
                break;

            case Key.Escape:
                ClearContext();
                Dismissed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }
}
