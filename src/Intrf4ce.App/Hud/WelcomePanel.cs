using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Intrf4ce.App.Hud;

/// <summary>What an empty stage says: what this is, and how to start.</summary>
public sealed class WelcomePanel : StackPanel
{
    private readonly TextBlock _hint;
    private readonly SelectableTextBlock _connect;

    public WelcomePanel(Skin theme)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 14;
        Margin = new Thickness(0, 0, 0, 120);

        Children.Add(new TextBlock
        {
            Text = "intrf4ce",
            FontSize = 64,
            FontWeight = FontWeight.Light,
            LetterSpacing = 6,
            Foreground = theme.Brush(theme.Text),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        Children.Add(new Border
        {
            Height = 2,
            Width = 120,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(theme.Cool, 0), new GradientStop(theme.Accent, 1) },
            },
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        _hint = new TextBlock
        {
            FontSize = 20,
            Foreground = theme.Brush(theme.Muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720,
            LineHeight = 30,
        };

        _connect = new SelectableTextBlock
        {
            FontSize = 14,
            Foreground = theme.Brush(theme.Faint),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 820,
        };

        Children.Add(_hint);
        Children.Add(_connect);
    }

    public void Set(string hint, string? connect)
    {
        _hint.Text = hint;
        _connect.Text = connect;
        _connect.IsVisible = connect is not null;
    }
}
