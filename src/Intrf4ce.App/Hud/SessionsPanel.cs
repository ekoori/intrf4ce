using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

// Shapes.Path, not System.IO.Path: the icon here is drawn.
using Path = Avalonia.Controls.Shapes.Path;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Every presentation on disk, in a column on the left: pick one up where it was left.
/// </summary>
/// <remarks>
/// Each is a folder under <c>sessions</c> holding its slides and the media they use, so this
/// list is the app's memory. It opens by itself when there is nothing on the stage and folds
/// to a strip once a presentation is running, because then the stage is what matters.
/// </remarks>
public sealed class SessionsPanel : Border
{
    private const string Icon = "M 0,1 H 14 V 3 H 0 Z M 0,6 H 14 V 8 H 0 Z M 0,11 H 14 V 13 H 0 Z";

    private readonly Skin _skin;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly TextBlock _title;
    private readonly TextBlock _hint;
    private readonly DockPanel _header;
    private readonly Path _icon;
    private readonly List<(string Id, Border Row)> _rows = [];
    private double _preferred = 300;
    private int _index;

    public SessionsPanel(Skin skin)
    {
        _skin = skin;

        Width = _preferred;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x18));
        BorderBrush = skin.Brush(skin.Line, 0.8);
        BorderThickness = new Thickness(0, 0, 1, 0);

        _icon = new Path
        {
            Data = Geometry.Parse(Icon),
            Fill = skin.Brush(skin.Muted),
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        _title = new TextBlock
        {
            Text = "Sessions",
            FontSize = 15,
            Foreground = skin.Brush(skin.Text, 0.9),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _hint = new TextBlock
        {
            Text = "L",
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _header = new DockPanel { Margin = new Thickness(18, 16, 18, 12) };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_hint, Dock.Right);
        _header.Children.Add(_icon);
        _header.Children.Add(_hint);
        _header.Children.Add(_title);

        _empty = new TextBlock
        {
            Text = "No presentations yet. Ask for something to be explained and this fills up.",
            Foreground = skin.Brush(skin.Faint),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };

        _list.Children.Add(_empty);
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(12, 0, 12, 14), Children = { _list } },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(_scroller);
        Child = layout;

        PointerPressed += (_, e) =>
        {
            if (Collapsed)
            {
                Collapse(false);
                e.Handled = true;
            }
        };
    }

    /// <summary>The user picked a session; the id is its folder name.</summary>
    public event EventHandler<string>? Picked;

    public bool Collapsed { get; private set; }

    /// <summary>Fills the list; <paramref name="currentId"/> is marked as the one on the stage.</summary>
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
    }

    /// <summary>Narrows to a strip, leaving a way back in.</summary>
    public void Collapse(bool collapsed)
    {
        Collapsed = collapsed;
        Width = collapsed ? 54 : _preferred;
        Cursor = new Cursor(collapsed ? StandardCursorType.Hand : StandardCursorType.Arrow);

        _scroller.IsVisible = !collapsed;
        _title.IsVisible = !collapsed;
        _hint.IsVisible = !collapsed;
        _header.Margin = collapsed ? new Thickness(0, 18, 0, 12) : new Thickness(18, 16, 18, 12);
        _icon.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        _icon.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    public void SetPreferredWidth(double width)
    {
        _preferred = width;
        if (!Collapsed)
        {
            Width = width;
        }
    }

    /// <summary>Handles the keys that belong to the list while it is open.</summary>
    public bool Handle(Key key)
    {
        if (Collapsed || _rows.Count == 0)
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

            case Key.Escape:
                Collapse(true);
                return true;

            default:
                return false;
        }
    }

    private void Move(int direction)
    {
        _index = Math.Clamp(_index + direction, 0, _rows.Count - 1);
        Highlight();
        _rows[_index].Row.BringIntoView();
    }

    private void Pick()
    {
        if (_index >= 0 && _index < _rows.Count)
        {
            Picked?.Invoke(this, _rows[_index].Id);
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
            FontSize = 16,
            Foreground = _skin.Brush(_skin.Text),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var detail = new TextBlock
        {
            Text = $"{deck.Slides} slide{(deck.Slides == 1 ? "" : "s")} · {When(deck.Modified)}" + (current ? " · open" : ""),
            FontSize = 12,
            Foreground = _skin.Brush(current ? _skin.Cool : _skin.Muted),
        };

        var row = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
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
            { TotalMinutes: < 120 } => $"{ago.TotalMinutes:0} minutes ago",
            { TotalHours: < 2 } => "an hour ago",
            { TotalHours: < 24 } => $"{ago.TotalHours:0} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 7 } => $"{ago.TotalDays:0} days ago",
            _ => moment.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        };
    }
}
