using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>A kicker, a big title and a subtitle, centred in the element's box.</summary>
public sealed class TitleView : ElementView
{
    private readonly TextBlock _kicker;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;

    public TitleView(ElementContext context, TitleElement element)
        : base(context, element)
    {
        _kicker = new TextBlock
        {
            Foreground = Skin.Brush(Skin.Accent),
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        _title = new TextBlock
        {
            Foreground = Skin.Brush(Skin.Text),
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = double.NaN,
        };

        _subtitle = new TextBlock
        {
            Foreground = Skin.Brush(Skin.Muted),
            FontSize = 32,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 1200,
        };

        Children.Add(new StackPanel
        {
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                _kicker,
                _title,
                new Border { Height = 3, Width = 96, Background = Skin.Brush(Skin.Accent, 0.85), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6) },
                _subtitle,
            },
        });
    }

    protected override Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var title = (TitleElement)element;
        _kicker.Text = title.Kicker?.ToUpperInvariant();
        _kicker.IsVisible = !string.IsNullOrWhiteSpace(title.Kicker);
        _title.Text = title.Text;

        // Long titles step down rather than wrapping into a wall of text.
        _title.FontSize = title.Text.Length switch
        {
            <= 24 => 88,
            <= 48 => 72,
            <= 80 => 58,
            _ => 46,
        };

        _subtitle.Text = title.Subtitle;
        _subtitle.IsVisible = !string.IsNullOrWhiteSpace(title.Subtitle);
        return Task.CompletedTask;
    }
}
