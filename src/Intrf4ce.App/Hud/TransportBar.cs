using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

// Shapes.Path, not System.IO.Path: the tick here is drawn, and this file never touches a file.
using Path = Avalonia.Controls.Shapes.Path;

namespace Intrf4ce.App.Hud;

/// <summary>
/// How the performance is controlled, in the bottom-left corner of the stage: play this
/// slide, keep going through the deck, the voice, the subtitles.
/// </summary>
/// <remarks>
/// Four decisions, not four buttons that do nearly the same thing. Play is about this slide;
/// the tick beside it is about the whole presentation, which is the difference between
/// hearing one answer again and sitting through the lot. The voice and the subtitles are
/// separate because a room where the sound is off still wants the words, and someone who
/// knows the subject wants the voice without a wall of text.
/// <para>
/// The keys do all of this too (space, C, M, S), but a presentation that speaks and moves on
/// by itself needs a visible way to stop it: nobody watching should have to know the keys.
/// </para>
/// </remarks>
public sealed class TransportBar : Border
{
    private readonly Skin _skin;
    private readonly IconButton _playPause;
    private readonly IconButton _voice;
    private readonly IconButton _captions;
    private readonly Border _box;
    private readonly Path _tick;
    private readonly TextBlock _state;

    public TransportBar(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;

        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Bottom;
        // Level with the strips' own buttons: every button along the bottom of the window
        // is the same size and sits on the same line.
        Margin = new Thickness(16, 0, 0, Metrics.BottomInset - Metrics.PillPadding);
        Padding = new Thickness(Metrics.PillPadding);
        CornerRadius = new CornerRadius((Metrics.Button / 2) + Metrics.PillPadding);

        // Its own backing, so the buttons and what they say read over any slide behind them.
        Background = new SolidColorBrush(Color.FromArgb(150, 0x08, 0x0C, 0x11));

        _playPause = new IconButton(skin, Glyph.Play, "Play or pause this slide", () => PlayPause?.Invoke(this, EventArgs.Empty), accent: true);

        _tick = new Path
        {
            Data = Geometry.Parse(Glyph.Tick),
            Stroke = skin.Brush(skin.Ink),
            StrokeThickness = 2.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _box = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = skin.Brush(skin.Line),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _tick,
        };

        _voice = new IconButton(skin, Glyph.Speaker, "Silence the voice (the slides carry on)", () => Mute?.Invoke(this, EventArgs.Empty));
        _captions = new IconButton(skin, Glyph.Subtitles, "Show or hide subtitles", () => Captions?.Invoke(this, EventArgs.Empty));

        _state = new TextBlock
        {
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(_playPause);
        row.Children.Add(Checkbox("keep going", "Play on through the rest of the presentation from here"));
        row.Children.Add(Divider());
        row.Children.Add(_voice);
        row.Children.Add(_captions);
        row.Children.Add(_state);
        Child = row;
    }

    /// <summary>Play or pause the narration of the slide on screen.</summary>
    public event EventHandler? PlayPause;

    /// <summary>Keep going through the deck, or stop at the end of this slide.</summary>
    public event EventHandler? Continuous;

    /// <summary>Silence the voice without stopping the presentation.</summary>
    public event EventHandler? Mute;

    public event EventHandler? Captions;

    /// <summary>Shows what each control would do now, and says when the presentation is held.</summary>
    /// <param name="state">What the narrator is doing, in its own words.</param>
    public void Update(bool speaking, bool held, bool continuous, bool muted, bool captions, string state)
    {
        // A pause holds the deck too, but it is still a pause: play carries on mid-sentence.
        var paused = state == "paused";
        _playPause.SetIcon(speaking ? Glyph.Pause : Glyph.Play);
        _playPause.SetTooltip(speaking ? "Pause" : paused ? "Carry on" : held ? "Play from here" : "Play this slide");

        _tick.IsVisible = continuous;
        _box.Background = continuous ? _skin.Brush(_skin.Accent) : Brushes.Transparent;
        _box.BorderBrush = continuous ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Line);

        _voice.SetIcon(Glyph.Speaker, muted ? Glyph.Cross : Glyph.Waves);
        _voice.Lit = !muted;
        _voice.SetTooltip(muted ? "Let the voice speak again" : "Silence the voice (the slides carry on)");

        _captions.SetIcon(Glyph.Subtitles, captions ? null : Glyph.Slash);
        _captions.Lit = captions;
        _captions.SetTooltip(captions ? "Hide the subtitles" : "Show the subtitles");

        _state.Text = paused ? state : held ? "stopped" : state is "speaking" or "preparing the voice" ? state : "";
    }

    private Border Divider() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = _skin.Brush(_skin.Line, 0.9),
    };

    /// <summary>The tick beside play: not an icon button, because it carries a word.</summary>
    private Border Checkbox(string label, string tooltip)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = _skin.Brush(_skin.Text, 0.8),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var shell = new Border
        {
            Height = Metrics.Button,
            Padding = new Thickness(8, 0, 12, 0),
            CornerRadius = new CornerRadius(Metrics.Button / 2),
            BorderThickness = new Thickness(1),
            BorderBrush = _skin.Brush(_skin.Line),
            Background = _skin.Brush(_skin.Surface2, 0.85),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _box, text },
            },
        };

        shell.SetValue(ToolTip.TipProperty, tooltip);

        var pressed = false;
        shell.PointerPressed += (_, e) =>
        {
            pressed = e.GetCurrentPoint(shell).Properties.IsLeftButtonPressed;
            e.Handled = true;
        };

        shell.PointerReleased += (_, e) =>
        {
            if (pressed)
            {
                Continuous?.Invoke(this, EventArgs.Empty);
            }

            pressed = false;
            e.Handled = true;
        };

        shell.DoubleTapped += (_, e) => e.Handled = true;
        return shell;
    }
}
