using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Every presentation on disk, in a column on the left, filed into groups.
/// </summary>
/// <remarks>
/// Each is a folder under <c>sessions</c> holding its slides, the media they use and the
/// conversation that built them, so this list is the app's memory. It opens by itself when
/// there is nothing on the stage and folds to a strip once a presentation is running.
/// <para>
/// Two faces rather than one squeezed: open, a list with a button for a new session at the
/// top and the settings at the foot; folded, a strip holding the same three things as
/// icons — the menu that opens it, a new session under it, the settings at the bottom, level
/// with the stage's own buttons.
/// </para>
/// <para>
/// Groups are how a long list stays usable. A session is filed by dragging it onto a group,
/// onto another session (it joins that one's group), or onto "new group" at the foot of the
/// list; right-click does the same without the mouse gymnastics. The agent can file them too.
/// </para>
/// </remarks>
public sealed class SessionsPanel : Border
{
    private const double DragThreshold = 6;

    private readonly Skin _skin;
    private readonly DockPanel _full;
    private readonly DockPanel _strip;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _newGroupTarget;
    private readonly List<(string Id, Border Row, string? Group)> _rows = [];
    private readonly List<(string Name, Border Header)> _headers = [];
    private readonly HashSet<string> _folded = new(StringComparer.Ordinal);
    private IReadOnlyList<DeckInfo> _decks = [];
    private string? _currentId;
    private double _preferred = 300;
    private int _index;

    // Dragging a session.
    private (string Id, Border Row, Point Start)? _pressed;
    private Border? _ghost;
    private Control? _dropTarget;

