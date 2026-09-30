using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

// Shapes.Path, not System.IO.Path: the window buttons are drawn.
using Path = Avalonia.Controls.Shapes.Path;

namespace Intrf4ce.App.Hud;

/// <summary>Sizes every control on the window's edges shares, so they line up.</summary>
internal static class Metrics
{
    /// <summary>Every round button: the transport, the arrows, the strips' icons.</summary>
    public const double Button = 36;

    /// <summary>From the bottom of the window to the bottom of every button along it.</summary>
    public const double BottomInset = 20;

    /// <summary>The padding of the pills the transport and the arrows sit in.</summary>
    public const double PillPadding = 5;

    /// <summary>A collapsed side panel.</summary>
    public const double Strip = 56;

    public const double TitleHeight = 34;
}

/// <summary>
/// The window's own title bar, in place of the system's.
/// </summary>
/// <remarks>
/// As in Fjord: the system bar is a strip of chrome in another visual language, showing a
/// name the user already knows. This one is the app's colour, carries the three window
/// buttons and whatever keys are half-typed, and is the handle the window is moved by —
/// double-click maximises, as everywhere. The frame stays (<c>BorderOnly</c>), so the window
/// still resizes from its edges and snaps.
/// </remarks>
public sealed class TitleBar : Border
{
    private const string Minimise = "M 3,8.5 H 13 V 9.5 H 3 Z";
    private const string Maximise = "M 3.5,3.5 H 12.5 V 12.5 H 3.5 Z M 4.5,4.5 V 11.5 H 11.5 V 4.5 Z";
    private const string Restore = "M 5.5,3 H 13 V 10.5 H 11.5 V 11 H 12 V 4 H 6 V 4.5 H 5.5 Z M 3,5.5 H 10.5 V 13 H 3 Z M 4,6.5 V 12 H 9.5 V 6.5 Z";
    private const string Close = "M 3.6,2.9 L 8,7.3 L 12.4,2.9 L 13.1,3.6 L 8.7,8 L 13.1,12.4 L 12.4,13.1 L 8,8.7 L 3.6,13.1 L 2.9,12.4 L 7.3,8 L 2.9,3.6 Z";

    private readonly Skin _skin;
    private readonly Window _window;
    private readonly TextBlock _title;
    private readonly TextBlock _keys;
    private readonly Path _maximise;

    public TitleBar(Skin skin, Window window)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(window);

        _skin = skin;
        _window = window;

        Height = Metrics.TitleHeight;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16));
        BorderBrush = skin.Brush(skin.Line, 0.6);
        BorderThickness = new Thickness(0, 0, 0, 1);

        var mark = new TextBlock
        {
            Text = "intrf4ce",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Accent, 0.9),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 12, 0),
        };

        _title = new TextBlock
        {
            FontSize = 13,
            Foreground = skin.Brush(skin.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _keys = new TextBlock
        {
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            Foreground = skin.Brush(skin.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
        };

        var maximise = WindowButton(Maximise, "Maximise", () =>
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        _maximise = (Path)maximise.Child!;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                WindowButton(Minimise, "Minimise", () => _window.WindowState = WindowState.Minimized),
                maximise,
                WindowButton(Close, "Close", _window.Close, closing: true),
            },
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(_keys, Dock.Right);
        row.Children.Add(mark);
        row.Children.Add(buttons);
        row.Children.Add(_keys);
        row.Children.Add(_title);
        Child = row;

        PointerPressed += OnPressed;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                _maximise.Data = Geometry.Parse(_window.WindowState == WindowState.Maximized ? Restore : Maximise);
            }
        };
    }

    /// <summary>What is on the stage, beside the app's name.</summary>
    public void SetTitle(string? title) => _title.Text = title is { Length: > 0 } ? "·  " + title : "";

    /// <summary>Keys typed so far that could still become a command — the <c>g</c> of <c>gg</c>.</summary>
    public void SetPendingKeys(string? keys) => _keys.Text = keys ?? "";

    /// <summary>Moves the window from anywhere nothing else wanted the press; double-click maximises.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount > 1)
        {
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        _window.BeginMoveDrag(e);
    }

    /// <summary>
    /// One of the three: drawn, not typed, and red only for close and only under the pointer —
    /// a permanently red cross in the corner reads as an error rather than a button.
    /// </summary>
    private Border WindowButton(string icon, string tooltip, Action click, bool closing = false)
    {
        var glyph = new Path
        {
            Data = Geometry.Parse(icon),
            Fill = _skin.Brush(_skin.Muted),
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Width = 46,
            Background = Brushes.Transparent,
            Child = glyph,
            [ToolTip.TipProperty] = tooltip,
        };

        button.PointerEntered += (_, _) =>
        {
            button.Background = closing ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : _skin.Brush(_skin.Surface2);
            glyph.Fill = _skin.Brush(_skin.Text);
        };

        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            glyph.Fill = _skin.Brush(_skin.Muted);
        };

        button.PointerPressed += (_, e) =>
        {
            // Or the press falls through and starts dragging the window instead.
            e.Handled = true;
        };

        button.PointerReleased += (_, e) =>
        {
            if (button.IsPointerOver)
            {
                click();
            }

            e.Handled = true;
        };

        return button;
    }
}
