using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Intrf4ce.App.Hud;

/// <summary>One line of the conversation.</summary>
public sealed record ChatMessage(bool FromUser, string Text, DateTime At, bool IsError);

/// <summary>
/// The strip at the bottom: the agent orb, what it is doing, the message box, and the last
/// few messages floating above it.
/// </summary>
/// <remarks>
/// Messages fade after a while so the chat never becomes a second document competing with the
/// stage; the full transcript is one keypress away (T).
/// </remarks>
public sealed class ChatBar : StackPanel
{
    private const int MaxBubbles = 3;

    private readonly Skin _theme;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly StackPanel _bubbles;
    private readonly Border _context;
    private readonly TextBlock _contextText;
    private string _placeholder = "Ask for anything to be explained…";

    public ChatBar(Skin theme)
    {
        _theme = theme;
        Orb = new AgentOrb(theme) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Width = 900;
        Spacing = 8;
        Margin = new Thickness(0, 0, 0, 22);

        _bubbles = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };

        _status = new TextBlock
        {
            Foreground = theme.Brush(theme.Cool, 0.85),
            FontSize = 14,
            Margin = new Thickness(18, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false,
        };

        _input = new TextBox
        {
            PlaceholderText = "Ask for anything to be explained…",
            FontSize = 18,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = theme.Brush(theme.Text),
            CaretBrush = theme.Brush(theme.Accent),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 12),
            AcceptsReturn = false,
            MaxLength = 4000,
        };

        _input.KeyDown += OnInputKey;

        // Fluent paints a focused box black with an accent border; the bar is the frame here,
        // so the box itself stays invisible in every state.
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
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 16, 0),
        };

        // What a question is about, after "Ask about this" on the stage; click to drop it.
        _contextText = new TextBlock
        {
            FontSize = 14,
            Foreground = theme.Brush(theme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _context = new Border
        {
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(10),
            Background = theme.Brush(theme.Accent, 0.14),
            BorderBrush = theme.Brush(theme.Accent, 0.5),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "about", FontSize = 13, Foreground = theme.Brush(theme.Muted), VerticalAlignment = VerticalAlignment.Center },
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

        var row = new DockPanel();
        DockPanel.SetDock(Orb, Dock.Left);
        DockPanel.SetDock(_context, Dock.Left);
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(Orb);
        row.Children.Add(_context);
        row.Children.Add(hint);
        row.Children.Add(_input);

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0x10, 0x15, 0x1C)),
            BorderBrush = theme.Brush(theme.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 30, Color = Color.FromArgb(160, 0, 0, 0), OffsetY = 8 }),
            Child = row,
        };

        Children.Add(_bubbles);
        Children.Add(_status);
        Children.Add(bar);
    }

    public AgentOrb Orb { get; }

    /// <summary>The user sent a message.</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>The user pressed Escape in the box, to give the keyboard back to the stage.</summary>
    public event EventHandler? Dismissed;

    /// <summary>The question stopped being about something on the stage.</summary>
    public event EventHandler? ContextCleared;

    /// <summary>A message was added, from either side, for the transcript.</summary>
    public event EventHandler<ChatMessage>? MessageAdded;

    public bool IsTyping => _input.IsFocused;

    public bool HasContext => _context.IsVisible;

    public void FocusInput() => _input.Focus();

    /// <summary>Makes the next message a question about <paramref name="label"/>.</summary>
    public void SetContext(string label)
    {
        _contextText.Text = label;
        _context.IsVisible = true;
        _input.PlaceholderText = "Ask about it…";
        FocusInput();
    }

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

    public void SetStatus(string? text)
    {
        _status.Text = text;
        _status.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        if (!HasContext)
        {
            _input.PlaceholderText = text;
        }
    }

    public void AddMessage(bool fromUser, string text, bool isError = false)
    {
        MessageAdded?.Invoke(this, new ChatMessage(fromUser, text, DateTime.Now, isError));

        var bubble = new Border
        {
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 720,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 10),
            Background = fromUser
                ? _theme.Brush(_theme.Surface2, 0.95)
                : new SolidColorBrush(isError ? Color.FromArgb(235, 0x3A, 0x12, 0x16) : Color.FromArgb(235, 0x0E, 0x22, 0x28)),
            BorderBrush = _theme.Brush(isError ? _theme.Danger : fromUser ? _theme.Line : _theme.Cool, fromUser ? 1 : 0.45),
            BorderThickness = new Thickness(1),
            Child = new SelectableTextBlock
            {
                Text = text,
                FontSize = 16,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _theme.Brush(_theme.Text, 0.95),
            },
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(300) }],
        };

        _bubbles.Children.Add(bubble);
        while (_bubbles.Children.Count > MaxBubbles)
        {
            _bubbles.Children.RemoveAt(0);
        }

        Dispatcher.UIThread.Post(() => bubble.Opacity = 1, DispatcherPriority.Background);

        // Long answers stay longer.
        var linger = TimeSpan.FromSeconds(Math.Clamp(8 + (text.Length / 40.0), 8, 40));
        DispatcherTimer.RunOnce(() => FadeAway(bubble), linger);
    }

    private void FadeAway(Border bubble)
    {
        bubble.Opacity = 0;
        DispatcherTimer.RunOnce(() => _bubbles.Children.Remove(bubble), TimeSpan.FromMilliseconds(350));
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !string.IsNullOrWhiteSpace(_input.Text) || HasContext:
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