    public SessionsPanel(Skin skin)
    {
        _skin = skin;

        Width = _preferred;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x18));
        BorderBrush = skin.Brush(skin.Line, 0.8);
        BorderThickness = new Thickness(0, 0, 1, 0);

        // ---- open
        var menu = new IconButton(skin, Glyph.Lines, "Fold the sessions away (q)", () => Collapse(true), size: Metrics.Button)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var title = new TextBlock
        {
            Text = "Sessions",
            FontSize = 15,
            Foreground = skin.Brush(skin.Text, 0.9),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hint = new TextBlock
        {
            Text = "q",
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Margin = new Thickness(10, 12, 18, 12) };
        DockPanel.SetDock(menu, Dock.Left);
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(menu);
        header.Children.Add(hint);
        header.Children.Add(title);

        var fresh = Wide(Glyph.Plus, "New presentation", "Start a fresh session (:new)", () => NewRequested?.Invoke(this, EventArgs.Empty), accent: true);
        fresh.Margin = new Thickness(12, 0, 12, 12);

        var settings = Wide(Glyph.Gear, "Settings", "Voice, subtitles, pacing (:settings)", () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        settings.Margin = new Thickness(12, 8, 12, Metrics.BottomInset - 6);

        _empty = new TextBlock
        {
            Text = "No presentations yet. Ask for something to be explained and this fills up.",
            Foreground = skin.Brush(skin.Faint),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };

        _newGroupTarget = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = skin.Brush(skin.Accent, 0.5),
            Background = skin.Brush(skin.Accent, 0.06),
            Child = new TextBlock
            {
                Text = "+  Drop here for a new group",
                FontSize = 13,
                Foreground = skin.Brush(skin.Accent, 0.9),
            },
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(12, 0, 12, 14), Children = { _list, _newGroupTarget } },
        };

        _full = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(fresh, Dock.Top);
        DockPanel.SetDock(settings, Dock.Bottom);
        _full.Children.Add(header);
        _full.Children.Add(fresh);
        _full.Children.Add(settings);
        _full.Children.Add(_scroller);

        // ---- folded: the same three things, as icons, centred
        var top = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new IconButton(skin, Glyph.Lines, "Sessions (q)", () => Collapse(false), size: Metrics.Button),
                new IconButton(skin, Glyph.Plus, "New presentation (:new)", () => NewRequested?.Invoke(this, EventArgs.Empty), size: Metrics.Button),
            },
        };

        var gear = new IconButton(skin, Glyph.Gear, "Settings (:settings)", () => SettingsRequested?.Invoke(this, EventArgs.Empty), size: Metrics.Button)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, Metrics.BottomInset),
        };

        _strip = new DockPanel { IsVisible = false };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(gear, Dock.Bottom);
        _strip.Children.Add(top);
        _strip.Children.Add(gear);
        _strip.Children.Add(new Panel());

        var layers = new Panel();
        layers.Children.Add(_full);
        layers.Children.Add(_strip);
        layers.Children.Add(_overlay);
        Child = layers;

        // Dragging is tracked on the whole panel, so a session can be carried past the edge of
        // the row it started on.
        AddHandler(PointerMovedEvent, OnDragMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnDragReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The user picked a session; the id is its folder name.</summary>
    public event EventHandler<string>? Picked;

    /// <summary>Start again: an empty stage and a conversation with no history behind it.</summary>
    public event EventHandler? NewRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>A session was filed under a group (null: taken out of its group).</summary>
    public event EventHandler<(string Id, string? Group)>? Regrouped;

    /// <summary>A session is to be deleted, from its menu.</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>A group was renamed, every session in it with it.</summary>
    public event EventHandler<(string From, string To)>? GroupRenamed;

    public bool Collapsed { get; private set; }

    /// <summary>Fills the list; <paramref name="currentId"/> is marked as the one on the stage.</summary>
    public void Show(IReadOnlyList<DeckInfo> decks, string? currentId)
    {
        ArgumentNullException.ThrowIfNull(decks);

        _decks = decks;
        _currentId = currentId;
        Rebuild();
    }

    /// <summary>The groups sessions are filed under, most recently used first.</summary>
    public IReadOnlyList<string> Groups =>
        [.. _decks.Where(d => d.Group is not null).GroupBy(d => d.Group!).OrderByDescending(g => g.Max(d => d.Modified)).Select(g => g.Key)];

    /// <summary>Narrows to a strip, leaving a way back in.</summary>
    public void Collapse(bool collapsed)
    {
        Collapsed = collapsed;
        Width = collapsed ? Metrics.Strip : _preferred;
        _full.IsVisible = !collapsed;
        _strip.IsVisible = collapsed;
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CollapsedChanged;

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
            case Key.Down or Key.J:
                Move(+1);
                return true;

            case Key.Up or Key.K:
                Move(-1);
                return true;

            case Key.Enter or Key.L:
                Pick();
                return true;

            case Key.Escape:
                Collapse(true);
                return true;

            default:
                return false;
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        _headers.Clear();

        if (_decks.Count == 0)
        {
            _list.Children.Add(_empty);
            return;
        }

        // Groups first, the most recently touched on top; then everything not filed.
        foreach (var group in Groups)
        {
            var members = _decks.Where(d => d.Group == group).ToList();
            var folded = _folded.Contains(group);
            var header = GroupHeader(group, members.Count, folded);
            _headers.Add((group, header));
            _list.Children.Add(header);

            if (folded)
            {
                continue;
            }

            foreach (var deck in members)
            {
                AddRow(deck, indented: true);
            }
        }

        var loose = _decks.Where(d => d.Group is null).ToList();
        if (loose.Count > 0 && _headers.Count > 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "Not in a group",
                FontSize = 12,
                Foreground = _skin.Brush(_skin.Faint),
                Margin = new Thickness(4, 10, 0, 2),
            });
        }

        foreach (var deck in loose)
        {
            AddRow(deck, indented: false);
        }

        _index = Math.Max(0, _rows.FindIndex(r => r.Id == _currentId));
        Highlight();
    }

    private void AddRow(DeckInfo deck, bool indented)
    {
        var row = Row(deck, deck.Id == _currentId);
        if (indented)
        {
            row.Margin = new Thickness(14, 0, 0, 0);
        }

        _rows.Add((deck.Id, row, deck.Group));
        _list.Children.Add(row);
    }

    private Border GroupHeader(string name, int count, bool folded)
    {
        var chevron = new TextBlock
        {
            Text = folded ? "▸" : "▾",
            FontSize = 12,
            Foreground = _skin.Brush(_skin.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 14,
        };

        var label = new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = _skin.Brush(_skin.Cool, 0.95),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var tally = new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            Foreground = _skin.Brush(_skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var line = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(tally, Dock.Right);
        line.Children.Add(chevron);
        line.Children.Add(tally);
        line.Children.Add(label);

        var header = new Border
        {
            Padding = new Thickness(6, 7),
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = line,
            Tag = name,
        };

        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (!_folded.Remove(name))
            {
                _folded.Add(name);
            }

            Rebuild();
            e.Handled = true;
        };

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename group…" };
        rename.Click += (_, _) => AskName(header, name, renamed =>
        {
            if (renamed != name)
            {
                GroupRenamed?.Invoke(this, (name, renamed));
            }
        });

        var ungroup = new MenuItem { Header = "Ungroup" };
        ungroup.Click += (_, _) =>
        {
            foreach (var deck in _decks.Where(d => d.Group == name).ToList())
            {
                Regrouped?.Invoke(this, (deck.Id, null));
            }
        };

        menu.Items.Add(rename);
        menu.Items.Add(ungroup);
        header.ContextMenu = menu;
        return header;
    }

    /// <summary>Replaces a header's name with a box to type a new one; Enter keeps it, Escape does not.</summary>
    private void AskName(Border header, string current, Action<string> done)
    {
        var box = new TextBox
        {
            Text = current,
            FontSize = 13,
            MinWidth = 120,
            Padding = new Thickness(6, 3),
            Foreground = _skin.Brush(_skin.Text),
            Background = _skin.Brush(_skin.Surface2),
            BorderBrush = _skin.Brush(_skin.Accent),
            BorderThickness = new Thickness(1),
        };

        var previous = header.Child;
        header.Child = box;
        var finished = false;

        void Finish(bool keep)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            var name = box.Text?.Trim() ?? "";
            header.Child = previous;
            if (keep && name.Length > 0)
            {
                done(name);
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Finish(e.Key == Key.Enter);
                e.Handled = true;
            }
        };

        box.LostFocus += (_, _) => Finish(keep: true);
        box.AttachedToVisualTree += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
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
            FontSize = 15,
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
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Spacing = 3, Children = { title, detail } },
            Tag = deck.Id,
        };

        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Not picked yet: this may be the start of a drag. Release decides.
            _pressed = (deck.Id, row, e.GetPosition(this));
            e.Handled = true;
        };

        row.ContextMenu = RowMenu(deck);
        return row;
    }

    /// <summary>Filing without dragging: every group, a new one, or none.</summary>
    private ContextMenu RowMenu(DeckInfo deck)
    {
        var menu = new ContextMenu();
        var into = new MenuItem { Header = "Move to group" };

        foreach (var group in Groups.Where(g => g != deck.Group))
        {
            var item = new MenuItem { Header = group };
            item.Click += (_, _) => Regrouped?.Invoke(this, (deck.Id, group));
            into.Items.Add(item);
        }

        var fresh = new MenuItem { Header = "New group…" };
        fresh.Click += (_, _) => NewGroupFor(deck.Id);
        into.Items.Add(fresh);
        menu.Items.Add(into);

        if (deck.Group is not null)
        {
            var remove = new MenuItem { Header = "Remove from group" };
            remove.Click += (_, _) => Regrouped?.Invoke(this, (deck.Id, null));
            menu.Items.Add(remove);
        }

        // No "are you sure": u puts it back, as it would in vim, until the app closes.
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete  (dd, u to undo)" };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, deck.Id);
        menu.Items.Add(delete);

        return menu;
    }

    /// <summary>Files a session under a group made for it, then asks what to call the group.</summary>
    private void NewGroupFor(string deckId)
    {
        var name = "New group";
        for (var n = 2; Groups.Contains(name); n++)
        {
            name = $"New group {n}";
        }

        Regrouped?.Invoke(this, (deckId, name));

        // The list has been rebuilt with the new group's header; let its name be typed.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (_headers.FirstOrDefault(h => h.Name == name).Header is { } header)
                {
                    AskName(header, name, renamed =>
                    {
                        if (renamed != name)
                        {
                            GroupRenamed?.Invoke(this, (name, renamed));
                        }
                    });
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------------------------
    // Drag and drop

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (_ghost is null)
        {
            if (Math.Abs(at.X - pressed.Start.X) + Math.Abs(at.Y - pressed.Start.Y) < DragThreshold)
            {
                return;
            }

            StartDrag(pressed.Id);
        }

        Canvas.SetLeft(_ghost!, at.X + 12);
        Canvas.SetTop(_ghost!, at.Y - 10);
        Target(DropTargetAt(e));
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not { } pressed)
        {
            return;
        }

        _pressed = null;

        // A press and release without moving is a click: open it.
        if (_ghost is null)
        {
            _index = _rows.FindIndex(r => r.Id == pressed.Id);
            Highlight();
            Pick();
            return;
        }

        var target = DropTargetAt(e);
        EndDrag();

        switch (target)
        {
            case Border { Tag: string group } header when _headers.Any(h => ReferenceEquals(h.Header, header)):
                Regrouped?.Invoke(this, (pressed.Id, group));
                break;

            case Border row when _rows.FirstOrDefault(r => ReferenceEquals(r.Row, row)) is { Id: not null } other && other.Id != pressed.Id:
                Regrouped?.Invoke(this, (pressed.Id, other.Group));
                break;

            case Border zone when ReferenceEquals(zone, _newGroupTarget):
                NewGroupFor(pressed.Id);
                break;
        }

        e.Handled = true;
    }

    private void StartDrag(string id)
    {
        var title = _decks.FirstOrDefault(d => d.Id == id)?.Title ?? id;
        _ghost = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(8),
            Background = _skin.Brush(_skin.Surface2, 0.95),
            BorderBrush = _skin.Brush(_skin.Accent),
            BorderThickness = new Thickness(1),
            Opacity = 0.9,
            MaxWidth = 220,
            Child = new TextBlock { Text = title, FontSize = 13, Foreground = _skin.Brush(_skin.Text), TextTrimming = TextTrimming.CharacterEllipsis },
        };

        _overlay.Children.Add(_ghost);
        _newGroupTarget.IsVisible = true;
    }

    private void EndDrag()
    {
        Target(null);
        if (_ghost is not null)
        {
            _overlay.Children.Remove(_ghost);
            _ghost = null;
        }

        _newGroupTarget.IsVisible = false;
    }

    /// <summary>A group header, a session, or the new-group zone under the pointer.</summary>
    private Control? DropTargetAt(PointerEventArgs e)
    {
        foreach (var (_, header) in _headers)
        {
            if (Contains(header, e))
            {
                return header;
            }
        }

        foreach (var (_, row, _) in _rows)
        {
            if (Contains(row, e))
            {
                return row;
            }
        }

        return _newGroupTarget.IsVisible && Contains(_newGroupTarget, e) ? _newGroupTarget : null;
    }

    private static bool Contains(Control control, PointerEventArgs e) =>
        control.IsVisible && new Rect(control.Bounds.Size).Contains(e.GetPosition(control));

    /// <summary>Lights whatever a dropped session would land on, and only that.</summary>
    private void Target(Control? target)
    {
        if (ReferenceEquals(target, _dropTarget))
        {
            return;
        }

        _dropTarget = target;

        // Everything back to how it looks at rest, then the one target lit.
        Highlight();
        foreach (var (_, header) in _headers)
        {
            header.BorderBrush = Brushes.Transparent;
        }

        _newGroupTarget.BorderBrush = _skin.Brush(_skin.Accent, 0.5);

        if (target is Border lit)
        {
            lit.BorderBrush = _skin.Brush(_skin.Accent);
        }
    }

    /// <summary>A wide button with an icon and a word, for the two things that are not a session.</summary>
    private Border Wide(string icon, string label, string tooltip, Action click, bool accent = false)
    {
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(icon),
            Fill = _skin.Brush(accent ? _skin.Ink : _skin.Muted),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var text = new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = _skin.Brush(accent ? _skin.Ink : _skin.Text, accent ? 1 : 0.85),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Height = Metrics.Button,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Background = accent ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Surface2, 0.7),
            BorderBrush = accent ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Line),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { glyph, text } },
            [ToolTip.TipProperty] = tooltip,
        };

        button.PointerEntered += (_, _) => button.Background = accent ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Surface2);
        button.PointerExited += (_, _) => button.Background = accent ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Surface2, 0.7);

        button.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            click();
        };

        return button;
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
