using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Intrf4ce.App.Hud;

/// <summary>Top-left: the deck's title, where the user is in it, and a dot per slide.</summary>
public sealed class HeaderBar : StackPanel
{
    private const int MaxDots = 40;

    private readonly Skin _theme;
    private readonly TextBlock _title;
    private readonly TextBlock _position;
    private readonly WrapPanel _dots;

    public HeaderBar(Skin theme)
    {
        _theme = theme;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(28, 20, 0, 0);
        Spacing = 6;
        IsHitTestVisible = false;

        _title = new TextBlock { Foreground = theme.Brush(theme.Text, 0.9), FontSize = 17, FontWeight = FontWeight.SemiBold };
        _position = new TextBlock { Foreground = theme.Brush(theme.Muted), FontSize = 13 };
        _dots = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 420 };

        Children.Add(_title);
        Children.Add(_position);
        Children.Add(_dots);
    }

    public void Update(string? title, int current, int count, bool overview)
    {
        IsVisible = title is not null;
        _title.Text = title;
        _position.Text = count == 0
            ? "waiting for the first slide"
            : overview ? $"{count} slides · overview" : $"{current + 1} of {count}";

        _dots.Children.Clear();
        for (var i = 0; i < Math.Min(count, MaxDots); i++)
        {
            var active = i == current;
            _dots.Children.Add(new Border
            {
                Width = active ? 18 : 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 2, 5, 2),
                Background = _theme.Brush(active ? _theme.Accent : _theme.Muted, active ? 1 : 0.45),
            });
        }
    }
}
