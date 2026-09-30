using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Back and forward through the deck, with where you are, in the bottom-right corner.
/// </summary>
/// <remarks>
/// Apart from the transport on the other side, because moving between slides is the user's
/// own business and playing is the performance's: putting them together had people press
/// "next" meaning "carry on" and skip the sentence they were listening to. Right-hand corner
/// because that is the direction the deck goes, and the hand that turns a page.
/// </remarks>
public sealed class SlideNav : Border
{
    private readonly IconButton _previous;
    private readonly IconButton _next;
    private readonly TextBlock _position;

    public SlideNav(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);

        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 16, Metrics.BottomInset - Metrics.PillPadding);
        Padding = new Thickness(Metrics.PillPadding);
        CornerRadius = new CornerRadius((Metrics.Button / 2) + Metrics.PillPadding);
        Background = new SolidColorBrush(Color.FromArgb(150, 0x08, 0x0C, 0x11));

        _previous = new IconButton(skin, Glyph.Previous, "Previous slide (←)", () => Previous?.Invoke(this, EventArgs.Empty));
        _next = new IconButton(skin, Glyph.Next, "Next slide (→)", () => Next?.Invoke(this, EventArgs.Empty));

        _position = new TextBlock
        {
            FontSize = 13,
            Foreground = skin.Brush(skin.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 46,
            TextAlignment = TextAlignment.Center,
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _previous, _position, _next },
        };
    }

    public event EventHandler? Previous;

    public event EventHandler? Next;

    /// <summary>Where the user is; an end of the deck dims the arrow that would do nothing.</summary>
    public void Update(int current, int count)
    {
        _position.Text = count == 0 ? "" : $"{current + 1} / {count}";
        _previous.Lit = current > 0;
        _next.Lit = count > 0 && current < count - 1;
    }
}
