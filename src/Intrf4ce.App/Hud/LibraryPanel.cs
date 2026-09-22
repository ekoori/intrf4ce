using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Hud;

/// <summary>
/// The decks on disk (L): newest first, opened with the arrow keys and Enter, or a click.
/// </summary>
/// <remarks>
/// Everything an agent has ever built is a folder in the library, so this is how the user
/// gets back to one without asking an agent to find it.
/// </remarks>
public sealed class LibraryPanel : Border
{
    private readonly Skin _skin;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly List<(string Id, Border Row)> _rows = [];
    private int _index;

    public LibraryPanel(Skin skin)
    {
        _skin = skin;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Width = 720;
        MaxHeight = 620;
        Padding = new Thickness(0, 18, 0, 18);
        Background = new SolidColorBrush(Color.FromArgb(246, 0x10, 0x15, 0x1C));
        BorderBrush = skin.Brush(skin.Line);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(18);
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 60, Color = Color.FromArgb(180, 0, 0, 0), OffsetY = 16 });
        IsVisible = false;

        _empty = new TextBlock
        {
            Text = "No decks yet. Ask an agent to present something.",
            Foreground = skin.Brush(skin.Faint),
            FontSize = 16,
            Margin = new Thickness(26, 10, 26, 10),
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(18, 0),
            Content = _list,
        };

        var title = new TextBlock
        {
            Text = "Library",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Text),
        };

        var hint = new TextBlock
        {
            Text = "↑↓ and Enter · Esc to close",
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Margin = new Thickness(26, 0, 26, 14) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_scroller);
        Child = layout;
    }

    /// <summary>The user picked a deck; the id is the folder name.</summary>
    public event EventHandler<string>? Picked;

    public void Show(IReadOnlyList<DeckInfo> decks, string? currentId)
    {
        ArgumentNullException.ThrowIfNull(decks);

        _list.Children.Clear();
        _rows.Clear();

        if (decks.Count == 0)
        {
            _list.Children.Add(_empty);
        }

        foreach (var deck in decks)
        {
            var row = Row(deck, deck.Id == currentId);
            _rows.Add((deck.Id, row));
            _list.Children.Add(row);
        }

        _index = Math.Max(0, _rows.FindIndex(r => r.Id == currentId));
        Highlight();
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    /// <summary>Handles the keys that belong to the list while it is open.</summary>
    public bool Handle(Key key)
    {
        if (!IsVisible)
        {
            return false;
        }

        switch (key)
        {
            case Key.Down:
                Move(+1);
                return true;

            case Key.Up:
                Move(-1);
                return true;

            case Key.Enter:
                Pick();
                return true;

            case Key.Escape or Key.L:
                Hide();
                return true;

            default:
                return false;
        }
    }

    private void Move(int direction)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _index = Math.Clamp(_index + direction, 0, _rows.Count - 1);
        Highlight();
        _rows[_index].Row.BringIntoView();
    }

    private void Pick()
    {
        if (_index >= 0 && _index < _rows.Count)
        {
            var id = _rows[_index].Id;
            Hide();
            Picked?.Invoke(this, id);
        }
    }

    private void Highlight()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var selected = i == _index;
            _rows[i].Row.Background = selected ? _skin.Brush(_skin.Accent, 0.16) : Brushes.Transparent;
            _rows[i].Row.BorderBrush = selected ? _skin.Brush(_skin.Accent, 0.8) : _skin.Brush(_skin.Line, 0.5);
        }
    }

    private Border Row(DeckInfo deck, bool current)
    {
        var title = new TextBlock
        {
            Text = deck.Title,
            FontSize = 19,
            Foreground = _skin.Brush(_skin.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var detail = new TextBlock
        {
            Text = $"{deck.Slides} slide{(deck.Slides == 1 ? "" : "s")} · {When(deck.Modified)}" + (current ? " · open" : ""),
            FontSize = 14,
            Foreground = _skin.Brush(current ? _skin.Cool : _skin.Muted),
        };

        var row = new Border
        {
            Padding = new Thickness(16, 12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Spacing = 3, Children = { title, detail } },
        };

        row.PointerPressed += (_, e) =>
        {
            _index = _rows.FindIndex(r => ReferenceEquals(r.Row, row));
            Highlight();
            Pick();
            e.Handled = true;
        };

        return row;
    }

    /// <summary>"12 minutes ago", "yesterday", "12 Mar": how a person would say it.</summary>
    private static string When(DateTimeOffset moment)
    {
        var ago = DateTimeOffset.Now - moment;
        return ago switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{ago.TotalMinutes:0} minutes ago",
            { TotalHours: < 24 } => $"{ago.TotalHours:0} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 7 } => $"{ago.TotalDays:0} days ago",
            _ => moment.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        };
    }
}
