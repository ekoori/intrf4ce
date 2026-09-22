using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

// Shapes.Path, not System.IO.Path: the icons here are drawn, and this file never touches a file.
using Path = Avalonia.Controls.Shapes.Path;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Play, pause, stop and the two arrows, in the corner of the stage.
/// </summary>
/// <remarks>
/// The keys do all of this too, but a presentation that speaks and moves on by itself needs a
/// visible way to stop it: someone watching should not have to know that Space is pause.
/// Icons are drawn rather than typed, so no font has to carry ⏸ for the app to look right.
/// </remarks>
public sealed class TransportBar : Border
{
    private const string PlayIcon = "M 1,0 L 13,7.5 L 1,15 Z";
    private const string PauseIcon = "M 1,0 H 5 V 15 H 1 Z M 9,0 H 13 V 15 H 9 Z";
    private const string StopIcon = "M 1,1 H 13 V 13 H 1 Z";
    private const string PreviousIcon = "M 1,0 H 3.5 V 15 H 1 Z M 13,0 L 4.5,7.5 L 13,15 Z";
    private const string NextIcon = "M 13,0 H 10.5 V 15 H 13 Z M 1,0 L 9.5,7.5 L 1,15 Z";

    private readonly Skin _skin;
    private readonly Path _playPause;
    private readonly Border _stop;
    private readonly TextBlock _state;

    public TransportBar(Skin skin)
    {
        _skin = skin;

        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(20, 0, 0, 20);
        Padding = new Thickness(6);
        CornerRadius = new CornerRadius(25);

        // Its own backing, so the buttons and what they say read over any slide behind them.
        Background = new SolidColorBrush(Color.FromArgb(150, 0x08, 0x0C, 0x11));

        var previous = Button(PreviousIcon, "Previous slide", () => Previous?.Invoke(this, EventArgs.Empty));
        var play = Button(PlayIcon, "Play or pause the narration", () => PlayPause?.Invoke(this, EventArgs.Empty), accent: true);
        _playPause = (Path)play.Child!;
        _stop = Button(StopIcon, "Stop: no voice, and no slide moves on by itself", () => Stop?.Invoke(this, EventArgs.Empty));
        var next = Button(NextIcon, "Next slide", () => Next?.Invoke(this, EventArgs.Empty));

        _state = new TextBlock
        {
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 12, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(previous);
        row.Children.Add(play);
        row.Children.Add(_stop);
        row.Children.Add(next);
        row.Children.Add(_state);
        Child = row;
    }

    public event EventHandler? Previous;

    public event EventHandler? PlayPause;

    public event EventHandler? Stop;

    public event EventHandler? Next;

    /// <summary>Shows what pressing play would do now, and says when the presentation is held.</summary>
    public void Update(bool speaking, bool held, string state)
    {
        _playPause.Data = Geometry.Parse(speaking ? PauseIcon : PlayIcon);
        _stop.Opacity = held ? 0.45 : 1;
        _state.Text = held ? "stopped" : state is "speaking" or "paused" or "preparing the voice" ? state : "";
    }

    private Border Button(string icon, string tooltip, Action click, bool accent = false)
    {
        var glyph = new Path
        {
            Data = Geometry.Parse(icon),
            Fill = _skin.Brush(accent ? _skin.Ink : _skin.Text, 0.9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = accent ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Surface2, 0.85),
            BorderBrush = _skin.Brush(_skin.Line),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = glyph,
            [ToolTip.TipProperty] = tooltip,
        };

        var over = false;
        button.PointerEntered += (_, _) =>
        {
            over = true;
            button.Background = accent ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Surface2);
        };

        button.PointerExited += (_, _) =>
        {
            over = false;
            button.Background = accent ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Surface2, 0.85);
        };

        // Handled where it lands, so the stage underneath does not take it for a drag.
        var pressed = false;
        button.PointerPressed += (_, e) =>
        {
            pressed = e.GetCurrentPoint(button).Properties.IsLeftButtonPressed;
            e.Handled = true;
        };

        button.PointerReleased += (_, e) =>
        {
            if (pressed && over)
            {
                click();
            }

            pressed = false;
            e.Handled = true;
        };

        button.DoubleTapped += (_, e) => e.Handled = true;
        return button;
    }
}
