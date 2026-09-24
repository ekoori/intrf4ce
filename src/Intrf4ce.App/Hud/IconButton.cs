using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

// Shapes.Path, not System.IO.Path: every icon in the HUD is drawn.
using Path = Avalonia.Controls.Shapes.Path;

namespace Intrf4ce.App.Hud;

/// <summary>The drawn icons the HUD uses, as path data on a 16 by 16 square.</summary>
/// <remarks>
/// Drawn rather than typed, so the app does not depend on a font happening to carry ⏸ or 🔇,
/// which on Windows arrives as a hollow box or an unwanted colour emoji.
/// </remarks>
internal static class Glyph
{
    public const string Play = "M 2,1 L 14,8 L 2,15 Z";
    public const string Pause = "M 2.5,1 H 6.5 V 15 H 2.5 Z M 9.5,1 H 13.5 V 15 H 9.5 Z";
    public const string Previous = "M 1.5,1 H 4 V 15 H 1.5 Z M 14,1 L 5,8 L 14,15 Z";
    public const string Next = "M 14.5,1 H 12 V 15 H 14.5 Z M 2,1 L 11,8 L 2,15 Z";
    public const string Speaker = "M 1,5.5 H 4 L 8,1.5 V 14.5 L 4,10.5 H 1 Z";
    public const string Waves = "M 10,5.4 A 3.5,3.5 0 0 1 10,10.6 M 12.4,3 A 7,7 0 0 1 12.4,13";
    public const string Cross = "M 10,5.5 L 14.5,10.5 M 14.5,5.5 L 10,10.5";
    public const string Subtitles = "M 2,5 H 7 V 6.6 H 2 Z M 8.5,5 H 14 V 6.6 H 8.5 Z M 2,8.4 H 5.5 V 10 H 2 Z M 7,8.4 H 14 V 10 H 7 Z";
    public const string Frame = "M 1,2.5 H 15 V 13.5 H 1 Z";
    public const string Slash = "M 2,14 L 14,2";
    public const string Close = "M 3.2,1.8 L 8,6.6 L 12.8,1.8 L 14.2,3.2 L 9.4,8 L 14.2,12.8 L 12.8,14.2 L 8,9.4 L 3.2,14.2 L 1.8,12.8 L 6.6,8 L 1.8,3.2 Z";
    public const string Tick = "M 2,8 L 6,12 L 14,3";
    public const string Gear = "M 8,5 A 3,3 0 1 0 8,11 A 3,3 0 1 0 8,5 Z M 7,0.6 H 9 L 9.3,2.6 L 10.9,3.3 L 12.5,2.1 L 13.9,3.5 L 12.7,5.1 L 13.4,6.7 L 15.4,7 V 9 L 13.4,9.3 L 12.7,10.9 L 13.9,12.5 L 12.5,13.9 L 10.9,12.7 L 9.3,13.4 L 9,15.4 H 7 L 6.7,13.4 L 5.1,12.7 L 3.5,13.9 L 2.1,12.5 L 3.3,10.9 L 2.6,9.3 L 0.6,9 V 7 L 2.6,6.7 L 3.3,5.1 L 2.1,3.5 L 3.5,2.1 L 5.1,3.3 L 6.7,2.6 Z";
    public const string Plus = "M 7,1 H 9 V 7 H 15 V 9 H 9 V 15 H 7 V 9 H 1 V 7 H 7 Z";
    public const string StopSquare = "M 3,3 H 13 V 13 H 3 Z";
    public const string Lines = "M 0,1 H 14 V 3 H 0 Z M 0,6 H 14 V 8 H 0 Z M 0,11 H 14 V 13 H 0 Z";
}

/// <summary>
/// A round button with a drawn icon: the one shape every control on the stage is made of.
/// </summary>
/// <remarks>
/// It handles its own pointer events and marks them handled, because the stage underneath
/// treats a press as the start of a camera drag; a button that let the press through moved
/// the whole presentation sideways.
/// </remarks>
public sealed class IconButton : Border
{
    private readonly Skin _skin;
    private readonly Path _icon;
    private readonly Path _overlay;
    private readonly bool _accent;
    private bool _over;
    private bool _pressed;
    private bool _lit = true;

    public IconButton(Skin skin, string icon, string tooltip, Action click, bool accent = false, double size = 38)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(click);

        _skin = skin;
        _accent = accent;

        _icon = new Path
        {
            Data = Geometry.Parse(icon),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _overlay = new Path
        {
            IsVisible = false,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Both on the same 16-unit square, so an overlay lands where the icon drew it.
        var art = new Panel { Width = 16, Height = 16 };
        art.Children.Add(_icon);
        art.Children.Add(_overlay);

        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        BorderBrush = skin.Brush(skin.Line);
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = art;
        SetValue(ToolTip.TipProperty, tooltip);
        Paint();

        PointerEntered += (_, _) =>
        {
            _over = true;
            Paint();
        };

        PointerExited += (_, _) =>
        {
            _over = false;
            Paint();
        };

        PointerPressed += (_, e) =>
        {
            _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
            e.Handled = true;
        };

        PointerReleased += (_, e) =>
        {
            if (_pressed && _over)
            {
                click();
            }

            _pressed = false;
            e.Handled = true;
        };

        DoubleTapped += (_, e) => e.Handled = true;
    }

    /// <summary>Whether the thing this button controls is on; an off button is dimmed.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            _lit = value;
            Paint();
        }
    }

    public void SetIcon(string fill, string? stroke = null)
    {
        _icon.Data = Geometry.Parse(fill);
        _overlay.IsVisible = stroke is not null;
        if (stroke is not null)
        {
            _overlay.Data = Geometry.Parse(stroke);
        }
    }

    public void SetTooltip(string text) => SetValue(ToolTip.TipProperty, text);

    private void Paint()
    {
        var opacity = _over ? 1 : 0.85;
        Background = _accent ? _skin.Brush(_skin.Accent, opacity) : _skin.Brush(_skin.Surface2, opacity);

        var colour = _accent ? _skin.Ink : _lit ? _skin.Text : _skin.Muted;
        _icon.Fill = _skin.Brush(colour, _lit || _accent ? 0.95 : 0.55);
        _overlay.Stroke = _skin.Brush(colour, _lit || _accent ? 0.95 : 0.55);
        Opacity = _lit || _accent ? 1 : 0.8;
    }
}
